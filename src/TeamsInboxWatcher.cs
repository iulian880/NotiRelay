
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace NotiRelay;

public sealed class TeamsInboxWatcher
{
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NotiRelay");

    private static readonly string ProcessedPath =
        Path.Combine(StateDirectory, "teams-processed.dat");

    private static readonly string BaselinePath =
        Path.Combine(StateDirectory, "teams-inbox-baseline.dat");

    // A file that still cannot be parsed after this many attempts (about
    // one minute at 10 s apart) is skipped instead of retried forever.
    internal const int MaxProcessingAttempts = 6;

    private readonly DeliveryQueue _queue;
    private readonly Func<string> _topicProvider;
    private readonly Action<string> _log;

    private readonly HashSet<string> _processedMessageKeys;
    private readonly HashSet<string> _baselineFileNames;

    private readonly Dictionary<string, FileState> _fileStates =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cancellation;
    private Task? _workerTask;
    private string _inboxPath = string.Empty;

    public string? RecoveryWarning { get; private set; }

    public TeamsInboxWatcher(
        DeliveryQueue queue,
        Func<string> topicProvider,
        Action<string> log)
    {
        _queue = queue;
        _topicProvider = topicProvider;
        _log = log;

        Directory.CreateDirectory(StateDirectory);

        _processedMessageKeys = LoadProcessedKeys();
        _baselineFileNames = LoadBaselineFileNames();
    }

    public void Start(string inboxPath)
    {
        if (_workerTask is not null)
            return;

        if (string.IsNullOrWhiteSpace(inboxPath))
            throw new ArgumentException("Teams inbox path is empty.");

        if (!Directory.Exists(inboxPath))
        {
            throw new DirectoryNotFoundException(
                "Teams inbox folder does not exist: " + inboxPath);
        }

        _inboxPath = Path.GetFullPath(inboxPath);

        // First run: ignore files already present in the inbox.
        if (!File.Exists(BaselinePath))
        {
            string[] existingFiles = Directory.GetFiles(
                _inboxPath,
                "*.json",
                SearchOption.TopDirectoryOnly);

            foreach (string path in existingFiles)
            {
                var info = new FileInfo(path);

                _baselineFileNames.Add(Path.GetFileName(path));

                _fileStates[path] = new FileState(
                    info.Length,
                    info.LastWriteTimeUtc)
                {
                    Processed = true
                };
            }

            SaveBaselineFileNames();

            Report(
                $"Initial Teams inbox baseline created. " +
                $"Existing files ignored: {existingFiles.Length}.");
        }
        else
        {
            // Restore the original baseline after application restart.
            foreach (string path in Directory.GetFiles(
                         _inboxPath,
                         "*.json",
                         SearchOption.TopDirectoryOnly))
            {
                if (!_baselineFileNames.Contains(Path.GetFileName(path)))
                    continue;

                var info = new FileInfo(path);

                _fileStates[path] = new FileState(
                    info.Length,
                    info.LastWriteTimeUtc)
                {
                    Processed = true
                };
            }

            Report(
                $"Teams inbox baseline restored. " +
                $"Tracked existing files: {_baselineFileNames.Count}.");
        }

        _cancellation = new CancellationTokenSource();

        Report("Teams inbox monitoring started: " + _inboxPath);

        _workerTask = RunAsync(_cancellation.Token);
    }

    public async Task StopAsync()
    {
        if (_cancellation is null)
            return;

        _cancellation.Cancel();

        if (_workerTask is not null)
            await _workerTask;

        _cancellation.Dispose();
        _cancellation = null;
        _workerTask = null;

        Report("Teams inbox monitoring stopped.");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ScanInboxAsync(cancellationToken);

                await Task.Delay(
                    TimeSpan.FromSeconds(2),
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Report(
                    "Teams inbox worker error: " +
                    ex.GetType().Name + ": " + ex.Message);

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Report("Teams inbox worker stopped.");
    }

    private async Task ScanInboxAsync(
        CancellationToken cancellationToken)
    {
        string[] files;

        try
        {
            files = Directory.GetFiles(
                _inboxPath,
                "*.json",
                SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            Report("Cannot scan Teams inbox: " + ex.Message);
            return;
        }

        var presentFiles = new HashSet<string>(
            files,
            StringComparer.OrdinalIgnoreCase);

        foreach (string oldPath in _fileStates.Keys
                     .Where(path => !presentFiles.Contains(path))
                     .ToList())
        {
            _fileStates.Remove(oldPath);
        }

        foreach (string path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var info = new FileInfo(path);

                if (!info.Exists)
                    continue;

                if (!_fileStates.TryGetValue(path, out FileState? state))
                {
                    bool existedInBaseline =
                        _baselineFileNames.Contains(Path.GetFileName(path));

                    _fileStates[path] = new FileState(
                        info.Length,
                        info.LastWriteTimeUtc)
                    {
                        Processed = existedInBaseline
                    };

                    continue;
                }

                // Detect changes during OneDrive synchronization.
                if (state.Length != info.Length ||
                    state.LastWriteTimeUtc != info.LastWriteTimeUtc)
                {
                    state.Length = info.Length;
                    state.LastWriteTimeUtc = info.LastWriteTimeUtc;
                    state.StableScans = 0;
                    state.Processed = false;
                    state.FailedAttempts = 0;
                    state.RetryAfterUtc = DateTime.MinValue;
                    continue;
                }

                if (state.Processed)
                    continue;

                // Require two stable observations before reading.
                if (state.StableScans < 1)
                {
                    state.StableScans++;
                    continue;
                }

                if (state.RetryAfterUtc > DateTime.UtcNow)
                    continue;

                await ProcessFileAsync(
                    path,
                    state,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Report(
                    $"Teams file processing error " +
                    $"({Path.GetFileName(path)}): {ex.Message}");
            }
        }
    }

    private async Task ProcessFileAsync(
        string path,
        FileState state,
        CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(
                path,
                cancellationToken);

            if (!TeamsMessageParser.TryParse(
                    json,
                    out TeamsInboxMessage? message,
                    out string error) ||
                message is null)
            {
                RetryLater(state, error, path);
                return;
            }

            // Message IDs are scoped to a conversation.
            string messageKey =
                message.ConversationId + ":" + message.MessageId;

            if (_processedMessageKeys.Contains(messageKey))
            {
                state.Processed = true;
                return;
            }

            string topic = _topicProvider();

            if (string.IsNullOrWhiteSpace(topic))
            {
                // Configuration problem, not a bad file: keep waiting.
                RetryLater(
                    state,
                    "ntfy topic is not configured.",
                    path,
                    countAttempt: false);
                return;
            }

            string sender =
                string.IsNullOrWhiteSpace(message.SenderName)
                    ? "Unknown sender"
                    : message.SenderName.Trim();

            // Notification title: sender only.
            string title = sender;

            if (title.Length > 120)
                title = title[..120];

            // Notification body: message only.
            string payload = message.Text;

            bool added = await _queue.EnqueueAsync(
                "teams:" + messageKey,
                payload,
                title,
                "high",
                topic);

            // Persist processed ID after the durable queue operation.
            _processedMessageKeys.Add(messageKey);

            try
            {
                SaveProcessedKeys();
            }
            catch
            {
                _processedMessageKeys.Remove(messageKey);
                throw;
            }

            state.Processed = true;
            state.RetryAfterUtc = DateTime.MinValue;
            state.FailedAttempts = 0;

            Report(added
                ? $"Teams message queued: {sender}. " +
                  $"Message ID: {message.MessageId}"
                : $"Teams message already exists in delivery queue: " +
                  message.MessageId);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RetryLater(
                state,
                ex.GetType().Name + ": " + ex.Message,
                path);
        }
    }

    private void RetryLater(
        FileState state,
        string reason,
        string path,
        bool countAttempt = true)
    {
        if (countAttempt)
            state.FailedAttempts++;

        if (countAttempt &&
            state.FailedAttempts >= MaxProcessingAttempts)
        {
            // Give up on this file version; a later change to the file
            // (e.g. OneDrive finishing a sync) resets the state and retries.
            state.Processed = true;
            state.RetryAfterUtc = DateTime.MinValue;

            Report(
                $"Teams file skipped after {state.FailedAttempts} " +
                $"failed attempts ({Path.GetFileName(path)}): {reason}");

            return;
        }

        state.RetryAfterUtc = DateTime.UtcNow.AddSeconds(10);

        if (DateTime.UtcNow - state.LastErrorLoggedUtc <
            TimeSpan.FromSeconds(30))
        {
            return;
        }

        state.LastErrorLoggedUtc = DateTime.UtcNow;

        Report("Teams inbox retry scheduled: " + reason);
    }

    private HashSet<string> LoadProcessedKeys()
    {
        if (!File.Exists(ProcessedPath))
            return new HashSet<string>(StringComparer.Ordinal);

        try
        {
            byte[] encrypted = File.ReadAllBytes(ProcessedPath);

            byte[] decrypted = ProtectedData.Unprotect(
                encrypted,
                null,
                DataProtectionScope.CurrentUser);

            try
            {
                string[] keys =
                    JsonSerializer.Deserialize<string[]>(decrypted)
                    ?? Array.Empty<string>();

                return new HashSet<string>(
                    keys,
                    StringComparer.Ordinal);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decrypted);
            }
        }
        catch (Exception ex) when (
            ex is CryptographicException ||
            ex is JsonException ||
            ex is FormatException ||
            ex is IOException)
        {
            string backupPath = ProcessedPath +
                ".unreadable-" +
                DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            try
            {
                File.Move(ProcessedPath, backupPath);

                RecoveryWarning =
                    "Teams processed-message registry was unreadable. " +
                    "Original preserved as " +
                    Path.GetFileName(backupPath) + ".";
            }
            catch (Exception moveException)
            {
                RecoveryWarning =
                    "Teams processed-message registry recovery failed: " +
                    moveException.Message;
            }

            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private HashSet<string> LoadBaselineFileNames()
    {
        var names = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(BaselinePath))
            return names;

        try
        {
            byte[] encrypted = File.ReadAllBytes(BaselinePath);

            byte[] decrypted = ProtectedData.Unprotect(
                encrypted,
                null,
                DataProtectionScope.CurrentUser);

            try
            {
                string[] stored =
                    JsonSerializer.Deserialize<string[]>(decrypted)
                    ?? Array.Empty<string>();

                foreach (string name in stored)
                    names.Add(name);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decrypted);
            }
        }
        catch (Exception ex) when (
            ex is CryptographicException ||
            ex is JsonException ||
            ex is FormatException ||
            ex is IOException)
        {
            string backupPath = BaselinePath +
                ".unreadable-" +
                DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            try
            {
                File.Move(BaselinePath, backupPath);

                RecoveryWarning =
                    "Teams inbox baseline was unreadable. " +
                    "Original preserved as " +
                    Path.GetFileName(backupPath) + ".";
            }
            catch (Exception moveException)
            {
                RecoveryWarning =
                    "Teams inbox baseline recovery failed: " +
                    moveException.Message;
            }
        }

        return names;
    }

    private void SaveBaselineFileNames()
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(
            _baselineFileNames.OrderBy(
                name => name,
                StringComparer.OrdinalIgnoreCase).ToArray());

        byte[] encrypted = ProtectedData.Protect(
            plain,
            null,
            DataProtectionScope.CurrentUser);

        string temporaryPath = BaselinePath + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, encrypted);

            File.Move(
                temporaryPath,
                BaselinePath,
                overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(encrypted);

            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void SaveProcessedKeys()
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(
            _processedMessageKeys.OrderBy(key => key).ToArray());

        byte[] encrypted = ProtectedData.Protect(
            plain,
            null,
            DataProtectionScope.CurrentUser);

        string temporaryPath = ProcessedPath + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, encrypted);

            File.Move(
                temporaryPath,
                ProcessedPath,
                overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(encrypted);

            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void Report(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Logging must never interrupt message ingestion.
        }
    }

    private sealed class FileState
    {
        public long Length { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public int StableScans { get; set; }
        public bool Processed { get; set; }
        public DateTime RetryAfterUtc { get; set; }
        public DateTime LastErrorLoggedUtc { get; set; }
        public int FailedAttempts { get; set; }

        public FileState(long length, DateTime lastWriteTimeUtc)
        {
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }
    }
}
