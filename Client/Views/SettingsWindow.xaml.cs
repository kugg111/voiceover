using System.Windows;
using Voiceover.Client.Models;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

public partial class SettingsWindow : FluentWindow
{
    private readonly ApiService _api;
    private readonly VoiceService? _voice;
    private VersionInfo? _latestVersion;

    public SettingsWindow(ApiService api, VoiceService? voice)
    {
        InitializeComponent();
        _api = api;
        _voice = voice;

        UpdateStatusText.Text = $"You're on version {UpdateChecker.CurrentVersion}" +
            (UpdateChecker.IsInstalled ? " (installed)." : " (portable).");
    }

    private void VoiceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;
        new VoiceSettingsWindow(_voice) { Owner = this }.ShowDialog();
    }

    private async void CheckForUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdateButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        _latestVersion = null;
        UpdateStatusText.Text = "Checking...";

        var latest = await _api.GetLatestVersionAsync();
        CheckForUpdateButton.IsEnabled = true;

        if (latest is null)
        {
            UpdateStatusText.Text = "Couldn't check for updates - try again later.";
            return;
        }

        if (UpdateChecker.IsNewer(latest.Version))
        {
            _latestVersion = latest;
            UpdateStatusText.Text = $"Version {latest.Version} is available (you have {UpdateChecker.CurrentVersion}).";
            InstallUpdateButton.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateStatusText.Text = $"You're up to date (version {UpdateChecker.CurrentVersion}).";
        }
    }

    // Only ever fires from this explicit click - checking for an update
    // never downloads or installs anything on its own. Downloads in the
    // background and swaps the files in place (see SelfUpdateService)
    // instead of just opening a browser tab - the app closes itself once
    // the download finishes so the swap can happen, then relaunches on its
    // own, so there's nothing further to do here on success.
    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latestVersion is null) return;

        InstallUpdateButton.IsEnabled = false;
        CheckForUpdateButton.IsEnabled = false;
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "Downloading update...";

        var progress = new Progress<double>(percent =>
        {
            UpdateProgressBar.Value = percent;
            UpdateStatusText.Text = $"Downloading update... {percent:F0}%";
        });

        try
        {
            await SelfUpdateService.DownloadAndApplyAsync(_api, _latestVersion.PortableUrl, progress);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update failed: {ex.Message}";
            InstallUpdateButton.IsEnabled = true;
            CheckForUpdateButton.IsEnabled = true;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
        }
    }
}
