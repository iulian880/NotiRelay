using System.IO;

namespace NotiRelay;

/// <summary>
/// Every 15 s reads the classic Outlook calendar (today to +7 days) and queues: changes
/// (new / moved / cancelled), today's agenda once a day from 08:45, and a reminder 10 minutes
/// before each meeting. The first read after start only records what exists. Everything goes
/// through the delivery queue, so Stop sending and the launch question apply. The activity log
/// never shows meeting titles.
/// </summary>
public sealed class CalendarWatcher
{
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ReminderLead = TimeSpan.FromMinutes(10);
    public static readonly TimeOnly AgendaTime = new(8, 45);
    public const int WindowDays = 7;

    private readonly DeliveryQueue _queue;
    private readonly Func<string> _getTopic;
    private readonly Action<string> _log;
    private readonly OutlookCalendarReader _reader = new();
    private readonly string _statePath;
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _cancellation;
    private Task? _task;

    // Used only by the scan loop.
    private IReadOnlyList<CalendarMeeting>? _previous;
    private DateTime _previousWindowEnd;
    private string? _lastStatus;
    private CalendarState? _state;

    public CalendarWatcher(DeliveryQueue queue, Func<string> getTopic, Action<string> log)
    {
        _queue = queue;
        _getTopic = getTopic;
        _log = log;
        _statePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NotiRelay",
            "calendar-state.json");
    }

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
                return _task is not null;
        }
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_task is not null)
                return;

            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;

            _previous = null;
            _lastStatus = null;
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
        _state ??= CalendarState.Load(_statePath);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync();
            }
            catch (Exception ex)
            {
                DebugTrace.Detail("Outlook calendar read failed", ex);
                ReportStatus("could not read the Outlook calendar (" + ex.GetType().Name +
                             "). Retrying every 15 s.");
            }

            try
            {
                await Task.Delay(ScanInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ScanOnceAsync()
    {
        DateTime now = DateTime.Now;
        DateTime from = now.Date;
        DateTime to = from.AddDays(WindowDays);

        IReadOnlyList<CalendarMeeting>? meetings = _reader.ReadMeetings(from, to);

        if (meetings is null)
        {
            ReportStatus(_reader.UnavailableReason + " Waiting for it.");
            return;
        }

        ReportStatus($"watching the Outlook calendar (next {WindowDays} days).");
        DebugTrace.Line($"Outlook calendar read: {meetings.Count} entries.");

        // An empty read right after a full one is more likely Outlook starting or closing
        // than a cleared calendar; keeping the last read avoids a burst of "new meeting"
        // notifications when the entries come back.
        if (meetings.Count == 0 && _previous is { Count: > 0 })
        {
            DebugTrace.Line("Outlook calendar returned no entries; keeping the previous read.");
            return;
        }

        if (_previous is not null)
        {
            foreach (CalendarChange change in
                     CalendarNotifications.Diff(_previous, meetings, now, _previousWindowEnd))
            {
                await EnqueueAsync(
                    $"cal:{change.Kind}:{change.Meeting.Key}:{DateTime.UtcNow.Ticks}",
                    CalendarNotifications.ChangeBody(change, now),
                    CalendarNotifications.ChangeTitle(change),
                    "high");

                _log($"Calendar: meeting {change.Kind.ToString().ToLowerInvariant()}, notification queued.");
            }
        }

        _previous = meetings;
        _previousWindowEnd = to;

        CalendarState state = _state!;
        bool changed = false;

        if (CalendarNotifications.IsAgendaDue(now, state.LastAgendaDate, AgendaTime))
        {
            IReadOnlyList<CalendarMeeting> agenda = CalendarNotifications.AgendaMeetings(meetings, now);

            if (agenda.Count > 0)
            {
                await EnqueueAsync(
                    $"cal:agenda:{now:yyyyMMdd}",
                    CalendarNotifications.AgendaBody(agenda),
                    CalendarNotifications.AgendaTitle(agenda.Count, now, AgendaTime),
                    "default");

                _log($"Calendar: today's agenda queued ({agenda.Count} entries).");
            }

            state.LastAgendaDate = DateOnly.FromDateTime(now);
            changed = true;
        }

        foreach (CalendarMeeting meeting in CalendarNotifications.DueReminders(
                     meetings, now, ReminderLead, state.WasReminderSent))
        {
            string key = CalendarNotifications.ReminderKey(meeting);

            await EnqueueAsync(
                "cal:reminder:" + key,
                CalendarNotifications.ReminderBody(meeting),
                CalendarNotifications.ReminderTitle(meeting, now),
                "urgent");

            state.MarkReminderSent(key, DateTime.UtcNow);
            changed = true;

            _log("Calendar: meeting reminder queued.");
        }

        int before = state.SentReminders.Count;
        state.Prune(DateTime.UtcNow);
        changed |= state.SentReminders.Count != before;

        if (changed)
        {
            try
            {
                state.Save();
            }
            catch (Exception ex)
            {
                DebugTrace.Detail("Could not save the calendar state", ex);
            }
        }
    }

    private Task EnqueueAsync(string id, string body, string title, string priority) =>
        _queue.EnqueueAsync(id, body, title, priority, _getTopic());

    // One line per change of status, not one per scan.
    private void ReportStatus(string status)
    {
        if (status == _lastStatus)
            return;

        _lastStatus = status;
        _log("Calendar: " + status);
    }
}
