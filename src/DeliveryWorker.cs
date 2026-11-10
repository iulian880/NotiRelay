
using System.Net.Http;

namespace NotiRelay;

public sealed class DeliveryWorker
{
    private readonly DeliveryQueue _queue;
    private readonly HttpClient _httpClient;
    private readonly Action<string> _log;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _lifecycleLock = new();

    private Task? _workerTask;
    private bool _started;

    public DeliveryWorker(
        DeliveryQueue queue,
        HttpClient httpClient,
        Action<string> log)
    {
        _queue = queue;
        _httpClient = httpClient;
        _log = log;
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
                    var item = await _queue.GetNextDueAsync();

                    if (item is null)
                    {
                        await Task.Delay(
                            1000,
                            cancellationToken);

                        continue;
                    }

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

                        int pending =
                            await _queue.GetPendingCountAsync();

                        Report(
                            $"Notification delivered successfully. " +
                            $"Pending: {pending}.");
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

                        await _queue.MarkFailedAsync(
                            item.Id,
                            ex.GetType().Name);

                        Report(
                            $"Delivery failed. Attempt {nextAttempt}. " +
                            $"Automatic retry scheduled. " +
                            $"Error: {ex.GetType().Name}");
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
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
