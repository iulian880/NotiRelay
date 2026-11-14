using System.Diagnostics;
using System.Net.Http;

namespace NotiRelay;

public sealed class DeliveryWorker
{
    private readonly DeliveryQueue _queue;
    private readonly HttpClient _httpClient;
    private readonly Action<string> _log;
    private readonly SendingGate _gate;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _lifecycleLock = new();

    // Used only by the worker loop.
    private readonly DeliveryOutage _outage = new();
    private readonly DeliveryThrottle _throttle = new();

    private Task? _workerTask;
    private bool _started;

    public DeliveryWorker(
        DeliveryQueue queue,
        HttpClient httpClient,
        Action<string> log,
        SendingGate gate)
    {
        _queue = queue;
        _httpClient = httpClient;
        _log = log;
        _gate = gate;
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_started)
                return;

            if (_cancellation.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "A stopped delivery worker cannot be restarted.");
            }

            _started = true;
            _workerTask = ProcessQueueAsync(_cancellation.Token);
        }

        Report("Delivery worker started.");
    }

    // Backward-compatible cancellation request.
    // New application shutdown code should use StopAsync().
    public void Stop()
    {
        _cancellation.Cancel();
    }

    // Requests cancellation and waits until the worker has actually stopped.
    public async Task StopAsync()
    {
        Task? workerTask;

        lock (_lifecycleLock)
        {
            _cancellation.Cancel();
            workerTask = _workerTask;
        }

        if (workerTask is not null)
        {
            try
            {
                await workerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }
    }

    private async Task ProcessQueueAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Held (launch question or Stop sending), ntfy.sh unreachable, or pacing
                    // after a 429: wait instead of taking the next item.
                    if (!_gate.IsOpen ||
                        DateTime.UtcNow < _outage.PausedUntilUtc ||
                        DateTime.UtcNow < _throttle.NextSendUtc)
                    {
                        await Task.Delay(250, cancellationToken);
                        continue;
                    }

                    var item = await _queue.GetNextDueAsync();

                    if (item is null)
                    {
                        await Task.Delay(
                            1000,
                            cancellationToken);

                        continue;
                    }

                    var watch = Stopwatch.StartNew();

                    try
                    {
                        var client = new NtfyClient(
                            item.Topic,
                            _httpClient);

                        await client.SendMessageAsync(
                            item.Message,
                            item.Title,
                            item.Priority,
                            cancellationToken);

                        await _queue.MarkSentAsync(item.Id);

                        DebugTrace.Line(
                            $"Delivered queue item {item.Id} in {watch.ElapsedMilliseconds} ms " +
                            $"(attempt {item.Attempts + 1}).");

                        string? recovered = _outage.RecordSuccess();

                        if (recovered is not null)
                            Report(recovered);

                        int pending =
                            await _queue.GetPendingCountAsync();

                        Report(
                            $"Notification delivered successfully. " +
                            $"Pending: {pending}.");

                        string? normalSpeed = _throttle.RecordSent(pending, DateTime.UtcNow);

                        if (normalSpeed is not null)
                            Report(normalSpeed);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        // Keep the item in the persistent queue.
                        // It can be retried after the next application start.
                        break;
                    }
                    catch (Exception ex)
                    {
                        int nextAttempt = item.Attempts + 1;
                        string reason = NtfyErrors.Describe(ex);

                        DebugTrace.Detail(
                            $"Delivery attempt {nextAttempt} for queue item {item.Id} failed " +
                            $"after {watch.ElapsedMilliseconds} ms " +
                            $"(affects the whole queue: {NtfyErrors.IsOutage(ex)})",
                            ex);

                        if (NtfyErrors.IsRateLimit(ex))
                        {
                            // Not the message's fault: keep it first in line and slow down.
                            string? slowDown = _throttle.RecordRateLimited(
                                await _queue.GetPendingCountAsync(),
                                DateTime.UtcNow);

                            if (slowDown is not null)
                                Report(slowDown);

                            continue;
                        }

                        await _queue.MarkFailedAsync(item.Id, reason);

                        if (NtfyErrors.IsOutage(ex))
                        {
                            // One line when the outage starts or changes, not one per item.
                            string? line = _outage.RecordFailure(
                                reason,
                                await _queue.GetPendingCountAsync(),
                                DateTime.UtcNow);

                            if (line is not null)
                                Report(line);

                            DebugTrace.Line(
                                "Delivery paused until " +
                                $"{_outage.PausedUntilUtc.ToLocalTime():HH:mm:ss}.");
                        }
                        else
                        {
                            Report(
                                $"Delivery rejected (attempt {nextAttempt}): {reason}. " +
                                "Retrying later.");
                        }
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DebugTrace.Detail("Delivery worker error", ex);

                    Report(
                        $"Delivery worker error: {ex.GetType().Name}");

                    await Task.Delay(
                        5000,
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            Report("Delivery worker stopped.");
        }
    }

    private void Report(string message)
    {
        try
        {
            _log(message);
        }
        catch
        {
            // Logging must never stop notification delivery.
        }
    }
}
