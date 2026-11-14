
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

        System.Windows.Application.Current.SessionEnding += (_, _) => SaveHeartbeat();

        TeamsInboxPathBox.Text = _settings.TeamsInboxPath;

        _deliveryQueue = new DeliveryQueue();

        _deliveryWorker = new DeliveryWorker(
            _deliveryQueue,
            _httpClient,
            LogFromWorker);

        _teamsInboxWatcher = new TeamsInboxWatcher(
            _deliveryQueue,
            () => _settings.GetTopic(),
            LogFromWorker);

        RefreshTopicState();
        ShowInitialNtfyStatus();

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

        // Recover and deliver previously queued notifications.
        _deliveryWorker.Start();

        // Automatically start configured integrations.
        Loaded += async (_, _) =>
        {
            // Launched by the "start with Windows" entry: go straight to the tray.
            if (Array.IndexOf(Environment.GetCommandLineArgs(), StartupManager.AutostartArgument) >= 0)
                WindowState = WindowState.Minimized;

            // New messages are relayed from now on; only those missed while the app was
            // closed wait for the user's answer.
            await AutoStartAsync();
            await AutoStartTeamsInboxAsync();
            await AskAboutMissedMessagesAsync();
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
            AddLog("A notification could not be checked for missed messages: " + ex.Message);
        }
    }

    private async Task AskAboutMissedMessagesAsync()
    {
        int teamsCount = _launchMissedTeams.Count;
        int outlookCount = _missedOutlook.Count;

        if (teamsCount + outlookCount == 0 || _isShuttingDown)
            return;

        DateTimeOffset? oldest = _launchMissedTeams
            .Select(missed => (DateTimeOffset?)missed.SortTimeUtc)
            .Concat(_missedOutlook.Select(toast => (DateTimeOffset?)toast.CreatedAt))
            .Min();

        string details = MissedMessages.Describe(
            teamsCount,
            outlookCount,
            oldest?.LocalDateTime,
            DateTime.Now);

        AddLog("Missed while closed: " + details + ". Waiting for your answer.");

        bool send = MissedMessagesDialog.Ask(details);

        if (_isShuttingDown)
            return;

        if (teamsCount > 0)
            _teamsInboxWatcher.ResolveHeld(send);

        _launchMissedTeams = Array.Empty<MissedTeamsMessage>();

        await ResolveMissedOutlookAsync(send);

        AddLog(send
            ? "You chose to send the missed messages; they are sent oldest first."
            : "You chose to skip the missed messages; relaying continues from now.");
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
            string topic = RequireTopic();
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
            AddLog("Could not queue the missed Outlook notifications: " + ex.Message);
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
            AddLog("Could not record when NotiRelay last ran: " + ex.Message);
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
                ex.GetType().Name + ": " + ex.Message);
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
                ex.GetType().Name + ": " + ex.Message);
        }

        // All background services have now been awaited.
        _allowExit = true;
        _trayIcon.Visible = false;

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

            AddLog("ntfy topic not saved: " + ex.Message);
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

            var client = GetNtfyClient();

            await client.SendSyntheticTestAsync();

            SetStatus(NtfyStatus,
                "Test sent. It should appear on your phone within a few seconds.",
                StatusKind.Success);

            AddLog("Synthetic ntfy test notification sent.");
        }
        catch (Exception ex)
        {
            SetStatus(NtfyStatus, "Test failed: " + NtfyErrors.Describe(ex) + ".", StatusKind.Error);

            AddLog("ntfy test failed: " + ex.Message);
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
                ex.Message);
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
                ex.Message);
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
        }
        catch (Exception ex)
        {
            SetStatus(TeamsInboxStatus,
                "Could not stop Teams monitoring: " + ex.Message, StatusKind.Error);

            AddLog(
                "Teams monitoring stop failed: " +
                ex.Message);
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
            AddLog("Could not look for missed Teams messages: " + ex.Message);
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
                ex.Message);
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

            AddLog("Could not save the Outlook confirmation: " + ex.Message);
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
                ex.Message);

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

                string topic = RequireTopic();

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
                ex.Message);
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

            AddLog("Could not change the start-with-Windows setting: " + ex.Message);

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
            AddLog("Could not copy the log: " + ex.Message);
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

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        ActivityList.Items.Insert(0, line);

        while (ActivityList.Items.Count > 150)
            ActivityList.Items.RemoveAt(
                ActivityList.Items.Count - 1);
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
