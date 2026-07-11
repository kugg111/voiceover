using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Voiceover.Client.Services;

// Low-level global keyboard hook (WH_KEYBOARD_LL) so push-to-talk/push-to-
// mute work while the app doesn't have focus - a normal WPF KeyDown/KeyUp
// handler only fires while the window is focused, which defeats the whole
// point of a PTT hotkey while gaming or tabbed into something else. This
// only listens (always calls CallNextHookEx to pass the key through
// untouched) - it never blocks/consumes the key, so it doesn't interfere
// with whatever app actually has focus.
public class GlobalHotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Kept as a field, not a local/lambda - SetWindowsHookEx only stores an
    // unmanaged function pointer to this delegate. If the delegate itself
    // isn't rooted somewhere the GC can collect it while the hook is still
    // installed, crashing the process the next time any key is pressed
    // anywhere on the system.
    private readonly LowLevelKeyboardProc _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _isDown;

    public Key WatchedKey { get; set; } = Key.RightCtrl;

    // Edge-triggered (fires once per press/release), not level-triggered -
    // Windows sends repeated WM_KEYDOWN messages for a held key (keyboard
    // auto-repeat), which would otherwise fire KeyDown continuously while
    // held instead of once.
    public event Action? KeyDown;
    public event Action? KeyUp;

    public GlobalHotkeyService()
    {
        _hookProc = HookCallback;
    }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero) return;

        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(curModule?.ModuleName), 0);
    }

    public void Stop()
    {
        if (_hookHandle == IntPtr.Zero) return;

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _isDown = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            var key = KeyInterop.KeyFromVirtualKey(vkCode);

            if (key == WatchedKey)
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

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
