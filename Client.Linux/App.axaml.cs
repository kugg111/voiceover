using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Voiceover.Client.Linux;

public partial class App : Application
{
    // Same production server the WPF client points at (Client/App.xaml.cs) -
    // both clients talk to the one self-hosted deployment, see REDEPLOY.txt.
    public const string ApiBaseUrl = "https://voiceover-production-c32a.up.railway.app/";
    public const string HubUrl = "https://voiceover-production-c32a.up.railway.app/hubs/chat";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Explicit, not relying on Avalonia's default - LoginWindow/
            // RegisterWindow/MainWindow each Show() a replacement and
            // Close() themselves (see LoginCompletion.cs), so the app must
            // stay alive until the LAST open window closes, not just
            // whichever one was set as MainWindow first.
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;

            // No "remember me" session restore yet (see SessionStorage.cs) -
            // every launch starts at LoginWindow, unlike the WPF client's
            // App.xaml.cs which can skip straight to MainWindow.
            desktop.MainWindow = new LoginWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
