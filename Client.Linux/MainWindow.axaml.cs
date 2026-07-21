using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Voiceover.Client.Services;

namespace Voiceover.Client.Linux;

public class ServerListItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // No AvatarView port yet (see plan) - the server rail shows initials
    // instead of the real IconUrl image until that lands.
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
}

public class ChannelListItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string DisplayName => Type == "Text" ? $"# {Name}" : $"🔊 {Name}";
}

public partial class MainWindow : Window
{
    // = null! - only the designer-only parameterless constructor below
    // ever leaves this unset, and that constructor is never used at real
    // runtime (see its own comment).
    private readonly ApiService _api = null!;

    private readonly ObservableCollection<ServerListItem> _servers = new();
    private readonly ObservableCollection<ChannelListItem> _textChannels = new();
    private readonly ObservableCollection<ChannelListItem> _voiceChannels = new();
    private int? _currentServerId;

    // Parameterless constructor required by Avalonia's XAML loader/designer -
    // never actually used at runtime, App.axaml.cs always goes through
    // LoginWindow -> LoginCompletion first (mirrors the WPF client, whose
    // App.xaml.cs never shows a MainWindow without a real ApiService either).
    public MainWindow() : this(null!) { }

    public MainWindow(ApiService api)
    {
        _api = api;
        InitializeComponent();
        WelcomeText.Text = $"Welcome, {_api?.CurrentUsername}!";

        ServerList.ItemsSource = _servers;
        TextChannelList.ItemsSource = _textChannels;
        VoiceChannelList.ItemsSource = _voiceChannels;

        if (_api is not null) _ = LoadServersAsync();
    }

    // Scoped deliberately to read-only navigation for this increment - no
    // message loading/SignalR/E2EE yet, that's a separate, larger slice
    // (see the Linux client plan's Phase 1 description).
    private async Task LoadServersAsync()
    {
        var servers = await _api.GetMyServersAsync();
        _servers.Clear();
        foreach (var s in servers)
            _servers.Add(new ServerListItem { Id = s.Id, Name = s.Name });
    }

    private async Task RefreshChannelsAsync(int serverId)
    {
        var channels = await _api.GetChannelsAsync(serverId);

        _textChannels.Clear();
        _voiceChannels.Clear();
        foreach (var c in channels.OrderBy(c => c.Position))
        {
            var item = new ChannelListItem { Id = c.Id, Name = c.Name, Type = c.Type };
            if (c.Type == "Text") _textChannels.Add(item);
            else _voiceChannels.Add(item);
        }
    }

    private async void ServerButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int serverId }) return;

        _currentServerId = serverId;
        var server = _servers.FirstOrDefault(s => s.Id == serverId);
        ServerNameText.Text = server?.Name ?? "Server";

        SelectedChannelText.Text = "Select a channel";
        SelectedChannelSubtext.IsVisible = false;

        await RefreshChannelsAsync(serverId);
    }

    private void ChannelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int channelId }) return;

        var channel = _textChannels.Concat(_voiceChannels).FirstOrDefault(c => c.Id == channelId);
        if (channel is null) return;

        SelectedChannelText.Text = channel.DisplayName;
        SelectedChannelSubtext.IsVisible = true;
    }

    private void LogOutButton_Click(object? sender, RoutedEventArgs e)
    {
        _ = _api.LogoutAsync();
        SessionStorage.Clear();
        new LoginWindow().Show();
        Close();
    }
}
