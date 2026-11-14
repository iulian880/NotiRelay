namespace NotiRelay;

/// <summary>
/// Platform-neutral rules for the ntfy topic: clean up what the user pasted, check it,
/// and describe it without revealing it. Kept free of WPF/DPAPI so it is unit-tested on any OS.
/// </summary>
public static class NtfyTopic
{
    public const int MaxLength = 64;

    // Anyone who knows the topic can read the messages, so short topics are easy to guess.
    public const int WeakBelowLength = 12;

    private const string MaskPrefix = "••••";

    /// <summary>
    /// Accepts a bare topic or an ntfy.sh link (with or without scheme, trailing slash,
    /// query or fragment) and returns the bare topic. Other hosts are left intact so
    /// <see cref="Validate"/> can reject them with a clear message.
    /// </summary>
    public static string Normalize(string? input)
    {
        string value = (input ?? string.Empty).Trim();

        foreach (string scheme in new[] { "https://", "http://" })
        {
            if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                value = value[scheme.Length..];
                break;
            }
        }

        const string host = "ntfy.sh/";
        if (value.StartsWith(host, StringComparison.OrdinalIgnoreCase))
            value = value[host.Length..];

        int cut = value.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0)
            value = value[..cut];

        return value.TrimEnd('/').Trim();
    }

    /// <summary>Returns null when the topic is valid, otherwise a message for the user.</summary>
    public static string? Validate(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return "Enter your ntfy topic first.";

        if (topic.Contains('/'))
        {
            return "Only ntfy.sh topics are supported. Paste just the topic name, " +
                   "or an https://ntfy.sh/<topic> link.";
        }

        if (topic.Length > MaxLength ||
            topic.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-'))
        {
            return "The topic can only contain letters, numbers, underscores or hyphens " +
                   $"(max {MaxLength} characters).";
        }

        return null;
    }

    public static bool IsWeak(string topic) => topic.Length < WeakBelowLength;

    /// <summary>Shows only the last 4 characters, enough to recognise which topic is saved.</summary>
    public static string Mask(string topic) =>
        topic.Length > 4 ? MaskPrefix + topic[^4..] : MaskPrefix;
}
