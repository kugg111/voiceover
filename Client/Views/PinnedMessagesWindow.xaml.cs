using System.Collections.ObjectModel;
using System.Windows;
using Voiceover.Client.Models;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

public class PinnedMessageItem
{
    public int Id { get; set; }
    public string AuthorUsername { get; set; } = string.Empty;
    public string? AuthorAvatarUrl { get; set; }
    public string Content { get; set; } = string.Empty;
    public string TimeDisplay { get; set; } = string.Empty;
    public Visibility CanUnpinVisibility { get; set; }
}

public partial class PinnedMessagesWindow : FluentWindow
{
    private readonly ApiService _api;
    private readonly int _serverId;
    private readonly int _channelId;
    private readonly bool _canManage;
    private readonly ObservableCollection<PinnedMessageItem> _pinned = new();

    public PinnedMessagesWindow(ApiService api, int serverId, int channelId, bool canManage)
    {
        InitializeComponent();
        _api = api;
        _serverId = serverId;
        _channelId = channelId;
        _canManage = canManage;
        PinnedList.ItemsSource = _pinned;

        Loaded += async (_, _) => await LoadPinnedAsync();
    }

    private async Task LoadPinnedAsync()
    {
        var pinned = await _api.GetPinnedMessagesAsync(_channelId);

        _pinned.Clear();
        foreach (var m in pinned)
        {
            var content = await _api.E2ee.DecryptForServerAsync(_serverId, m.Content);
            _pinned.Add(new PinnedMessageItem
            {
                Id = m.Id,
                AuthorUsername = m.AuthorUsername,
                AuthorAvatarUrl = App.ResolveUploadUrl(m.AuthorAvatarUrl),
                Content = content,
                TimeDisplay = m.SentAt.ToLocalTime().ToString("g"),
                CanUnpinVisibility = _canManage ? Visibility.Visible : Visibility.Collapsed
            });
        }

        EmptyStateText.Visibility = _pinned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UnpinButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int messageId }) return;

        var success = await _api.UnpinMessageAsync(_channelId, messageId);
        if (success)
        {
            var item = _pinned.FirstOrDefault(p => p.Id == messageId);
            if (item is not null) _pinned.Remove(item);
            EmptyStateText.Visibility = _pinned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
