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

    // Applied once at startup, not live-switchable: every custom color in
    // this app (BgDark/TextNormal/etc., see Themes/DarkPalette.xaml and
    // LightPalette.xaml) is referenced via StaticResource throughout the
    // XAML, which resolves once at load time and does not react to the
    // resource dictionary changing afterwards - only WPF-UI's own restyled
    // controls (via ApplicationThemeManager) support switching live.
    // Rewriting every StaticResource reference across the app to
    // DynamicResource just to support a live toggle wasn't worth the risk
    // for this feature; SettingsWindow's theme toggle instead tells the user
    // to restart, same as it would for a genuinely native theme switch.
    public static void ApplyTheme(bool isLightTheme)
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            isLightTheme ? Wpf.Ui.Appearance.ApplicationTheme.Light : Wpf.Ui.Appearance.ApplicationTheme.Dark);

        var paletteUri = isLightTheme
            ? new Uri("Themes/LightPalette.xaml", UriKind.Relative)
            : new Uri("Themes/DarkPalette.xaml", UriKind.Relative);
        Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = paletteUri });
    }

    public App()
    {
        // Most event handlers in this app are `async void` (WPF's Click/etc. signatures
        // require it), which means an exception inside one - e.g. the server being
        // unreachable during a network call - can't be caught by the caller and would
        // otherwise crash the whole process. This is a last-resort safety net so a
        // transient failure shows an error dialog instead of taking the app down.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyTheme(ThemeStorage.LoadIsLightTheme());

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
            if (await api.RestoreSessionAsync(session.RefreshToken, session.UserId, session.Username, session.AvatarUrl))
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
                return;
            }
        }

        new LoginWindow().Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "voiceover_client_crash.log"),
                $"{DateTime.Now:O}\n{e.Exception}\n\n");
        }
        catch { }

        MessageBox.Show($"Something went wrong: {e.Exception.Message}", "Unexpected Error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
