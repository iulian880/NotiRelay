using Windows.System.Power;

namespace NotiRelay;

/// <summary>
/// Every 30 s reads the laptop battery from Windows and queues a low-battery alert at 30% and
/// at 10% while on battery (see BatteryAlerts). Alerts go through the delivery queue, so Stop
/// sending and the launch question apply. The current level is reported for the window.
/// </summary>
public sealed class BatteryWatcher
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly DeliveryQueue _queue;
    private readonly Func<string> _getTopic;
    private readonly Action<string> _log;
    private readonly Action<string> _status;
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _cancellation;
    private Task? _task;

    // Used only by the polling loop.
    private BatteryAlerts _alerts = new();
    private string? _lastError;

    public BatteryWatcher(
        DeliveryQueue queue,
        Func<string> getTopic,
        Action<string> log,
        Action<string> status)
    {
        _queue = queue;
        _getTopic = getTopic;
        _log = log;
        _status = status;
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_task is not null)
                return;

            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;

            _alerts = new BatteryAlerts();
            _lastError = null;
            _task = Task.Run(() => RunAsync(token));
        }
    }

    public async Task StopAsync()
    {
        Task? task;
        CancellationTokenSource? cancellation;

        lock (_lifecycleLock)
        {
            task = _task;
            cancellation = _cancellation;
            _task = null;
            _cancellation = null;
        }

        if (task is null)
            return;

        cancellation?.Cancel();

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync();
                _lastError = null;
            }
            catch (Exception ex)
            {
                DebugTrace.Detail("Battery check failed", ex);

                string error = "Could not read the battery (" + ex.GetType().Name + "). Retrying.";
                if (error != _lastError)
                {
                    _lastError = error;
                    _status(error);
                }
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckOnceAsync()
    {
        BatteryStatus status = PowerManager.BatteryStatus;

        if (status == BatteryStatus.NotPresent)
        {
            _status("No battery found on this PC, so there is nothing to alert about.");
            return;
        }

        int percent = PowerManager.RemainingChargePercent;
        bool onBattery = status == BatteryStatus.Discharging;

        _status($"Battery {percent}%, {(onBattery ? "on battery" : "plugged in")}. " +
                "Alerts at 30% and 10%.");

        BatteryAlert? alert = _alerts.Update(percent, onBattery);

        if (alert is null)
            return;

        await _queue.EnqueueAsync(
            $"battery:{alert.Threshold}:{DateTime.UtcNow.Ticks}",
            alert.Body,
            alert.Title,
            alert.Priority,
            _getTopic());

        _log($"Power & battery: low-battery alert queued ({alert.Threshold}%).");
    }
}
