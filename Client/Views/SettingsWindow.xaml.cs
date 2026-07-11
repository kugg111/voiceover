using System.Diagnostics;
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

        UpdateStatusText.Text = $"You're on version {UpdateChecker.CurrentVersion}.";
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
    // never downloads or installs anything on its own.
    private void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latestVersion is null) return;

        // Hands off to the browser/shell rather than silently self-replacing
        // a running single-file exe - that needs a separate updater process,
        // real extra complexity not worth it for a friends-scale app.
        var url = UpdateChecker.IsInstalled ? _latestVersion.InstallerUrl : _latestVersion.PortableUrl;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
