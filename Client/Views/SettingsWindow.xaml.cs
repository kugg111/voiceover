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

    // Set right before Close() in DeleteAccountButton_Click. MainWindow
    // checks this after ShowDialog() returns and does the actual
    // LoginWindow/Owner-closing sequence itself - closing our own Owner from
    // inside this window's own event handler, while still nested inside our
    // own ShowDialog() message pump, is a known-risky WPF pattern that was
    // producing a LoginWindow with missing elements.
    public bool AccountWasDeleted { get; private set; }

    public SettingsWindow(ApiService api, VoiceService? voice)
    {
        InitializeComponent();
        _api = api;

        if (voice is not null) VoicePanel.Initialize(voice);

        AccountAvatarView.DisplayName = _api.CurrentUsername ?? "?";
        AccountAvatarView.ImageUrl = _api.CurrentUserAvatarUrl;
        AccountUsernameText.Text = _api.CurrentUsername;
        CustomStatusBox.Text = _api.CurrentUserCustomStatus ?? "";
        UpdateCustomStatusCounter();

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

    private void CustomStatusBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateCustomStatusCounter();

    private async void ExportDataButton_Click(object sender, RoutedEventArgs e)
    {
        ExportDataButton.IsEnabled = false;
        var data = await _api.ExportMyDataAsync();
        ExportDataButton.IsEnabled = true;

        if (data is null)
        {
            MessageBox.Show("Could not export your data.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var dialog = new SaveFileDialog { FileName = "voiceover-data-export.json", Filter = "JSON files (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;

        var json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await System.IO.File.WriteAllTextAsync(dialog.FileName, json);
        AccountStatusText.Text = "Data exported.";
    }

    private async void DeleteAccountButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ConfirmDialog("Delete Account",
            "This permanently deletes your account and everything tied to it. This cannot be undone. Are you sure?",
            "Delete", destructive: true) { Owner = this };
        confirm.ShowDialog();
        if (!confirm.Result) return;

        // Servers with 0 other members just get deleted, and with exactly 1
        // that member is auto-promoted server-side - only surface a picker
        // for servers where a choice actually needs to be made.
        List<OwnershipTransfer>? transfers = null;
        var needingTransfer = await _api.GetOwnedServersNeedingTransferAsync();
        if (needingTransfer.Count > 0)
        {
            var picker = new TransferOwnershipWindow(needingTransfer) { Owner = this };
            picker.ShowDialog();
            if (!picker.Result) return;
            transfers = picker.Selections;
        }

        DeleteAccountButton.IsEnabled = false;
        var (success, error) = await _api.DeleteMyAccountAsync(transfers);
        if (!success)
        {
            DeleteAccountButton.IsEnabled = true;
            MessageBox.Show(error ?? "Could not delete your account.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Deleting your own account ends the session the same way logging
        // out does, but the actual LoginWindow/Owner-closing sequence is
        // done by MainWindow after ShowDialog() returns (see
        // AccountWasDeleted) - not here.
        SessionStorage.Clear();
        AccountWasDeleted = true;
        Close();
    }

    private void UpdateCustomStatusCounter() =>
        CustomStatusCounterText.Text = $"{CustomStatusBox.Text.Length}/{CustomStatusBox.MaxLength}";

    private async void SaveCustomStatusButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCustomStatusButton.IsEnabled = false;
        AccountStatusText.Text = "Saving...";

        var success = await _api.SetMyCustomStatusAsync(CustomStatusBox.Text);
        SaveCustomStatusButton.IsEnabled = true;

        AccountStatusText.Text = success ? "Status updated." : "Could not update your status.";
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
