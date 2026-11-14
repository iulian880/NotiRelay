using NotiRelay;
using Xunit;

public class DeliveryOutageTests
{
    private static readonly DateTime T = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    private const string Reason = "could not reach ntfy.sh: No such host is known. (ntfy.sh:443)";

    [Fact]
    public void FirstFailure_LogsTheReason_AndPausesFiveSeconds()
    {
        var outage = new DeliveryOutage();

        string? line = outage.RecordFailure(Reason, 9, T);

        Assert.Equal(
            "Delivery paused: " + Reason + ". 9 notifications waiting; retrying automatically.",
            line);
        Assert.Equal(T.AddSeconds(5), outage.PausedUntilUtc);
    }

    [Fact]
    public void RepeatedSameFailure_IsSilent_AndBacksOffUpToFiveMinutes()
    {
        var outage = new DeliveryOutage();
        outage.RecordFailure(Reason, 9, T);

        Assert.Null(outage.RecordFailure(Reason, 9, T));
        Assert.Equal(T.AddSeconds(10), outage.PausedUntilUtc);

        for (int i = 0; i < 20; i++)
            Assert.Null(outage.RecordFailure(Reason, 9, T));

        Assert.Equal(T.AddMinutes(5), outage.PausedUntilUtc);
    }

    [Fact]
    public void DifferentReason_IsLoggedAgain()
    {
        var outage = new DeliveryOutage();
        outage.RecordFailure(Reason, 9, T);

        Assert.Equal(
            "Delivery paused: ntfy.sh did not answer within 15 seconds. 1 notification waiting; retrying automatically.",
            outage.RecordFailure("ntfy.sh did not answer within 15 seconds", 1, T));
    }

    [Fact]
    public void SuccessAfterOutage_LogsRecovery_AndResets()
    {
        var outage = new DeliveryOutage();
        outage.RecordFailure(Reason, 9, T);
        outage.RecordFailure(Reason, 9, T);

        Assert.Equal("ntfy.sh reachable again; delivering waiting notifications.", outage.RecordSuccess());
        Assert.Equal(DateTime.MinValue, outage.PausedUntilUtc);
        Assert.NotNull(outage.RecordFailure(Reason, 9, T));
        Assert.Equal(T.AddSeconds(5), outage.PausedUntilUtc);
    }

    [Fact]
    public void SuccessWithoutOutage_IsSilent()
    {
        Assert.Null(new DeliveryOutage().RecordSuccess());
    }
}
