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

public class DmConversationItem
{
    public int OtherUserId { get; set; }
    public string OtherUsername { get; set; } = string.Empty;
    public string PreviewText { get; set; } = string.Empty;
    public string Initial => OtherUsername.Length > 0 ? OtherUsername[..1].ToUpperInvariant() : "?";
}

public class UserSearchResultItem
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
}

public class FriendItem
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PresenceState { get; set; } = "Offline";
    public bool IsOnline => PresenceState != "Offline";
}

public class FriendRequestItem
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public bool IsIncoming => Direction == "Incoming";
    public string SecondaryActionLabel => IsIncoming ? "Decline" : "Cancel";
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
    private readonly ObservableCollection<DmConversationItem> _dmConversations = new();
    private readonly ObservableCollection<UserSearchResultItem> _dmSearchResults = new();
    private readonly ObservableCollection<FriendItem> _friends = new();
    private readonly ObservableCollection<FriendRequestItem> _friendRequests = new();
    private readonly ObservableCollection<UserSearchResultItem> _friendSearchResults = new();

    // Populated for every server up front (LoadServersAsync) so a live
    // message arriving via SignalR can resolve which server's E2EE key to
    // decrypt with, even for a server whose channel list hasn't been
    // opened yet this session - mirrors the WPF client's own field.
    private readonly Dictionary<int, int> _channelServerIds = new();
    private readonly HashSet<int> _joinedChannelIds = new();

    private int? _currentServerId;
    private int? _currentChannelId;

    // Exactly one of _currentChannelId/_dmActiveUserId is non-null whenever
    // the messaging UI is showing - SendMessageAsync/OnMessageReceived/
    // OnDirectMessageReceived branch on which one to decide channel vs DM.
    private int? _dmActiveUserId;
    private string? _dmActiveUsername;

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
        DmConversationList.ItemsSource = _dmConversations;
        DmSearchResultsList.ItemsSource = _dmSearchResults;
        FriendList.ItemsSource = _friends;
        FriendRequestList.ItemsSource = _friendRequests;
        FriendSearchResultsList.ItemsSource = _friendSearchResults;

        if (_api is null) return;

        _hub.MessageReceived += OnMessageReceived;
        _hub.DirectMessageReceived += OnDirectMessageReceived;
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
    // text-channel/DM messaging with E2EE + a friends list. No edit/delete/
    // reactions/replies/pins/attachments/unread badges/voice yet - each is
    // its own separate slice (see the Linux client plan's Phase 1).
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

    private void SwitchToServerView()
    {
        _dmActiveUserId = null;
        _dmActiveUsername = null;
        ServerSidebarPanel.IsVisible = true;
        MessagesSidebarPanel.IsVisible = false;
        FriendsSidebarPanel.IsVisible = false;
    }

    private async void ServerButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int serverId }) return;

        SwitchToServerView();
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

    // --- Direct messages ---

    private async void MessagesButton_Click(object? sender, RoutedEventArgs e)
    {
        _currentChannelId = null;
        _dmActiveUserId = null;
        ServerSidebarPanel.IsVisible = false;
        MessagesSidebarPanel.IsVisible = true;
        FriendsSidebarPanel.IsVisible = false;

        ShowWelcome("Direct Messages", subtext: "Pick a conversation to start messaging.");
        await LoadDmConversationsAsync();
    }

    private async Task LoadDmConversationsAsync()
    {
        var conversations = await _api.GetDmConversationsAsync();
        _dmConversations.Clear();
        foreach (var c in conversations)
            _dmConversations.Add(new DmConversationItem { OtherUserId = c.OtherUserId, OtherUsername = c.OtherUsername, PreviewText = c.LastMessagePreview });
    }

    private async void DmSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var query = (DmSearchBox.Text ?? "").Trim();
        if (query.Length < 2) { _dmSearchResults.Clear(); return; }

        var results = await _api.SearchUsersAsync(query);
        _dmSearchResults.Clear();
        foreach (var r in results.Where(r => r.Id != _api.CurrentUserId))
            _dmSearchResults.Add(new UserSearchResultItem { Id = r.Id, Username = r.Username });
    }

    private async void DmSearchResult_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int userId } control) return;
        var username = (control.DataContext as UserSearchResultItem)?.Username ?? "user";
        await OpenDmConversationAsync(userId, username);
    }

    private async void DmConversation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int userId }) return;
        var convo = _dmConversations.FirstOrDefault(c => c.OtherUserId == userId);
        await OpenDmConversationAsync(userId, convo?.OtherUsername ?? "user");
    }

    private async Task OpenDmConversationAsync(int userId, string username)
    {
        _dmActiveUserId = userId;
        _dmActiveUsername = username;
        _currentChannelId = null;

        DmSearchBox.Text = "";
        _dmSearchResults.Clear();

        ShowMessages();
        SelectedChannelText.Text = $"@{username}";

        var history = await _api.GetDmHistoryAsync(userId);
        var items = await Task.WhenAll(history.Select(async m => new MessageListItem
        {
            Id = m.Id,
            Content = await _api.E2ee.DecryptAsync(userId, m.Content),
            AuthorUsername = m.SenderId == _api.CurrentUserId ? (_api.CurrentUsername ?? "You") : username,
            SentAt = m.SentAt
        }));

        _messages.Clear();
        foreach (var item in items) _messages.Add(item);
        ScrollMessagesToEnd();

        try { await _hub.MarkDmReadAsync(userId); } catch { /* best-effort */ }
    }

    private async void OnDirectMessageReceived(DirectMessageResponse dm)
    {
        var otherUserId = dm.SenderId == _api.CurrentUserId ? dm.RecipientId : dm.SenderId;
        if (otherUserId != _dmActiveUserId) return;

        var content = await _api.E2ee.DecryptAsync(otherUserId, dm.Content);
        var authorUsername = dm.SenderId == _api.CurrentUserId ? (_api.CurrentUsername ?? "You") : (_dmActiveUsername ?? "them");

        Dispatcher.UIThread.Post(() =>
        {
            _messages.Add(new MessageListItem { Id = dm.Id, Content = content, AuthorUsername = authorUsername, SentAt = dm.SentAt });
            ScrollMessagesToEnd();
        });
    }

    // --- Friends ---

    private async void FriendsButton_Click(object? sender, RoutedEventArgs e)
    {
        _currentChannelId = null;
        _dmActiveUserId = null;
        ServerSidebarPanel.IsVisible = false;
        MessagesSidebarPanel.IsVisible = false;
        FriendsSidebarPanel.IsVisible = true;

        ShowWelcome("Friends", subtext: "Click a friend to start a conversation.");
        await LoadFriendsAsync();
        await LoadFriendRequestsAsync();
    }

    private async Task LoadFriendsAsync()
    {
        var friends = await _api.GetFriendsAsync();
        _friends.Clear();
        foreach (var f in friends)
            _friends.Add(new FriendItem { UserId = f.UserId, Username = f.Username, PresenceState = f.PresenceState });
    }

    private async Task LoadFriendRequestsAsync()
    {
        var requests = await _api.GetFriendRequestsAsync();
        _friendRequests.Clear();
        foreach (var r in requests)
            _friendRequests.Add(new FriendRequestItem { Id = r.Id, UserId = r.UserId, Username = r.Username, Direction = r.Direction });
    }

    private async void FriendSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var query = (FriendSearchBox.Text ?? "").Trim();
        if (query.Length < 2) { _friendSearchResults.Clear(); return; }

        var results = await _api.SearchUsersAsync(query);
        var existingIds = _friends.Select(f => f.UserId).Concat(_friendRequests.Select(r => r.UserId)).ToHashSet();

        _friendSearchResults.Clear();
        foreach (var r in results.Where(r => r.Id != _api.CurrentUserId && !existingIds.Contains(r.Id)))
            _friendSearchResults.Add(new UserSearchResultItem { Id = r.Id, Username = r.Username });
    }

    private async void AddFriendButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int userId }) return;

        var (success, _) = await _api.SendFriendRequestAsync(userId);
        if (!success) return;

        FriendSearchBox.Text = "";
        _friendSearchResults.Clear();
        await LoadFriendRequestsAsync();
    }

    private async void FriendRequestAcceptButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int friendshipId }) return;

        await _api.AcceptFriendRequestAsync(friendshipId);
        await LoadFriendsAsync();
        await LoadFriendRequestsAsync();
    }

    private async void FriendRequestRemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int friendshipId }) return;

        await _api.RemoveFriendshipAsync(friendshipId);
        await LoadFriendRequestsAsync();
    }

    private async void FriendListItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int userId } control) return;
        var username = (control.DataContext as FriendItem)?.Username ?? "user";
        await OpenDmConversationAsync(userId, username);
    }

    // --- Sending / receiving (shared between channel and DM messaging) ---

    private async void SendButton_Click(object? sender, RoutedEventArgs e) => await SendMessageAsync();

    private async void MessageInputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SendMessageAsync();
    }

    private async Task SendMessageAsync()
    {
        var text = (MessageInputBox.Text ?? "").Trim();
        if (text.Length == 0) return;

        if (_dmActiveUserId is { } otherUserId)
        {
            var dmEncrypted = await _api.E2ee.EncryptAsync(otherUserId, text);
            if (dmEncrypted is null) return;

            await _hub.SendDirectMessageAsync(otherUserId, dmEncrypted);
            MessageInputBox.Text = "";
            return;
        }

        if (_currentChannelId is not { } channelId || _currentServerId is not { } serverId) return;

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
