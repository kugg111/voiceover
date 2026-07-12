using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Voiceover.Client.Models;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace Voiceover.Client.Views;

public partial class SettingsWindow : FluentWindow
{
    private readonly ApiService _api;
    private VersionInfo? _latestVersion;

    public SettingsWindow(ApiService api, VoiceService? voice)
    {
        InitializeComponent();
        _api = api;

        if (voice is not null) VoicePanel.Initialize(voice);

        AccountAvatarView.DisplayName = _api.CurrentUsername ?? "?";
        AccountAvatarView.ImageUrl = _api.CurrentUserAvatarUrl;
        AccountUsernameText.Text = _api.CurrentUsername;

        UpdateStatusText.Text = $"You're on version {UpdateChecker.CurrentVersion}" +
            (UpdateChecker.IsInstalled ? " (installed)." : " (portable).");

        ShowAccountTab();
    }

    private void AccountTabButton_Click(object sender, RoutedEventArgs e) => ShowAccountTab();
    private void VoiceTabButton_Click(object sender, RoutedEventArgs e) => ShowVoiceTab();
    private void UpdateTabButton_Click(object sender, RoutedEventArgs e) => ShowUpdateTab();

    private void ShowAccountTab()
    {
        AccountTabContent.Visibility = Visibility.Visible;
        VoiceTabContent.Visibility = Visibility.Collapsed;
        UpdateTabContent.Visibility = Visibility.Collapsed;
        SetActiveTab(AccountTabButton, VoiceTabButton, UpdateTabButton);
    }

    private void ShowVoiceTab()
    {
        AccountTabContent.Visibility = Visibility.Collapsed;
        VoiceTabContent.Visibility = Visibility.Visible;
        UpdateTabContent.Visibility = Visibility.Collapsed;
        SetActiveTab(VoiceTabButton, AccountTabButton, UpdateTabButton);
    }

    private void ShowUpdateTab()
    {
        AccountTabContent.Visibility = Visibility.Collapsed;
        VoiceTabContent.Visibility = Visibility.Collapsed;
        UpdateTabContent.Visibility = Visibility.Visible;
        SetActiveTab(UpdateTabButton, AccountTabButton, VoiceTabButton);
    }

    private void SetActiveTab(Button active, params Button[] inactive)
    {
        active.Background = (Brush)FindResource("AccentBlurple");
        active.Foreground = Brushes.White;
        foreach (var button in inactive)
        {
            button.Background = Brushes.Transparent;
            button.Foreground = (Brush)FindResource("TextNormal");
        }
    }

    private async void ChangeAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.webp"
        };
        if (dialog.ShowDialog() != true) return;

        ChangeAvatarButton.IsEnabled = false;
        AccountStatusText.Text = "Uploading...";

        var (upload, error) = await _api.UploadFileAsync(dialog.FileName);
        if (upload is null)
        {
            AccountStatusText.Text = "";
            MessageBox.Show(error ?? "Upload failed.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ChangeAvatarButton.IsEnabled = true;
            return;
        }

        var success = await _api.SetMyAvatarAsync(upload.Url);
        ChangeAvatarButton.IsEnabled = true;

        if (!success)
        {
            AccountStatusText.Text = "";
            MessageBox.Show("Could not update your avatar.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Not persisted server-side just by uploading - the app's own idea
        // of "your avatar" needs updating too, same as every other place
        // that reads ApiService.CurrentUserAvatarUrl (MainWindow refreshes
        // its own copy from this once the dialog closes - see
        // MyAvatarBorder_MouseLeftButtonUp).
        _api.CurrentUserAvatarUrl = App.ResolveUploadUrl(upload.Url);
        AccountAvatarView.ImageUrl = _api.CurrentUserAvatarUrl;
        AccountStatusText.Text = "Avatar updated.";
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
