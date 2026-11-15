using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotiRelay;

/// <summary>
/// What the calendar watcher must remember across restarts: the day the agenda was last
/// sent and which reminders went out (so a restart does not repeat them). Holds no meeting
/// titles, only keys. Platform-neutral; unit-tested on any OS.
/// </summary>
public sealed class CalendarState
{
    private static readonly TimeSpan KeepReminders = TimeSpan.FromDays(2);

    public DateOnly? LastAgendaDate { get; set; }

    /// <summary>Reminder key → when it was sent (UTC).</summary>
    public Dictionary<string, DateTime> SentReminders { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public string FilePath { get; private set; } = string.Empty;

    /// <summary>Reads the state; a missing or unreadable file starts empty.</summary>
    public static CalendarState Load(string path)
    {
        CalendarState state;

        try
        {
            state = File.Exists(path)
                ? JsonSerializer.Deserialize<CalendarState>(File.ReadAllText(path)) ?? new CalendarState()
                : new CalendarState();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            state = new CalendarState();
        }

        state.SentReminders = new Dictionary<string, DateTime>(
            state.SentReminders ?? new Dictionary<string, DateTime>(), StringComparer.Ordinal);
        state.FilePath = path;
        return state;
    }

    /// <summary>Writes atomically (temporary file, then replace).</summary>
    public void Save()
    {
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, FilePath, overwrite: true);
    }

    public bool WasReminderSent(string key) => SentReminders.ContainsKey(key);

    public void MarkReminderSent(string key, DateTime nowUtc) => SentReminders[key] = nowUtc;

    public void Prune(DateTime nowUtc)
    {
        foreach (string key in SentReminders
                     .Where(entry => nowUtc - entry.Value > KeepReminders)
                     .Select(entry => entry.Key)
                     .ToList())
        {
            SentReminders.Remove(key);
        }
    }
}
