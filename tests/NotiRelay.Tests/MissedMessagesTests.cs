using NotiRelay;
using Xunit;

public class MissedMessagesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    // Saturday 14 Nov 2026, 18:00 local.
    private static readonly DateTime Now = new(2026, 11, 14, 18, 0, 0);

    private static TeamsInboxMessage Msg(string id, DateTimeOffset? created, string conversation = "c1") =>
        new(id, conversation, "Ana", "s1", "hello " + id, created, "", "message");

    private static readonly HashSet<string> NoNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> NoKeys = new(StringComparer.Ordinal);

    [Fact]
    public void MessageKey_CombinesConversationAndMessageId()
    {
        Assert.Equal("c1:m1", MissedMessages.MessageKey(Msg("m1", null)));
    }

    [Fact]
    public void SelectUnsent_SkipsBaselineAndProcessed_OldestFirst()
    {
        var files = new[]
        {
            ("b.json", Msg("m2", T0.AddMinutes(5)), T0),
            ("a.json", Msg("m1", T0.AddMinutes(1)), T0),
            ("BASE.json", Msg("m0", T0), T0),
            ("done.json", Msg("m3", T0), T0),
        };
        var baseline = new HashSet<string>(new[] { "base.json" }, StringComparer.OrdinalIgnoreCase);
        var processed = new HashSet<string>(new[] { "c1:m3" }, StringComparer.Ordinal);

        var missed = MissedMessages.SelectUnsent(files, baseline, processed);

        Assert.Equal(new[] { "c1:m1", "c1:m2" }, missed.Select(m => m.Key));
    }

    [Fact]
    public void SelectUnsent_UsesFileTimeWhenMessageHasNoTimestamp()
    {
        var files = new[]
        {
            ("x.json", Msg("late", null), T0.AddMinutes(10)),
            ("y.json", Msg("early", T0.AddMinutes(3)), T0),
        };

        var missed = MissedMessages.SelectUnsent(files, NoNames, NoKeys);

        Assert.Equal(new[] { "c1:early", "c1:late" }, missed.Select(m => m.Key));
        Assert.Equal(T0.AddMinutes(10), missed[1].SortTimeUtc);
    }

    [Fact]
    public void SelectUnsent_MergesFilesThatCarryTheSameMessage()
    {
        var files = new[]
        {
            ("one.json", Msg("m1", T0), T0),
            ("copy.json", Msg("m1", T0), T0),
        };

        var missed = Assert.Single(MissedMessages.SelectUnsent(files, NoNames, NoKeys));

        Assert.Equal(new[] { "one.json", "copy.json" }, missed.FileNames);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void IsMissedOutlook_OnlyAfterLastActive(int minutesAfter, bool expected)
    {
        Assert.Equal(expected, MissedMessages.IsMissedOutlook(T0.AddMinutes(minutesAfter), T0));
    }

    [Fact]
    public void IsMissedOutlook_FalseWhenNeverRanBefore()
    {
        Assert.False(MissedMessages.IsMissedOutlook(T0, null));
    }

    [Theory]
    [InlineData("2026-11-14 09:14", "today 09:14")]
    [InlineData("2026-11-14 19:30", "today 19:30")]
    [InlineData("2026-11-13 23:05", "yesterday 23:05")]
    [InlineData("2026-11-12 09:14", "Thu 09:14")]
    [InlineData("2026-11-08 09:14", "Sun 09:14")]
    [InlineData("2026-11-07 09:14", "7 Nov 09:14")]
    [InlineData("2025-12-31 09:14", "31 Dec 2025 09:14")]
    public void FormatWhen_IsRelativeToToday(string local, string expected)
    {
        Assert.Equal(expected, MissedMessages.FormatWhen(DateTime.Parse(local), Now));
    }

    [Fact]
    public void Describe_ListsBothSourcesAndOldest()
    {
        Assert.Equal(
            "12 Teams messages · 3 Outlook notifications · oldest from Thu 09:14",
            MissedMessages.Describe(12, 3, new DateTime(2026, 11, 12, 9, 14, 0), Now));
    }

    [Fact]
    public void Describe_UsesSingularAndOmitsEmptyParts()
    {
        Assert.Equal(
            "1 Teams message · oldest from today 09:00",
            MissedMessages.Describe(1, 0, new DateTime(2026, 11, 14, 9, 0, 0), Now));
        Assert.Equal("1 Outlook notification", MissedMessages.Describe(0, 1, null, Now));
    }

    [Fact]
    public void Title_IsPrefixedAndCapped()
    {
        Assert.Equal("Missed · Ana Pop", MissedMessages.Title("Ana Pop"));
        Assert.Equal(120, MissedMessages.Title(new string('x', 200)).Length);
    }

    [Fact]
    public void Body_StartsWithOriginalTime()
    {
        Assert.Equal("[today 09:14] hi", MissedMessages.Body("hi", new DateTime(2026, 11, 14, 9, 14, 0), Now));
        Assert.Equal("hi", MissedMessages.Body("hi", null, Now));
    }
}
