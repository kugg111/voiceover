using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Voiceover.Client.Services;

namespace Voiceover.Client.Linux;

public class MemberItem
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool CanKick { get; set; }
}

public partial class MembersWindow : Window
{
    private readonly ApiService _api = null!;
    private readonly int _serverId;
    private readonly ObservableCollection<MemberItem> _members = new();

    public MembersWindow() : this(null!, 0) { }

    public MembersWindow(ApiService api, int serverId)
    {
        _api = api;
        _serverId = serverId;
        InitializeComponent();
        MemberList.ItemsSource = _members;

        if (_api is null) return;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var members = await _api.GetMembersAsync(_serverId);
        var self = members.FirstOrDefault(m => m.UserId == _api.CurrentUserId);
        var canManage = self is { Role: "Owner" or "Moderator" };

        _members.Clear();
        foreach (var m in members)
            _members.Add(new MemberItem
            {
                UserId = m.UserId,
                Username = m.Username,
                Role = m.Role,
                CanKick = canManage && m.Role != "Owner" && m.UserId != _api.CurrentUserId
            });
    }

    private async void KickButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int userId }) return;

        var success = await _api.KickMemberAsync(_serverId, userId);
        if (!success) return;

        var item = _members.FirstOrDefault(m => m.UserId == userId);
        if (item is not null) _members.Remove(item);
    }
}
