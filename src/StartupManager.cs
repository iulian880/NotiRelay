using Microsoft.Win32;

namespace NotiRelay;

/// <summary>
/// Per-user "start with Windows" using the <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// key. No administrator rights, no service, no scheduled task. When enabled, Windows launches the
/// current executable at sign-in with <c>--autostart</c>, which starts the app directly in the tray.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "NotiRelay";

    public const string AutostartArgument = "--autostart";

    private static string LaunchCommand =>
        $"\"{Environment.ProcessPath}\" {AutostartArgument}";

    /// <summary>True when the Run value exists and points at this executable.</summary>
    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value
               && value.Contains($"\"{Environment.ProcessPath}\"", StringComparison.OrdinalIgnoreCase);
    }

    public static void Enable()
    {
        using RegistryKey key =
            Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(ValueName, LaunchCommand, RegistryValueKind.String);
    }

    public static void Disable()
    {
        using RegistryKey? key =
            Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
