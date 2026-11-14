using NotiRelay;
using Xunit;

public class DeliveryThrottleTests
{
    private static readonly DateTime T0 = new(2026, 11, 14, 23, 49, 22, DateTimeKind.Utc);

    [Fact]
    public void FirstRateLimit_StartsSlowMode_WithOneLogLine()
    {
        var throttle = new DeliveryThrottle();

        string? line = throttle.RecordRateLimited(143, T0);

        Assert.True(throttle.IsActive);
        Assert.Equal(T0.AddSeconds(5), throttle.NextSendUtc);
        Assert.Equal(
            "ntfy.sh is limiting how fast notifications can be sent (429). " +
            "Sending one every 5 s; 143 waiting.",
            line);
    }

    [Fact]
    public void WhileSlow_SendsAndLimitsAreSilent_AndPaceStaysFiveSeconds()
    {
        var throttle = new DeliveryThrottle();
        throttle.RecordRateLimited(143, T0);

        string? sent = throttle.RecordSent(142, T0.AddSeconds(5));
        string? limited = throttle.RecordRateLimited(142, T0.AddSeconds(5.1));
        string? sentAgain = throttle.RecordSent(141, T0.AddSeconds(10));

        Assert.Null(sent);
        Assert.Null(limited);
        Assert.Null(sentAgain);
        Assert.Equal(T0.AddSeconds(15), throttle.NextSendUtc);
    }

    [Fact]
    public void ConsecutiveRateLimits_DoubleTheWait_UpToFiveMinutes()
    {
        var throttle = new DeliveryThrottle();

        throttle.RecordRateLimited(10, T0);
        Assert.Equal(T0.AddSeconds(5), throttle.NextSendUtc);

        throttle.RecordRateLimited(10, T0);
        Assert.Equal(T0.AddSeconds(10), throttle.NextSendUtc);

        throttle.RecordRateLimited(10, T0);
        Assert.Equal(T0.AddSeconds(20), throttle.NextSendUtc);

        for (int i = 0; i < 20; i++)
            throttle.RecordRateLimited(10, T0);

        Assert.Equal(T0.AddSeconds(300), throttle.NextSendUtc);
    }

    [Fact]
    public void SuccessResetsTheDoubling()
    {
        var throttle = new DeliveryThrottle();
        throttle.RecordRateLimited(10, T0);
        throttle.RecordRateLimited(10, T0);
        throttle.RecordSent(9, T0);

        throttle.RecordRateLimited(9, T0);

        Assert.Equal(T0.AddSeconds(5), throttle.NextSendUtc);
    }

    [Fact]
    public void QueueEmpty_EndsSlowMode_WithOneLogLine()
    {
        var throttle = new DeliveryThrottle();
        throttle.RecordRateLimited(1, T0);

        string? line = throttle.RecordSent(0, T0.AddSeconds(5));

        Assert.False(throttle.IsActive);
        Assert.Equal(DateTime.MinValue, throttle.NextSendUtc);
        Assert.Equal("All waiting notifications sent; back to normal speed.", line);
    }

    [Fact]
    public void NotSlow_SendIsSilentAndUnpaced()
    {
        var throttle = new DeliveryThrottle();

        Assert.Null(throttle.RecordSent(0, T0));
        Assert.Equal(DateTime.MinValue, throttle.NextSendUtc);
    }
}
