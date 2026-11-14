namespace NotiRelay;

/// <summary>
/// Process-wide sink for the debug log. Off (no file) unless the Debugging button is on, in
/// which case the activity log is mirrored here and workers add error details the screen
/// does not show. Every call is a cheap no-op while debugging is off. Safe from any thread.
/// </summary>
public static class DebugTrace
{
    private static DebugLogFile? _file;

    public static DebugLogFile? File => Volatile.Read(ref _file);

    public static bool IsEnabled => File is not null;

    public static void Attach(DebugLogFile file) => Volatile.Write(ref _file, file);

    public static DebugLogFile? Detach() => Interlocked.Exchange(ref _file, null);

    /// <summary>Writes one entry. True when written or when debugging is off.</summary>
    public static bool Line(string message) =>
        File?.TryWrite(DateTime.Now, message) ?? true;

    /// <summary>Writes the context followed by the full cause chain and stack trace.</summary>
    public static void Detail(string context, Exception ex)
    {
        DebugLogFile? file = File;
        if (file is null)
            return;

        string text;
        try
        {
            text = context + Environment.NewLine + DebugReport.Describe(ex);
        }
        catch
        {
            text = context + " (" + ex.GetType().FullName + ")";
        }

        file.TryWrite(DateTime.Now, text);
    }
}
