using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Voiceover.Client.Services;
using Voiceover.Client.Views;

namespace Voiceover.Client;

public partial class App : Application
{
    // Points at the deployed Railway server so friends can connect from their
    // own PCs. Switch back to the localhost values below for local dev against
    // `dotnet run` in Server/.
    public const string ApiBaseUrl = "https://voiceover-production-c32a.up.railway.app/";
    public const string HubUrl = "https://voiceover-production-c32a.up.railway.app/hubs/chat";
    // public const string ApiBaseUrl = "http://localhost:5220/";
    // public const string HubUrl = "http://localhost:5220/hubs/chat";

    // Avatar/icon/attachment URLs come back from the server as relative
    // paths ("/uploads/xxx.png") - same resolution AttachmentLink already
    // did inline before this existed, centralized here since avatars need
    // it in a lot more places.
    public static string? ResolveUploadUrl(string? relativeOrNull) =>
        string.IsNullOrEmpty(relativeOrNull) ? null : ApiBaseUrl.TrimEnd('/') + relativeOrNull;

    public App()
    {
        // Most event handlers in this app are `async void` (WPF's Click/etc. signatures
        // require it), which means an exception inside one - e.g. the server being
        // unreachable during a network call - can't be caught by the caller and would
        // otherwise crash the whole process. This is a last-resort safety net so a
        // transient failure shows an error dialog instead of taking the app down.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    // Held for the process's entire lifetime (a static field, not a local) -
    // the GC must never finalize/release it early, which would let a later
    // launch wrongly conclude no instance is running.
    private static Mutex? _singleInstanceMutex;

    private const string SingleInstanceMutexName = "Voiceover_SingleInstance_3F2504E0-4F89-11D3-9A0C-0305E82C3301";

    // A registered message, not a raw ShowWindow/SetForegroundWindow call
    // alone - those only flip the OS-level visibility/focus bits on the
    // found HWND from this (foreign) process, without ever running WPF's
    // own Show()/Activate() logic. That distinction matters specifically
    // for a MainWindow parked in the tray via Hide() (see
    // MainWindow.MainWindow_Closing): WPF's internal Visibility state and
    // ui:FluentWindow's DWM-backed backdrop/composition only get
    // re-established by that window's own Show() call on its own thread -
    // an external ShowWindow(SW_RESTORE) makes the window visible and
    // focused but leaves its content unpainted (a blank window). Posting
    // this to the found HWND lets the owning process restore itself the
    // correct way instead. RegisterWindowMessage is what makes a message
    // ID shareable by name across the process boundary.
    public static readonly uint ShowExistingInstanceMessage =
        RegisterWindowMessage("Voiceover_ShowMainWindow_3F2504E0-4F89-11D3-9A0C-0305E82C3301");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Named Mutex, not "list processes and decide" - CreateMutex is
        // atomic at the OS level, so this can't race two simultaneous
        // launches the way a process-list check could. If another instance
        // already holds it, activate that instance's window instead of
        // launching a second, fully independent one - two copies both
        // capturing the mic and both reacting to the same global hotkeys is
        // exactly the confusing state this exists to prevent. Checked before
        // anything else in startup so a rejected second launch creates zero
        // windows and does zero work (no update check, no session restore)
        // before exiting.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            BringExistingInstanceToForeground();
            Shutdown();
            return;
        }

        // WPF's default ShutdownMode (OnLastWindowClose) tears the whole
        // app down the instant the update gate window closes, if that
        // happens to be the only open window at that exact moment - which
        // it always is here, since the gate window closes before
        // LoginWindow/MainWindow gets created a few lines below (that
        // continuation resumes via the dispatcher queue, so there's a real
        // gap with zero windows open). Suppress that until a real window
        // is up, then restore normal behavior so closing Login/MainWindow
        // still quits the app exactly as before.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Always runs first, before either startup path below - checks for
        // an update and, if one exists, blocks here until the user updates
        // or skips (skip is unavailable for an update the developer marked
        // Mandatory). Returns false if it already handled everything else
        // (mid-update relaunch, or the user quit instead of updating).
        if (!await UpdateGateWindow.CheckAndShowIfNeededAsync())
        {
            Shutdown();
            return;
        }

        var session = SessionStorage.Load();
        if (session is not null)
        {
            var api = new ApiService(ApiBaseUrl);
            // Exchanges the saved refresh token for a fresh access token -
            // "remember me" sessions can be days old, so there's no point
            // trying to reuse a short-lived access token from last time.
            // False means the refresh token itself is no longer valid
            // (expired past its 30-day life, or revoked via /logout-all) -
            // RestoreSessionAsync already cleared it, so just fall back to
            // a normal login same as if there'd been no saved session.
            var restored = await api.RestoreSessionAsync(session.RefreshToken, session.UserId, session.Username, session.AvatarUrl);
            if (restored)
            {
                // No password was entered this launch, so E2EE can only be
                // unlocked from the wrapping key cached alongside the
                // refresh token (see SessionStorage) - absent for sessions
                // saved before E2EE shipped, in which case DMs stay locked
                // (clear placeholder text, see E2eeService.DecryptAsync)
                // until the next interactive login.
                if (session.E2eeWrappingKey is not null)
                    await api.E2ee.UnlockWithCachedKeyAsync(Convert.FromBase64String(session.E2eeWrappingKey));

                new MainWindow(api).Show();
                ShutdownMode = ShutdownMode.OnLastWindowClose;
                return;
            }
        }

        new LoginWindow().Show();
        ShutdownMode = ShutdownMode.OnLastWindowClose;
    }

    // Finds the already-running instance's top-level window - whichever one
    // it currently has open (LoginWindow, MainWindow, etc.), including a
    // MainWindow hidden in the system tray, since Hide() only makes a window
    // invisible, it doesn't destroy the HWND - EnumWindows/ShowWindow/
    // SetForegroundWindow all still work against an invisible window - and
    // activates it.
    private static void BringExistingInstanceToForeground()
    {
        var currentProcessId = (uint)Environment.ProcessId;
        var otherProcessIds = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName)
            .Select(p => (uint)p.Id)
            .Where(id => id != currentProcessId)
            .ToHashSet();
        if (otherProcessIds.Count == 0) return;

        var found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            // GW_OWNER == Zero excludes owned child windows (tooltips,
            // owned popups) - only a real top-level window like
            // LoginWindow/MainWindow itself should be activated.
            if (otherProcessIds.Contains(pid) && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero)
            {
                found = hWnd;
                return false; // stop enumerating
            }
            return true;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero) return;

        const int SW_RESTORE = 9;
        // Un-minimizes an OS-minimized (but not tray-hidden) window - a
        // legitimate use of ShowWindow here, since that's a genuine OS-level
        // window state with no WPF-internal counterpart to desync.
        ShowWindow(found, SW_RESTORE);
        // Ask the owning process to restore itself properly (see
        // ShowExistingInstanceMessage's own comment) - a window that isn't
        // listening for this (LoginWindow, UpdateGateWindow - never tray-
        // hidden) just ignores it via DefWindowProc, harmless no-op.
        PostMessage(found, ShowExistingInstanceMessage, IntPtr.Zero, IntPtr.Zero);
        // Called from here, not from the found window's own process: Windows
        // only lets the process that most recently received user input (this
        // newly-launched one, since the user just ran it again) call
        // SetForegroundWindow - the already-running instance calling this on
        // itself in response to the message above would likely be silently
        // ignored by Windows' foreground-lock rules.
        SetForegroundWindow(found);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "voiceover_client_crash.log"),
                $"{DateTime.Now:O}\n{e.Exception}\n\n");
        }
        catch { }

        // Deliberately a native MessageBox, not the themed AlertAsync used
        // everywhere else - this is the last-resort handler for an
        // exception that made it all the way up here, which could mean the
        // app's own themed window stack (styles, resources, FluentWindow
        // backdrop) is exactly what's broken. A plain OS dialog has no
        // dependency on any of that, so it's the one thing guaranteed to
        // still render.
        MessageBox.Show($"Something went wrong: {e.Exception.Message}", "Unexpected Error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
