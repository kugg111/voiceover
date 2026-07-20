using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Voiceover.Client.Models;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

public class BlockedUserItem
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
}

public partial class SettingsPage : UserControl
{
    private readonly MainWindow _mainWindow;
    private readonly ApiService _api;
    private VersionInfo? _latestVersion;
    private readonly ObservableCollection<BlockedUserItem> _blockedUsers = new();

    public SettingsPage(MainWindow mainWindow, ApiService api, VoiceService? voice)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _api = api;

        if (voice is not null) VoicePanel.Initialize(voice);

        AccountAvatarView.DisplayName = _api.CurrentUsername ?? "?";
        AccountAvatarView.ImageUrl = _api.CurrentUserAvatarUrl;
        AccountUsernameText.Text = _api.CurrentUsername;
        CustomStatusBox.Text = _api.CurrentUserCustomStatus ?? "";
        UpdateCustomStatusCounter();

        MinimizeToTrayCheck.IsChecked = TraySettingsStorage.MinimizeToTrayEnabled;

        RefreshTwoFactorStatus();

        UpdateStatusText.Text = $"You're on version {UpdateChecker.CurrentVersion}" +
            (UpdateChecker.IsInstalled ? " (installed)." : " (portable).");

        BlockedUsersList.ItemsSource = _blockedUsers;

        ShowAccountTab();

        // My Account may have just changed the avatar - refresh MainWindow's
        // own copy once this page leaves the PageHost, the same timing the
        // old Window-based version refreshed it after ShowDialog() returned.
        Unloaded += (_, _) => _mainWindow.RefreshMyAvatarView();

        Loaded += async (_, _) => await LoadBlockedUsersAsync();
    }

    private void MinimizeToTrayCheck_Changed(object sender, RoutedEventArgs e) =>
        TraySettingsStorage.MinimizeToTrayEnabled = MinimizeToTrayCheck.IsChecked == true;

    private void RefreshTwoFactorStatus()
    {
        var enabled = _api.CurrentUserTwoFactorEnabled;
        TwoFactorStatusText.Text = enabled
            ? "Two-factor authentication is enabled."
            : "Two-factor authentication is disabled. Enabling it requires an authenticator code on top of your password every time you log in.";
        Enable2FaButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        Disable2FaButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    // Enrollment: setup (generates a pending secret) -> QR/code modal ->
    // confirm (verifies the code, actually turns 2FA on, issues recovery
    // codes) -> show the codes exactly once. Any step can be backed out of
    // (setup's pending secret is simply discarded/overwritten next time)
    // without ever having enabled 2FA.
    private async void Enable2FaButton_Click(object sender, RoutedEventArgs e)
    {
        var setup = await _api.Setup2FaAsync();
        if (setup is null)
        {
            await _mainWindow.AlertAsync("Error", "Could not start 2FA setup.");
            return;
        }

        var code = await _mainWindow.ShowTotpSetupAsync(setup.Secret, setup.QrCodePngBase64);
        if (code is null) return; // cancelled

        var (recoveryCodes, error) = await _api.Confirm2FaAsync(code);
        if (recoveryCodes is null)
        {
            await _mainWindow.AlertAsync("Error", error ?? "Invalid code.");
            return;
        }

        _api.CurrentUserTwoFactorEnabled = true;
        await _mainWindow.ShowRecoveryCodesAsync(recoveryCodes);
        RefreshTwoFactorStatus();
    }

    private async void Disable2FaButton_Click(object sender, RoutedEventArgs e)
    {
        var password = await _mainWindow.PromptPasswordAsync("Disable Two-Factor Authentication",
            "Enter your password to confirm - this removes the extra login step and your saved recovery codes.");
        if (string.IsNullOrEmpty(password)) return;

        var (success, error) = await _api.Disable2FaAsync(password);
        if (!success)
        {
            await _mainWindow.AlertAsync("Error", error ?? "Could not disable 2FA.");
            return;
        }

        _api.CurrentUserTwoFactorEnabled = false;
        RefreshTwoFactorStatus();
    }

    private async Task LoadBlockedUsersAsync()
    {
        List<BlockedUserResponse> blocked;
        try
        {
            blocked = await _api.GetBlockedUsersAsync();
        }
        catch
        {
            // Same reasoning as BanListPage.LoadAsync's own try/catch - a
            // network blip shouldn't surface as an uncaught exception out of
            // this Loaded event's async lambda.
            return;
        }

        _blockedUsers.Clear();
        foreach (var b in blocked) _blockedUsers.Add(new BlockedUserItem { UserId = b.UserId, Username = b.Username });
        BlockedEmptyText.Visibility = _blockedUsers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UnblockButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int userId }) return;

        var success = await _api.UnblockUserAsync(userId);
        if (!success) return;

        var item = _blockedUsers.FirstOrDefault(b => b.UserId == userId);
        if (item is not null) _blockedUsers.Remove(item);
        BlockedEmptyText.Visibility = _blockedUsers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            await _mainWindow.AlertAsync("Error", error ?? "Upload failed.");
            ChangeAvatarButton.IsEnabled = true;
            return;
        }

        var success = await _api.SetMyAvatarAsync(upload.Url);
        ChangeAvatarButton.IsEnabled = true;

        if (!success)
        {
            AccountStatusText.Text = "";
            await _mainWindow.AlertAsync("Error", "Could not update your avatar.");
            return;
        }

        // Not persisted server-side just by uploading - the app's own idea
        // of "your avatar" needs updating too, same as every other place
        // that reads ApiService.CurrentUserAvatarUrl (MainWindow refreshes
        // its own copy from this once this page unloads - see
        // MainWindow.RefreshMyAvatarView).
        _api.CurrentUserAvatarUrl = App.ResolveUploadUrl(upload.Url);
        AccountAvatarView.ImageUrl = _api.CurrentUserAvatarUrl;
        AccountStatusText.Text = "Avatar updated.";
    }

    private void CustomStatusBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateCustomStatusCounter();

    private async void ExportDataButton_Click(object sender, RoutedEventArgs e)
    {
        ExportDataButton.IsEnabled = false;
        var data = await _api.ExportMyDataAsync();
        ExportDataButton.IsEnabled = true;

        if (data is null)
        {
            await _mainWindow.AlertAsync("Error", "Could not export your data.");
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
        if (!await _mainWindow.ConfirmAsync("Delete Account",
            "This permanently deletes your account and everything tied to it. This cannot be undone. Are you sure?",
            "Delete", destructive: true)) return;

        // Servers with 0 other members just get deleted, and with exactly 1
        // that member is auto-promoted server-side - only surface a picker
        // for servers where a choice actually needs to be made.
        List<OwnershipTransfer>? transfers = null;
        var needingTransfer = await _api.GetOwnedServersNeedingTransferAsync();
        if (needingTransfer.Count > 0)
        {
            transfers = await _mainWindow.PickOwnershipTransfersAsync(needingTransfer);
            if (transfers is null) return;
        }

        DeleteAccountButton.IsEnabled = false;
        var (success, error) = await _api.DeleteMyAccountAsync(transfers);
        if (!success)
        {
            DeleteAccountButton.IsEnabled = true;
            await _mainWindow.AlertAsync("Error", error ?? "Could not delete your account.");
            return;
        }

        // Deleting your own account ends the session the same way logging
        // out does.
        SessionStorage.Clear();
        _mainWindow.HandleAccountDeleted();
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
