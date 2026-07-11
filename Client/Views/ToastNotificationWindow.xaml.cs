using System.Windows;
using System.Windows.Input;
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
    private readonly DispatcherTimer _dismissTimer;
    private bool _dismissing;

    public ToastNotificationWindow(string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;

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
