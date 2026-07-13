using System.Collections.ObjectModel;
using System.Windows;
using Voiceover.Client.Models;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

public class CallHistoryItem
{
    public string OtherUsername { get; set; } = string.Empty;
    public string? OtherUserAvatarUrl { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string SummaryColor { get; set; } = "#B9BBBE";
    public string TimeDisplay { get; set; } = string.Empty;
}

public partial class CallHistoryWindow : FluentWindow
{
    private readonly ApiService _api;
    private readonly ObservableCollection<CallHistoryItem> _calls = new();

    public CallHistoryWindow(ApiService api)
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
