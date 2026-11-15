using System.Globalization;
using System.IO;
using System.Text;

namespace NotiRelay;

/// <summary>
/// The debug log file written while the Debugging button is on. Each entry gets a date and
/// milliseconds; the ntfy topic is masked before anything reaches the disk; at the size cap
/// the file becomes <see cref="OldFileName"/> and a new one starts, so at most two files
/// exist. Safe to call from any thread. Platform-neutral so it is unit-tested on any OS.
/// </summary>
public sealed class DebugLogFile
{
    public const string FileName = "NotiRelay-debug.log";
    public const string OldFileName = "NotiRelay-debug.old.log";
    public const long DefaultMaxBytes = 5L * 1024 * 1024;

    private const string StampFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private static readonly string Indent = new(' ', StampFormat.Length + 2);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly object _gate = new();
    private readonly Func<IReadOnlyList<string>> _topics;
    private readonly long _maxBytes;

    private DebugLogFile(string filePath, Func<IReadOnlyList<string>> topics, long maxBytes)
    {
        FilePath = filePath;
        _topics = topics;
        _maxBytes = maxBytes;
    }

    public string FilePath { get; }

    /// <summary>Why the last write failed, or null after a successful write.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Opens the log in the first directory where the file can be created and appended to.
    /// Returns null, with the reasons in <paramref name="error"/>, when none is writable.
    /// </summary>
    public static DebugLogFile? Open(
        IReadOnlyList<string> directories,
        Func<string?> topic,
        out string? error,
        long maxBytes = DefaultMaxBytes) =>
        OpenForTopics(
            directories,
            () => topic() is { Length: > 0 } single ? new[] { single } : Array.Empty<string>(),
            out error,
            maxBytes);

    /// <summary>Like <see cref="Open"/>, masking every topic the provider returns.</summary>
    public static DebugLogFile? OpenForTopics(
        IReadOnlyList<string> directories,
        Func<IReadOnlyList<string>> topics,
        out string? error,
        long maxBytes = DefaultMaxBytes)
    {
        var problems = new List<string>();

        foreach (string directory in directories)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, FileName);

                using (OpenForAppend(path))
                {
                }

                error = null;
                return new DebugLogFile(path, topics, maxBytes);
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                problems.Add($"{directory}: {ex.Message}");
            }
        }

        error = problems.Count == 0
            ? "No folder to write the debug log to."
            : string.Join(" | ", problems);

        return null;
    }

    /// <summary>Appends one entry. Returns false (see <see cref="LastError"/>) instead of throwing.</summary>
    public bool TryWrite(DateTime localTime, string message)
    {
        string text;

        try
        {
            text = Format(localTime, RedactAll(message, _topics()));
        }
        catch (Exception ex)
        {
            LastError = "Could not prepare the entry: " + ex.Message;
            return false;
        }

        byte[] bytes = Utf8NoBom.GetBytes(text);

        lock (_gate)
        {
            try
            {
                RotateIfNeeded(bytes.Length);

                using FileStream stream = OpenForAppend(FilePath);
                stream.Write(bytes, 0, bytes.Length);

                LastError = null;
                return true;
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                LastError = ex.Message;
                return false;
            }
        }
    }

    /// <summary>Date and milliseconds on the first line; following lines indented under it.</summary>
    public static string Format(DateTime localTime, string message)
    {
        string[] lines = (message ?? string.Empty)
            .Replace("\r\n", "\n")
            .Split('\n');

        var builder = new StringBuilder();

        builder
            .Append(localTime.ToString(StampFormat, CultureInfo.InvariantCulture))
            .Append("  ")
            .Append(lines[0])
            .Append(Environment.NewLine);

        for (int i = 1; i < lines.Length; i++)
            builder.Append(Indent).Append(lines[i]).Append(Environment.NewLine);

        return builder.ToString();
    }

    /// <summary>
    /// Hides credentials embedded in URLs, then masks the topic: always inside an ntfy.sh
    /// address, and anywhere else once it is long enough that masking it does not garble
    /// ordinary words.
    /// </summary>
    public static string Redact(string text, string? topic)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        text = DebugReport.HideUrlCredentials(text);

        if (string.IsNullOrEmpty(topic))
            return text;

        string mask = MaskTopic(topic);

        text = text.Replace(
            "ntfy.sh/" + topic,
            "ntfy.sh/" + mask,
            StringComparison.OrdinalIgnoreCase);

        if (topic.Length >= 4)
            text = text.Replace(topic, mask, StringComparison.Ordinal);

        return text;
    }

    /// <summary>Applies <see cref="Redact"/> for every topic (longest first).</summary>
    public static string RedactAll(string text, IEnumerable<string?> topics)
    {
        // Credentials are hidden even when no topic is configured.
        text = Redact(text, null);

        foreach (string? topic in topics
                     .Where(topic => !string.IsNullOrEmpty(topic))
                     .OrderByDescending(topic => topic!.Length))
        {
            text = Redact(text, topic);
        }

        return text;
    }

    /// <summary>
    /// The topic as the debug log shows it: last 4 characters only for long topics, since
    /// for a short one they would give most of it away to whoever receives the file.
    /// </summary>
    public static string MaskTopic(string topic) =>
        topic.Length >= NtfyTopic.WeakBelowLength ? NtfyTopic.Mask(topic) : "••••";

    private void RotateIfNeeded(int incomingBytes)
    {
        var info = new FileInfo(FilePath);

        if (!info.Exists || info.Length == 0 || info.Length + incomingBytes <= _maxBytes)
            return;

        string oldPath = Path.Combine(
            Path.GetDirectoryName(FilePath) ?? string.Empty,
            OldFileName);

        File.Move(FilePath, oldPath, overwrite: true);
    }

    // ReadWrite + Delete sharing so the user can open, copy or move the file while it is in use.
    private static FileStream OpenForAppend(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    private static bool IsFileProblem(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException;
}
