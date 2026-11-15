namespace NotiRelay;

public sealed record BatteryAlert(int Threshold, int Percent, string Title, string Body, string Priority);

/// <summary>
/// Decides when to warn about a low laptop battery: once at 30% and once at 10% while on
/// battery. A threshold is armed again only after the battery climbs 5 points above it, so
/// readings that wobble around 30% do not repeat the alert. Starting below 10% sends only
/// the 10% alert. Platform-neutral; the battery reading is in BatteryWatcher.
/// </summary>
public sealed class BatteryAlerts
{
    public static readonly IReadOnlyList<int> Thresholds = new[] { 30, 10 };

    private const int ReArmMargin = 5;

    private readonly HashSet<int> _armed = new(Thresholds);

    /// <summary>Returns the alert to send for this reading, or null.</summary>
    public BatteryAlert? Update(int percent, bool onBattery)
    {
        if (percent is < 0 or > 100)
            return null;

        foreach (int threshold in Thresholds)
        {
            if (percent >= threshold + ReArmMargin)
                _armed.Add(threshold);
        }

        if (!onBattery)
            return null;

        int[] reached = Thresholds
            .Where(threshold => _armed.Contains(threshold) && percent <= threshold)
            .ToArray();

        if (reached.Length == 0)
            return null;

        int hit = reached.Min();

        // Passing a threshold also passes every higher one.
        foreach (int threshold in Thresholds.Where(threshold => threshold >= hit))
            _armed.Remove(threshold);

        return hit <= 10
            ? new BatteryAlert(hit, percent, $"Laptop battery critical: {percent}%",
                "Not charging. Plug in now, or the laptop will soon go to sleep.", "urgent")
            : new BatteryAlert(hit, percent, $"Laptop battery low: {percent}%",
                "Not charging. Plug in soon.", "high");
    }
}
