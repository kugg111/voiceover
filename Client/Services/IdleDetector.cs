using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Voiceover.Client.Services;

// Detects system-wide (not just app-focused) mouse/keyboard inactivity via
// the Win32 last-input timestamp, the same mechanism screensavers/lock
// screens use - a WPF PreviewMouseMove/KeyDown handler would only see
// input while the window has focus, which misses "away" entirely while
// tabbed into a game or another app.
public class IdleDetector : IDisposable
{
    public static readonly TimeSpan AwayThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private readonly DispatcherTimer _timer;
    private bool _isIdle;

    // Fires only on the Online<->Away transition, not on every poll.
    public event Action<bool>? IdleChanged;

    public IdleDetector()
    {
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => CheckIdle();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void CheckIdle()
    {
        var nowIdle = GetIdleTime() >= AwayThreshold;
        if (nowIdle == _isIdle) return;

        _isIdle = nowIdle;
        IdleChanged?.Invoke(nowIdle);
    }

    public static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

        // Both dwTime and the current tick count wrap at ~49.7 days (uint
        // milliseconds) - unsigned subtraction handles that wraparound
        // correctly as long as the actual idle gap never exceeds that span.
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    public void Dispose() => Stop();
}
