
using System.IO;
using System.Net.Http;
using System.Windows;
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

    public MainWindow()
    {
        InitializeComponent();

        ShowInTaskbar = false;

        _trayImage = CreateApplicationTrayImage();
        _trayIcon = CreateTrayIcon(_trayImage);

        StateChanged += MainWindow_StateChanged;

        _settings = AppSettings.Load();

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

        RefreshTopicLabel();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };

        _pollTimer.Tick += PollTimer_Tick;

        AuthorizationCheckBox.IsChecked = true;

        _suppressStartupToggle = true;
        StartWithWindowsCheckBox.IsChecked = StartupManager.IsEnabled();
        _suppressStartupToggle = false;

        SetRunningState(false);
        SetTeamsInboxRunningState(false);

        AddLog("NotiRelay application opened.");

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

            await AutoStartAsync();
            await AutoStartTeamsInboxAsync();
        };
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
            return;
        }

        base.OnClosing(e);
    }

    // ==========================================
    // NTFY SETTINGS
    // ==========================================

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettingsIfNeeded();

            NtfyStatus.Text = "ntfy settings saved successfully.";
            AddLog("ntfy settings saved.");
        }
        catch (Exception ex)
        {
            NtfyStatus.Text =
                "Could not save settings: " + ex.Message;

            AddLog("Settings error: " + ex.Message);
        }
    }

    private async void TestNtfy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettingsIfNeeded();

            var client = GetNtfyClient();

            await client.SendSyntheticTestAsync();

            NtfyStatus.Text =
                "Test notification delivered successfully.";

            AddLog("Synthetic ntfy test notification sent.");
        }
        catch (Exception ex)
        {
            NtfyStatus.Text =
                "ntfy test failed: " + ex.Message;

            AddLog("ntfy test failed: " + ex.Message);
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
                TeamsInboxStatus.Text =
                    "The selected folder does not exist.";

                return;
            }

            _settings.TeamsInboxPath = selectedPath;
            _settings.Save();

            TeamsInboxPathBox.Text = selectedPath;

            TeamsInboxStatus.Text =
                "Teams inbox folder saved successfully.";

            AddLog("Teams inbox folder configured: " + selectedPath);
        }
        catch (Exception ex)
        {
            TeamsInboxStatus.Text =
                "Could not configure Teams inbox: " + ex.Message;

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
            TeamsInboxStatus.Text =
                "Could not start Teams monitoring: " + ex.Message;

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

            TeamsInboxStatus.Text =
                "Teams inbox monitoring stopped.";

            AddLog("Teams inbox monitoring stopped by user.");
        }
        catch (Exception ex)
        {
            TeamsInboxStatus.Text =
                "Could not stop Teams monitoring: " + ex.Message;

            AddLog(
                "Teams monitoring stop failed: " +
                ex.Message);
        }
    }

    private void StartTeamsInboxMonitoring(bool automatic)
    {
        if (_isTeamsInboxRunning)
            return;

        if (_isShuttingDown)
            return;

        SaveSettingsIfNeeded();

        string topic = _settings.GetTopic();

        ValidateTopic(topic);

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

        _teamsInboxWatcher.Start(inboxPath);

        _isTeamsInboxRunning = true;

        SetTeamsInboxRunningState(true);

        TeamsInboxStatus.Text =
            "Teams inbox monitoring active. New messages are being processed.";

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

    private Task AutoStartTeamsInboxAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.TeamsInboxPath))
        {
            AddLog(
                "Teams auto-start skipped: inbox folder is not configured.");

            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_settings.ProtectedTopic))
        {
            AddLog(
                "Teams auto-start skipped: ntfy topic is not configured.");

            return Task.CompletedTask;
        }

        if (!Directory.Exists(_settings.TeamsInboxPath))
        {
            TeamsInboxStatus.Text =
                "Configured Teams inbox folder is unavailable. " +
                "Check OneDrive synchronization.";

            AddLog(
                "Teams auto-start skipped: configured inbox folder is unavailable.");

            return Task.CompletedTask;
        }

        try
        {
            StartTeamsInboxMonitoring(automatic: true);
        }
        catch (Exception ex)
        {
            TeamsInboxStatus.Text =
                "Teams auto-start failed: " + ex.Message;

            AddLog(
                "Teams auto-start failed: " +
                ex.Message);
        }

        return Task.CompletedTask;
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
            System.Windows.MessageBox.Show(
                "Confirm the notification forwarding setting first.",
                "Confirmation required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        await StartForwardingAsync(automatic: false);
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
                "Windows notification auto-start skipped: confirmation not checked.");

            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.ProtectedTopic))
        {
            AddLog(
                "Windows notification auto-start skipped: ntfy topic not configured.");

            NtfyStatus.Text =
                "Enter your ntfy topic and save settings first.";

            return;
        }

        await StartForwardingAsync(automatic: true);
    }

    private async Task<bool> StartForwardingAsync(bool automatic)
    {
        if (_isRunning)
            return true;

        if (_isShuttingDown)
            return false;

        try
        {
            SaveSettingsIfNeeded();

            string topic = _settings.GetTopic();

            ValidateTopic(topic);

            _ntfy = GetNtfyClient();

            _listener ??= UserNotificationListener.Current;

            var access = _listener.GetAccessStatus();

            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                ListenerStatus.Text =
                    "Requesting Windows notification access...";

                access = await _listener.RequestAccessAsync();
            }

            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                ListenerStatus.Text =
                    "Windows notification permission was not granted. " +
                    "Check Windows notification privacy settings and app permissions.";

                AddLog(
                    "Windows notification access not granted: " + access);

                return false;
            }

            // Existing notifications are ignored.
            var current = await _listener.GetNotificationsAsync(
                NotificationKinds.Toast);

            _seenNotificationIds.Clear();

            foreach (var item in current)
                _seenNotificationIds.Add(item.Id);

            _isRunning = true;

            _pollTimer.Start();

            SetRunningState(true);

            ListenerStatus.Text =
                $"Monitoring active. Existing notifications ignored: {current.Count}.";

            AddLog(automatic
                ? "Windows notification monitoring started automatically."
                : "Windows notification monitoring started. Existing notifications excluded.");

            return true;
        }
        catch (Exception ex)
        {
            ListenerStatus.Text =
                "Could not start: " + ex.Message;

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

                string topic = _settings.GetTopic();

                ValidateTopic(topic);

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

    private void SaveSettingsIfNeeded()
    {
        string enteredTopic = TopicBox.Password.Trim();

        if (!string.IsNullOrWhiteSpace(enteredTopic))
        {
            _settings.SetTopic(enteredTopic);

            // Prevent using a client cached for the previous topic.
            _ntfy = null;
        }

        _settings.Save();

        if (!string.IsNullOrWhiteSpace(enteredTopic))
            TopicBox.Clear();

        RefreshTopicLabel();
    }

    private NtfyClient GetNtfyClient()
    {
        if (_ntfy is not null)
            return _ntfy;

        string topic = _settings.GetTopic();

        ValidateTopic(topic);

        _ntfy = new NtfyClient(topic, _httpClient);

        return _ntfy;
    }

    private static void ValidateTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            throw new InvalidOperationException(
                "Enter your ntfy topic first.");
        }

        if (topic.Length > 64 ||
            topic.Any(c =>
                !char.IsAsciiLetterOrDigit(c) &&
                c != '_' &&
                c != '-'))
        {
            throw new InvalidOperationException(
                "The topic must contain only letters, numbers, underscores or hyphens, with a maximum of 64 characters.");
        }
    }

    // ==========================================
    // UI
    // ==========================================

    private void StopForwarding(string reason)
    {
        _pollTimer.Stop();

        _isRunning = false;

        SetRunningState(false);

        ListenerStatus.Text = reason;

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
        bool active = _isRunning || _isTeamsInboxRunning;

        StateBadge.Text = active ? "RELAYING" : "STOPPED";
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

    private void RefreshTopicLabel()
    {
        TopicSavedLabel.Text =
            string.IsNullOrWhiteSpace(_settings.ProtectedTopic)
                ? "No saved topic"
                : "An ntfy topic is saved securely for this Windows user.";
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
