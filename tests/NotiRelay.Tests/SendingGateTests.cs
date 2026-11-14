using NotiRelay;
using Xunit;

public class SendingGateTests
{
    [Fact]
    public void NewGate_IsOpen()
    {
        Assert.True(new SendingGate().IsOpen);
    }

    [Fact]
    public void Hold_ClosesUntilEveryReasonIsReleased()
    {
        var gate = new SendingGate();

        gate.Hold(SendingHold.LaunchQuestion);
        gate.Hold(SendingHold.StoppedByUser);
        gate.Release(SendingHold.LaunchQuestion);

        Assert.False(gate.IsOpen);
        Assert.True(gate.IsHeld(SendingHold.StoppedByUser));
        Assert.False(gate.IsHeld(SendingHold.LaunchQuestion));

        gate.Release(SendingHold.StoppedByUser);

        Assert.True(gate.IsOpen);
    }

    [Fact]
    public void HoldTwice_ReleaseOnce_Opens()
    {
        var gate = new SendingGate();

        gate.Hold(SendingHold.StoppedByUser);
        gate.Hold(SendingHold.StoppedByUser);
        gate.Release(SendingHold.StoppedByUser);

        Assert.True(gate.IsOpen);
    }

    [Fact]
    public void ConcurrentHoldAndRelease_DoNotLoseReasons()
    {
        var gate = new SendingGate();
        gate.Hold(SendingHold.StoppedByUser);

        Parallel.For(0, 10_000, i =>
        {
            gate.Hold(SendingHold.LaunchQuestion);
            gate.Release(SendingHold.LaunchQuestion);
        });

        Assert.True(gate.IsHeld(SendingHold.StoppedByUser));
        Assert.False(gate.IsHeld(SendingHold.LaunchQuestion));
    }

    [Fact]
    public void Debounce_IgnoresASecondClickInsideTheWindow()
    {
        var debounce = new ActionDebounce(TimeSpan.FromMilliseconds(700));
        var t0 = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(debounce.TryAccept(t0));
        Assert.False(debounce.TryAccept(t0.AddMilliseconds(250)));
        Assert.True(debounce.TryAccept(t0.AddMilliseconds(1000)));
    }
}
