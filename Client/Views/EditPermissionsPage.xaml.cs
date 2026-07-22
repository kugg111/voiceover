using System.Windows.Controls;
using Voiceover.Client.Models;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

// One checkbox per ServerPermission bit - Owner-only (see MainWindow's
// EditPermissionsVisibility gate), only ever opened for a Moderator target.
public partial class EditPermissionsPage : UserControl
{
    private readonly MainWindow _mainWindow;
    private readonly ApiService _api;
    private readonly int _serverId;
    private readonly int _userId;

    public EditPermissionsPage(MainWindow mainWindow, ApiService api, int serverId, int userId, string username, ServerPermission current)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _api = api;
        _serverId = serverId;
        _userId = userId;

        MemberNameText.Text = $"Permissions for {username}";
        ManageChannelsCheck.IsChecked = current.HasFlag(ServerPermission.ManageChannels);
        KickMembersCheck.IsChecked = current.HasFlag(ServerPermission.KickMembers);
        ManageMessagesCheck.IsChecked = current.HasFlag(ServerPermission.ManageMessages);
        MuteMembersCheck.IsChecked = current.HasFlag(ServerPermission.MuteMembers);
        MentionEveryoneCheck.IsChecked = current.HasFlag(ServerPermission.MentionEveryone);
        ManageRolesCheck.IsChecked = current.HasFlag(ServerPermission.ManageRoles);
        ManageServerCheck.IsChecked = current.HasFlag(ServerPermission.ManageServer);
        ViewAuditLogCheck.IsChecked = current.HasFlag(ServerPermission.ViewAuditLog);
    }

    private async void SaveButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var permissions = ServerPermission.None;
        if (ManageChannelsCheck.IsChecked == true) permissions |= ServerPermission.ManageChannels;
        if (KickMembersCheck.IsChecked == true) permissions |= ServerPermission.KickMembers;
        if (ManageMessagesCheck.IsChecked == true) permissions |= ServerPermission.ManageMessages;
        if (MuteMembersCheck.IsChecked == true) permissions |= ServerPermission.MuteMembers;
        if (MentionEveryoneCheck.IsChecked == true) permissions |= ServerPermission.MentionEveryone;
        if (ManageRolesCheck.IsChecked == true) permissions |= ServerPermission.ManageRoles;
        if (ManageServerCheck.IsChecked == true) permissions |= ServerPermission.ManageServer;
        if (ViewAuditLogCheck.IsChecked == true) permissions |= ServerPermission.ViewAuditLog;

        var success = await _api.SetPermissionsAsync(_serverId, _userId, permissions);
        if (!success)
        {
            await _mainWindow.AlertAsync("Error", "Could not save permissions.");
            return;
        }

        _mainWindow.GoBack();
    }

    private void CancelButton_Click(object sender, System.Windows.RoutedEventArgs e) => _mainWindow.GoBack();
}
