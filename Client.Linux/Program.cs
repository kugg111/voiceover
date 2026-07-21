using Avalonia;
using System;
using System.IO;

namespace Voiceover.Client.Linux;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Diagnostic last-resort net, not a fix by itself - the real
        // defense is every async-void SignalR event handler in MainWindow
        // catching its own exceptions (see OnDirectMessageReceived) so a
        // transient failure never reaches here. This just makes sure that
        // IF something still gets through, it's logged instead of vanishing
        // with zero trace (there's no crash report UI on Linux yet).
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash(e.Exception); e.SetObserved(); };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "voiceover_client_linux_crash.log"),
                $"{DateTime.Now:O}\n{ex}\n\n");
        }
        catch { }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
