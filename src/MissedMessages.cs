using System.Globalization;

namespace NotiRelay;

/// <summary>A Teams message that arrived while NotiRelay was closed and was never forwarded.</summary>
public sealed record MissedTeamsMessage(
    IReadOnlyList<string> FileNames,
    string Key,
    TeamsInboxMessage Message,
    DateTimeOffset SortTimeUtc);

/// <summary>
/// Platform-neutral rules for messages missed while the app was closed: which ones count,
/// in what order they are sent, and how they are described. Unit-tested on any OS.
/// </summary>
public static class MissedMessages
{
    public const string Question =
        "Do you want me to send the messages that you missed while the app was closed?";

    private const string TitlePrefix = "Missed · ";
    private const int MaxTitleLength = 120;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Message IDs are scoped to a conversation.</summary>
    public static string MessageKey(TeamsInboxMessage message) =>
        message.ConversationId + ":" + message.MessageId;

    /// <summary>
    /// Inbox files that were never forwarded: not part of the first-run baseline and whose
    /// message key was never processed. Files carrying the same message are merged; the
    /// result is ordered oldest first (message time, or file time when the message has none).
    /// </summary>
    public static IReadOnlyList<MissedTeamsMessage> SelectUnsent(
        IEnumerable<(string FileName, TeamsInboxMessage Message, DateTimeOffset FileTimeUtc)> files,
        ISet<string> baselineFileNames,
        ISet<string> processedKeys)
    {
        return files
            .Where(file => !baselineFileNames.Contains(file.FileName))
            .Select(file => (
                file.FileName,
                file.Message,
                Key: MessageKey(file.Message),
                SortTime: file.Message.CreatedAt ?? file.FileTimeUtc))
            .Where(item => !processedKeys.Contains(item.Key))
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => new MissedTeamsMessage(
                group.Select(item => item.FileName).ToList(),
                group.Key,
                group.First().Message,
                group.Min(item => item.SortTime)))
            .OrderBy(missed => missed.SortTimeUtc)
            .ToList();
    }

    /// <summary>
    /// A toast counts as missed only if it appeared after NotiRelay last ran. Without a
    /// record of the last run (first launch after the update) nothing is offered.
    /// </summary>
    public static bool IsMissedOutlook(DateTimeOffset createdUtc, DateTimeOffset? lastActiveUtc) =>
        lastActiveUtc is not null && createdUtc > lastActiveUtc.Value;

    public static string FormatWhen(DateTime local, DateTime nowLocal)
    {
        int daysAgo = (nowLocal.Date - local.Date).Days;
        string time = local.ToString("HH:mm", Invariant);

        if (daysAgo <= 0)
            return "today " + time;

        if (daysAgo == 1)
            return "yesterday " + time;

        if (daysAgo < 7)
            return local.ToString("ddd", Invariant) + " " + time;

        return local.Year == nowLocal.Year
            ? local.ToString("d MMM", Invariant) + " " + time
            : local.ToString("d MMM yyyy", Invariant) + " " + time;
    }

    public static string Describe(int teamsCount, int outlookCount, DateTime? oldestLocal, DateTime nowLocal)
    {
        var parts = new List<string>();

        if (teamsCount > 0)
            parts.Add(teamsCount == 1 ? "1 Teams message" : $"{teamsCount} Teams messages");

        if (outlookCount > 0)
            parts.Add(outlookCount == 1 ? "1 Outlook notification" : $"{outlookCount} Outlook notifications");

        if (oldestLocal is not null)
            parts.Add("oldest from " + FormatWhen(oldestLocal.Value, nowLocal));

        return string.Join(" · ", parts);
    }

    public static string Title(string title)
    {
        string full = TitlePrefix + title;
        return full.Length > MaxTitleLength ? full[..MaxTitleLength] : full;
    }

    public static string Body(string text, DateTime? whenLocal, DateTime nowLocal) =>
        whenLocal is null ? text : $"[{FormatWhen(whenLocal.Value, nowLocal)}] {text}";
}
