using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

public class SearchResultItem
{
    public int Id { get; set; }
    public string AuthorUsername { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string TimeDisplay { get; set; } = string.Empty;
}

// Client-side only, by necessity - channel/DM content is E2EE ciphertext
// (see Client/Services/E2eeService.cs), so the server can never search it;
// this fetches the whole conversation's history (walking the same
// beforeId-cursor pagination "Load Older Messages" already uses, capped at
// a few thousand messages so an extremely long-lived channel doesn't hang
// the page indefinitely), decrypts every page client-side, and filters in
// memory. Clicking a result closes this page and jumps to/highlights the
// message in the main view (see MainWindow.JumpToMessageAsync).
public partial class MessageSearchPage : UserControl
{
    private const int FetchCap = 2000;

    private readonly MainWindow _mainWindow;
    private readonly ApiService _api;
    private readonly int? _serverId;
    private readonly int? _channelId;
    private readonly int? _otherUserId;
    private readonly string _otherUsername;
    private readonly ObservableCollection<SearchResultItem> _results = new();
    private bool _isSearching;

    // Channel search: pass serverId + channelId, otherUserId/otherUsername null.
    // DM search: pass otherUserId + otherUsername, serverId/channelId null.
    public MessageSearchPage(MainWindow mainWindow, ApiService api, int? serverId, int? channelId, int? otherUserId, string otherUsername)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _api = api;
        _serverId = serverId;
        _channelId = channelId;
        _otherUserId = otherUserId;
        _otherUsername = otherUsername;
        ResultsList.ItemsSource = _results;

        StatusText.Text = "Type a search term and press Enter or click Search.";
        Loaded += (_, _) => SearchBox.Focus();
    }

    // The whole conversation (channel or DM) this search is scoped to is
    // already open behind this page (MessageSearchPage is only ever reached
    // via its own conversation's search button) - GoBack reveals it again,
    // then JumpToMessageAsync scrolls/highlights the target row, walking
    // "load older" pages first if it isn't loaded yet.
    private async void SearchResult_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SearchResultItem result }) return;

        _mainWindow.GoBack();
        await _mainWindow.JumpToMessageAsync(result.Id);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = RunSearchAsync();
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _ = RunSearchAsync();

    private async Task RunSearchAsync()
    {
        if (_isSearching) return;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;

        _isSearching = true;
        SearchButton.IsEnabled = false;
        StatusText.Text = "Searching...";
        _results.Clear();

        try
        {
            var matches = await FetchAndFilterAsync(query);
            foreach (var m in matches)
                _results.Add(m);

            StatusText.Text = matches.Count == 0
                ? "No matches found."
                : $"{matches.Count} match{(matches.Count == 1 ? "" : "es")} found.";
        }
        catch
        {
            StatusText.Text = "Search failed - try again.";
        }
        finally
        {
            _isSearching = false;
            SearchButton.IsEnabled = true;
        }
    }

    private async Task<List<SearchResultItem>> FetchAndFilterAsync(string query)
    {
        var results = new List<SearchResultItem>();
        int? beforeId = null;
        var fetched = 0;

        while (fetched < FetchCap)
        {
            List<SearchResultItem> page;
            int pageRawCount;

            if (_channelId.HasValue && _serverId.HasValue)
            {
                var history = await _api.GetMessageHistoryAsync(_channelId.Value, beforeId);
                pageRawCount = history.Count;
                if (pageRawCount == 0) break;

                page = new List<SearchResultItem>();
                foreach (var m in history)
                {
                    var content = await _api.E2ee.DecryptChannelMessageAsync(m.AuthorId, m.WrappedKeyForMe, m.Content);
                    if (content.Contains(query, StringComparison.OrdinalIgnoreCase))
                        page.Add(new SearchResultItem { Id = m.Id, AuthorUsername = m.AuthorUsername, Content = content, TimeDisplay = m.SentAt.ToLocalTime().ToString("g") });
                }
                beforeId = history[0].Id;
            }
            else if (_otherUserId.HasValue)
            {
                var history = await _api.GetDmHistoryAsync(_otherUserId.Value, beforeId);
                pageRawCount = history.Count;
                if (pageRawCount == 0) break;

                page = new List<SearchResultItem>();
                foreach (var m in history)
                {
                    var content = await _api.E2ee.DecryptAsync(_otherUserId.Value, m.Content);
                    if (content.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        var author = m.SenderId == _api.CurrentUserId ? "You" : _otherUsername;
                        page.Add(new SearchResultItem { Id = m.Id, AuthorUsername = author, Content = content, TimeDisplay = m.SentAt.ToLocalTime().ToString("g") });
                    }
                }
                beforeId = history[0].Id;
            }
            else
            {
                break;
            }

            results.AddRange(page);
            fetched += pageRawCount;
            if (pageRawCount < 50) break; // fewer than a full page - reached the start of history
        }

        // Fetch order is neither fully ascending nor descending: each page
        // is oldest-first internally, but pages themselves arrive newest-
        // page-first (beforeId walks backward one page at a time) - a plain
        // Reverse() would just flip that same jumbled interleaving. Sorting
        // by Id once at the end is what actually gives a clean
        // most-recent-first order.
        results.Sort((a, b) => b.Id.CompareTo(a.Id));
        return results;
    }
}
