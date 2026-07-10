using System.Windows;
using System.Windows.Threading;

namespace DiscordClone.Client;

public partial class App : Application
{
    // Change this if your server runs on a different port (see Server/appsettings.json).
    public const string ApiBaseUrl = "http://localhost:5220/";
    public const string HubUrl = "http://localhost:5220/hubs/chat";

    public App()
    {
        // Most event handlers in this app are `async void` (WPF's Click/etc. signatures
        // require it), which means an exception inside one - e.g. the server being
        // unreachable during a network call - can't be caught by the caller and would
        // otherwise crash the whole process. This is a last-resort safety net so a
        // transient failure shows an error dialog instead of taking the app down.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Something went wrong: {e.Exception.Message}", "Unexpected Error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
