using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using Button = System.Windows.Controls.Button;

namespace Voiceover.Client.Views;

public class MemberListItem
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool CanChangeRole { get; set; }

    public string RoleButtonLabel => Role == "Moderator" ? "Demote" : "Promote";
    public string NextRole => Role == "Moderator" ? "Member" : "Moderator";
    public Visibility RoleButtonVisibility => CanChangeRole ? Visibility.Visible : Visibility.Collapsed;
}

public class InviteListItem
{
    public string Code { get; set; } = string.Empty;
    public string Display { get; set; } = string.Empty;
}

public partial class MembersWindow : FluentWindow
{
    private readonly ApiService _api;
    private readonly int _serverId;
    private readonly ObservableCollection<MemberListItem> _members = new();
    private readonly ObservableCollection<InviteListItem> _invites = new();

    public MembersWindow(ApiService api, int serverId)
    {
        InitializeComponent();
        _api = api;
        _serverId = serverId;
        MemberList.ItemsSource = _members;
        InviteList.ItemsSource = _invites;

        Loaded += async (_, _) =>
        {
            await LoadMembersAsync();
            await LoadInvitesAsync();
        };
    }

    private async Task LoadMembersAsync()
    {
        var members = await _api.GetMembersAsync(_serverId);
        var isOwner = members.FirstOrDefault(m => m.UserId == _api.CurrentUserId)?.Role == "Owner";

        _members.Clear();
        foreach (var m in members)
            _members.Add(new MemberListItem
            {
                UserId = m.UserId,
                Username = m.Username,
                Role = m.Role,
                CanChangeRole = isOwner && m.Role != "Owner"
            });
    }

    private async Task LoadInvitesAsync()
    {
        var invites = await _api.ListInvitesAsync(_serverId);
        _invites.Clear();
        foreach (var i in invites)
        {
            var expiry = i.ExpiresAt is null ? "never expires" : $"expires {i.ExpiresAt.Value.ToLocalTime():g}";
            var uses = i.MaxUses is null ? $"{i.UseCount} uses" : $"{i.UseCount}/{i.MaxUses} uses";
            _invites.Add(new InviteListItem { Code = i.Code, Display = $"{i.Code}   ·   {expiry}   ·   {uses}" });
        }
    }

    private async void GenerateInviteButton_Click(object sender, RoutedEventArgs e)
    {
        var invite = await _api.CreateInviteAsync(_serverId, expiresInHours: 24 * 7); // 1 week
        InviteCodeBox.Text = invite?.Code ?? "Failed to generate invite (are you an owner/moderator?)";
        await LoadInvitesAsync();
    }

    private async void KickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int userId }) return;

        var confirm = MessageBox.Show("Remove this member from the server?", "Confirm Kick",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var success = await _api.KickMemberAsync(_serverId, userId);
        if (!success)
        {
            MessageBox.Show("Could not kick this member (you may lack permission, or they're the owner).",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await LoadMembersAsync();
    }

    private async void RoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int userId }) return;

        var item = _members.FirstOrDefault(m => m.UserId == userId);
        if (item is null) return;

        var success = await _api.ChangeRoleAsync(_serverId, userId, item.NextRole);
        if (!success)
        {
            MessageBox.Show("Could not change this member's role.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await LoadMembersAsync();
    }
}
