using NotiRelay;
using Xunit;

public class CalendarStateTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "notirelay-calendar-tests-" + Guid.NewGuid().ToString("N"));

    public CalendarStateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string StatePath => Path.Combine(_dir, "calendar-state.json");

    [Fact]
    public void SaveAndLoad_KeepAgendaDateAndSentReminders()
    {
        var now = new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);
        CalendarState state = CalendarState.Load(StatePath);
        state.LastAgendaDate = new DateOnly(2026, 11, 16);
        state.MarkReminderSent("a@202611160930", now);
        state.Save();

        CalendarState loaded = CalendarState.Load(StatePath);

        Assert.Equal(new DateOnly(2026, 11, 16), loaded.LastAgendaDate);
        Assert.True(loaded.WasReminderSent("a@202611160930"));
        Assert.False(loaded.WasReminderSent("b@202611160930"));
    }

    [Fact]
    public void Load_MissingOrCorruptFile_StartsEmpty()
    {
        Assert.Null(CalendarState.Load(StatePath).LastAgendaDate);

        File.WriteAllText(StatePath, "{ not json");
        CalendarState state = CalendarState.Load(StatePath);

        Assert.Null(state.LastAgendaDate);
        Assert.False(state.WasReminderSent("a"));
    }

    [Fact]
    public void Prune_DropsRemindersOlderThanTwoDays()
    {
        var now = new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);
        CalendarState state = CalendarState.Load(StatePath);
        state.MarkReminderSent("old", now.AddDays(-3));
        state.MarkReminderSent("recent", now.AddHours(-5));

        state.Prune(now);

        Assert.False(state.WasReminderSent("old"));
        Assert.True(state.WasReminderSent("recent"));
    }
}
