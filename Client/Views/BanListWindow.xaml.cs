using System.Collections.ObjectModel;
using System.Windows;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

public class BannedUserItem
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string ReasonDisplay { get; set; } = string.Empty;
    public string DetailDisplay { get; set; } = string.Empty;
}

public partial class BanListWindow : FluentWindow
{
    private readonly ApiService _api;
    private readonly int _serverId;
    private readonly ObservableCollection<BannedUserItem> _bans = new();

    public BanListWindow(ApiService api, int serverId)
    {
        InitializeComponent();
        _api = api;
        _serverId = serverId;
        BanList.ItemsSource = _bans;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var bans = await _api.GetBansAsync(_serverId);

        _bans.Clear();
        foreach (var b in bans)
        {
            _bans.Add(new BannedUserItem
            {
                UserId = b.UserId,
                Username = b.Username,
                ReasonDisplay = string.IsNullOrEmpty(b.Reason) ? "No reason given" : b.Reason,
                DetailDisplay = $"Banned by {b.BannedByUsername} on {b.CreatedAt.ToLocalTime():g}"
            });
        }

        EmptyStateText.Visibility = _bans.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UnbanButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int userId }) return;

        var success = await _api.UnbanMemberAsync(_serverId, userId);
        if (success)
        {
            var item = _bans.FirstOrDefault(b => b.UserId == userId);
            if (item is not null) _bans.Remove(item);
            EmptyStateText.Visibility = _bans.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
