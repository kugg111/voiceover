using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voiceover.Client.Models;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;

namespace Voiceover.Client.Views;

public class UserSearchResultItem
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
}

public class DmMessageListItem
{
    public string AuthorLabel { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public partial class DirectMessageWindow : FluentWindow
{
    private readonly ApiService _api;
    private readonly SignalRService _hub;

    private int? _activeUserId;
    private string? _activeUsername;

    private readonly ObservableCollection<UserSearchResultItem> _searchResults = new();
    private readonly ObservableCollection<DmMessageListItem> _dmMessages = new();

    public DirectMessageWindow(ApiService api, SignalRService hub)
    {
        InitializeComponent();
        _api = api;
        _hub = hub;

        SearchResultsList.ItemsSource = _searchResults;
        DmMessageList.ItemsSource = _dmMessages;

        _hub.DirectMessageReceived += OnDirectMessageReceived;
        Closed += (_, _) => _hub.DirectMessageReceived -= OnDirectMessageReceived;
    }

    private async void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (query.Length < 2)
        {
            _searchResults.Clear();
            return;
        }

        var results = await _api.SearchUsersAsync(query);
        _searchResults.Clear();
        foreach (var r in results.Where(r => r.Id != _api.CurrentUserId))
            _searchResults.Add(new UserSearchResultItem { Id = r.Id, Username = r.Username });
    }

    private async void UserResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int userId } button) return;

        _activeUserId = userId;
        _activeUsername = (button.Content as string) ?? "user";
        ConversationHeader.Text = $"@{_activeUsername}";

        var history = await _api.GetDmHistoryAsync(userId);
        _dmMessages.Clear();
        foreach (var m in history)
            _dmMessages.Add(ToListItem(m));

        DmScroll.ScrollToEnd();
    }

    private async void SendDmButton_Click(object sender, RoutedEventArgs e) => await SendCurrentDm();

    private async void DmInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await SendCurrentDm();
    }

    private async Task SendCurrentDm()
    {
        if (_activeUserId is null || string.IsNullOrWhiteSpace(DmInput.Text)) return;

        await _hub.SendDirectMessageAsync(_activeUserId.Value, DmInput.Text.Trim());
        DmInput.Clear();
    }

    private void OnDirectMessageReceived(DirectMessageResponse dm)
    {
        // Only show it if it belongs to the conversation currently open.
        if (_activeUserId is null) return;
        var belongsToActiveConvo = (dm.SenderId == _activeUserId || dm.RecipientId == _activeUserId);
        if (!belongsToActiveConvo) return;

        Dispatcher.Invoke(() =>
        {
            _dmMessages.Add(ToListItem(dm));
            DmScroll.ScrollToEnd();
        });
    }

    private DmMessageListItem ToListItem(DirectMessageResponse dm) => new()
    {
        AuthorLabel = dm.SenderId == _api.CurrentUserId ? "You" : (_activeUsername ?? "them"),
        Content = dm.Content
    };
}
