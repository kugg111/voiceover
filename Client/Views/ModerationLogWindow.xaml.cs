using System.Collections.ObjectModel;
using System.Windows;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

public class ModerationLogItem
{
    public string ActorUsername { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? TargetUsername { get; set; }
    public string? Details { get; set; }
    public string TimeDisplay { get; set; } = string.Empty;
    public Visibility DetailsVisibility => string.IsNullOrEmpty(Details) ? Visibility.Collapsed : Visibility.Visible;
}

public partial class ModerationLogWindow : FluentWindow
{
    private readonly ApiService _api;
    private readonly int _serverId;
    private readonly ObservableCollection<ModerationLogItem> _entries = new();

    public ModerationLogWindow(ApiService api, int serverId)
    {
        InitializeComponent();
        _api = api;
        _serverId = serverId;
        LogList.ItemsSource = _entries;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var entries = await _api.GetModerationLogAsync(_serverId);

        _entries.Clear();
        foreach (var e in entries)
        {
            _entries.Add(new ModerationLogItem
            {
                ActorUsername = e.ActorUsername,
                Action = e.Action,
                TargetUsername = e.TargetUsername,
                Details = e.Details,
                TimeDisplay = e.CreatedAt.ToLocalTime().ToString("g")
            });
        }

        EmptyStateText.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
