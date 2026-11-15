
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace NotiRelay;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _pollTimer;
    private readonly HashSet<uint> _seenNotificationIds = new();

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly AppSettings _settings;
    private readonly DeliveryQueue _deliveryQueue;
    private readonly DeliveryWorker _deliveryWorker;
    private readonly TeamsInboxWatcher _teamsInboxWatcher;
    private readonly CalendarWatcher _calendarWatcher;
    private bool _isCalendarRunning;

    private readonly BatteryWatcher _batteryWatcher;
    private bool _isBatteryRunning;
    private bool _suppressBatteryToggle;

    // The "topic per notification type" rows in the ntfy card.
    private Dictionary<NotificationCategory, (System.Windows.Controls.TextBox Box, TextBlock Placeholder,
        TextBlock State, System.Windows.Controls.Button Clear)>? _categoryTopicRows;

    private readonly System.Drawing.Icon _trayImage;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;

    private UserNotificationListener? _listener;
    private NtfyClient? _ntfy;

    private bool _isPolling;
    private bool _isRunning;
    private bool _isTeamsInboxRunning;
    private bool _isShuttingDown;
    private bool _allowExit;
    private bool _suppressStartupToggle;
    private bool _suppressConsentToggle;
    private bool _isTestingNtfy;
    private bool _topicRevealed;
    private bool _trayHintShown;
    private bool _heartbeatErrorLogged;

    // Debug log: the topic to mask (refreshed whenever the saved topic changes) and
    // whether a write failure was already shown on screen.
    private string? _debugTopic;
    private IReadOnlyList<string> _debugTopics = Array.Empty<string>();
    private bool _debugWriteWarned;

    // Sending to ntfy is held until the launch question is answered, and while the user
    // has pressed Stop sending.
    private readonly SendingGate _sendingGate = new();
    private System.Windows.Forms.ToolStripMenuItem? _traySendingItem;
    private bool _resumeInProgress;
    private readonly ActionDebounce _sendingToggleDebounce = new(TimeSpan.FromMilliseconds(700));

    // Windows is signing out or shutting down: a prompt closed by the system is not the
    // user's "No", so nothing is discarded and sending is not reopened.
    private bool _sessionEnding;

    // When NotiRelay last ran, read before this run overwrites it: toasts newer than
    // this were missed while the app was closed.
    private readonly DateTimeOffset? _previousActiveUtc;
    private readonly DispatcherTimer _heartbeatTimer;

    private readonly List<MissedOutlookToast> _missedOutlook = new();
    private IReadOnlyList<MissedTeamsMessage> _launchMissedTeams = Array.Empty<MissedTeamsMessage>();

    private sealed record MissedOutlookToast(string Text, DateTimeOffset CreatedAt, string QueueId);

    private enum StatusKind { Neutral, Success, Warning, Error }

    public MainWindow()
    {
        InitializeComponent();

        FitToWorkArea();

        ShowInTaskbar = false;

        _trayImage = CreateApplicationTrayImage();
        _trayIcon = CreateTrayIcon(_trayImage);

        StateChanged += MainWindow_StateChanged;

        _settings = AppSettings.Load();

        _previousActiveUtc = _settings.LastActiveUtc;

        // Record that NotiRelay is running (every 30 s, on every forwarded toast, on exit
        // and when Windows signs out), so the next launch knows what was missed.
        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _heartbeatTimer.Tick += (_, _) => SaveHeartbeat();
        _heartbeatTimer.Start();

        System.Windows.Application.Current.SessionEnding += (_, _) =>
        {
            _sessionEnding = true;
            SaveHeartbeat();
        };

        TeamsInboxPathBox.Text = _settings.TeamsInboxPath;

        _deliveryQueue = new DeliveryQueue();

        _deliveryWorker = new DeliveryWorker(
            _deliveryQueue,
            _httpClient,
            LogFromWorker,
            _sendingGate);

        _calendarWatcher = new CalendarWatcher(
            _deliveryQueue,
            () => _settings.GetTopicFor(NotificationCategory.Calendar),
            LogFromWorker);

        _batteryWatcher = new BatteryWatcher(
            _deliveryQueue,
            () => _settings.GetTopicFor(NotificationCategory.Battery),
            LogFromWorker,
            ShowBatteryStatus);

        _teamsInboxWatcher = new TeamsInboxWatcher(
            _deliveryQueue,
            () => _settings.GetTopicFor(NotificationCategory.Teams),
            LogFromWorker);

        InitCategoryTopicRows();
        RefreshTopicState();
        RefreshCategoryTopicStates();
        ShowInitialNtfyStatus();

        SetBatteryCheckBox(_settings.BatteryAlertsEnabled);

        HookUnhandledExceptionsIntoDebugLog();

        // Debugging stays on across restarts, so startup (when the queue is flushed) is captured.
        if (_settings.DebugLoggingEnabled)
            StartDebugLog("NotiRelay started with Debugging on");
        else
            UpdateDebugButton();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };

        _pollTimer.Tick += PollTimer_Tick;

        // The confirmation is the user's own, remembered choice; the app never ticks it.
        _suppressConsentToggle = true;
        AuthorizationCheckBox.IsChecked = _settings.OutlookForwardingConfirmed;
        _suppressConsentToggle = false;

        _suppressStartupToggle = true;
        StartWithWindowsCheckBox.IsChecked = StartupManager.IsEnabled();
        _suppressStartupToggle = false;

        SetRunningState(false);
        SetTeamsInboxRunningState(false);

        FooterText.Text =
            $"NotiRelay {GetAppVersion()} · personal Teams and Outlook notification relay";

        AddLog("NotiRelay application opened.");

        if (!string.IsNullOrWhiteSpace(_settings.LoadWarning))
            AddLog("SETTINGS WARNING: " + _settings.LoadWarning);

        if (!string.IsNullOrWhiteSpace(_deliveryQueue.RecoveryWarning))
        {
            AddLog(
                "QUEUE WARNING: " +
                _deliveryQueue.RecoveryWarning);
        }

        if (!string.IsNullOrWhiteSpace(_teamsInboxWatcher.RecoveryWarning))
        {
            AddLog(
                "TEAMS INBOX WARNING: " +
                _teamsInboxWatcher.RecoveryWarning);
        }

        // Previously queued notifications wait for the launch question: nothing is sent
        // until the user answers it (or there is nothing old to ask about).
        _sendingGate.Hold(SendingHold.LaunchQuestion);
        _deliveryWorker.Start();

        // Automatically start configured integrations.
        Loaded += async (_, _) =>
        {
            // Launched by the "start with Windows" entry: go straight to the tray.
            if (Array.IndexOf(Environment.GetCommandLineArgs(), StartupManager.AutostartArgument) >= 0)
                WindowState = WindowState.Minimized;

            // Old queue items are listed before monitoring can add new ones, so
            // "start from now" removes only what was waiting from before.
            PendingSnapshot backlog = await SnapshotQueueAsync();

            await AutoStartAsync();
            await AutoStartTeamsInboxAsync();

            if (_settings.CalendarEnabled)
                StartCalendar(automatic: true);

            if (_settings.BatteryAlertsEnabled)
                StartBatteryAlerts(automatic: true);

            await AskAboutBacklogAtLaunchAsync(backlog);
        };
    }

    // ==========================================
    // MESSAGES MISSED WHILE THE APP WAS CLOSED
    // ==========================================

    private void TryCaptureMissedOutlook(UserNotification item)
    {
        try
        {
            if (!MissedMessages.IsMissedOutlook(item.CreationTime, _previousActiveUtc))
                return;

            if (!IsSupportedApp(item.AppInfo?.DisplayInfo?.DisplayName ?? string.Empty))
                return;

            string text = ExtractNotificationText(item);

            if (string.IsNullOrWhiteSpace(text))
                return;

            _missedOutlook.Add(new MissedOutlookToast(
                text,
                item.CreationTime,
                $"{item.Id}:{item.CreationTime.UtcDateTime.Ticks}"));
        }
        catch (Exception ex)
        {
            AddLog("A notification could not be checked for missed messages: " + ex.Message, ex);
        }
    }

    private async Task<PendingSnapshot> SnapshotQueueAsync()
    {
        try
        {
            return await _deliveryQueue.GetPendingSnapshotAsync();
        }
        catch (Exception ex)
        {
            AddLog("Could not read the delivery queue: " + ex.Message, ex);
            return PendingSnapshot.Empty;
        }
    }

    // Asked at launch when anything old would be sent: unsent queue items from before,
    // Teams messages and Outlook toasts missed while closed. Sending stays held until then.
    private async Task AskAboutBacklogAtLaunchAsync(PendingSnapshot backlog)
    {
        try
        {
            int queuedCount = backlog.Ids.Count;
            int teamsCount = _launchMissedTeams.Count;
            int outlookCount = _missedOutlook.Count;

            if (queuedCount + teamsCount + outlookCount == 0 || _isShuttingDown)
                return;

            DateTimeOffset? queuedOldest = backlog.OldestCreatedUtc is { } created
                ? new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc))
                : null;

            DateTimeOffset? oldest = _launchMissedTeams
                .Select(missed => (DateTimeOffset?)missed.SortTimeUtc)
                .Concat(_missedOutlook.Select(toast => (DateTimeOffset?)toast.CreatedAt))
                .Append(queuedOldest)
                .Min();

            string details = MissedMessages.Describe(
                queuedCount,
                teamsCount,
                outlookCount,
                oldest?.LocalDateTime,
                DateTime.Now);

            AddLog("Not sent yet: " + details + ". Nothing is sent until you answer.");

            bool send = MissedMessagesDialog.Ask(MissedMessages.LaunchPrompt(details));

            // Closed by Exit or by Windows shutting down: not an answer, keep everything.
            if (_isShuttingDown || _sessionEnding)
                return;

            if (teamsCount > 0)
                _teamsInboxWatcher.ResolveHeld(send);

            _launchMissedTeams = Array.Empty<MissedTeamsMessage>();

            await ResolveMissedOutlookAsync(send);

            if (!send && queuedCount > 0 &&
                !await DiscardQueuedAsync(backlog.Ids, "from before"))
            {
                // Better held than sent against the user's answer.
                _sendingGate.Hold(SendingHold.StoppedByUser);
                AddLog("Sending stays stopped so they are not sent. Press Resume sending to try again.");
            }

            AddLog(send
                ? "You chose to send them; they go out oldest first."
                : "You chose to start from now; only new messages are relayed.");
        }
        finally
        {
            if (!_isShuttingDown && !_sessionEnding)
            {
                _sendingGate.Release(SendingHold.LaunchQuestion);
                UpdateSendingControls();
            }
        }
    }

    private async Task<bool> DiscardQueuedAsync(IReadOnlyList<string> ids, string what)
    {
        try
        {
            int removed = await _deliveryQueue.RemoveAsync(ids);
            AddLog($"Discarded {NotificationsCount(removed)} {what}.");
            return true;
        }
        catch (Exception ex)
        {
            AddLog("Could not discard the waiting notifications: " + ex.Message, ex);
            return false;
        }
    }

    private static string NotificationsCount(int count) =>
        count == 1 ? "1 notification" : $"{count} notifications";

    // ==========================================
    // TOPIC PER NOTIFICATION TYPE
    // ==========================================

    private void InitCategoryTopicRows()
    {
        _categoryTopicRows = new()
        {
            [NotificationCategory.Teams] = (TopicTeamsBox, TopicTeamsPlaceholder, TopicTeamsState, TopicTeamsClear),
            [NotificationCategory.Outlook] = (TopicOutlookBox, TopicOutlookPlaceholder, TopicOutlookState, TopicOutlookClear),
            [NotificationCategory.Calendar] = (TopicCalendarBox, TopicCalendarPlaceholder, TopicCalendarState, TopicCalendarClear),
            [NotificationCategory.Battery] = (TopicBatteryBox, TopicBatteryPlaceholder, TopicBatteryState, TopicBatteryClear)
        };
    }

    private static NotificationCategory CategoryOf(object sender) =>
        Enum.Parse<NotificationCategory>((string)((FrameworkElement)sender).Tag);

    private void CategoryTopic_Changed(object sender, TextChangedEventArgs e)
    {
        if (_categoryTopicRows is null)
            return;

        var row = _categoryTopicRows[CategoryOf(sender)];
        row.Placeholder.Visibility = row.Box.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SaveCategoryTopics_Click(object sender, RoutedEventArgs e)
    {
        if (_categoryTopicRows is null)
            return;

        var saved = new List<string>();

        foreach (var (category, row) in _categoryTopicRows)
        {
            string topic = NtfyTopic.Normalize(row.Box.Text);

            if (topic.Length == 0)
                continue;

            string name = NotificationTopics.DisplayName(category);
            string? error = NtfyTopic.Validate(topic);

            if (error is not null)
            {
                RefreshCategoryTopicStates();
                SetStatus(CategoryTopicsStatus, $"{name}: {error}", StatusKind.Error);
                row.Box.Focus();
                return;
            }

            try
            {
                _settings.SaveCategoryTopic(category, topic);
                row.Box.Clear();
                saved.Add(name);
            }
            catch (Exception ex)
            {
                RefreshCategoryTopicStates();
                SetStatus(CategoryTopicsStatus, $"{name}: could not save the topic: {ex.Message}", StatusKind.Error);
                AddLog($"ntfy topic for {name} not saved: " + ex.Message, ex);
                return;
            }
        }

        RefreshCategoryTopicStates();

        if (saved.Count == 0)
        {
            SetStatus(CategoryTopicsStatus,
                "Type a topic in a row first. Empty rows use the main topic.",
                StatusKind.Warning);
            return;
        }

        SetStatus(CategoryTopicsStatus,
            "Saved for " + string.Join(", ", saved) + ". New notifications of these types go to their own topic.",
            StatusKind.Success);

        AddLog("ntfy topic saved for: " + string.Join(", ", saved) + ".");
    }

    private void CategoryTopicClear_Click(object sender, RoutedEventArgs e)
    {
        NotificationCategory category = CategoryOf(sender);
        string name = NotificationTopics.DisplayName(category);

        try
        {
            _settings.SaveCategoryTopic(category, null);
            RefreshCategoryTopicStates();
            SetStatus(CategoryTopicsStatus, $"{name} go to the main topic again.", StatusKind.Neutral);
            AddLog($"{name} now use the main ntfy topic.");
        }
        catch (Exception ex)
        {
            SetStatus(CategoryTopicsStatus, $"{name}: could not change the topic: {ex.Message}", StatusKind.Error);
            AddLog($"ntfy topic for {name} not changed: " + ex.Message, ex);
        }
    }

    private void RefreshCategoryTopicStates()
    {
        if (_categoryTopicRows is not null)
        {
            foreach (var (category, row) in _categoryTopicRows)
            {
                bool own = _settings.TryGetCategoryTopic(category, out string topic);
                bool unreadable = !own && _settings.HasStoredCategoryTopic(category);

                row.State.Text = own
                    ? "✓ " + NtfyTopic.Mask(topic)
                    : unreadable ? "⚠ can't be read; using main" : "main topic";

                row.Clear.Visibility = own || unreadable ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        RefreshDebugTopics();
    }

    // Every configured topic is masked in the debug log.
    private void RefreshDebugTopics()
    {
        var topics = new List<string>();

        if (!string.IsNullOrEmpty(_debugTopic))
            topics.Add(_debugTopic);

        topics.AddRange(_settings.GetCategoryTopics().Values);

        _debugTopics = topics.Distinct(StringComparer.Ordinal).ToArray();
    }

    // ==========================================
    // POWER & BATTERY
    // ==========================================

    private void BatteryAlerts_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressBatteryToggle)
            return;

        if (BatteryAlertsCheckBox.IsChecked == true)
            StartBatteryAlerts(automatic: false);
        else
            _ = StopBatteryAlertsAsync();
    }

    private void StartBatteryAlerts(bool automatic)
    {
        if (_isBatteryRunning || _isShuttingDown)
            return;

        try
        {
            SaveEnteredTopicIfAny();
            RequireTopic();

            _batteryWatcher.Start();
            _isBatteryRunning = true;
            SetBatteryCheckBox(true);
            SaveBatterySetting(true);

            SetStatus(BatteryStatusText, "Checking the battery…", StatusKind.Neutral);

            AddLog(automatic
                ? "Low-battery alerts started automatically."
                : "Low-battery alerts started.");
        }
        catch (Exception ex)
        {
            SetBatteryCheckBox(false);
            SetStatus(BatteryStatusText, "Could not start low-battery alerts: " + ex.Message, StatusKind.Error);
            AddLog("Low-battery alerts start failed: " + ex.Message, ex);
        }
    }

    private async Task StopBatteryAlertsAsync()
    {
        try
        {
            await _batteryWatcher.StopAsync();
        }
        catch (Exception ex)
        {
            AddLog("Low-battery alerts stop error: " + ex.Message, ex);
        }

        _isBatteryRunning = false;
        SaveBatterySetting(false);

        SetStatus(BatteryStatusText, "Low-battery alerts are off.", StatusKind.Neutral);
        AddLog("Low-battery alerts stopped by user.");
    }

    private void SetBatteryCheckBox(bool value)
    {
        _suppressBatteryToggle = true;
        BatteryAlertsCheckBox.IsChecked = value;
        _suppressBatteryToggle = false;
    }

    private void SaveBatterySetting(bool enabled)
    {
        if (_settings.BatteryAlertsEnabled == enabled)
            return;

        _settings.BatteryAlertsEnabled = enabled;

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            AddLog("Could not remember the battery alerts setting: " + ex.Message, ex);
        }
    }

    // Called by the battery watcher from a background thread.
    private void ShowBatteryStatus(string text)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isBatteryRunning)
                SetStatus(BatteryStatusText, text, StatusKind.Neutral);
        }));
    }

    // ==========================================
    // OUTLOOK CALENDAR
    // ==========================================

    private void StartCalendar_Click(object sender, RoutedEventArgs e) =>
        StartCalendar(automatic: false);

    private void StartCalendar(bool automatic)
    {
        if (_isCalendarRunning || _isShuttingDown)
            return;

        try
        {
            SaveEnteredTopicIfAny();
            RequireTopic();

            _calendarWatcher.Start();
            _isCalendarRunning = true;
            SetCalendarRunningState(true);
            SaveCalendarSetting(true);

            SetStatus(
                CalendarStatus,
                "Sending your meetings: changes right away, today's agenda at 08:45, " +
                "and a reminder 10 minutes before each one.",
                StatusKind.Success);

            AddLog(automatic
                ? "Outlook calendar started automatically."
                : "Outlook calendar started.");
        }
        catch (Exception ex)
        {
            SetStatus(CalendarStatus, "Could not start the calendar: " + ex.Message, StatusKind.Error);
            AddLog("Outlook calendar start failed: " + ex.Message, ex);
        }
    }

    private async void StopCalendar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _calendarWatcher.StopAsync();

            _isCalendarRunning = false;
            SetCalendarRunningState(false);
            SaveCalendarSetting(false);

            SetStatus(CalendarStatus, "Outlook calendar is stopped.", StatusKind.Neutral);
            AddLog("Outlook calendar stopped by user.");

            await HintWaitingAfterSourceStopAsync();
        }
        catch (Exception ex)
        {
            SetStatus(CalendarStatus, "Could not stop the calendar: " + ex.Message, StatusKind.Error);
            AddLog("Outlook calendar stop failed: " + ex.Message, ex);
        }
    }

    private void SetCalendarRunningState(bool running)
    {
        StartCalendarButton.IsEnabled = !running;
        StopCalendarButton.IsEnabled = running;
        UpdateHeaderState();
    }

    private void SaveCalendarSetting(bool enabled)
    {
        if (_settings.CalendarEnabled == enabled)
            return;

        _settings.CalendarEnabled = enabled;

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            AddLog("Could not remember the calendar setting: " + ex.Message, ex);
        }
    }

    // ==========================================
    // STOP / RESUME SENDING
    // ==========================================

    private async void SendingButton_Click(object sender, RoutedEventArgs e) =>
        await ToggleSendingAsync();

    private async Task ToggleSendingAsync()
    {
        if (_isShuttingDown || !_sendingToggleDebounce.TryAccept(DateTime.UtcNow))
            return;

        if (_sendingGate.IsHeld(SendingHold.StoppedByUser))
            await ResumeSendingAsync();
        else
            await StopSendingAsync();
    }

    private async Task StopSendingAsync()
    {
        _sendingGate.Hold(SendingHold.StoppedByUser);
        UpdateSendingControls();

        int pending = 0;
        try
        {
            pending = await _deliveryQueue.GetPendingCountAsync();
        }
        catch (Exception ex)
        {
            DebugTrace.Detail("Could not count waiting notifications", ex);
        }

        AddLog($"Sending stopped. {NotificationsCount(pending)} waiting; " +
               "nothing goes to ntfy until you press Resume sending.");
    }

    private async Task ResumeSendingAsync()
    {
        if (_resumeInProgress)
            return;

        _resumeInProgress = true;

        try
        {
            PendingSnapshot waiting = await _deliveryQueue.GetPendingSnapshotAsync();

            if (waiting.Ids.Count > 0)
            {
                DateTime? oldestLocal = waiting.OldestCreatedUtc is { } created
                    ? DateTime.SpecifyKind(created, DateTimeKind.Utc).ToLocalTime()
                    : null;

                bool send = MissedMessagesDialog.Ask(MissedMessages.ResumePrompt(
                    MissedMessages.DescribeWaiting(waiting.Ids.Count, oldestLocal, DateTime.Now)));

                // Closed by Exit or by Windows shutting down: not an answer, stay stopped.
                if (_isShuttingDown || _sessionEnding)
                    return;

                if (!send &&
                    !await DiscardQueuedAsync(waiting.Ids, "collected while sending was stopped"))
                {
                    AddLog("Sending stays stopped so they are not sent. Press Resume sending to try again.");
                    return;
                }
            }

            _sendingGate.Release(SendingHold.StoppedByUser);
            UpdateSendingControls();
            AddLog("Sending resumed.");
        }
        catch (Exception ex)
        {
            AddLog("Could not resume sending: " + ex.Message, ex);
        }
        finally
        {
            _resumeInProgress = false;
        }
    }

    private void UpdateSendingControls()
    {
        bool stopped = _sendingGate.IsHeld(SendingHold.StoppedByUser);

        SendingButton.Content = stopped ? "Resume sending" : "Stop sending";
        SendingButton.Style = (Style)FindResource(stopped ? "Primary" : "DangerButton");
        SendingButton.ToolTip = stopped
            ? "Send to ntfy again. If notifications are waiting, you choose: send or discard them."
            : "Stop sending anything to ntfy right now, including notifications already waiting.";

        if (_traySendingItem is not null)
            _traySendingItem.Text = stopped ? "Resume sending" : "Stop sending";

        UpdateHeaderState();
    }

    // Stopping Teams or Outlook only stops capturing; say so when the queue is still sending.
    private async Task HintWaitingAfterSourceStopAsync()
    {
        if (_sendingGate.IsHeld(SendingHold.StoppedByUser))
            return;

        try
        {
            int pending = await _deliveryQueue.GetPendingCountAsync();

            if (pending > 0)
            {
                AddLog($"{NotificationsCount(pending)} still waiting to be sent to ntfy. " +
                       "Press Stop sending to hold them.");
            }
        }
        catch (Exception ex)
        {
            DebugTrace.Detail("Could not count waiting notifications", ex);
        }
    }

    private async Task ResolveMissedOutlookAsync(bool send)
    {
        if (_missedOutlook.Count == 0)
            return;

        var toasts = _missedOutlook.OrderBy(toast => toast.CreatedAt).ToList();
        _missedOutlook.Clear();

        if (!send)
        {
            AddLog($"Missed Outlook notifications skipped: {toasts.Count}.");
            return;
        }

        try
        {
            RequireTopic();
            string topic = _settings.GetTopicFor(NotificationCategory.Outlook);
            DateTime nowLocal = DateTime.Now;

            foreach (MissedOutlookToast toast in toasts)
            {
                await _deliveryQueue.EnqueueAsync(
                    toast.QueueId,
                    MissedMessages.Body(
                        BuildNtfyPayload(toast.Text, toast.CreatedAt),
                        toast.CreatedAt.LocalDateTime,
                        nowLocal),
                    MissedMessages.Title("OUTLOOK"),
                    "urgent",
                    topic);
            }

            AddLog($"Missed Outlook notifications queued: {toasts.Count}.");
        }
        catch (Exception ex)
        {
            AddLog("Could not queue the missed Outlook notifications: " + ex.Message, ex);
        }
    }

    private void SaveHeartbeat()
    {
        try
        {
            _settings.LastActiveUtc = DateTimeOffset.UtcNow;
            _settings.Save();
        }
        catch (Exception ex)
        {
            if (_heartbeatErrorLogged)
                return;

            _heartbeatErrorLogged = true;
            AddLog("Could not record when NotiRelay last ran: " + ex.Message, ex);
        }
    }

    // ==========================================
    // SYSTEM TRAY
    // ==========================================

    private static System.Drawing.Icon CreateApplicationTrayImage()
    {
        try
        {
            string? executablePath = Environment.ProcessPath;

            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var extracted =
                    System.Drawing.Icon.ExtractAssociatedIcon(executablePath);

                if (extracted is not null)
                    return extracted;
            }
        }
        catch
        {
            // Use fallback icon if extraction fails.
        }

        return (System.Drawing.Icon)
            System.Drawing.SystemIcons.Application.Clone();
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon(
        System.Drawing.Icon applicationIcon)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        menu.Items.Add(
            "Open NotiRelay",
            null,
            (_, _) => RestoreFromTray());

        _traySendingItem = new System.Windows.Forms.ToolStripMenuItem(
            "Stop sending",
            null,
            (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = ToggleSendingAsync())));

        menu.Items.Add(_traySendingItem);

        menu.Items.Add(
            "Exit",
            null,
            (_, _) => ExitApplication());

        var icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = applicationIcon,
            Text = "NotiRelay",
            ContextMenuStrip = menu,
            Visible = true
        };

        icon.DoubleClick += (_, _) => RestoreFromTray();

        return icon;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    private void RestoreFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RestoreFromTray));
            return;
        }

        if (_isShuttingDown || _allowExit)
            return;

        WindowState = WindowState.Normal;

        if (!IsVisible)
            Show();

        Activate();
    }

    private async void ExitApplication()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ExitApplication));
            return;
        }

        if (_isShuttingDown || _allowExit)
            return;

        _isShuttingDown = true;

        _heartbeatTimer.Stop();
        SaveHeartbeat();

        AddLog("NotiRelay graceful shutdown started.");

        // Stop accepting new Windows toast notifications.
        _pollTimer.Stop();
        _isRunning = false;
        SetRunningState(false);

        // Disable UI actions during shutdown.
        StartTeamsInboxButton.IsEnabled = false;
        StopTeamsInboxButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;

        try
        {
            await _teamsInboxWatcher.StopAsync();

            _isTeamsInboxRunning = false;

            AddLog("Teams inbox watcher stopped.");
        }
        catch (Exception ex)
        {
            AddLog(
                "Teams watcher shutdown error: " +
                ex.GetType().Name + ": " + ex.Message, ex);
        }

        try
        {
            await _batteryWatcher.StopAsync();
            _isBatteryRunning = false;
        }
        catch (Exception ex)
        {
            AddLog("Battery alerts shutdown error: " + ex.Message, ex);
        }

        try
        {
            await _calendarWatcher.StopAsync();
            _isCalendarRunning = false;
        }
        catch (Exception ex)
        {
            AddLog("Calendar shutdown error: " + ex.Message, ex);
        }

        try
        {
            await _deliveryWorker.StopAsync();

            AddLog("Delivery worker stopped.");
        }
        catch (Exception ex)
        {
            AddLog(
                "Delivery worker shutdown error: " +
                ex.GetType().Name + ": " + ex.Message, ex);
        }

        // All background services have now been awaited.
        _allowExit = true;
        _trayIcon.Visible = false;

        DebugTrace.Line("=== NotiRelay exiting ===");

        Close();
    }

    protected override void OnClosing(
        System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            Hide();
            ShowTrayHintOnce();
            return;
        }

        base.OnClosing(e);
    }

    // ==========================================
    // NTFY SETTINGS
    // ==========================================

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveTopicFromInput();
    }

    private void TopicInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        SaveTopicFromInput();
    }

    private void TopicInput_Changed(object sender, RoutedEventArgs e)
    {
        TopicPlaceholder.Visibility = GetEnteredTopic().Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RevealTopic_Click(object sender, RoutedEventArgs e)
    {
        _topicRevealed = !_topicRevealed;

        if (_topicRevealed)
        {
            TopicPlainBox.Text = TopicBox.Password;
            TopicBox.Clear();
            TopicBox.Visibility = Visibility.Collapsed;
            TopicPlainBox.Visibility = Visibility.Visible;
            TopicPlainBox.CaretIndex = TopicPlainBox.Text.Length;
            TopicPlainBox.Focus();
        }
        else
        {
            TopicBox.Password = TopicPlainBox.Text;
            TopicPlainBox.Clear();
            TopicPlainBox.Visibility = Visibility.Collapsed;
            TopicBox.Visibility = Visibility.Visible;
            TopicBox.Focus();
        }

        RevealTopicButton.Content = _topicRevealed ? "Hide" : "Show";
        TopicInput_Changed(sender, e);
    }

    private string GetEnteredTopic() =>
        _topicRevealed ? TopicPlainBox.Text : TopicBox.Password;

    private void ClearEnteredTopic()
    {
        TopicBox.Clear();
        TopicPlainBox.Clear();
        TopicPlaceholder.Visibility = Visibility.Visible;
    }

    private void FocusTopicInput()
    {
        if (_topicRevealed)
            TopicPlainBox.Focus();
        else
            TopicBox.Focus();
    }

    private void SaveTopicFromInput()
    {
        string entered = GetEnteredTopic();

        if (string.IsNullOrWhiteSpace(entered))
        {
            if (_settings.TryGetTopic(out _))
            {
                SetStatus(NtfyStatus,
                    "A topic is already saved. To change it, type the new one and press Save topic.",
                    StatusKind.Neutral);
            }
            else
            {
                SetStatus(NtfyStatus, "Enter your ntfy topic first.", StatusKind.Error);
            }

            FocusTopicInput();
            return;
        }

        try
        {
            bool fromLink = entered.Contains('/');
            string topic = SaveEnteredTopicIfAny()!;

            if (NtfyTopic.IsWeak(topic))
            {
                SetStatus(NtfyStatus,
                    $"Topic saved, but it is only {topic.Length} characters and easy to guess. " +
                    $"Anyone who knows it can read your notifications; use at least {NtfyTopic.WeakBelowLength}.",
                    StatusKind.Warning);
            }
            else
            {
                SetStatus(NtfyStatus,
                    (fromLink ? "Topic taken from the ntfy.sh link, encrypted and saved. " : "Topic encrypted and saved. ") +
                    "Press Send test to check that your phone receives it.",
                    StatusKind.Success);
            }

            AddLog("ntfy topic saved.");
        }
        catch (Exception ex)
        {
            // Validation and write errors leave the previously saved topic untouched.
            SetStatus(NtfyStatus,
                ex is InvalidOperationException
                    ? ex.Message
                    : "Could not save the topic: " + ex.Message,
                StatusKind.Error);

            AddLog("ntfy topic not saved: " + ex.Message, ex);
            FocusTopicInput();
        }
    }

    private async void TestNtfy_Click(object sender, RoutedEventArgs e)
    {
        if (_isTestingNtfy)
            return;

        _isTestingNtfy = true;
        TestNtfyButton.IsEnabled = false;
        TestNtfyButton.Content = "Sending…";
        SetStatus(NtfyStatus, "Sending a test notification…", StatusKind.Neutral);

        try
        {
            SaveEnteredTopicIfAny();

            string mainTopic = RequireTopic();

            // One test per distinct topic, titled with the notification types that use it.
            IReadOnlyList<TopicTarget> targets =
                NotificationTopics.DistinctTargets(mainTopic, _settings.GetCategoryTopics());

            foreach (TopicTarget target in targets)
            {
                await new NtfyClient(target.Topic, _httpClient).SendMessageAsync(
                    "Test reușit! NotiRelay comunică cu ntfy.",
                    NotificationTopics.TestTitle(target),
                    "high");
            }

            SetStatus(NtfyStatus,
                targets.Count == 1
                    ? "Test sent. It should appear on your phone within a few seconds."
                    : $"Test sent to {targets.Count} topics; the title of each says which notification types use it.",
                StatusKind.Success);

            AddLog(targets.Count == 1
                ? "Synthetic ntfy test notification sent."
                : $"Synthetic ntfy test sent to {targets.Count} topics.");
        }
        catch (Exception ex)
        {
            SetStatus(NtfyStatus, "Test failed: " + NtfyErrors.Describe(ex) + ".", StatusKind.Error);

            AddLog("ntfy test failed: " + ex.Message, ex);
        }
        finally
        {
            _isTestingNtfy = false;
            TestNtfyButton.IsEnabled = !_isShuttingDown;
            TestNtfyButton.Content = "Send test";
        }
    }

    // ==========================================
    // MICROSOFT TEAMS - ONEDRIVE INBOX
    // ==========================================

    private void BrowseTeamsInbox_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            using var dialog =
                new System.Windows.Forms.FolderBrowserDialog
                {
                    Description =
                        "Select the locally synchronized NRL-Inbox folder.",
                    ShowNewFolderButton = false
                };

            if (Directory.Exists(_settings.TeamsInboxPath))
            {
                dialog.SelectedPath = _settings.TeamsInboxPath;
            }

            if (dialog.ShowDialog() !=
                System.Windows.Forms.DialogResult.OK)
            {
                return;
            }

            string selectedPath = dialog.SelectedPath;

            if (!Directory.Exists(selectedPath))
            {
                SetStatus(TeamsInboxStatus,
                    "The selected folder does not exist.", StatusKind.Error);

                return;
            }

            string previousPath = _settings.TeamsInboxPath;
            _settings.TeamsInboxPath = selectedPath;

            try
            {
                _settings.Save();
            }
            catch
            {
                _settings.TeamsInboxPath = previousPath;
                throw;
            }

            TeamsInboxPathBox.Text = selectedPath;

            SetStatus(TeamsInboxStatus,
                _isTeamsInboxRunning
                    ? "Folder saved. Stop and start Teams monitoring to switch to it."
                    : "Teams inbox folder saved.",
                _isTeamsInboxRunning ? StatusKind.Warning : StatusKind.Success);

            AddLog("Teams inbox folder configured: " + selectedPath);
        }
        catch (Exception ex)
        {
            SetStatus(TeamsInboxStatus,
                "Could not configure Teams inbox: " + ex.Message, StatusKind.Error);

            AddLog(
                "Teams inbox configuration error: " +
                ex.Message, ex);
        }
    }

    private void StartTeamsInbox_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isShuttingDown)
            return;

        try
        {
            StartTeamsInboxMonitoring(automatic: false);
        }
        catch (Exception ex)
        {
            SetStatus(TeamsInboxStatus,
                "Could not start Teams monitoring: " + ex.Message, StatusKind.Error);

            AddLog(
                "Teams monitoring start failed: " +
                ex.Message, ex);
        }
    }

    private async void StopTeamsInbox_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            await _teamsInboxWatcher.StopAsync();

            _isTeamsInboxRunning = false;

            SetTeamsInboxRunningState(false);

            SetStatus(TeamsInboxStatus,
                "Teams inbox monitoring stopped.", StatusKind.Neutral);

            AddLog("Teams inbox monitoring stopped by user.");

            await HintWaitingAfterSourceStopAsync();
        }
        catch (Exception ex)
        {
            SetStatus(TeamsInboxStatus,
                "Could not stop Teams monitoring: " + ex.Message, StatusKind.Error);

            AddLog(
                "Teams monitoring stop failed: " +
                ex.Message, ex);
        }
    }

    private void StartTeamsInboxMonitoring(
        bool automatic,
        IReadOnlyList<MissedTeamsMessage>? held = null)
    {
        if (_isTeamsInboxRunning)
            return;

        if (_isShuttingDown)
            return;

        SaveEnteredTopicIfAny();

        RequireTopic();

        string inboxPath = _settings.TeamsInboxPath;

        if (string.IsNullOrWhiteSpace(inboxPath))
        {
            throw new InvalidOperationException(
                "Select your locally synchronized NRL-Inbox folder first.");
        }

        if (!Directory.Exists(inboxPath))
        {
            throw new DirectoryNotFoundException(
                "The configured Teams inbox folder does not exist: " +
                inboxPath);
        }

        _teamsInboxWatcher.Start(inboxPath, held);

        _isTeamsInboxRunning = true;

        SetTeamsInboxRunningState(true);

        SetStatus(TeamsInboxStatus,
            "Teams inbox monitoring active. New messages are being forwarded.",
            StatusKind.Success);

        AddLog(automatic
            ? "Teams inbox monitoring started automatically."
            : "Teams inbox monitoring started by user.");

        if (!string.IsNullOrWhiteSpace(_teamsInboxWatcher.RecoveryWarning))
        {
            AddLog(
                "TEAMS INBOX WARNING: " +
                _teamsInboxWatcher.RecoveryWarning);
        }
    }

    private async Task AutoStartTeamsInboxAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.TeamsInboxPath))
        {
            AddLog(
                "Teams auto-start skipped: inbox folder is not configured.");

            return;
        }

        if (!_settings.TryGetTopic(out _))
        {
            AddLog(
                "Teams auto-start skipped: ntfy topic is not configured.");

            return;
        }

        if (!Directory.Exists(_settings.TeamsInboxPath))
        {
            SetStatus(TeamsInboxStatus,
                "Configured Teams inbox folder is unavailable. " +
                "Check OneDrive synchronization.",
                StatusKind.Warning);

            AddLog(
                "Teams auto-start skipped: configured inbox folder is unavailable.");

            return;
        }

        // Messages that arrived while the app was closed are held until the user answers.
        IReadOnlyList<MissedTeamsMessage> missed = Array.Empty<MissedTeamsMessage>();
        string inboxPath = _settings.TeamsInboxPath;

        try
        {
            missed = await Task.Run(() => _teamsInboxWatcher.FindUnsent(inboxPath));
        }
        catch (Exception ex)
        {
            AddLog("Could not look for missed Teams messages: " + ex.Message, ex);
        }

        try
        {
            StartTeamsInboxMonitoring(automatic: true, held: missed);
            _launchMissedTeams = missed;
        }
        catch (Exception ex)
        {
            SetStatus(TeamsInboxStatus,
                "Teams auto-start failed: " + ex.Message, StatusKind.Error);

            AddLog(
                "Teams auto-start failed: " +
                ex.Message, ex);
        }
    }

    private void SetTeamsInboxRunningState(bool running)
    {
        StartTeamsInboxButton.IsEnabled = !running;
        StopTeamsInboxButton.IsEnabled = running;
        UpdateHeaderState();
    }

    // ==========================================
    // LEGACY WINDOWS NOTIFICATION MONITORING
    // ==========================================

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_isShuttingDown)
            return;

        if (AuthorizationCheckBox.IsChecked != true)
        {
            SetStatus(ListenerStatus,
                "Tick the confirmation above first, then press Start Outlook monitoring.",
                StatusKind.Warning);

            AuthorizationCheckBox.Focus();
            return;
        }

        await StartForwardingAsync(automatic: false);
    }

    private void OutlookConsent_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressConsentToggle)
            return;

        bool confirmed = AuthorizationCheckBox.IsChecked == true;
        bool previous = _settings.OutlookForwardingConfirmed;

        _settings.OutlookForwardingConfirmed = confirmed;

        try
        {
            _settings.Save();

            AddLog(confirmed
                ? "Outlook forwarding confirmed."
                : "Outlook forwarding confirmation removed.");
        }
        catch (Exception ex)
        {
            _settings.OutlookForwardingConfirmed = previous;

            AddLog("Could not save the Outlook confirmation: " + ex.Message, ex);
            SetStatus(ListenerStatus,
                "Could not save the confirmation: " + ex.Message, StatusKind.Error);
        }

        // Withdrawing consent stops forwarding immediately.
        if (!confirmed && _isRunning)
            StopForwarding("Outlook monitoring stopped: confirmation removed.");
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopForwarding(
            "Windows notification monitoring stopped by user.");

        _ = HintWaitingAfterSourceStopAsync();
    }

    private async Task AutoStartAsync()
    {
        if (AuthorizationCheckBox.IsChecked != true)
        {
            AddLog(
                "Outlook auto-start skipped: confirmation not checked.");

            return;
        }

        if (!_settings.TryGetTopic(out _))
        {
            AddLog(
                "Outlook auto-start skipped: ntfy topic not configured.");

            return;
        }

        await StartForwardingAsync(automatic: true, captureMissed: true);
    }

    private async Task<bool> StartForwardingAsync(bool automatic, bool captureMissed = false)
    {
        if (_isRunning)
            return true;

        if (_isShuttingDown)
            return false;

        try
        {
            SaveEnteredTopicIfAny();

            _ntfy = GetNtfyClient();

            _listener ??= UserNotificationListener.Current;

            var access = _listener.GetAccessStatus();

            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                SetStatus(ListenerStatus,
                    "Requesting Windows notification access…", StatusKind.Neutral);

                access = await _listener.RequestAccessAsync();
            }

            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                SetStatus(ListenerStatus,
                    "Windows notification permission was not granted. " +
                    "Check Windows notification privacy settings and app permissions.",
                    StatusKind.Error);

                AddLog(
                    "Windows notification access not granted: " + access);

                return false;
            }

            // Existing notifications are not forwarded by the poll. At launch, the Outlook
            // ones that appeared after NotiRelay last ran are kept aside for the
            // "send what you missed?" question.
            var current = await _listener.GetNotificationsAsync(
                NotificationKinds.Toast);

            _seenNotificationIds.Clear();
            _missedOutlook.Clear();

            foreach (var item in current)
            {
                _seenNotificationIds.Add(item.Id);

                if (captureMissed)
                    TryCaptureMissedOutlook(item);
            }

            _isRunning = true;

            _pollTimer.Start();

            SetRunningState(true);

            SetStatus(ListenerStatus,
                _missedOutlook.Count > 0
                    ? $"Outlook monitoring active. Missed while closed: {_missedOutlook.Count}."
                    : $"Outlook monitoring active. Existing notifications ignored: {current.Count}.",
                StatusKind.Success);

            AddLog(automatic
                ? "Windows notification monitoring started automatically."
                : "Windows notification monitoring started. Existing notifications excluded.");

            return true;
        }
        catch (Exception ex)
        {
            SetStatus(ListenerStatus,
                "Could not start: " + ex.Message, StatusKind.Error);

            AddLog(
                "Windows notification start failed: " +
                ex.Message, ex);

            return false;
        }
    }

    // ==========================================
    // WINDOWS NOTIFICATION DETECTION AND QUEUING
    // ==========================================

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isRunning ||
            _isPolling ||
            _listener is null ||
            _isShuttingDown)
        {
            return;
        }

        _isPolling = true;

        try
        {
            var notifications =
                await _listener.GetNotificationsAsync(
                    NotificationKinds.Toast);

            foreach (var notification in notifications)
            {
                if (_seenNotificationIds.Contains(notification.Id))
                    continue;

                string appName =
                    notification.AppInfo?.DisplayInfo?.DisplayName
                    ?? string.Empty;

                AddLog($"Detected notification app: '{appName}'");

                if (!IsSupportedApp(appName))
                {
                    _seenNotificationIds.Add(notification.Id);
                    continue;
                }

                string messageText =
                    ExtractNotificationText(notification);

                if (string.IsNullOrWhiteSpace(messageText))
                {
                    _seenNotificationIds.Add(notification.Id);

                    AddLog(
                        "A notification had no readable text; skipped.");

                    continue;
                }

                RequireTopic();
                string topic = _settings.GetTopicFor(NotificationCategory.Outlook);

                string payload = BuildNtfyPayload(
                    messageText,
                    notification.CreationTime);

                // The listener only forwards Outlook notifications; Teams is ingested from the OneDrive inbox.
                string title = "OUTLOOK";

                string queueId =
                    $"{notification.Id}:{notification.CreationTime.UtcDateTime.Ticks}";

                bool added = await _deliveryQueue.EnqueueAsync(
                    queueId,
                    payload,
                    title,
                    "urgent",
                    topic);

                _seenNotificationIds.Add(notification.Id);

                // Forwarded toasts must never be offered again as "missed", even if
                // the app is killed before the next heartbeat.
                SaveHeartbeat();

                int pending =
                    await _deliveryQueue.GetPendingCountAsync();

                if (added)
                {
                    AddLog(
                        $"{appName} notification saved to delivery queue. " +
                        $"Pending: {pending}.");
                }
                else
                {
                    AddLog(
                        $"{appName} notification already exists in delivery queue.");
                }
            }
        }
        catch (Exception ex)
        {
            AddLog(
                "Windows notification detection/queue error: " +
                ex.Message, ex);
        }
        finally
        {
            _isPolling = false;
        }
    }

    private static bool IsSupportedApp(string appDisplayName)
    {
        // Teams messages are ingested from the OneDrive inbox (Power Automate), not from toasts.
        // The Windows listener only handles Outlook notifications.
        return appDisplayName.Contains(
            "Outlook",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractNotificationText(
        UserNotification notification)
    {
        try
        {
            var binding = notification.Notification?
                .Visual?
                .GetBinding(KnownNotificationBindings.ToastGeneric);

            if (binding is null)
                return string.Empty;

            var pieces = binding.GetTextElements()
                .Select(element => element.Text?.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            return string.Join("\n", pieces!);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildNtfyPayload(
        string content,
        DateTimeOffset createdAt)
    {
        const int maxContentLength = 2500;

        return content.Length > maxContentLength
            ? content[..maxContentLength] + "… [truncated]"
            : content;
    }

    // ==========================================
    // DELIVERY WORKER LOGGING
    // ==========================================

    private void LogFromWorker(string message)
    {
        if (Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            AddLog(message);
        }
        else
        {
            Dispatcher.BeginInvoke(
                new Action(() => AddLog(message)));
        }
    }

    // ==========================================
    // SETTINGS STORAGE
    // ==========================================

    /// <summary>
    /// Saves the topic typed in the box, if any. The value is normalised (an ntfy.sh link
    /// becomes the bare topic) and validated before anything is written, so an invalid
    /// entry throws <see cref="InvalidOperationException"/> and the saved topic stays as it was.
    /// Returns the saved topic, or null when the box is empty.
    /// </summary>
    private string? SaveEnteredTopicIfAny()
    {
        string entered = GetEnteredTopic();

        if (string.IsNullOrWhiteSpace(entered))
            return null;

        string topic = NtfyTopic.Normalize(entered);

        ValidateTopic(topic);

        _settings.SaveTopic(topic);

        // Prevent using a client cached for the previous topic.
        _ntfy = null;

        ClearEnteredTopic();
        RefreshTopicState();

        return topic;
    }

    /// <summary>Returns the saved topic or throws a message the user can act on.</summary>
    private string RequireTopic()
    {
        if (!_settings.HasStoredTopic)
        {
            throw new InvalidOperationException(
                "Enter your ntfy topic and press Save topic first.");
        }

        if (!_settings.TryGetTopic(out string topic))
        {
            throw new InvalidOperationException(
                "The saved ntfy topic can't be decrypted for this Windows user. " +
                "Enter it again and press Save topic.");
        }

        ValidateTopic(topic);

        return topic;
    }

    private NtfyClient GetNtfyClient()
    {
        if (_ntfy is not null)
            return _ntfy;

        _ntfy = new NtfyClient(RequireTopic(), _httpClient);

        return _ntfy;
    }

    private static void ValidateTopic(string topic)
    {
        string? error = NtfyTopic.Validate(topic);

        if (error is not null)
            throw new InvalidOperationException(error);
    }

    // ==========================================
    // UI
    // ==========================================

    private void StopForwarding(string reason)
    {
        _pollTimer.Stop();

        _isRunning = false;

        SetRunningState(false);

        SetStatus(ListenerStatus, reason, StatusKind.Neutral);

        AddLog(reason);
    }

    private void SetRunningState(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        UpdateHeaderState();
    }

    /// <summary>
    /// Single source of truth for the header status pill. It reflects whichever source is active
    /// (Teams inbox or the Windows listener), not just one of them.
    /// </summary>
    private void UpdateHeaderState()
    {
        var sources = new List<string>();

        if (_isTeamsInboxRunning)
            sources.Add("Teams");

        if (_isRunning)
            sources.Add("Outlook");

        if (_isCalendarRunning)
            sources.Add("Calendar");

        bool active = sources.Count > 0;

        StateBadge.Text = active
            ? "RELAYING · " + string.Join(" + ", sources).ToUpperInvariant()
            : "STOPPED";

        StatePill.ToolTip = active
            ? "Forwarding new notifications from " + string.Join(" and ", sources) + "."
            : "Nothing is being forwarded. Start Teams or Outlook monitoring below.";

        _trayIcon.Text = active
            ? $"NotiRelay — relaying ({string.Join(", ", sources)})"
            : "NotiRelay — stopped";
        StateBadge.Foreground = new System.Windows.Media.SolidColorBrush(
            active
                ? System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D)
                : System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x8B));

        StateDot.Fill = new System.Windows.Media.SolidColorBrush(
            active
                ? System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E)
                : System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8));

        StatePill.Background = new System.Windows.Media.SolidColorBrush(
            active
                ? System.Windows.Media.Color.FromRgb(0xEC, 0xFD, 0xF3)
                : System.Windows.Media.Color.FromRgb(0xF1, 0xF5, 0xF9));

        if (active)
        {
            var pulse = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1.0,
                To = 0.3,
                Duration = TimeSpan.FromSeconds(0.9),
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };
            StateDot.BeginAnimation(System.Windows.UIElement.OpacityProperty, pulse);
        }
        else
        {
            StateDot.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
            StateDot.Opacity = 1.0;
        }

        // Stop sending wins over the sources: nothing reaches the phone while it is on.
        if (_sendingGate.IsHeld(SendingHold.StoppedByUser))
        {
            StateBadge.Text = "SENDING STOPPED";
            StatePill.ToolTip = "Nothing is sent to ntfy. Press Resume sending to continue.";
            _trayIcon.Text = "NotiRelay — sending stopped";

            StateBadge.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xB9, 0x1C, 0x1C));
            StateDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
            StatePill.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFE, 0xF2, 0xF2));

            StateDot.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
            StateDot.Opacity = 1.0;
        }
    }

    private void StartWithWindows_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressStartupToggle)
            return;

        bool enable = StartWithWindowsCheckBox.IsChecked == true;

        try
        {
            if (enable)
                StartupManager.Enable();
            else
                StartupManager.Disable();

            AddLog(enable
                ? "Start with Windows enabled."
                : "Start with Windows disabled.");
        }
        catch (Exception ex)
        {
            // Registry write failed: revert the checkbox to the real state without re-running this handler.
            _suppressStartupToggle = true;
            StartWithWindowsCheckBox.IsChecked = !enable;
            _suppressStartupToggle = false;

            AddLog("Could not change the start-with-Windows setting: " + ex.Message, ex);

            System.Windows.MessageBox.Show(
                "Could not change the start-with-Windows setting:\n" + ex.Message,
                "NotiRelay",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>Shows what is really stored: nothing, a readable topic (masked), or an unreadable one.</summary>
    private void RefreshTopicState()
    {
        _debugTopic = _settings.TryGetTopic(out string savedTopic) ? savedTopic : null;
        RefreshDebugTopics();

        if (!_settings.HasStoredTopic)
        {
            TopicSavedLabel.Text = "No topic saved yet.";
            TopicSavedLabel.Foreground = Brush("SubtleBrush");
        }
        else if (_settings.TryGetTopic(out string topic))
        {
            TopicSavedLabel.Text =
                $"✓ Saved: {NtfyTopic.Mask(topic)} · encrypted for this Windows user";
            TopicSavedLabel.Foreground = Brush("SuccessBrush");
        }
        else
        {
            TopicSavedLabel.Text =
                "⚠ The saved topic can't be decrypted on this Windows account. Enter it again.";
            TopicSavedLabel.Foreground = Brush("DangerBrush");
        }
    }

    private void ShowInitialNtfyStatus()
    {
        if (!string.IsNullOrWhiteSpace(_settings.LoadWarning))
            SetStatus(NtfyStatus, _settings.LoadWarning, StatusKind.Error);
        else if (!_settings.HasStoredTopic)
            SetStatus(NtfyStatus, "Not configured yet. Enter your topic and press Save topic.", StatusKind.Neutral);
        else if (!_settings.TryGetTopic(out _))
            SetStatus(NtfyStatus, "Enter the topic again and press Save topic.", StatusKind.Error);
        else
            SetStatus(NtfyStatus, "ntfy is configured.", StatusKind.Neutral);
    }

    private void SetStatus(TextBlock target, string text, StatusKind kind)
    {
        target.Text = text;
        target.Foreground = Brush(kind switch
        {
            StatusKind.Success => "SuccessBrush",
            StatusKind.Warning => "WarningBrush",
            StatusKind.Error => "DangerBrush",
            _ => "MutedBrush"
        });
    }

    private System.Windows.Media.Brush Brush(string key) =>
        (System.Windows.Media.Brush)FindResource(key);

    private void TeamsInboxPath_Changed(object sender, TextChangedEventArgs e)
    {
        if (TeamsInboxPlaceholder is null)
            return;

        TeamsInboxPlaceholder.Visibility = string.IsNullOrEmpty(TeamsInboxPathBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Keeps the window (and its title bar) on screen on small or scaled displays.</summary>
    private void FitToWorkArea()
    {
        Rect workArea = SystemParameters.WorkArea;

        if (Height > workArea.Height)
            Height = Math.Max(MinHeight, workArea.Height);

        if (Width > workArea.Width)
            Width = Math.Max(MinWidth, workArea.Width);
    }

    private void ShowTrayHintOnce()
    {
        if (_trayHintShown || _isShuttingDown)
            return;

        _trayHintShown = true;

        _trayIcon.ShowBalloonTip(
            4000,
            "NotiRelay is still running",
            "It keeps forwarding from the tray. Double-click the tray icon to reopen it, or right-click → Exit to quit.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private static string GetAppVersion() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "1.0";

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        // The list shows newest first; copy oldest first so it reads like a log file.
        string text = string.Join(
            Environment.NewLine,
            ActivityList.Items.Cast<object>().Select(item => item.ToString()).Reverse());

        try
        {
            System.Windows.Clipboard.SetText(text);
            AddLog("Activity log copied to the clipboard.");
        }
        catch (Exception ex)
        {
            // The clipboard can be briefly locked by another application.
            AddLog("Could not copy the log: " + ex.Message, ex);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        ActivityList.Items.Clear();
        AddLog("Activity log cleared.");
    }

    private void AddLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted &&
                !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => AddLog(message)));
            }

            return;
        }

        // Error messages can contain a proxy address with its user and password.
        message = DebugReport.HideUrlCredentials(message);

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        ActivityList.Items.Insert(0, line);

        while (ActivityList.Items.Count > 150)
            ActivityList.Items.RemoveAt(
                ActivityList.Items.Count - 1);

        MirrorToDebugLog(message);
    }

    // Same line on screen; the debug log also gets the full error (cause chain, stack trace).
    private void AddLog(string message, Exception ex)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted &&
                !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => AddLog(message, ex)));
            }

            return;
        }

        AddLog(message);
        DebugTrace.Detail("Error details for the line above:", ex);
    }

    // ==========================================
    // DEBUG LOG
    // ==========================================

    private void DebugButton_Click(object sender, RoutedEventArgs e)
    {
        if (DebugTrace.IsEnabled)
            StopDebugLog();
        else
            StartDebugLog("turned on with the Debugging button");
    }

    private void ShowDebugFile_Click(object sender, RoutedEventArgs e)
    {
        string? path = DebugTrace.File?.FilePath;
        if (path is null)
            return;

        try
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            AddLog("Could not open File Explorer: " + ex.Message, ex);
        }
    }

    private void StartDebugLog(string reason)
    {
        string? exeFolder = Path.GetDirectoryName(Environment.ProcessPath);
        string fallbackFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NotiRelay");

        var folders = new List<string>();
        if (!string.IsNullOrEmpty(exeFolder))
            folders.Add(exeFolder);
        folders.Add(fallbackFolder);

        DebugLogFile? file = DebugLogFile.OpenForTopics(folders, () => _debugTopics, out string? error);

        if (file is null)
        {
            UpdateDebugButton();
            AddLog("Debugging could not start: " + error);
            return;
        }

        DebugTrace.Attach(file);
        _debugWriteWarned = false;

        DebugTrace.Line(DebugReport.SessionHeader(
            reason, GetAppVersion(), Environment.ProcessPath, file.FilePath));
        DebugTrace.Line(DebugSettingsSummary());
        DebugTrace.Line(ScreenLogSnapshot());

        SaveDebugSetting(true);
        UpdateDebugButton();

        bool usedFallback = !string.Equals(
            Path.GetDirectoryName(file.FilePath), exeFolder, StringComparison.OrdinalIgnoreCase);

        AddLog("Debugging ON. Writing everything to " + file.FilePath +
               (usedFallback ? " (the NotiRelay.exe folder is not writable)." : "."));

        _ = RunNetworkCheckAsync();
    }

    private void StopDebugLog()
    {
        string? path = DebugTrace.File?.FilePath;

        DebugTrace.Line("=== Debugging turned off ===");
        DebugTrace.Detach();

        SaveDebugSetting(false);
        UpdateDebugButton();

        AddLog("Debugging OFF. The debug log stays at " + path + ".");
    }

    private void SaveDebugSetting(bool enabled)
    {
        if (_settings.DebugLoggingEnabled == enabled)
            return;

        _settings.DebugLoggingEnabled = enabled;

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            AddLog("Could not remember the Debugging setting: " + ex.Message, ex);
        }
    }

    private void UpdateDebugButton()
    {
        DebugLogFile? file = DebugTrace.File;

        if (file is null)
        {
            DebugButton.Content = "Debugging";
            DebugButton.ClearValue(BackgroundProperty);
            DebugButton.ClearValue(BorderBrushProperty);
            DebugButton.ClearValue(ForegroundProperty);
            DebugButton.ToolTip =
                "Write everything that happens to NotiRelay-debug.log next to NotiRelay.exe";
            ShowDebugFileButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            DebugButton.Content = "Debugging ON";
            DebugButton.Background = Brush("WarningBrush");
            DebugButton.BorderBrush = Brush("WarningBrush");
            DebugButton.Foreground = System.Windows.Media.Brushes.White;
            DebugButton.ToolTip = "Writing to " + file.FilePath + ". Click to stop.";
            ShowDebugFileButton.Visibility = Visibility.Visible;
        }
    }

    private void MirrorToDebugLog(string message)
    {
        if (DebugTrace.Line(message))
        {
            _debugWriteWarned = false;
            return;
        }

        if (_debugWriteWarned)
            return;

        // Shown once, straight on screen (not through AddLog, which would try the file again).
        _debugWriteWarned = true;
        ActivityList.Items.Insert(
            0,
            $"[{DateTime.Now:HH:mm:ss}] Debug log could not be written: {DebugTrace.File?.LastError}");
    }

    private string DebugSettingsSummary()
    {
        string topic = !_settings.HasStoredTopic
            ? "not set"
            : _settings.TryGetTopic(out string saved)
                ? DebugLogFile.MaskTopic(saved) + (NtfyTopic.IsWeak(saved) ? " (short)" : string.Empty)
                : "saved but cannot be decrypted on this Windows account";

        string teamsFolder = string.IsNullOrWhiteSpace(_settings.TeamsInboxPath)
            ? "not set"
            : _settings.TeamsInboxPath +
              (Directory.Exists(_settings.TeamsInboxPath) ? string.Empty : " (folder not found)");

        string lastActive = _previousActiveUtc is { } when
            ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "never";

        string startWithWindows;
        try
        {
            startWithWindows = YesNo(StartupManager.IsEnabled());
        }
        catch (Exception ex)
        {
            startWithWindows = "unknown (" + ex.Message + ")";
        }

        return string.Join(Environment.NewLine, new[]
        {
            "Settings:",
            "  ntfy topic: " + topic,
            "  Teams inbox folder: " + teamsFolder,
            "  Outlook forwarding confirmed: " + YesNo(_settings.OutlookForwardingConfirmed),
            "  Start with Windows: " + startWithWindows,
            "  Outlook monitoring running: " + YesNo(_isRunning),
            "  Teams monitoring running: " + YesNo(_isTeamsInboxRunning),
            "  Outlook calendar running: " + YesNo(_isCalendarRunning),
            "  Low-battery alerts running: " + YesNo(_isBatteryRunning),
            "  Topics: " + string.Join(", ", NotificationTopics.All.Select(category =>
                NotificationTopics.DisplayName(category) + " = " +
                (_settings.TryGetCategoryTopic(category, out string own)
                    ? DebugLogFile.MaskTopic(own)
                    : _settings.HasStoredCategoryTopic(category) ? "unreadable, main" : "main"))),
            "  Last active before this start: " + lastActive,
            "  ntfy request timeout: " + _httpClient.Timeout.TotalSeconds + " s",
            "  Sending: " + (_sendingGate.IsHeld(SendingHold.StoppedByUser)
                ? "stopped by the user"
                : _sendingGate.IsHeld(SendingHold.LaunchQuestion)
                    ? "waiting for the launch question"
                    : "on")
        });

        static string YesNo(bool value) => value ? "yes" : "no";
    }

    private string ScreenLogSnapshot()
    {
        // The list shows newest first; the file reads oldest first.
        List<string> lines = ActivityList.Items
            .Cast<object>()
            .Select(item => "  " + item)
            .Reverse()
            .ToList();

        return lines.Count == 0
            ? "Activity log on screen: (empty)"
            : "Activity log on screen so far (oldest first):" + Environment.NewLine +
              string.Join(Environment.NewLine, lines);
    }

    private async Task RunNetworkCheckAsync()
    {
        try
        {
            int pending = await _deliveryQueue.GetPendingCountAsync();
            DebugTrace.Line($"Delivery queue: {pending} waiting.");

            // Proxy auto-detection can block for seconds, so the whole check runs off the UI thread.
            NetworkProbeResult result = await Task.Run(
                () => NetworkProbe.CreateDefault(_httpClient).RunAsync(CancellationToken.None));

            DebugTrace.Line(string.Join(Environment.NewLine, result.Lines));
            AddLog("Network check: " + result.Summary + " Details are in the debug log.");
        }
        catch (Exception ex)
        {
            AddLog("Network check could not run: " + ex.Message, ex);
        }
    }

    private void HookUnhandledExceptionsIntoDebugLog()
    {
        // Only recorded; the app's existing behaviour for these errors does not change.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                DebugTrace.Detail("Unhandled exception (NotiRelay is closing)", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
            DebugTrace.Detail("Unobserved background task exception", e.Exception);

        System.Windows.Application.Current.DispatcherUnhandledException += (_, e) =>
            DebugTrace.Detail("Unhandled exception on the UI thread", e.Exception);
    }

    // ==========================================
    // CLEAN EXIT
    // ==========================================

    protected override void OnClosed(EventArgs e)
    {
        StopForwarding(
            "NotiRelay closed. Windows notification monitoring is OFF.");

        // TeamsInboxWatcher and DeliveryWorker have already been awaited
        // by ExitApplication before the HTTP client is disposed.

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon.ContextMenuStrip?.Dispose();

        _trayImage.Dispose();

        _httpClient.Dispose();

        base.OnClosed(e);
    }
}
