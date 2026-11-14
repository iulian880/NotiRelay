namespace NotiRelay;

/// <summary>Why sending to ntfy is held. Several reasons can hold it at once.</summary>
[Flags]
public enum SendingHold
{
    None = 0,

    /// <summary>The launch question about old notifications has not been answered yet.</summary>
    LaunchQuestion = 1,

    /// <summary>The user pressed Stop sending.</summary>
    StoppedByUser = 2
}

/// <summary>
/// Decides whether the delivery worker may take the next queued notification: only when no
/// reason holds it. Holding stops sending before the next item (a request already in flight
/// finishes). Thread-safe; platform-neutral so it is unit-tested on any OS.
/// </summary>
public sealed class SendingGate
{
    private int _holds;

    public bool IsOpen => Volatile.Read(ref _holds) == 0;

    public bool IsHeld(SendingHold reason) => (Volatile.Read(ref _holds) & (int)reason) != 0;

    public void Hold(SendingHold reason) => Interlocked.Or(ref _holds, (int)reason);

    public void Release(SendingHold reason) => Interlocked.And(ref _holds, ~(int)reason);
}

/// <summary>
/// Ignores a repeat of the same action inside a short window, so a double-click on
/// Stop sending does not stop and immediately resume.
/// </summary>
public sealed class ActionDebounce
{
    private readonly TimeSpan _window;
    private DateTime _lastAcceptedUtc = DateTime.MinValue;

    public ActionDebounce(TimeSpan window) => _window = window;

    public bool TryAccept(DateTime nowUtc)
    {
        if (nowUtc - _lastAcceptedUtc < _window)
            return false;

        _lastAcceptedUtc = nowUtc;
        return true;
    }
}
