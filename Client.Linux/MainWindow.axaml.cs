using System.Collections.ObjectModel;
using System.ComponentModel;
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

public class ChannelListItem : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string DisplayName => Type == "Text" ? $"# {Name}" : $"🔊 {Name}";

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (_unreadCount == value) return;
            _unreadCount = value;
            OnPropertyChanged(nameof(UnreadCount));
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(UnreadCountDisplay));
        }
    }
    public bool HasUnread => UnreadCount > 0;
    public string UnreadCountDisplay => UnreadCount > 99 ? "99+" : UnreadCount.ToString();

    // Only populated/shown for voice channels - who's currently connected.
    // A fixed collection reference (see MessageListItem.Reactions for why).
    public ObservableCollection<VoiceRosterItem> Members { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class VoiceRosterItem : INotifyPropertyChanged
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;

    private bool _isMuted;
    public bool IsMuted { get => _isMuted; set { if (_isMuted == value) return; _isMuted = value; OnPropertyChanged(nameof(IsMuted)); } }

    private bool _isDeafened;
    public bool IsDeafened { get => _isDeafened; set { if (_isDeafened == value) return; _isDeafened = value; OnPropertyChanged(nameof(IsDeafened)); } }

    private bool _isSpeaking;
    public bool IsSpeaking { get => _isSpeaking; set { if (_isSpeaking == value) return; _isSpeaking = value; OnPropertyChanged(nameof(IsSpeaking)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ReactionItem
{
    public int MessageId { get; set; }
    public string Emoji { get; set; } = string.Empty;
    public int Count { get; set; }
    public bool ReactedByMe { get; set; }
    public string DisplayText => $"{Emoji} {Count}";
}

public class MessageListItem
{
    public int Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public string AuthorUsername { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public bool IsOwnMessage { get; set; }
    public string TimeDisplay => SentAt.ToLocalTime().ToString("t");

    // A fixed collection reference (never swapped out after construction) -
    // ItemsControl reacts to Add/Remove/Replace on this without the parent
    // MessageListItem itself needing INotifyPropertyChanged.
    public ObservableCollection<ReactionItem> Reactions { get; } = new();
}

public class DmConversationItem : INotifyPropertyChanged
{
    public int OtherUserId { get; set; }
    public string OtherUsername { get; set; } = string.Empty;
    public string PreviewText { get; set; } = string.Empty;
    public string Initial => OtherUsername.Length > 0 ? OtherUsername[..1].ToUpperInvariant() : "?";

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (_unreadCount == value) return;
            _unreadCount = value;
            OnPropertyChanged(nameof(UnreadCount));
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(UnreadCountDisplay));
        }
    }
    public bool HasUnread => UnreadCount > 0;
    public string UnreadCountDisplay => UnreadCount > 99 ? "99+" : UnreadCount.ToString();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
    private VoiceServiceLinux? _voice;

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

    // Unread counts survive even for channels/DMs that aren't currently
    // loaded into _textChannels/_dmConversations (e.g. a different server) -
    // re-applied to the matching list item whenever that list is (re)built.
    private readonly Dictionary<int, int> _unreadChannelCounts = new();
    private readonly Dictionary<int, int> _unreadDmCounts = new();

    private int? _currentServerId;
    private int? _currentChannelId;
    private int? _currentVoiceChannelId;

    // Exactly one of _currentChannelId/_dmActiveUserId is non-null whenever
    // the messaging UI is showing - SendMessageAsync/OnMessageReceived/
    // OnDirectMessageReceived branch on which one to decide channel vs DM.
    private int? _dmActiveUserId;
    private string? _dmActiveUsername;

    // Set while MessageInputBox holds an in-progress edit (see
    // EditMessageMenuItem_Click) instead of a new message to send.
    private int? _editingMessageId;

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
        _hub.MessageEdited += OnMessageEdited;
        _hub.MessageDeleted += OnMessageDeleted;
        _hub.DirectMessageReceived += OnDirectMessageReceived;
        _hub.DirectMessageEdited += OnDirectMessageEdited;
        _hub.DirectMessageDeleted += OnDirectMessageDeleted;
        _hub.MessageReactionToggled += (_, messageId, emoji, userId, added) => Dispatcher.UIThread.Post(() => ApplyReactionToggle(messageId, emoji, userId, added));
        _hub.DirectMessageReactionToggled += (messageId, emoji, userId, added) => Dispatcher.UIThread.Post(() => ApplyReactionToggle(messageId, emoji, userId, added));
        _hub.ServerKeyRequested += OnServerKeyRequested;
        _hub.ServerKeyProvisioned += OnServerKeyProvisioned;
        _hub.VoiceUserJoined += (userId, username, channelId, _) => Dispatcher.UIThread.Post(() => OnVoiceUserJoined(userId, username, channelId));
        _hub.VoiceUserLeft += (userId, _, channelId) => Dispatcher.UIThread.Post(() => OnVoiceUserLeft(userId, channelId));
        _hub.UserSpeaking += (userId, channelId, isSpeaking) => Dispatcher.UIThread.Post(() => SetVoiceMemberState(userId, channelId, m => m.IsSpeaking = isSpeaking));
        _hub.UserMuted += (userId, channelId, isMuted) => Dispatcher.UIThread.Post(() => SetVoiceMemberState(userId, channelId, m => m.IsMuted = isMuted));
        _hub.UserDeafened += (userId, channelId, isDeafened) => Dispatcher.UIThread.Post(() => SetVoiceMemberState(userId, channelId, m => m.IsDeafened = isDeafened));

        Closed += (_, _) => { _ = _voice?.DisposeAsync().AsTask(); };

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await _hub.ConnectAsync(App.HubUrl, _api.GetFreshAccessTokenAsync);
        _voice = new VoiceServiceLinux(_hub, _api.CurrentUserId!.Value);
        await LoadServersAsync();
    }

    // Scoped deliberately for this increment: server rail + channel nav +
    // text-channel/DM messaging with E2EE + friends + edit/delete/reactions/
    // unread badges. No replies/pins/attachments/voice yet - each is its own
    // separate slice (see the Linux client plan's Phase 1).
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

            var item = new ChannelListItem { Id = c.Id, Name = c.Name, Type = c.Type, UnreadCount = _unreadChannelCounts.GetValueOrDefault(c.Id) };
            if (c.Type == "Text") _textChannels.Add(item);
            else _voiceChannels.Add(item);

            if (c.Type == "Text" && _joinedChannelIds.Add(c.Id))
                await _hub.JoinChannelAsync(c.Id);
        }

        await LoadVoiceRostersAsync(serverId);
    }

    private async Task LoadVoiceRostersAsync(int serverId)
    {
        var rosters = await _hub.GetVoiceRostersForServerAsync(serverId);
        foreach (var roster in rosters)
        {
            var channel = _voiceChannels.FirstOrDefault(c => c.Id == roster.ChannelId);
            if (channel is null) continue;

            channel.Members.Clear();
            foreach (var m in roster.Members)
                channel.Members.Add(new VoiceRosterItem { UserId = m.UserId, Username = m.Username });
        }
    }

    private void SwitchToServerView()
    {
        _dmActiveUserId = null;
        _dmActiveUsername = null;
        CancelEdit();
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

        CancelEdit();
        SelectedChannelText.Text = channel.DisplayName;

        if (channel.Type != "Text")
        {
            _currentChannelId = null;
            await JoinVoiceChannelAsync(channel);
            return;
        }

        _currentChannelId = channelId;
        _unreadChannelCounts.Remove(channelId);
        channel.UnreadCount = 0;

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
        var items = await Task.WhenAll(history.Select(async m =>
        {
            var content = await _api.E2ee.DecryptForServerAsync(serverId, m.Content);
            return BuildMessageItem(m.Id, content, m.AuthorUsername, m.SentAt, m.AuthorId == _api.CurrentUserId, m.Reactions);
        }));

        _messages.Clear();
        foreach (var item in items) _messages.Add(item);
        ScrollMessagesToEnd();
    }

    private static MessageListItem BuildMessageItem(int id, string content, string authorUsername, DateTime sentAt, bool isOwnMessage, List<ReactionSummaryResponse>? reactions)
    {
        var item = new MessageListItem { Id = id, Content = content, AuthorUsername = authorUsername, SentAt = sentAt, IsOwnMessage = isOwnMessage };
        foreach (var r in reactions ?? new())
            item.Reactions.Add(new ReactionItem { MessageId = id, Emoji = r.Emoji, Count = r.Count, ReactedByMe = r.ReactedByMe });
        return item;
    }

    // --- Voice ---

    private async Task JoinVoiceChannelAsync(ChannelListItem channel)
    {
        if (_currentVoiceChannelId == channel.Id)
        {
            ShowWelcome(channel.DisplayName, subtext: "You're connected to this voice channel.");
            return;
        }

        if (_currentVoiceChannelId is { } previousChannelId)
            await LeaveVoiceChannelAsync(previousChannelId);

        ShowWelcome(channel.DisplayName, subtext: "Connecting...");

        try
        {
            var roster = await _hub.JoinVoiceChannelAsync(channel.Id);
            channel.Members.Clear();
            foreach (var p in roster)
                channel.Members.Add(new VoiceRosterItem { UserId = p.UserId, Username = p.Username });

            await _voice!.JoinChannelAsync(channel.Id);
            _currentVoiceChannelId = channel.Id;

            MuteMicButton.Content = "🎤";
            DeafenButton.Content = "🎧";
            VoiceControlBar.IsVisible = true;

            ShowWelcome(channel.DisplayName, subtext: "You're connected to this voice channel.");
        }
        catch (Exception ex)
        {
            ShowWelcome(channel.DisplayName, subtext: $"Couldn't join voice: {ex.Message}");
        }
    }

    private async Task LeaveVoiceChannelAsync(int channelId)
    {
        if (_voice is not null) await _voice.LeaveAllAsync();
        try { await _hub.LeaveVoiceChannelAsync(channelId); } catch { /* best-effort */ }

        // Best-effort local reset - a VoiceUserLeft broadcast for this
        // device (if it arrives) removes it from the roster too; this just
        // avoids showing a stale "still connected" entry for ourselves in
        // the meantime.
        var channel = _voiceChannels.FirstOrDefault(c => c.Id == channelId);
        if (channel is not null && _api.CurrentUserId is { } selfId)
        {
            var selfEntry = channel.Members.FirstOrDefault(m => m.UserId == selfId);
            if (selfEntry is not null) channel.Members.Remove(selfEntry);
        }

        _currentVoiceChannelId = null;
        VoiceControlBar.IsVisible = false;
    }

    private async void LeaveVoiceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentVoiceChannelId is { } channelId) await LeaveVoiceChannelAsync(channelId);
        ShowWelcome("Select a channel", subtext: null);
    }

    private void MuteMicButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_voice is null || _currentVoiceChannelId is not { } channelId) return;

        _voice.IsMicMuted = !_voice.IsMicMuted;
        MuteMicButton.Content = _voice.IsMicMuted ? "🔇" : "🎤";
        _ = _hub.SendMutedAsync(channelId, _voice.IsMicMuted);
    }

    private void DeafenButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_voice is null || _currentVoiceChannelId is not { } channelId) return;

        _voice.IsDeafened = !_voice.IsDeafened;
        DeafenButton.Content = _voice.IsDeafened ? "🔇" : "🎧";
        MuteMicButton.Content = _voice.IsMicMuted ? "🔇" : "🎤";
        _ = _hub.SendDeafenedAsync(channelId, _voice.IsDeafened);
        _ = _hub.SendMutedAsync(channelId, _voice.IsMicMuted);
    }

    private void OnVoiceUserJoined(int userId, string username, int channelId)
    {
        var channel = _voiceChannels.FirstOrDefault(c => c.Id == channelId);
        if (channel is null) return;
        if (channel.Members.Any(m => m.UserId == userId)) return;

        channel.Members.Add(new VoiceRosterItem { UserId = userId, Username = username });
    }

    private void OnVoiceUserLeft(int userId, int channelId)
    {
        var channel = _voiceChannels.FirstOrDefault(c => c.Id == channelId);
        var member = channel?.Members.FirstOrDefault(m => m.UserId == userId);
        if (member is not null) channel!.Members.Remove(member);
    }

    private void SetVoiceMemberState(int userId, int channelId, Action<VoiceRosterItem> apply)
    {
        var channel = _voiceChannels.FirstOrDefault(c => c.Id == channelId);
        var member = channel?.Members.FirstOrDefault(m => m.UserId == userId);
        if (member is not null) apply(member);
    }

    // --- Direct messages ---

    private async void MessagesButton_Click(object? sender, RoutedEventArgs e)
    {
        _currentChannelId = null;
        _dmActiveUserId = null;
        CancelEdit();
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
            _dmConversations.Add(new DmConversationItem
            {
                OtherUserId = c.OtherUserId,
                OtherUsername = c.OtherUsername,
                PreviewText = c.LastMessagePreview,
                UnreadCount = _unreadDmCounts.GetValueOrDefault(c.OtherUserId)
            });
        UpdateMessagesUnreadBadge();
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
        CancelEdit();
        _dmActiveUserId = userId;
        _dmActiveUsername = username;
        _currentChannelId = null;

        _unreadDmCounts.Remove(userId);
        var convo = _dmConversations.FirstOrDefault(c => c.OtherUserId == userId);
        if (convo is not null) convo.UnreadCount = 0;
        UpdateMessagesUnreadBadge();

        DmSearchBox.Text = "";
        _dmSearchResults.Clear();

        ShowMessages();
        SelectedChannelText.Text = $"@{username}";

        var history = await _api.GetDmHistoryAsync(userId);
        var items = await Task.WhenAll(history.Select(async m =>
        {
            var content = await _api.E2ee.DecryptAsync(userId, m.Content);
            var authorUsername = m.SenderId == _api.CurrentUserId ? (_api.CurrentUsername ?? "You") : username;
            return BuildMessageItem(m.Id, content, authorUsername, m.SentAt, m.SenderId == _api.CurrentUserId, m.Reactions);
        }));

        _messages.Clear();
        foreach (var item in items) _messages.Add(item);
        ScrollMessagesToEnd();

        try { await _hub.MarkDmReadAsync(userId); } catch { /* best-effort */ }
    }

    // async void - SignalR invokes this on its own background thread with
    // no synchronization context to route an unhandled exception to, so an
    // uncaught throw here (e.g. a transient HttpRequestException from
    // GetServerKeyAsync's network call inside DecryptAsync) takes the whole
    // process down instead of just failing this one message. Best-effort:
    // worst case this message doesn't render/decrypt and the rest of the
    // app keeps working.
    private async void OnDirectMessageReceived(DirectMessageResponse dm)
    {
        try
        {
            var otherUserId = dm.SenderId == _api.CurrentUserId ? dm.RecipientId : dm.SenderId;

            if (otherUserId != _dmActiveUserId)
            {
                if (dm.SenderId == _api.CurrentUserId) return;

                var newCount = _unreadDmCounts.GetValueOrDefault(otherUserId) + 1;
                _unreadDmCounts[otherUserId] = newCount;
                Dispatcher.UIThread.Post(() =>
                {
                    var convo = _dmConversations.FirstOrDefault(c => c.OtherUserId == otherUserId);
                    if (convo is not null) convo.UnreadCount = newCount;
                    UpdateMessagesUnreadBadge();
                });
                return;
            }

            var content = await _api.E2ee.DecryptAsync(otherUserId, dm.Content);
            var authorUsername = dm.SenderId == _api.CurrentUserId ? (_api.CurrentUsername ?? "You") : (_dmActiveUsername ?? "them");

            Dispatcher.UIThread.Post(() =>
            {
                _messages.Add(BuildMessageItem(dm.Id, content, authorUsername, dm.SentAt, dm.SenderId == _api.CurrentUserId, dm.Reactions));
                ScrollMessagesToEnd();
            });
        }
        catch { /* best-effort - see method comment */ }
    }

    private async void OnDirectMessageEdited(DirectMessageResponse dm)
    {
        try
        {
            var otherUserId = dm.SenderId == _api.CurrentUserId ? dm.RecipientId : dm.SenderId;
            if (otherUserId != _dmActiveUserId) return;

            var content = await _api.E2ee.DecryptAsync(otherUserId, dm.Content);
            Dispatcher.UIThread.Post(() => ReplaceMessageContent(dm.Id, content));
        }
        catch { /* best-effort - see OnDirectMessageReceived */ }
    }

    private void OnDirectMessageDeleted(int messageId, int senderId, int recipientId)
    {
        try
        {
            var otherUserId = senderId == _api.CurrentUserId ? recipientId : senderId;
            if (otherUserId != _dmActiveUserId) return;

            Dispatcher.UIThread.Post(() => RemoveMessage(messageId));
        }
        catch { /* best-effort - see OnDirectMessageReceived */ }
    }

    private void UpdateMessagesUnreadBadge()
    {
        var total = _unreadDmCounts.Values.Sum();
        MessagesUnreadBadge.IsVisible = total > 0;
        MessagesUnreadBadgeText.Text = total > 99 ? "99+" : total.ToString();
    }

    // --- Friends ---

    private async void FriendsButton_Click(object? sender, RoutedEventArgs e)
    {
        _currentChannelId = null;
        _dmActiveUserId = null;
        CancelEdit();
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

    // --- Message editing / deleting ---

    private void EditMessageMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: MessageListItem msg } || !msg.IsOwnMessage) return;

        _editingMessageId = msg.Id;
        MessageInputBox.Text = msg.Content;
        MessageInputBox.Focus();
        EditBanner.IsVisible = true;
    }

    private async void DeleteMessageMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: MessageListItem msg } || !msg.IsOwnMessage) return;

        var success = _dmActiveUserId is { } otherUserId
            ? await _api.DeleteDirectMessageAsync(otherUserId, msg.Id)
            : _currentChannelId is { } channelId && await _api.DeleteMessageAsync(channelId, msg.Id);

        if (success) RemoveMessage(msg.Id);
    }

    private void CancelEditButton_Click(object? sender, RoutedEventArgs e) => CancelEdit();

    private void CancelEdit()
    {
        _editingMessageId = null;
        MessageInputBox.Text = "";
        EditBanner.IsVisible = false;
    }

    private async Task SaveEditAsync(int messageId, string newText)
    {
        if (_dmActiveUserId is { } otherUserId)
        {
            // ApiService.EditDirectMessageAsync encrypts internally and
            // returns the response with Content already decrypted (unlike
            // the channel version below) - see its own doc comment.
            var updated = await _api.EditDirectMessageAsync(otherUserId, messageId, newText);
            if (updated is not null) ReplaceMessageContent(messageId, updated.Content);
        }
        else if (_currentServerId is { } serverId && _currentChannelId is { } channelId)
        {
            var encrypted = await _api.E2ee.EncryptForServerAsync(serverId, newText);
            if (encrypted is not null)
            {
                var updated = await _api.EditMessageAsync(channelId, messageId, encrypted);
                if (updated is not null) ReplaceMessageContent(messageId, await _api.E2ee.DecryptForServerAsync(serverId, updated.Content));
            }
        }

        CancelEdit();
    }

    private async void OnMessageEdited(MessageResponse msg)
    {
        try
        {
            if (msg.ChannelId != _currentChannelId) return;
            if (_currentServerId is not { } serverId) return;

            var content = await _api.E2ee.DecryptForServerAsync(serverId, msg.Content);
            Dispatcher.UIThread.Post(() => ReplaceMessageContent(msg.Id, content));
        }
        catch { /* best-effort - see OnDirectMessageReceived */ }
    }

    private void OnMessageDeleted(int messageId, int channelId)
    {
        try
        {
            if (channelId != _currentChannelId) return;
            Dispatcher.UIThread.Post(() => RemoveMessage(messageId));
        }
        catch { /* best-effort - see OnDirectMessageReceived */ }
    }

    private void ReplaceMessageContent(int messageId, string newContent)
    {
        var item = _messages.FirstOrDefault(m => m.Id == messageId);
        if (item is null) return;

        var index = _messages.IndexOf(item);
        var replacement = new MessageListItem { Id = item.Id, Content = newContent, AuthorUsername = item.AuthorUsername, SentAt = item.SentAt, IsOwnMessage = item.IsOwnMessage };
        foreach (var r in item.Reactions) replacement.Reactions.Add(r);
        _messages[index] = replacement;
    }

    private void RemoveMessage(int messageId)
    {
        var item = _messages.FirstOrDefault(m => m.Id == messageId);
        if (item is not null) _messages.Remove(item);
    }

    // --- Reactions ---

    private async void ReactionMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: MessageListItem msg } menuItem) return;
        var emoji = menuItem.Header?.ToString();
        if (string.IsNullOrEmpty(emoji)) return;

        if (_dmActiveUserId is not null)
            await _hub.ToggleDirectMessageReactionAsync(msg.Id, emoji);
        else
            await _hub.ToggleMessageReactionAsync(msg.Id, emoji);
    }

    private async void ReactionPill_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ReactionItem reaction }) return;

        if (_dmActiveUserId is not null)
            await _hub.ToggleDirectMessageReactionAsync(reaction.MessageId, reaction.Emoji);
        else
            await _hub.ToggleMessageReactionAsync(reaction.MessageId, reaction.Emoji);
    }

    private void ApplyReactionToggle(int messageId, string emoji, int userId, bool added)
    {
        var msg = _messages.FirstOrDefault(m => m.Id == messageId);
        if (msg is null) return;

        var isMe = userId == _api.CurrentUserId;
        var index = -1;
        for (var i = 0; i < msg.Reactions.Count; i++)
        {
            if (msg.Reactions[i].Emoji == emoji) { index = i; break; }
        }

        if (added)
        {
            if (index >= 0)
            {
                var existing = msg.Reactions[index];
                msg.Reactions[index] = new ReactionItem { MessageId = messageId, Emoji = emoji, Count = existing.Count + 1, ReactedByMe = existing.ReactedByMe || isMe };
            }
            else
            {
                msg.Reactions.Add(new ReactionItem { MessageId = messageId, Emoji = emoji, Count = 1, ReactedByMe = isMe });
            }
            return;
        }

        if (index < 0) return;
        var current = msg.Reactions[index];
        var newCount = current.Count - 1;
        if (newCount <= 0) msg.Reactions.RemoveAt(index);
        else msg.Reactions[index] = new ReactionItem { MessageId = messageId, Emoji = emoji, Count = newCount, ReactedByMe = isMe ? false : current.ReactedByMe };
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

        HideSendError();

        if (_editingMessageId is { } editingId)
        {
            await SaveEditAsync(editingId, text);
            return;
        }

        if (_dmActiveUserId is { } otherUserId)
        {
            // Null means this device's own keys aren't unlocked, or the
            // recipient has never logged in since E2EE was added (no public
            // key on file yet) - either way there's no key to encrypt with,
            // so surface that instead of silently doing nothing (see the
            // WPF client's matching AlertAsync for this same case).
            var dmEncrypted = await _api.E2ee.EncryptAsync(otherUserId, text);
            if (dmEncrypted is null)
            {
                ShowSendError("Couldn't send - this person hasn't set up secure messaging yet, or your own keys aren't unlocked. Try logging out and back in.");
                return;
            }

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
            ShowSendError("Couldn't send yet - waiting for another online member to grant this device access to the channel key.");
            return;
        }

        await _hub.SendMessageAsync(channelId, encrypted);
        MessageInputBox.Text = "";
    }

    private void ShowSendError(string message)
    {
        SendStatusText.Text = message;
        SendStatusText.IsVisible = true;
    }

    private void HideSendError() => SendStatusText.IsVisible = false;

    private async void OnMessageReceived(MessageResponse msg)
    {
        try
        {
            if (msg.ChannelId != _currentChannelId)
            {
                if (msg.AuthorId == _api.CurrentUserId) return;

                var newCount = _unreadChannelCounts.GetValueOrDefault(msg.ChannelId) + 1;
                _unreadChannelCounts[msg.ChannelId] = newCount;
                Dispatcher.UIThread.Post(() =>
                {
                    var item = _textChannels.FirstOrDefault(c => c.Id == msg.ChannelId);
                    if (item is not null) item.UnreadCount = newCount;
                });
                return;
            }

            if (_currentServerId is not { } serverId) return;

            var content = await _api.E2ee.DecryptForServerAsync(serverId, msg.Content);

            Dispatcher.UIThread.Post(() =>
            {
                _messages.Add(BuildMessageItem(msg.Id, content, msg.AuthorUsername, msg.SentAt, msg.AuthorId == _api.CurrentUserId, msg.Reactions));
                ScrollMessagesToEnd();
            });
        }
        catch { /* best-effort - see OnDirectMessageReceived */ }
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
        HideSendError();
        SelectedChannelText.Text = headerText;
        WelcomePanel.IsVisible = true;
        MessagesScrollViewer.IsVisible = false;
        MessageInputRow.IsVisible = false;
        SelectedChannelSubtext.Text = subtext ?? "Messages aren't wired up in this build yet - coming in a future update.";
        SelectedChannelSubtext.IsVisible = subtext is not null;
    }

    private void ShowMessages()
    {
        HideSendError();
        WelcomePanel.IsVisible = false;
        MessagesScrollViewer.IsVisible = true;
        MessageInputRow.IsVisible = true;
    }

    private void ScrollMessagesToEnd() => MessagesScrollViewer.ScrollToEnd();

    private void LogOutButton_Click(object? sender, RoutedEventArgs e)
    {
        _ = _voice?.DisposeAsync().AsTask();
        _ = _api.LogoutAsync();
        SessionStorage.Clear();
        new LoginWindow().Show();
        Close();
    }

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_voice is null) return;
        new SettingsWindow(_voice).Show();
    }

    private void MembersButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentServerId is not { } serverId) return;
        new MembersWindow(_api, serverId).Show();
    }
}
