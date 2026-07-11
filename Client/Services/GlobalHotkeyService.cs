using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Voiceover.Client.Services;

// Low-level global keyboard (WH_KEYBOARD_LL) and mouse (WH_MOUSE_LL) hooks so
// push-to-talk/push-to-mute work while the app doesn't have focus - a normal
// WPF KeyDown/KeyUp or Click handler only fires while the window is
// focused, which defeats the whole point of a PTT hotkey while gaming or
// tabbed into something else. Both hooks only listen (always call
// CallNextHookEx to pass the input through untouched) - neither blocks/
// consumes anything, so they don't interfere with whatever app actually has
// focus or with normal clicking.
//
// The watched trigger is either a keyboard key OR a mouse button, never
// both - setting one clears the other (see WatchedKey/WatchedMouseButton).
// Only Middle/XButton1/XButton2 are supported as mouse triggers; Left/Right
// are deliberately not recordable, since watching those would fire on every
// normal click anywhere on the system.
public class GlobalHotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private const int WH_MOUSE_LL = 14;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Kept as fields, not locals/lambdas - SetWindowsHookEx only stores an
    // unmanaged function pointer to the delegate. If the delegate itself
    // isn't rooted somewhere the GC can collect it while the hook is still
    // installed, crashing the process the next time any key/click happens
    // anywhere on the system.
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;
    private IntPtr _keyboardHookHandle = IntPtr.Zero;
    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private bool _isDown;

    private Key? _watchedKey = Key.RightCtrl;
    public Key? WatchedKey
    {
        get => _watchedKey;
        set
        {
            _watchedKey = value;
            if (value is not null) _watchedMouseButton = null;
        }
    }

    private MouseButton? _watchedMouseButton;
    public MouseButton? WatchedMouseButton
    {
        get => _watchedMouseButton;
        set
        {
            _watchedMouseButton = value;
            if (value is not null) _watchedKey = null;
        }
    }

    // Edge-triggered (fires once per press/release), not level-triggered -
    // Windows sends repeated WM_KEYDOWN messages for a held key (keyboard
    // auto-repeat), which would otherwise fire KeyDown continuously while
    // held instead of once. Fires the same way regardless of whether the
    // configured trigger is a key or a mouse button.
    public event Action? KeyDown;
    public event Action? KeyUp;

    public GlobalHotkeyService()
    {
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public void Start()
    {
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = GetModuleHandle(curModule?.ModuleName);

        if (_keyboardHookHandle == IntPtr.Zero)
            _keyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
        if (_mouseHookHandle == IntPtr.Zero)
            _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, moduleHandle, 0);
    }

    public void Stop()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
        }
        if (_mouseHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
        }
        _isDown = false;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _watchedKey is not null)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            var key = KeyInterop.KeyFromVirtualKey(vkCode);

            if (key == _watchedKey)
            {
                int msg = wParam.ToInt32();
                if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && !_isDown)
                {
                    _isDown = true;
                    KeyDown?.Invoke();
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    _isDown = false;
                    KeyUp?.Invoke();
                }
            }
        }

        return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _watchedMouseButton is not null)
        {
            int msg = wParam.ToInt32();
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            // XBUTTON1/XBUTTON2 aren't distinguished by the message id the
            // way left/right/middle are - which one is packed into the high
            // word of mouseData (1 = XBUTTON1, 2 = XBUTTON2), same layout
            // Win32's GET_XBUTTON_WPARAM macro reads for the non-low-level
            // window message version of this.
            var xButton = (int)(hookStruct.mouseData >> 16);
            var pressedButton = msg switch
            {
                WM_MBUTTONDOWN or WM_MBUTTONUP => MouseButton.Middle,
                WM_XBUTTONDOWN or WM_XBUTTONUP => xButton == 1 ? MouseButton.XButton1 : MouseButton.XButton2,
                _ => (MouseButton?)null
            };

            if (pressedButton == _watchedMouseButton)
            {
                bool isDownMsg = msg is WM_MBUTTONDOWN or WM_XBUTTONDOWN;
                bool isUpMsg = msg is WM_MBUTTONUP or WM_XBUTTONUP;

                if (isDownMsg && !_isDown)
                {
                    _isDown = true;
                    KeyDown?.Invoke();
                }
                else if (isUpMsg)
                {
                    _isDown = false;
                    KeyUp?.Invoke();
                }
            }
        }

        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
