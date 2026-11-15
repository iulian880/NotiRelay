namespace NotiRelay;

/// <summary>What kind of notification it is; each kind can go to its own ntfy topic.</summary>
public enum NotificationCategory
{
    Teams,
    Outlook,
    Calendar,
    Battery
}

/// <summary>One ntfy topic and the categories that send to it (for Send test).</summary>
public sealed record TopicTarget(string Topic, IReadOnlyList<NotificationCategory> Categories);

/// <summary>
/// Picks the ntfy topic for a category: its own topic when one is set, otherwise the main
/// topic. Platform-neutral so it is unit-tested on any OS; the encrypted storage is in
/// AppSettings.
/// </summary>
public static class NotificationTopics
{
    public static readonly IReadOnlyList<NotificationCategory> All = new[]
    {
        NotificationCategory.Teams,
        NotificationCategory.Outlook,
        NotificationCategory.Calendar,
        NotificationCategory.Battery
    };

    public static string DisplayName(NotificationCategory category) => category switch
    {
        NotificationCategory.Teams => "Teams messages",
        NotificationCategory.Outlook => "Outlook notifications",
        NotificationCategory.Calendar => "Meetings",
        _ => "Power & battery"
    };

    public static string Resolve(
        NotificationCategory category,
        string mainTopic,
        IReadOnlyDictionary<NotificationCategory, string> overrides) =>
        overrides.TryGetValue(category, out string? own) && !string.IsNullOrWhiteSpace(own)
            ? own
            : mainTopic;

    /// <summary>One entry per distinct topic, in category order, with the categories using it.</summary>
    public static IReadOnlyList<TopicTarget> DistinctTargets(
        string mainTopic,
        IReadOnlyDictionary<NotificationCategory, string> overrides) =>
        All
            .GroupBy(category => Resolve(category, mainTopic, overrides), StringComparer.Ordinal)
            .Select(group => new TopicTarget(group.Key, group.ToList()))
            .ToList();

    public static string TestTitle(TopicTarget target) =>
        "NOTIRELAY TEST · " + string.Join(", ", target.Categories.Select(DisplayName));
}
