namespace TaskbarWidget.Timing;

/// <summary>
/// Manages Win32 timers with SetInterval/SetTimeout semantics.
/// Timer IDs start at 100 to avoid collision with internal tooltip/fade timer IDs.
/// </summary>
internal sealed class TimerManager
{
    private int _nextId = 100;
    private readonly Dictionary<int, TimerEntry> _timers = new();
    private IntPtr _hwnd;

    public void SetHwnd(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>
    /// Repeating timer. Returns handle for ClearTimer.
    /// </summary>
    public int SetInterval(TimeSpan interval, Action callback)
    {
        int id = _nextId++;
        _timers[id] = new TimerEntry { Callback = callback, OneShot = false };
        Native.SetTimer(_hwnd, (IntPtr)id, (uint)interval.TotalMilliseconds, IntPtr.Zero);
        return id;
    }

    /// <summary>
    /// One-shot timer. Auto-clears after firing.
    /// </summary>
    public int SetTimeout(TimeSpan delay, Action callback)
    {
        int id = _nextId++;
        _timers[id] = new TimerEntry { Callback = callback, OneShot = true };
        Native.SetTimer(_hwnd, (IntPtr)id, (uint)delay.TotalMilliseconds, IntPtr.Zero);
        return id;
    }

    /// <summary>
    /// Cancel a timer.
    /// </summary>
    public void ClearTimer(int handle)
    {
        if (_timers.Remove(handle))
            Native.KillTimer(_hwnd, (IntPtr)handle);
    }

    /// <summary>
    /// Called from WndProc on WM_TIMER. Returns true if this timer was handled.
    /// </summary>
    public bool OnTimer(IntPtr wParam)
    {
        int id = (int)wParam;
        if (!_timers.TryGetValue(id, out var entry)) return false;

        if (entry.OneShot)
        {
            Native.KillTimer(_hwnd, (IntPtr)id);
            _timers.Remove(id);
        }

        entry.Callback();
        return true;
    }

    /// <summary>
    /// Kill all timers.
    /// </summary>
    public void Dispose()
    {
        foreach (var id in _timers.Keys)
            Native.KillTimer(_hwnd, (IntPtr)id);
        _timers.Clear();
    }

    private sealed class TimerEntry
    {
        public required Action Callback { get; init; }
        public required bool OneShot { get; init; }
    }
}
