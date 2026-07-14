using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Voiceover.Client.Models;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

public class CallHistoryItem
{
    public int Id { get; set; }
    public string OtherUsername { get; set; } = string.Empty;
    public string? OtherUserAvatarUrl { get; set; }
    public string Summary { get; set; } = string.Empty;

    // A hex string, not a Brush - CallHistoryPage.xaml binds this directly
    // to a TextBlock.Foreground (a Brush property). That works because WPF's
    // binding engine falls back to the target property's own TypeConverter
    // (BrushConverter, which parses "#RRGGBB") when the source value is a
    // string, but it's an implicit fallback rather than an explicit
    // IValueConverter - if this binding is ever rewritten to go through one,
    // this needs to become a real Brush at that point instead.
    public string SummaryColor { get; set; } = "#B9BBBE";
    public string TimeDisplay { get; set; } = string.Empty;
}

public partial class CallHistoryPage : UserControl
{
    private const int PageSize = 50;

    private readonly ApiService _api;
    private readonly ObservableCollection<CallHistoryItem> _calls = new();
    private bool _hasMore;
    private bool _isLoadingMore;

    public CallHistoryPage(ApiService api)
    {
        InitializeComponent();
        _api = api;
        CallList.ItemsSource = _calls;

        Loaded += async (_, _) => await LoadCallHistoryAsync();
    }

    private async Task LoadCallHistoryAsync()
    {
        var history = await _api.GetCallHistoryAsync();

        _calls.Clear();
        foreach (var c in history)
            _calls.Add(ToCallHistoryItem(c));

        EmptyStateText.Visibility = _calls.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetHasMore(history.Count);
    }

    private async void LoadMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoadingMore || !_hasMore || _calls.Count == 0) return;

        _isLoadingMore = true;
        LoadMoreButton.IsEnabled = false;

        var oldest = _calls[^1].Id;
        var older = await _api.GetCallHistoryAsync(oldest);
        foreach (var c in older)
            _calls.Add(ToCallHistoryItem(c));

        SetHasMore(older.Count);
        _isLoadingMore = false;
        LoadMoreButton.IsEnabled = true;
    }

    private void SetHasMore(int lastPageCount)
    {
        _hasMore = lastPageCount >= PageSize;
        LoadMoreButton.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;
    }

    private static CallHistoryItem ToCallHistoryItem(CallRecordResponse c)
    {
        var direction = c.WasIncoming ? "Incoming" : "Outgoing";
        var (summary, color) = c.Outcome switch
        {
            "Completed" => ($"{direction} · {FormatDuration(c.DurationSeconds)}", "#43B581"),
            "Missed" => ("Missed call", "#F04747"),
            "Declined" => ($"{direction} · Declined", "#F04747"),
            "Cancelled" => ($"{direction} · Cancelled", "#B9BBBE"),
            _ => (direction, "#B9BBBE")
        };

        return new CallHistoryItem
        {
            Id = c.Id,
            OtherUsername = c.OtherUsername,
            OtherUserAvatarUrl = App.ResolveUploadUrl(c.OtherUserAvatarUrl),
            Summary = summary,
            SummaryColor = color,
            TimeDisplay = c.EndedAt.ToLocalTime().ToString("g")
        };
    }

    private static string FormatDuration(int? seconds)
    {
        if (seconds is not { } s || s < 0) return "Completed";
        var span = TimeSpan.FromSeconds(s);
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }
}
