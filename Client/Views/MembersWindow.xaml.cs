using System.Collections.ObjectModel;
using System.Windows;
using DiscordClone.Client.Services;

namespace DiscordClone.Client.Views;

public class MemberListItem
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public partial class MembersWindow : Window
{
    private readonly ApiService _api;
    private readonly int _serverId;
    private readonly ObservableCollection<MemberListItem> _members = new();

    public MembersWindow(ApiService api, int serverId)
    {
        InitializeComponent();
        _api = api;
        _serverId = serverId;
        MemberList.ItemsSource = _members;

        Loaded += async (_, _) => await LoadMembersAsync();
    }

    private async Task LoadMembersAsync()
    {
        var members = await _api.GetMembersAsync(_serverId);
        _members.Clear();
        foreach (var m in members)
            _members.Add(new MemberListItem { UserId = m.UserId, Username = m.Username, Role = m.Role });
    }

    private async void GenerateInviteButton_Click(object sender, RoutedEventArgs e)
    {
        var invite = await _api.CreateInviteAsync(_serverId, expiresInHours: 24 * 7); // 1 week
        InviteCodeBox.Text = invite?.Code ?? "Failed to generate invite (are you an owner/moderator?)";
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
}
