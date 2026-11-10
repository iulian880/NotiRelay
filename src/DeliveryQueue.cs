using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace NotiRelay;

public sealed record QueuedNotification
{
    public string Id { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Priority { get; init; } = "urgent";
    public string Topic { get; init; } = string.Empty;

    public DateTime CreatedAtUtc { get; init; }
    public DateTime NextAttemptUtc { get; init; }

    public int Attempts { get; init; }
    public string LastError { get; init; } = string.Empty;
}

public sealed class DeliveryQueue
{
    private const int MaxPendingItems = 500;

    private readonly string _storagePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private List<QueuedNotification> _items;

    public string? RecoveryWarning { get; private set; }

    public DeliveryQueue()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "NotiRelay");

        Directory.CreateDirectory(directory);

        _storagePath = Path.Combine(
            directory,
            "delivery-queue.dat");

        _items = LoadQueue();
    }

    private List<QueuedNotification> LoadQueue()
    {
        if (!File.Exists(_storagePath))
            return new List<QueuedNotification>();

        try
        {
            byte[] encrypted = File.ReadAllBytes(_storagePath);

            byte[] decrypted = ProtectedData.Unprotect(
                encrypted,
                null,
                DataProtectionScope.CurrentUser);

            try
            {
                return JsonSerializer.Deserialize<List<QueuedNotification>>(
                    decrypted) ?? new List<QueuedNotification>();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decrypted);
            }
        }
        catch (Exception ex) when (
            ex is CryptographicException ||
            ex is JsonException ||
            ex is FormatException)
        {
            string backupPath = _storagePath +
                ".unreadable-" +
                DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            File.Move(_storagePath, backupPath);

            RecoveryWarning =
                "The previous delivery queue could not be read. " +
                "The original file was preserved as " +
                Path.GetFileName(backupPath) +
                ". Pending notifications were not recovered.";

            return new List<QueuedNotification>();
        }
    }

    public async Task<bool> EnqueueAsync(
        string id,
        string message,
        string title,
        string priority,
        string topic)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Queue ID cannot be empty.");

        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Message cannot be empty.");

        if (string.IsNullOrWhiteSpace(topic))
            throw new ArgumentException("Topic cannot be empty.");

        await _gate.WaitAsync();

        try
        {
            if (_items.Any(item => item.Id == id))
                return false;

            if (_items.Count >= MaxPendingItems)
            {
                throw new InvalidOperationException(
                    "Delivery queue is full. Pending notifications were not discarded.");
            }

            var item = new QueuedNotification
            {
                Id = id,
                Message = message,
                Title = title,
                Priority = priority,
                Topic = topic,
                CreatedAtUtc = DateTime.UtcNow,
                NextAttemptUtc = DateTime.UtcNow,
                Attempts = 0
            };

            _items.Add(item);

            try
            {
                SaveQueueLocked();
            }
            catch
            {
                _items.Remove(item);
                throw;
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QueuedNotification?> GetNextDueAsync()
    {
        await _gate.WaitAsync();

        try
        {
            DateTime now = DateTime.UtcNow;

            return _items
                .Where(item => item.NextAttemptUtc <= now)
                .OrderBy(item => item.CreatedAtUtc)
                .FirstOrDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkSentAsync(string id)
    {
        await _gate.WaitAsync();

        try
        {
            int index = _items.FindIndex(item => item.Id == id);

            if (index < 0)
                return;

            var removed = _items[index];

            _items.RemoveAt(index);

            try
            {
                SaveQueueLocked();
            }
            catch
            {
                _items.Insert(index, removed);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkFailedAsync(string id, string error)
    {
        await _gate.WaitAsync();

        try
        {
            int index = _items.FindIndex(item => item.Id == id);

            if (index < 0)
                return;

            var previous = _items[index];

            int attempts = previous.Attempts + 1;

            double delaySeconds = Math.Min(
                300,
                5 * Math.Pow(2, attempts - 1));

            string safeError = string.IsNullOrWhiteSpace(error)
                ? "Delivery failed."
                : error[..Math.Min(error.Length, 200)];

            var updated = previous with
            {
                Attempts = attempts,
                NextAttemptUtc = DateTime.UtcNow.AddSeconds(delaySeconds),
                LastError = safeError
            };

            _items[index] = updated;

            try
            {
                SaveQueueLocked();
            }
            catch
            {
                _items[index] = previous;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetPendingCountAsync()
    {
        await _gate.WaitAsync();

        try
        {
            return _items.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SaveQueueLocked()
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(_items);

        byte[] encrypted = ProtectedData.Protect(
            plain,
            null,
            DataProtectionScope.CurrentUser);

        string temporaryPath = _storagePath + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, encrypted);

            File.Move(
                temporaryPath,
                _storagePath,
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
}
