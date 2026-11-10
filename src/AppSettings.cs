
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NotiRelay;

public sealed class AppSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NotiRelay");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDirectory, "settings.json");

    public string ProtectedTopic { get; set; } = string.Empty;

    // Local synchronized OneDrive folder containing Teams JSON messages.
    public string TeamsInboxPath { get; set; } = string.Empty;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void SetTopic(string topic)
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

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);

        string json = JsonSerializer.Serialize(
            this,
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(SettingsPath, json, Encoding.UTF8);
    }
}
