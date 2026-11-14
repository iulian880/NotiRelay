namespace NotiRelay;

/// <summary>
/// Paces sending after ntfy.sh answers 429 (Too Many Requests): ntfy allows a burst and then
/// about one message every few seconds, so instead of failing and pausing on every item the
/// worker keeps the item and sends one every 5 s until the queue is empty. While every try is
/// refused (e.g. a daily quota) the wait doubles up to 5 minutes. One log line when slow mode
/// starts and one when it ends. Used only by the delivery worker loop.
/// </summary>
public sealed class DeliveryThrottle
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private const double MaxWaitSeconds = 300;

    private int _consecutiveLimited;

    public bool IsActive { get; private set; }

    /// <summary>The worker sends nothing before this moment.</summary>
    public DateTime NextSendUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Records a 429. Returns a log line when slow mode starts, otherwise null.</summary>
    public string? RecordRateLimited(int pendingCount, DateTime nowUtc)
    {
        _consecutiveLimited++;

        double wait = Math.Min(
            MaxWaitSeconds,
            Interval.TotalSeconds * Math.Pow(2, Math.Min(_consecutiveLimited - 1, 16)));

        NextSendUtc = nowUtc.AddSeconds(wait);

        if (IsActive)
            return null;

        IsActive = true;

        return "ntfy.sh is limiting how fast notifications can be sent (429). " +
               $"Sending one every {Interval.TotalSeconds:0} s; {pendingCount} waiting.";
    }

    /// <summary>Records a delivered item. Returns a log line when slow mode ends.</summary>
    public string? RecordSent(int pendingCount, DateTime nowUtc)
    {
        _consecutiveLimited = 0;

        if (!IsActive)
            return null;

        if (pendingCount > 0)
        {
            NextSendUtc = nowUtc + Interval;
            return null;
        }

        IsActive = false;
        NextSendUtc = DateTime.MinValue;

        return "All waiting notifications sent; back to normal speed.";
    }
}
