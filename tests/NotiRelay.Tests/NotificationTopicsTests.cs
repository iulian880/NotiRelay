using NotiRelay;
using Xunit;

public class NotificationTopicsTests
{
    private const string Main = "main-topic-1234";

    [Fact]
    public void Resolve_UsesTheCategoryTopic_OrFallsBackToMain()
    {
        var overrides = new Dictionary<NotificationCategory, string>
        {
            [NotificationCategory.Calendar] = "meetings-topic-99",
            [NotificationCategory.Battery] = "   "
        };

        Assert.Equal("meetings-topic-99",
            NotificationTopics.Resolve(NotificationCategory.Calendar, Main, overrides));
        Assert.Equal(Main, NotificationTopics.Resolve(NotificationCategory.Teams, Main, overrides));
        Assert.Equal(Main, NotificationTopics.Resolve(NotificationCategory.Battery, Main, overrides));
    }

    [Fact]
    public void DistinctTargets_GroupsCategoriesSharingATopic()
    {
        var overrides = new Dictionary<NotificationCategory, string>
        {
            [NotificationCategory.Calendar] = "meetings-topic-99",
            [NotificationCategory.Battery] = "battery-topic-77",
            [NotificationCategory.Outlook] = Main
        };

        IReadOnlyList<TopicTarget> targets = NotificationTopics.DistinctTargets(Main, overrides);

        Assert.Equal(3, targets.Count);
        Assert.Equal(Main, targets[0].Topic);
        Assert.Equal(
            new[] { NotificationCategory.Teams, NotificationCategory.Outlook },
            targets[0].Categories);
        Assert.Equal("meetings-topic-99", targets[1].Topic);
        Assert.Equal("battery-topic-77", targets[2].Topic);
    }

    [Fact]
    public void TestTitle_NamesTheCategories()
    {
        var target = new TopicTarget(Main, new[] { NotificationCategory.Teams, NotificationCategory.Outlook });

        Assert.Equal("NOTIRELAY TEST · Teams messages, Outlook notifications",
            NotificationTopics.TestTitle(target));
    }

    [Fact]
    public void DisplayNames_MatchTheWindow()
    {
        Assert.Equal("Meetings", NotificationTopics.DisplayName(NotificationCategory.Calendar));
        Assert.Equal("Power & battery", NotificationTopics.DisplayName(NotificationCategory.Battery));
    }
}
