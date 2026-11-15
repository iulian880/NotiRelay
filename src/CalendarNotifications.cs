using System.Globalization;

namespace NotiRelay;

/// <summary>One calendar entry as read from Outlook (local times). Key is stable per occurrence.</summary>
public sealed record CalendarMeeting(
    string Key,
    string Subject,
    DateTime StartLocal,
    DateTime EndLocal,
    string Location,
    bool AllDay = false,
    bool Cancelled = false,
    bool Declined = false,
    bool Free = false)
{
    /// <summary>A meeting the user still has to attend (not cancelled, not declined).</summary>
    public bool IsActive => !Cancelled && !Declined;
}

public enum CalendarChangeKind
{
    Added,
    Moved,
    Cancelled
}

public sealed record CalendarChange(
    CalendarChangeKind Kind,
    CalendarMeeting Meeting,
    CalendarMeeting? Before);

/// <summary>
/// What to tell the phone about the calendar: changes between two reads, the daily agenda
/// and reminders shortly before a meeting, plus their texts. Platform-neutral so it is
/// unit-tested on any OS; the Outlook reading lives in OutlookCalendarReader.
/// </summary>
public static class CalendarNotifications
{
    private const int MaxTitleLength = 120;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// Changes between two reads. Only future meetings count; a meeting that only appears
    /// because the window moved forward at midnight (it starts at or after the previous
    /// window's end) is not new. Cancelled means Outlook's own "Canceled" flag: a meeting that
    /// simply disappears (declined in classic Outlook, deleted, or moved more than 7 days out)
    /// is not reported, since the read cannot tell those apart.
    /// </summary>
    public static IReadOnlyList<CalendarChange> Diff(
        IReadOnlyList<CalendarMeeting> before,
        IReadOnlyList<CalendarMeeting> after,
        DateTime nowLocal,
        DateTime previousWindowEndLocal)
    {
        Dictionary<string, CalendarMeeting> previous = ByKey(before);
        Dictionary<string, CalendarMeeting> current = ByKey(after);
        var changes = new List<CalendarChange>();

        foreach (CalendarMeeting meeting in current.Values.OrderBy(m => m.StartLocal))
        {
            if (!previous.TryGetValue(meeting.Key, out CalendarMeeting? old))
            {
                if (meeting.IsActive &&
                    meeting.StartLocal > nowLocal &&
                    meeting.StartLocal < previousWindowEndLocal)
                {
                    changes.Add(new CalendarChange(CalendarChangeKind.Added, meeting, null));
                }

                continue;
            }

            if (meeting.EndLocal <= nowLocal || !old.IsActive)
                continue;

            if (meeting.Cancelled)
            {
                changes.Add(new CalendarChange(CalendarChangeKind.Cancelled, meeting, null));
            }
            else if (meeting.IsActive &&
                     (meeting.StartLocal != old.StartLocal || meeting.EndLocal != old.EndLocal))
            {
                changes.Add(new CalendarChange(CalendarChangeKind.Moved, meeting, old));
            }
        }

        return changes;
    }

    /// <summary>
    /// Stable key per occurrence. Recurring occurrences share one Outlook id, so the date of
    /// the occurrence's original slot tells them apart; an occurrence moved to another day
    /// keeps its key and is reported as moved.
    /// </summary>
    public static string OccurrenceKey(
        string id, bool recurring, DateTime startLocal, DateTime? originalStartLocal = null) =>
        recurring
            ? id + "#" + (originalStartLocal ?? startLocal).ToString("yyyyMMdd", Invariant)
            : id;

    public static string ChangeTitle(CalendarChange change) => change.Kind switch
    {
        CalendarChangeKind.Added => Title("New meeting: ", change.Meeting),
        CalendarChangeKind.Moved => Title("Meeting moved: ", change.Meeting),
        _ => Title("Meeting cancelled: ", change.Meeting)
    };

    public static string ChangeBody(CalendarChange change, DateTime nowLocal)
    {
        string text = change.Kind switch
        {
            CalendarChangeKind.Moved when change.Before is not null =>
                $"now {Span(change.Meeting, nowLocal)} (was {Span(change.Before, nowLocal)})",
            CalendarChangeKind.Cancelled =>
                "was " + Span(change.Meeting, nowLocal),
            _ => Span(change.Meeting, nowLocal)
        };

        return WithLocation(text, change.Meeting);
    }

    /// <summary>Changes when the meeting moves, so a moved meeting gets a new reminder.</summary>
    public static string ReminderKey(CalendarMeeting meeting) =>
        meeting.Key + "@" + meeting.StartLocal.ToString("yyyyMMddHHmm", Invariant);

    public static IReadOnlyList<CalendarMeeting> DueReminders(
        IReadOnlyList<CalendarMeeting> meetings,
        DateTime nowLocal,
        TimeSpan lead,
        Func<string, bool> wasSent) =>
        meetings
            .Where(m => m.IsActive && !m.AllDay && !m.Free)
            .Where(m => m.StartLocal > nowLocal && m.StartLocal - nowLocal <= lead)
            .Where(m => !wasSent(ReminderKey(m)))
            .OrderBy(m => m.StartLocal)
            .ToList();

    public static string ReminderTitle(CalendarMeeting meeting, DateTime nowLocal)
    {
        int minutes = Math.Max(1, (int)Math.Ceiling((meeting.StartLocal - nowLocal).TotalMinutes));
        return Title($"In {minutes} min: ", meeting);
    }

    public static string ReminderBody(CalendarMeeting meeting) =>
        WithLocation(Times(meeting), meeting);

    public static bool IsAgendaDue(DateTime nowLocal, DateOnly? lastAgendaDate, TimeOnly agendaTime) =>
        TimeOnly.FromDateTime(nowLocal) >= agendaTime &&
        lastAgendaDate != DateOnly.FromDateTime(nowLocal);

    /// <summary>Today's meetings that have not ended yet, all-day ones first.</summary>
    public static IReadOnlyList<CalendarMeeting> AgendaMeetings(
        IReadOnlyList<CalendarMeeting> meetings,
        DateTime nowLocal)
    {
        DateTime today = nowLocal.Date;

        return meetings
            .Where(m => m.IsActive && m.EndLocal > nowLocal)
            .Where(m => m.AllDay
                ? m.StartLocal.Date <= today && m.EndLocal > today
                : m.StartLocal.Date == today)
            .OrderBy(m => m.AllDay ? 0 : 1)
            .ThenBy(m => m.StartLocal)
            .ToList();
    }

    public static string AgendaTitle(int count, DateTime nowLocal, TimeOnly agendaTime)
    {
        bool late = TimeOnly.FromDateTime(nowLocal) > agendaTime.AddMinutes(30);
        string meetings = count == 1 ? "1 meeting" : $"{count} meetings";
        return (late ? "Rest of today: " : "Today: ") + meetings;
    }

    public static string AgendaBody(IReadOnlyList<CalendarMeeting> meetings) =>
        string.Join("\n", meetings.Select(m =>
            m.AllDay
                ? "All day  " + Subject(m)
                : m.StartLocal.ToString("HH:mm", Invariant) + "  " + Subject(m) +
                  (string.IsNullOrWhiteSpace(m.Location) ? string.Empty : $" ({m.Location.Trim()})")));

    private static Dictionary<string, CalendarMeeting> ByKey(IReadOnlyList<CalendarMeeting> meetings)
    {
        var result = new Dictionary<string, CalendarMeeting>(StringComparer.Ordinal);
        foreach (CalendarMeeting meeting in meetings)
            result.TryAdd(meeting.Key, meeting);
        return result;
    }

    private static string Title(string prefix, CalendarMeeting meeting)
    {
        string full = prefix + Subject(meeting);
        return full.Length > MaxTitleLength ? full[..MaxTitleLength] : full;
    }

    private static string Subject(CalendarMeeting meeting) =>
        string.IsNullOrWhiteSpace(meeting.Subject) ? "(no subject)" : meeting.Subject.Trim();

    private static string Span(CalendarMeeting meeting, DateTime nowLocal)
    {
        string day = Day(meeting.StartLocal.Date, nowLocal.Date);
        return meeting.AllDay ? day + ", all day" : day + " " + Times(meeting);
    }

    private static string Times(CalendarMeeting meeting) =>
        meeting.StartLocal.ToString("HH:mm", Invariant) + "–" +
        meeting.EndLocal.ToString("HH:mm", Invariant);

    private static string Day(DateTime date, DateTime today)
    {
        if (date == today)
            return "today";

        if (date == today.AddDays(1))
            return "tomorrow";

        return date.ToString("ddd d MMM", Invariant);
    }

    private static string WithLocation(string text, CalendarMeeting meeting) =>
        string.IsNullOrWhiteSpace(meeting.Location) ? text : text + " · " + meeting.Location.Trim();
}
