using NotiRelay;
using Xunit;

public class CalendarNotificationsTests
{
    // Monday 16 Nov 2026, 09:00 local.
    private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0);
    private static readonly DateTime WindowEnd = Now.Date.AddDays(7);
    private static readonly TimeSpan Lead = TimeSpan.FromMinutes(10);

    private static CalendarMeeting M(
        string key, DateTime start, int minutes = 30, string subject = "Standup",
        string location = "", bool allDay = false, bool cancelled = false,
        bool declined = false, bool free = false) =>
        new(key, subject, start, start.AddMinutes(minutes), location, allDay, cancelled, declined, free);

    private static IReadOnlyList<CalendarChange> Diff(
        IReadOnlyList<CalendarMeeting> before, IReadOnlyList<CalendarMeeting> after) =>
        CalendarNotifications.Diff(before, after, Now, WindowEnd);

    [Fact]
    public void Diff_NewFutureMeeting_IsAdded()
    {
        var added = M("a", Now.AddHours(2));

        CalendarChange change = Assert.Single(Diff(Array.Empty<CalendarMeeting>(), new[] { added }));

        Assert.Equal(CalendarChangeKind.Added, change.Kind);
        Assert.Equal(added, change.Meeting);
    }

    [Fact]
    public void Diff_MeetingEnteringTheWindowAtMidnight_IsNotAdded()
    {
        var newLastDay = M("a", WindowEnd.AddHours(9));

        Assert.Empty(Diff(Array.Empty<CalendarMeeting>(), new[] { newLastDay }));
    }

    [Fact]
    public void Diff_NewPastDeclinedOrCancelledMeeting_IsNotAdded()
    {
        var after = new[]
        {
            M("past", Now.AddHours(-2)),
            M("declined", Now.AddHours(2), declined: true),
            M("cancelled", Now.AddHours(3), cancelled: true)
        };

        Assert.Empty(Diff(Array.Empty<CalendarMeeting>(), after));
    }

    [Fact]
    public void Diff_TimeChange_IsMoved_WithThePreviousTime()
    {
        var before = M("a", Now.AddHours(2));
        var after = before with { StartLocal = Now.AddDays(1), EndLocal = Now.AddDays(1).AddMinutes(30) };

        CalendarChange change = Assert.Single(Diff(new[] { before }, new[] { after }));

        Assert.Equal(CalendarChangeKind.Moved, change.Kind);
        Assert.Equal(before, change.Before);
        Assert.Equal(after, change.Meeting);
    }

    [Fact]
    public void Diff_CancelledFlag_IsCancelled()
    {
        var before = M("a", Now.AddHours(2));

        CalendarChange change = Assert.Single(
            Diff(new[] { before }, new[] { before with { Cancelled = true } }));

        Assert.Equal(CalendarChangeKind.Cancelled, change.Kind);
    }

    [Fact]
    public void Diff_MeetingThatDisappears_IsNotReported()
    {
        // Declining in classic Outlook, deleting it, or moving it more than 7 days out all
        // remove it from the read; only Outlook's own "Canceled" flag means cancelled.
        var future = M("future", Now.AddHours(2));
        var yesterday = M("yesterday", Now.AddDays(-1));

        Assert.Empty(Diff(new[] { future, yesterday }, Array.Empty<CalendarMeeting>()));
    }

    [Fact]
    public void OccurrenceKey_UsesTheOriginalDateForRecurringMeetings()
    {
        var tuesday = new DateTime(2026, 11, 17, 9, 0, 0);
        var wednesday = new DateTime(2026, 11, 18, 9, 0, 0);

        Assert.Equal("id", CalendarNotifications.OccurrenceKey("id", recurring: false, tuesday));
        Assert.Equal("id#20261117", CalendarNotifications.OccurrenceKey("id", recurring: true, tuesday));

        // This week's Tuesday occurrence moved to Wednesday keeps its key, so it reads as moved.
        Assert.Equal(
            CalendarNotifications.OccurrenceKey("id", recurring: true, tuesday),
            CalendarNotifications.OccurrenceKey("id", recurring: true, wednesday, originalStartLocal: tuesday));
    }

    [Fact]
    public void Diff_UnchangedOrDeclinedByMe_ReportsNothing()
    {
        var meeting = M("a", Now.AddHours(2));

        Assert.Empty(Diff(new[] { meeting }, new[] { meeting }));
        Assert.Empty(Diff(new[] { meeting }, new[] { meeting with { Declined = true } }));
    }

    [Fact]
    public void ChangeTexts_DescribeWhatHappened()
    {
        var before = M("a", new DateTime(2026, 11, 16, 14, 0, 0), 60, "Sprint Review", "Room 4");
        var moved = before with
        {
            StartLocal = new DateTime(2026, 11, 17, 10, 0, 0),
            EndLocal = new DateTime(2026, 11, 17, 11, 0, 0)
        };

        var added = new CalendarChange(CalendarChangeKind.Added, before, null);
        var movedChange = new CalendarChange(CalendarChangeKind.Moved, moved, before);
        var cancelled = new CalendarChange(CalendarChangeKind.Cancelled, moved, null);

        Assert.Equal("New meeting: Sprint Review", CalendarNotifications.ChangeTitle(added));
        Assert.Equal("today 14:00–15:00 · Room 4", CalendarNotifications.ChangeBody(added, Now));

        Assert.Equal("Meeting moved: Sprint Review", CalendarNotifications.ChangeTitle(movedChange));
        Assert.Equal(
            "now tomorrow 10:00–11:00 (was today 14:00–15:00) · Room 4",
            CalendarNotifications.ChangeBody(movedChange, Now));

        Assert.Equal("Meeting cancelled: Sprint Review", CalendarNotifications.ChangeTitle(cancelled));
        Assert.Equal("was tomorrow 10:00–11:00 · Room 4", CalendarNotifications.ChangeBody(cancelled, Now));
    }

    [Fact]
    public void ChangeBody_LaterDayAndAllDay()
    {
        var thursday = M("a", new DateTime(2026, 11, 19, 8, 30, 0), 30, "Planning");
        var offsite = M("b", new DateTime(2026, 11, 20, 0, 0, 0), 24 * 60, "Offsite", allDay: true);

        Assert.Equal("Thu 19 Nov 08:30–09:00",
            CalendarNotifications.ChangeBody(new CalendarChange(CalendarChangeKind.Added, thursday, null), Now));
        Assert.Equal("Fri 20 Nov, all day",
            CalendarNotifications.ChangeBody(new CalendarChange(CalendarChangeKind.Added, offsite, null), Now));
    }

    [Fact]
    public void DueReminders_OnlyActiveTimedMeetingsStartingWithinTheLead_NotSentBefore()
    {
        var soon = M("soon", Now.AddMinutes(10));
        var later = M("later", Now.AddMinutes(11));
        var started = M("started", Now.AddMinutes(-1));
        var alreadySent = M("sent", Now.AddMinutes(5));
        var meetings = new[]
        {
            soon, later, started, alreadySent,
            M("allday", Now.AddMinutes(5), allDay: true),
            M("free", Now.AddMinutes(5), free: true),
            M("declined", Now.AddMinutes(5), declined: true),
            M("cancelled", Now.AddMinutes(5), cancelled: true)
        };
        string sentKey = CalendarNotifications.ReminderKey(alreadySent);

        IReadOnlyList<CalendarMeeting> due =
            CalendarNotifications.DueReminders(meetings, Now, Lead, key => key == sentKey);

        Assert.Equal(new[] { "soon" }, due.Select(m => m.Key));
    }

    [Fact]
    public void ReminderKey_ChangesWhenTheMeetingMoves()
    {
        var meeting = M("a", Now.AddHours(1));

        Assert.NotEqual(
            CalendarNotifications.ReminderKey(meeting),
            CalendarNotifications.ReminderKey(meeting with { StartLocal = Now.AddHours(2) }));
    }

    [Fact]
    public void ReminderTexts_RoundMinutesUp()
    {
        var meeting = M("a", Now.AddMinutes(2.5), 30, "Standup", "Teams");

        Assert.Equal("In 3 min: Standup", CalendarNotifications.ReminderTitle(meeting, Now));
        Assert.Equal("09:02–09:32 · Teams", CalendarNotifications.ReminderBody(meeting));
        Assert.Equal("In 10 min: Standup",
            CalendarNotifications.ReminderTitle(M("b", Now.AddMinutes(10)), Now));
    }

    [Fact]
    public void IsAgendaDue_OncePerDayFromTheAgendaTime()
    {
        var agendaTime = new TimeOnly(8, 0);
        DateOnly today = DateOnly.FromDateTime(Now);

        Assert.False(CalendarNotifications.IsAgendaDue(Now.Date.AddHours(7.9), null, agendaTime));
        Assert.True(CalendarNotifications.IsAgendaDue(Now, null, agendaTime));
        Assert.True(CalendarNotifications.IsAgendaDue(Now, today.AddDays(-1), agendaTime));
        Assert.False(CalendarNotifications.IsAgendaDue(Now, today, agendaTime));
    }

    [Fact]
    public void Agenda_ListsTheRestOfTodayInOrder()
    {
        var meetings = new[]
        {
            M("late", Now.Date.AddHours(14), 60, "1:1", "Room 2"),
            M("ended", Now.Date.AddHours(8), 30, "Early sync"),
            M("now", Now.Date.AddHours(9).AddMinutes(30), 15, "Standup"),
            M("allday", Now.Date, 24 * 60, "Office day", allDay: true),
            M("tomorrow", Now.Date.AddDays(1).AddHours(9), 30, "Planning"),
            M("declined", Now.Date.AddHours(11), 30, "Optional", declined: true)
        };

        IReadOnlyList<CalendarMeeting> agenda = CalendarNotifications.AgendaMeetings(meetings, Now);

        Assert.Equal(new[] { "allday", "now", "late" }, agenda.Select(m => m.Key));
        Assert.Equal(
            "All day  Office day\n09:30  Standup\n14:00  1:1 (Room 2)",
            CalendarNotifications.AgendaBody(agenda));
    }

    [Fact]
    public void AgendaTitle_SaysRestOfTodayWhenSentLate()
    {
        var agendaTime = new TimeOnly(8, 0);

        Assert.Equal("Today: 3 meetings",
            CalendarNotifications.AgendaTitle(3, Now.Date.AddHours(8.1), agendaTime));
        Assert.Equal("Rest of today: 1 meeting",
            CalendarNotifications.AgendaTitle(1, Now.Date.AddHours(13), agendaTime));
    }
}
