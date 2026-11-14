namespace NotiRelay;

/// <summary>
/// While ntfy.sh is unreachable, the delivery worker pauses the whole queue instead of
/// trying (and logging) every waiting item in turn. One probe per pause, the pause
/// doubling from 5 s up to 5 min; the log gets one line when the outage starts or its
/// reason changes, and one when delivery recovers.
/// </summary>
public sealed class DeliveryOutage
{
    private const double FirstPauseSeconds = 5;
    private const double MaxPauseSeconds = 300;

    private int _consecutiveFailures;
    private string? _lastReason;

    public DateTime PausedUntilUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Returns a log line, or null when this failure adds nothing new.</summary>
    public string? RecordFailure(string reason, int pendingCount, DateTime nowUtc)
    {
        _consecutiveFailures++;

        double pause = Math.Min(
            MaxPauseSeconds,
            FirstPauseSeconds * Math.Pow(2, Math.Min(_consecutiveFailures - 1, 16)));

        PausedUntilUtc = nowUtc.AddSeconds(pause);

        bool isNews = _consecutiveFailures == 1 || reason != _lastReason;
        _lastReason = reason;

        if (!isNews)
            return null;

        string waiting = pendingCount == 1
            ? "1 notification waiting"
            : $"{pendingCount} notifications waiting";

        return $"Delivery paused: {reason}. {waiting}; retrying automatically.";
    }

    /// <summary>Returns a log line when this success ends an outage.</summary>
    public string? RecordSuccess()
    {
        if (_consecutiveFailures == 0)
            return null;

        _consecutiveFailures = 0;
        _lastReason = null;
        PausedUntilUtc = DateTime.MinValue;

        return "ntfy.sh reachable again; delivering waiting notifications.";
    }
}
