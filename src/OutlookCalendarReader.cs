using System.Globalization;
using System.Runtime.InteropServices;

namespace NotiRelay;

/// <summary>
/// Reads the default calendar of the classic Outlook already running on this PC, through
/// Outlook's own programming interface (late-bound COM). It never starts Outlook, never reads
/// meeting bodies or attendees (no Outlook security prompt), and releases every reference
/// after each read so Outlook can close normally. "New Outlook" has no such interface.
/// </summary>
public sealed class OutlookCalendarReader
{
    private const int OlFolderCalendar = 9;
    private const int OlAppointmentClass = 26;
    private const int OlMeetingCanceled = 5;
    private const int OlMeetingReceivedAndCanceled = 7;
    private const int OlResponseDeclined = 4;
    private const int OlFree = 0;
    private const int OlApptException = 3;
    private const int MaxItems = 2000;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Why the last read returned null.</summary>
    public string UnavailableReason { get; private set; } = string.Empty;

    /// <summary>Meetings overlapping [fromLocal, toLocal), or null when Outlook is not available.</summary>
    public IReadOnlyList<CalendarMeeting>? ReadMeetings(DateTime fromLocal, DateTime toLocal)
    {
        object? application = GetRunningOutlook();

        if (application is null)
            return null;

        var references = new List<object> { application };

        try
        {
            dynamic outlook = application;

            object mapi = Track(references, outlook.GetNamespace("MAPI"));
            object folder = Track(references, ((dynamic)mapi).GetDefaultFolder(OlFolderCalendar));
            object items = Track(references, ((dynamic)folder).Items);

            // Recurring meetings are expanded into occurrences only when sorted by Start first.
            dynamic all = items;
            all.Sort("[Start]");
            all.IncludeRecurrences = true;

            string filter =
                $"[Start] < '{JetDate(toLocal)}' AND [End] > '{JetDate(fromLocal)}'";

            object restricted = Track(references, all.Restrict(filter));
            dynamic found = restricted;

            var meetings = new List<CalendarMeeting>();
            object? current = found.GetFirst();

            for (int i = 0; current is not null && i < MaxItems; i++)
            {
                try
                {
                    // Re-check the window: the filter's date text depends on regional settings.
                    if (TryRead(current, out CalendarMeeting? meeting) &&
                        meeting.EndLocal > fromLocal &&
                        meeting.StartLocal < toLocal)
                    {
                        meetings.Add(meeting);
                    }
                }
                finally
                {
                    Release(current);
                }

                current = found.GetNext();
            }

            if (current is not null)
                Release(current);

            return meetings;
        }
        finally
        {
            for (int i = references.Count - 1; i >= 0; i--)
                Release(references[i]);
        }
    }

    private static bool TryRead(object item, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CalendarMeeting? meeting)
    {
        meeting = null;
        dynamic appointment = item;

        if ((int)appointment.Class != OlAppointmentClass)
            return false;

        DateTime start = (DateTime)appointment.Start;
        DateTime end = (DateTime)appointment.End;

        string id = (string?)appointment.GlobalAppointmentID ?? string.Empty;
        if (id.Length == 0)
            id = (string?)appointment.EntryID ?? string.Empty;

        // An occurrence moved away from its usual slot keeps the key of that slot.
        DateTime? originalStart = (int)appointment.RecurrenceState == OlApptException
            ? TryOriginalStart(item, start)
            : null;

        string key = CalendarNotifications.OccurrenceKey(
            id, (bool)appointment.IsRecurring, start, originalStart);

        int meetingStatus = (int)appointment.MeetingStatus;

        meeting = new CalendarMeeting(
            key,
            (string?)appointment.Subject ?? string.Empty,
            start,
            end,
            (string?)appointment.Location ?? string.Empty,
            AllDay: (bool)appointment.AllDayEvent,
            Cancelled: meetingStatus is OlMeetingCanceled or OlMeetingReceivedAndCanceled,
            Declined: (int)appointment.ResponseStatus == OlResponseDeclined,
            Free: (int)appointment.BusyStatus == OlFree);

        return true;
    }

    // For an exception occurrence: find it among the series' exceptions to learn the slot
    // it was moved from. Any failure falls back to the occurrence's own date.
    private static DateTime? TryOriginalStart(object occurrence, DateTime start)
    {
        var references = new List<object>();

        try
        {
            object master = Track(references, ((dynamic)occurrence).Parent);
            object pattern = Track(references, ((dynamic)master).GetRecurrencePattern());
            object exceptions = Track(references, ((dynamic)pattern).Exceptions);

            dynamic list = exceptions;
            int count = (int)list.Count;

            for (int i = 1; i <= count; i++)
            {
                object exception = Track(references, list.Item(i));
                dynamic entry = exception;

                if ((bool)entry.Deleted)
                    continue;

                object moved = Track(references, entry.AppointmentItem);

                if ((DateTime)((dynamic)moved).Start == start)
                    return (DateTime)entry.OriginalDate;
            }
        }
        catch (Exception ex)
        {
            DebugTrace.Detail("Could not read a recurring meeting's original slot", ex);
        }
        finally
        {
            for (int i = references.Count - 1; i >= 0; i--)
                Release(references[i]);
        }

        return null;
    }

    private object? GetRunningOutlook()
    {
        if (!OperatingSystem.IsWindows())
        {
            UnavailableReason = "Outlook calendar is only available on Windows.";
            return null;
        }

        if (CLSIDFromProgID("Outlook.Application", out Guid clsid) != 0)
        {
            UnavailableReason = "Classic Outlook is not installed (New Outlook cannot be read).";
            return null;
        }

        if (GetActiveObject(ref clsid, IntPtr.Zero, out object? application) != 0 ||
            application is null)
        {
            UnavailableReason = "Outlook is not open.";
            return null;
        }

        UnavailableReason = string.Empty;
        return application;
    }

    // Outlook parses filter dates with the Windows regional settings.
    private static string JetDate(DateTime local) =>
        local.ToString("g", CultureInfo.CurrentCulture);

    private static object Track(List<object> references, object value)
    {
        references.Add(value);
        return value;
    }

    private static void Release(object value)
    {
        try
        {
            if (OperatingSystem.IsWindows() && Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }
        catch
        {
            // Releasing is best effort.
        }
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(
        ref Guid clsid,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object? unknown);
}
