using System.Windows;
using Voiceover.Client.Models;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

// One checkbox per ServerPermission bit - Owner-only (see MainWindow's
// EditPermissionsVisibility gate), only ever opened for a Moderator target.
public partial class EditPermissionsWindow : FluentWindow
{
    private readonly ApiService _api;
    private readonly int _serverId;
    private readonly int _userId;

    public EditPermissionsWindow(ApiService api, int serverId, int userId, string username, ServerPermission current)
    {
        InitializeComponent();
        _api = api;
        _serverId = serverId;
        _userId = userId;

        MemberNameText.Text = $"Permissions for {username}";
        ManageChannelsCheck.IsChecked = current.HasFlag(ServerPermission.ManageChannels);
        KickMembersCheck.IsChecked = current.HasFlag(ServerPermission.KickMembers);
        ManageMessagesCheck.IsChecked = current.HasFlag(ServerPermission.ManageMessages);
        MuteMembersCheck.IsChecked = current.HasFlag(ServerPermission.MuteMembers);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var permissions = ServerPermission.None;
        if (ManageChannelsCheck.IsChecked == true) permissions |= ServerPermission.ManageChannels;
        if (KickMembersCheck.IsChecked == true) permissions |= ServerPermission.KickMembers;
        if (ManageMessagesCheck.IsChecked == true) permissions |= ServerPermission.ManageMessages;
        if (MuteMembersCheck.IsChecked == true) permissions |= ServerPermission.MuteMembers;

        var success = await _api.SetPermissionsAsync(_serverId, _userId, permissions);
        if (!success)
        {
            System.Windows.MessageBox.Show("Could not save permissions.", "Error",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
