using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Voiceover.Client.Views;

// Discord-style in-game voice overlay: a transparent, click-through,
// always-on-top window pinned to the top-left of whatever monitor a
// fullscreen/borderless game is running on, listing who's in the current
// voice channel and who's speaking. It reuses the exact VoiceMemberItem
// instances the main window's voice roster already maintains (see
// MainWindow.SyncOverlayRoster), so speaking/mute/deafen state updates here
// for free as those items raise PropertyChanged.
//
// Scope (see the game-voice-overlay plan): borderless/windowed games only.
// It does NOT hook the game's graphics pipeline (the DLL-injection technique
// Discord/Steam use for exclusive-fullscreen), which would risk anti-cheat
// bans - so over a true exclusive-fullscreen game it simply won't show, same
// limitation Discord's overlay has. Users are told to run Borderless mode.
public partial class OverlayWindow : Window
{
    // --- Win32 extended-window-style interop (same user32.dll P/Invoke style
    // already used by GlobalHotkeyService/IdleDetector). WS_EX_TRANSPARENT is
    // what makes mouse input pass straight through to the game underneath;
    // TOOLWINDOW keeps it out of Alt-Tab; NOACTIVATE means showing it never
    // steals focus from the game. ---
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private readonly int _currentProcessId = Environment.ProcessId;
    private readonly DispatcherTimer _timer;
    private IntPtr _hwnd = IntPtr.Zero;

    // ShouldShow is the AND of all four - only when the feature is on, the
    // user hasn't toggled it off, they're actually in a voice channel, and a
    // fullscreen game currently has focus.
    private bool _enabled = true;
    private bool _toggledOn = true;
    private bool _inVoice;
    private RECT? _lastPositionedMonitor;

    public OverlayWindow()
    {
        InitializeComponent();

        // Force HWND creation now (without showing the window) so the
        // click-through extended styles are applied before it's ever
        // displayed, and so PositionAtMonitorTopLeft's DPI transform is
        // available. SourceInitialized fires from inside EnsureHandle.
        SourceInitialized += OnSourceInitialized;
        new WindowInteropHelper(this).EnsureHandle();

        // 400ms is responsive enough that alt-tabbing into a game shows the
        // overlay near-instantly, without the polling being noticeable.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Evaluate();
        _timer.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    // --- Public control surface (all called on the UI thread by MainWindow) ---

    public void SetUserEnabled(bool enabled)
    {
        _enabled = enabled;
        Evaluate();
    }

    // Only the card's background alpha changes - member names/avatars/rings
    // stay fully opaque so the overlay stays readable no matter how
    // see-through the backdrop is set.
    public void SetBackgroundOpacity(double opacity)
    {
        var alpha = (byte)(Math.Clamp(opacity, 0.0, 1.0) * 255);
        OverlayCard.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x1E, 0x1F, 0x22));
    }

    // Null roster = not in a voice channel. Binds directly to the live
    // ObservableCollection the main window maintains, so joins/leaves/speaking
    // propagate without any further calls here.
    public void SetRoster(ObservableCollection<VoiceMemberItem>? members)
    {
        _inVoice = members is not null;
        MemberList.ItemsSource = members;
        Evaluate();
    }

    // Fired by the global toggle hotkey.
    public void ToggleVisibility()
    {
        _toggledOn = !_toggledOn;
        Evaluate();
    }

    // This runs unattended on a 400ms timer for the entire app lifetime, so it
    // must never let an exception escape: WPF's rendering pipeline
    // (CompositionTarget.TransformFromDevice, used below) can genuinely throw
    // while DWM composition is suspended - which real exclusive-fullscreen
    // games do, and which this class's own rect-based heuristic can't always
    // tell apart from the borderless case it's meant to support (see the
    // header comment) - or transiently during any fullscreen mode-switch.
    // Previously, an exception here propagated to App.xaml.cs's
    // DispatcherUnhandledException handler, which shows a blocking
    // MessageBox, and left the overlay silently wedged afterward (see
    // RepositionIfMonitorChanged below for the specific "silently wedged"
    // mechanism). Swallowing and retrying next tick is the correct behavior
    // either way: if it was transient, the overlay just shows ~400ms later
    // than usual; if it's persistent (a real exclusive-fullscreen game), it
    // simply never shows, matching the documented scope limitation, instead
    // of popping an error dialog.
    private void Evaluate()
    {
        try
        {
            EvaluateCore();
        }
        catch
        {
            // Best-effort - see method header above.
        }
    }

    private void EvaluateCore()
    {
        // Pre-initialized so it's definitely assigned even when the &&
        // short-circuits before IsFullscreenAppActive runs (which is the point
        // of the short-circuit - skip the Win32 calls when disabled/not in
        // voice).
        RECT monitor = default;
        var shouldShow = _enabled && _toggledOn && _inVoice && IsFullscreenAppActive(out monitor);

        if (shouldShow)
        {
            RepositionIfMonitorChanged(monitor);
            if (!IsVisible) Show();
            // Re-assert topmost each tick so a game grabbing the top slot on a
            // focus change can't bury the overlay. NOACTIVATE so this never
            // pulls focus off the game.
            if (_hwnd != IntPtr.Zero)
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        else if (IsVisible)
        {
            Hide();
            _lastPositionedMonitor = null;
        }
    }

    // "A fullscreen app has focus" heuristic: the foreground window belongs to
    // another process and covers the entire monitor bounds (rcMonitor, NOT the
    // work area). A merely maximized window stops at the work area to leave the
    // taskbar visible, so this distinguishes a borderless-fullscreen game from
    // an ordinary maximized app.
    private bool IsFullscreenAppActive(out RECT monitorRect)
    {
        monitorRect = default;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        // Never treat our own windows (main window, this overlay, dialogs) as
        // "the game" - a maximized window of ours isn't a game, and the
        // overlay must never detect itself.
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == (uint)_currentProcessId) return false;

        // The desktop itself covers the full monitor - exclude the shell so an
        // empty desktop doesn't read as a fullscreen game.
        var cls = GetClassName(fg);
        if (cls is "Progman" or "WorkerW") return false;

        if (!GetWindowRect(fg, out var wr)) return false;

        var mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;

        const int tol = 2; // a few px of slop for off-by-one borderless rects
        var covers =
            wr.Left <= mi.rcMonitor.Left + tol &&
            wr.Top <= mi.rcMonitor.Top + tol &&
            wr.Right >= mi.rcMonitor.Right - tol &&
            wr.Bottom >= mi.rcMonitor.Bottom - tol;

        if (!covers) return false;

        monitorRect = mi.rcMonitor;
        return true;
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    private void RepositionIfMonitorChanged(RECT monitor)
    {
        if (_lastPositionedMonitor is { } last &&
            last.Left == monitor.Left && last.Top == monitor.Top &&
            last.Right == monitor.Right && last.Bottom == monitor.Bottom)
            return;

        // Monitor bounds come back in physical pixels; WPF Left/Top are in DIPs.
        // Convert via this window's own device transform so multi-monitor +
        // per-monitor-DPI setups land the overlay correctly, then inset by a
        // small margin from the very corner.
        const double margin = 16.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            // TransformFromDevice can throw (non-invertible matrix) while DWM
            // composition is suspended, which happens transiently during a
            // fullscreen mode-switch or persistently under true exclusive
            // fullscreen. _lastPositionedMonitor is deliberately only updated
            // AFTER this succeeds - setting it first (as an earlier version
            // did) meant a throw here left it permanently marked "already
            // positioned" for this monitor, so the early-return guard above
            // silently skipped retrying on every later tick, appearing to
            // wedge the overlay for the rest of the session even after DWM
            // composition resumed. Evaluate()'s own try/catch still covers
            // this too - this is the actual state-safety fix, that's the
            // outer backstop.
            var topLeft = source.CompositionTarget.TransformFromDevice.Transform(
                new Point(monitor.Left, monitor.Top));
            Left = topLeft.X + margin;
            Top = topLeft.Y + margin;
        }
        else
        {
            Left = monitor.Left + margin;
            Top = monitor.Top + margin;
        }

        _lastPositionedMonitor = monitor;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
