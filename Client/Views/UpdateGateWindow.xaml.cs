using System.ComponentModel;
using System.Windows;
using Voiceover.Client.Models;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

// Shown once at every launch, before LoginWindow/MainWindow - mirrors
// Discord's own startup flow (always checks, only ever prompts when
// there's actually something to prompt about). An optional update can be
// skipped; a mandatory one (VersionInfo.Mandatory, flipped by hand in
// Server/Site/downloads/version.json per release) cannot - closing the
// window any way other than "Skip for now" quits the app instead of
// letting it through, same as closing any other single-window startup
// screen would.
public partial class UpdateGateWindow : FluentWindow
{
    private readonly ApiService _api;
    private VersionInfo? _latest;
    private bool _isUpdating;
    private readonly TaskCompletionSource<bool> _resultTcs = new();

    private UpdateGateWindow(ApiService api)
    {
        InitializeComponent();
        _api = api;
    }

    // Returns true if App.OnStartup should continue its normal session-
    // restore/login flow; false if this window already handled everything
    // else (mid-update relaunch, or the user quit rather than update).
    public static async Task<bool> CheckAndShowIfNeededAsync()
    {
        var api = new ApiService(App.ApiBaseUrl);
        var window = new UpdateGateWindow(api);
        window.Show();

        var latest = await CheckWithTimeoutAsync(api);

        if (latest is null || !UpdateChecker.IsNewer(latest.Version))
        {
            window.Close();
            return true;
        }

        window._latest = latest;
        window.ShowUpdatePrompt();
        return await window._resultTcs.Task;
    }

    // Bounded independently of GetLatestVersionAsync's own (already
    // failure-swallowing) implementation - a dead/slow network must never
    // leave this splash stuck for HttpClient's full default timeout.
    private static async Task<VersionInfo?> CheckWithTimeoutAsync(ApiService api)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var checkTask = api.GetLatestVersionAsync();
        var timeoutTask = Task.Delay(Timeout.Infinite, cts.Token);
        var completed = await Task.WhenAny(checkTask, timeoutTask);
        return completed == checkTask ? await checkTask : null;
    }

    private void ShowUpdatePrompt()
    {
        CheckingSpinner.Visibility = Visibility.Collapsed;
        TitleText.Text = $"Version {_latest!.Version} is available";
        DetailText.Text = $"You're on {UpdateChecker.CurrentVersion}.";
        MandatoryText.Visibility = _latest.Mandatory ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = _latest.Mandatory ? Visibility.Collapsed : Visibility.Visible;
        ActionButtons.Visibility = Visibility.Visible;
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        _resultTcs.TrySetResult(true);
        Close();
    }

    // Downloads in the background and swaps the files in place (see
    // SelfUpdateService) instead of just opening a browser tab - on
    // success it shuts the app down and relaunches on its own, so
    // _resultTcs never needs to resolve for that path.
    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        ActionButtons.Visibility = Visibility.Collapsed;
        DownloadProgressBar.Visibility = Visibility.Visible;
        TitleText.Text = "Downloading update…";
        DetailText.Text = "";

        var progress = new Progress<double>(percent =>
        {
            DownloadProgressBar.Value = percent;
            DetailText.Text = $"{percent:F0}%";
        });

        try
        {
            _isUpdating = true;
            await SelfUpdateService.DownloadAndApplyAsync(_api, _latest!.PortableUrl, progress);
        }
        catch (Exception ex)
        {
            _isUpdating = false;
            TitleText.Text = "Update failed";
            DetailText.Text = ex.Message;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = Visibility.Visible;
            UpdateButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
        }
    }

    // Covers Alt+F4/taskbar-close/system-menu-close - anything that isn't
    // the explicit Skip button. There's no other window open yet at this
    // point in startup, so closing this one is the natural "quit the app"
    // gesture, same as it would be for any other first-window-of-the-
    // session; letting it fall through to login instead would silently
    // override that.
    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_isUpdating) return;
        _resultTcs.TrySetResult(false);
    }
}
