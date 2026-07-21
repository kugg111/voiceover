using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Voiceover.Client.Models;
using Voiceover.Client.Services;

namespace Voiceover.Client.Linux;

public class ServerListItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int OwnerId { get; set; }

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

public class MessageListItem
{
    public int Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public string AuthorUsername { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public string TimeDisplay => SentAt.ToLocalTime().ToString("t");
}

public partial class MainWindow : Window
{
    // = null! - only the designer-only parameterless constructor below
    // ever leaves this unset, and that constructor is never used at real
    // runtime (see its own comment).
    private readonly ApiService _api = null!;
    private readonly SignalRService _hub = new();

    private readonly ObservableCollection<ServerListItem> _servers = new();
    private readonly ObservableCollection<ChannelListItem> _textChannels = new();
    private readonly ObservableCollection<ChannelListItem> _voiceChannels = new();
    private readonly ObservableCollection<MessageListItem> _messages = new();

    // Populated for every server up front (LoadServersAsync) so a live
    // message arriving via SignalR can resolve which server's E2EE key to
    // decrypt with, even for a server whose channel list hasn't been
    // opened yet this session - mirrors the WPF client's own field.
    private readonly Dictionary<int, int> _channelServerIds = new();
    private readonly HashSet<int> _joinedChannelIds = new();

    private int? _currentServerId;
    private int? _currentChannelId;

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
        MessageList.ItemsSource = _messages;

        if (_api is null) return;

        _hub.MessageReceived += OnMessageReceived;
        _hub.ServerKeyRequested += OnServerKeyRequested;
        _hub.ServerKeyProvisioned += OnServerKeyProvisioned;

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await _hub.ConnectAsync(App.HubUrl, _api.GetFreshAccessTokenAsync);
        await LoadServersAsync();
    }

    // Scoped deliberately for this increment: server rail + channel nav +
    // read/send text-channel messages with E2EE. No edit/delete/reactions/
    // replies/pins/attachments/unread badges/DMs/friends/voice yet - each
    // is its own separate slice (see the Linux client plan's Phase 1).
    private async Task LoadServersAsync()
    {
        var servers = await _api.GetMyServersAsync();
        _servers.Clear();
        foreach (var s in servers)
            _servers.Add(new ServerListItem { Id = s.Id, Name = s.Name, OwnerId = s.OwnerId });

        var channelLists = await Task.WhenAll(servers.Select(s => _api.GetChannelsAsync(s.Id)));
        foreach (var channels in channelLists)
        {
            foreach (var c in channels)
                _channelServerIds[c.Id] = c.GuildServerId;

            foreach (var c in channels.Where(c => c.Type == "Text" && _joinedChannelIds.Add(c.Id)))
                await _hub.JoinChannelAsync(c.Id);
        }
    }

    private async Task RefreshChannelsAsync(int serverId)
    {
        var channels = await _api.GetChannelsAsync(serverId);

        _textChannels.Clear();
        _voiceChannels.Clear();
        foreach (var c in channels.OrderBy(c => c.Position))
        {
            _channelServerIds[c.Id] = c.GuildServerId;

            var item = new ChannelListItem { Id = c.Id, Name = c.Name, Type = c.Type };
            if (c.Type == "Text") _textChannels.Add(item);
            else _voiceChannels.Add(item);

            if (c.Type == "Text" && _joinedChannelIds.Add(c.Id))
                await _hub.JoinChannelAsync(c.Id);
        }
    }

    private async void ServerButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int serverId }) return;

        _currentServerId = serverId;
        _currentChannelId = null;
        var server = _servers.FirstOrDefault(s => s.Id == serverId);
        ServerNameText.Text = server?.Name ?? "Server";

        ShowWelcome("Select a channel", subtext: null);

        await RefreshChannelsAsync(serverId);
    }

    private async void ChannelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int channelId }) return;

        var channel = _textChannels.Concat(_voiceChannels).FirstOrDefault(c => c.Id == channelId);
        if (channel is null) return;

        SelectedChannelText.Text = channel.DisplayName;

        if (channel.Type != "Text")
        {
            // Voice channels aren't wired up yet (see plan Phase 2) - showing
            // an inert "join" UI here would be a fake feature, so this just
            // says so instead of pretending to support it.
            _currentChannelId = null;
            ShowWelcome(channel.DisplayName, subtext: "Voice channels aren't supported in this build yet.");
            return;
        }

        _currentChannelId = channelId;
        await _hub.JoinChannelAsync(channelId);
        ShowMessages();
        await LoadChannelHistoryAsync(channelId);
    }

    // Split out from ChannelButton_Click so OnServerKeyProvisioned can
    // re-run it once this device receives a key it didn't have yet -
    // mirrors the WPF client's own LoadChannelHistoryAsync split.
    private async Task LoadChannelHistoryAsync(int channelId)
    {
        if (_currentServerId is not { } serverId) return;

        if (await _api.E2ee.GetServerKeyAsync(serverId) is null)
        {
            var isOwner = _servers.FirstOrDefault(s => s.Id == serverId)?.OwnerId == _api.CurrentUserId;
            if (!isOwner || !await _api.E2ee.GenerateAndUploadServerKeyAsync(serverId))
                await _hub.RequestServerKeyAsync(serverId);
        }

        var history = await _api.GetMessageHistoryAsync(channelId);
        var items = await Task.WhenAll(history.Select(async m => new MessageListItem
        {
            Id = m.Id,
            Content = await _api.E2ee.DecryptForServerAsync(serverId, m.Content),
            AuthorUsername = m.AuthorUsername,
            SentAt = m.SentAt
        }));

        _messages.Clear();
        foreach (var item in items) _messages.Add(item);
        ScrollMessagesToEnd();
    }

    private async void SendButton_Click(object? sender, RoutedEventArgs e) => await SendMessageAsync();

    private async void MessageInputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SendMessageAsync();
    }

    private async Task SendMessageAsync()
    {
        if (_currentChannelId is not { } channelId || _currentServerId is not { } serverId) return;

        var text = (MessageInputBox.Text ?? "").Trim();
        if (text.Length == 0) return;

        var encrypted = await _api.E2ee.EncryptForServerAsync(serverId, text);
        if (encrypted is null)
        {
            // Mirrors the WPF client's own "couldn't send" fallback - this
            // device doesn't have the server's key yet, ask peers for it.
            await _hub.RequestServerKeyAsync(serverId);
            return;
        }

        await _hub.SendMessageAsync(channelId, encrypted);
        MessageInputBox.Text = "";
    }

    private async void OnMessageReceived(MessageResponse msg)
    {
        // Ignores messages for channels other than the one currently open -
        // no unread-badge tracking in this increment (see class comment).
        if (msg.ChannelId != _currentChannelId) return;
        if (_currentServerId is not { } serverId) return;

        var content = await _api.E2ee.DecryptForServerAsync(serverId, msg.Content);

        Dispatcher.UIThread.Post(() =>
        {
            _messages.Add(new MessageListItem { Id = msg.Id, Content = content, AuthorUsername = msg.AuthorUsername, SentAt = msg.SentAt });
            ScrollMessagesToEnd();
        });
    }

    private async void OnServerKeyRequested(int serverId, int requestingUserId)
    {
        try { await _api.E2ee.ProvisionServerKeyForPeerAsync(serverId, requestingUserId); }
        catch { /* best-effort - some other online member may still answer */ }
    }

    private async void OnServerKeyProvisioned(int serverId)
    {
        try
        {
            if (_currentServerId != serverId || _currentChannelId is not { } channelId) return;
            await LoadChannelHistoryAsync(channelId);
        }
        catch { /* best-effort - placeholder just stays until reopened */ }
    }

    private void ShowWelcome(string headerText, string? subtext)
    {
        SelectedChannelText.Text = headerText;
        WelcomePanel.IsVisible = true;
        MessagesScrollViewer.IsVisible = false;
        MessageInputRow.IsVisible = false;
        SelectedChannelSubtext.Text = subtext ?? "Messages aren't wired up in this build yet - coming in a future update.";
        SelectedChannelSubtext.IsVisible = subtext is not null;
    }

    private void ShowMessages()
    {
        WelcomePanel.IsVisible = false;
        MessagesScrollViewer.IsVisible = true;
        MessageInputRow.IsVisible = true;
    }

    private void ScrollMessagesToEnd() => MessagesScrollViewer.ScrollToEnd();

    private void LogOutButton_Click(object? sender, RoutedEventArgs e)
    {
        _ = _api.LogoutAsync();
        SessionStorage.Clear();
        new LoginWindow().Show();
        Close();
    }
}
