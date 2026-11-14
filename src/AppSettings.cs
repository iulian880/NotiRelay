
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotiRelay;

public sealed class AppSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NotiRelay");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDirectory, "settings.json");

    private static readonly string CorruptBackupPath =
        Path.Combine(SettingsDirectory, "settings.corrupt.json");

    public string ProtectedTopic { get; set; } = string.Empty;

    // Local synchronized OneDrive folder containing Teams JSON messages.
    public string TeamsInboxPath { get; set; } = string.Empty;

    // Explicit consent for forwarding Windows (Outlook) toasts. Persisted so it is
    // given once by the user and never switched on by the app itself.
    public bool OutlookForwardingConfirmed { get; set; }

    /// <summary>Set when settings.json existed but could not be read.</summary>
    [JsonIgnore]
    public string? LoadWarning { get; private set; }

    [JsonIgnore]
    public bool HasStoredTopic => !string.IsNullOrWhiteSpace(ProtectedTopic);

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings();

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(
                       File.ReadAllText(SettingsPath))
                   ?? throw new JsonException("The settings file is empty.");
        }
        catch (Exception ex)
        {
            // Never lose the old file silently: keep a copy before it gets overwritten.
            string backupNote = TryBackupCorruptFile()
                ? " The unreadable file was kept as settings.corrupt.json."
                : string.Empty;

            return new AppSettings
            {
                LoadWarning =
                    $"Saved settings could not be read ({ex.GetType().Name})." +
                    backupNote +
                    " Enter your ntfy topic again."
            };
        }
    }

    private static bool TryBackupCorruptFile()
    {
        try
        {
            File.Copy(SettingsPath, CorruptBackupPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Encrypts and persists a new topic. All or nothing: if writing fails, the
    /// previously saved topic stays in effect and the exception is rethrown.
    /// </summary>
    public void SaveTopic(string topic)
    {
        string previous = ProtectedTopic;

        SetTopic(topic);

        try
        {
            Save();
        }
        catch
        {
            ProtectedTopic = previous;
            throw;
        }
    }

    private void SetTopic(string topic)
    {
        byte[] plain = Encoding.UTF8.GetBytes(topic);

        try
        {
            byte[] encrypted = ProtectedData.Protect(
                plain, null, DataProtectionScope.CurrentUser);

            ProtectedTopic = Convert.ToBase64String(encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public string GetTopic()
    {
        if (string.IsNullOrWhiteSpace(ProtectedTopic))
            return string.Empty;

        byte[] encrypted = Convert.FromBase64String(ProtectedTopic);
        byte[] plain = ProtectedData.Unprotect(
            encrypted, null, DataProtectionScope.CurrentUser);

        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// False when no topic is stored or it cannot be decrypted for this Windows user
    /// (for example settings copied from another PC or account).
    /// </summary>
    public bool TryGetTopic(out string topic)
    {
        topic = string.Empty;

        if (!HasStoredTopic)
            return false;

        try
        {
            topic = GetTopic();
            return topic.Length > 0;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes to a temporary file first and then swaps it in, so a crash or power
    /// loss mid-write cannot leave a half-written settings.json behind.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);

        string json = JsonSerializer.Serialize(
            this,
            new JsonSerializerOptions { WriteIndented = true });

        string tempPath = SettingsPath + ".tmp";

        using (var stream = new FileStream(
                   tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, SettingsPath, overwrite: true);
    }
}
