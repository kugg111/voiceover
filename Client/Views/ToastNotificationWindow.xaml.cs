using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Voiceover.Client.Views;

// A custom in-app "toast" rather than the real Windows toast/notification-
// center API - deliberately, not just for simplicity. This app ships as an
// unpackaged single-file exe via a plain Inno Setup installer (no MSIX),
// and unpackaged Win32 apps have a long history of the real toast API
// silently not showing anything unless specific AppUserModelID/shortcut
// setup is done. A plain borderless popup window works reliably regardless
// of how the app was installed.
public partial class ToastNotificationWindow : Window
{
    // WS_EX_NOACTIVATE - without this, Show() both reorders this Topmost
    // window above whatever's currently in front AND takes window
    // activation, which is exactly what makes Windows forcibly minimize an
    // exclusive-fullscreen game the instant a voice join/leave or new
    // message pops a toast while the user is mid-game. Same fix/same
    // reasoning as OverlayWindow's own WS_EX_NOACTIVATE (see its
    // OnSourceInitialized) - this window still takes mouse clicks fine
    // (Window_MouseLeftButtonDown below) since NOACTIVATE only blocks
    // activation, not input.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly DispatcherTimer _dismissTimer;
    private bool _dismissing;

    public ToastNotificationWindow(string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
        };

        Opacity = 0;
        Loaded += (_, _) =>
        {
            PositionBottomRight();
            FadeIn();
        };

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _dismissTimer.Tick += (_, _) => Dismiss();
        _dismissTimer.Start();
    }

    private void PositionBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 20;
        Top = workArea.Bottom - ActualHeight - 20;
    }

    private void FadeIn()
    {
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        BeginAnimation(OpacityProperty, anim);
    }

    private void Dismiss()
    {
        if (_dismissing) return;
        _dismissing = true;

        _dismissTimer.Stop();
        var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
        anim.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, anim);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Dismiss();
}
