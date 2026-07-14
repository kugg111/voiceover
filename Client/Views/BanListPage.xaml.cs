using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

public class BannedUserItem
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string ReasonDisplay { get; set; } = string.Empty;
    public string DetailDisplay { get; set; } = string.Empty;
}

public partial class BanListPage : UserControl
{
    private readonly ApiService _api;
    private readonly int _serverId;
    private readonly SignalRService? _hub;
    private readonly Action<int, int>? _onMemberBanChanged;
    private readonly ObservableCollection<BannedUserItem> _bans = new();

    public BanListPage(ApiService api, int serverId, SignalRService? hub = null)
    {
        InitializeComponent();
        _api = api;
        _serverId = serverId;
        _hub = hub;
        BanList.ItemsSource = _bans;

        // Bystander live-refresh - lets this stay open and up to date while
        // another mod bans/unbans someone, instead of needing to be closed
        // and reopened to see the change. hub is optional purely so this
        // constructor still compiles for any future caller without one on
        // hand; MainWindow's own call site always passes it.
        if (_hub is not null)
        {
            _onMemberBanChanged = (serverId2, userId) => Dispatcher.Invoke(() => OnMemberBanChanged(serverId2, userId));
            _hub.MemberBanned += _onMemberBanChanged;
            _hub.MemberUnbanned += _onMemberBanChanged;
            // PageHost.GoBack() sets PageHostContent.Content = null, which
            // fires Unloaded on this page - the same unsubscribe hook Closed
            // used to give the old Window-based version.
            Unloaded += (_, _) =>
            {
                _hub.MemberBanned -= _onMemberBanChanged;
                _hub.MemberUnbanned -= _onMemberBanChanged;
            };
        }

        Loaded += async (_, _) => await LoadAsync();
    }

    private async void OnMemberBanChanged(int serverId, int userId)
    {
        if (serverId != _serverId) return;
        await LoadAsync();
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
