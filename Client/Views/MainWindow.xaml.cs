using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Voiceover.Client.Models;
using Voiceover.Client.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using Button = System.Windows.Controls.Button;

namespace Voiceover.Client.Views;

public class ServerListItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? IconUrl { get; set; }
    public int OwnerId { get; set; }
}

public class VoiceMemberItem : INotifyPropertyChanged
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }

    // A volume slider only makes sense for someone else's audio, not your
    // own - set once at construction (see everywhere Members.Add happens).
    public bool IsSelf { get; set; }
    public Visibility VolumeSliderVisibility => IsSelf ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SelfMenuItemVisibility => IsSelf ? Visibility.Visible : Visibility.Collapsed;

    private bool _isSpeaking;
    public bool IsSpeaking
    {
        get => _isSpeaking;
        set
        {
            if (_isSpeaking == value) return;
            _isSpeaking = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpeaking)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeakingDotVisibility)));
        }
    }

    public Visibility SpeakingDotVisibility => IsSpeaking ? Visibility.Visible : Visibility.Collapsed;

    // Set only on the local user's own row, only while their LiveKit
    // connection is still spinning up (see VoiceChannelButton_Click). Other
    // clients never see this - they aren't told about the join at all until
    // it actually succeeds, so this state never reaches them.
    private bool _isJoining;
    public bool IsJoining
    {
        get => _isJoining;
        set
        {
            if (_isJoining == value) return;
            _isJoining = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsJoining)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsernameForeground)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(JoiningTooltip)));
        }
    }

    public System.Windows.Media.Brush UsernameForeground =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[IsJoining ? "TextJoining" : "TextMuted"];

    public string? JoiningTooltip => IsJoining ? "Joining..." : null;

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            _isMuted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMuted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MutedIconVisibility)));
        }
    }

    public Visibility MutedIconVisibility => IsMuted ? Visibility.Visible : Visibility.Collapsed;

    private bool _isDeafened;
    public bool IsDeafened
    {
        get => _isDeafened;
        set
        {
            if (_isDeafened == value) return;
            _isDeafened = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDeafened)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DeafenedIconVisibility)));
        }
    }

    public Visibility DeafenedIconVisibility => IsDeafened ? Visibility.Visible : Visibility.Collapsed;

    // 0-200%, 100 = unchanged. Persisted locally per-user (see
    // UserVolumeStorage) so it carries over the next time you're in a
    // voice channel with the same person, instead of resetting every join.
    private double _volume = 100;
    public double Volume
    {
        get => _volume;
        set
        {
            if (_volume == value) return;
            _volume = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Volume)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumeDisplay)));
        }
    }

    public string VolumeDisplay => $"{(int)Volume}%";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class ChannelListItem : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    // Only populated/shown for voice channels - who's currently connected.
    public ObservableCollection<VoiceMemberItem> Members { get; } = new();

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (_unreadCount == value) return;
            _unreadCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUnread)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCountDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadBadgeVisibility)));
        }
    }

    public bool HasUnread => UnreadCount > 0;
    public string UnreadCountDisplay => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public Visibility UnreadBadgeVisibility => UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class MessageListItem : INotifyPropertyChanged
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public string AuthorUsername { get; set; } = string.Empty;
    public string? AuthorAvatarUrl { get; set; }
    public string TimeDisplay { get; set; } = string.Empty;
    public string? AttachmentUrl { get; set; }

    // Both set at construction (ToListItem/ToDmListItem). Edit is
    // author-only everywhere - moderators can remove someone else's words,
    // not rewrite them. Delete for a channel message is shown to everyone
    // and left to the server to actually authorize (author or a
    // moderator/owner - see MessagesController.Delete); for a DM there's no
    // moderation concept, so it's only shown for your own messages,
    // matching the server's author-only rule exactly with no round trip
    // needed just to decide whether to show the menu item.
    public bool IsOwnMessage { get; set; }
    public bool IsChannelMessage { get; set; }
    public Visibility EditMenuVisibility => IsOwnMessage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DeleteMenuVisibility => IsOwnMessage || IsChannelMessage ? Visibility.Visible : Visibility.Collapsed;

    private string _content = string.Empty;
    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentVisibility)));
        }
    }

    private bool _isEdited;
    public bool IsEdited
    {
        get => _isEdited;
        set
        {
            if (_isEdited == value) return;
            _isEdited = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEdited)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditedTagVisibility)));
        }
    }

    // Local-only UI state (not persisted/broadcast) - true while this row's
    // inline edit box is open, swapping the read-only TextBlock for an
    // editable TextBox in the DataTemplate.
    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditBoxVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentVisibility)));
        }
    }

    // What the edit TextBox is bound to - separate from Content so
    // Cancel can discard in-progress typing without touching the real value.
    public string EditingContent { get; set; } = string.Empty;

    public string AttachmentDisplay => AttachmentUrl is null ? "" : $"📎 {System.IO.Path.GetFileName(AttachmentUrl)}";
    public Visibility ContentVisibility => !IsEditing && !string.IsNullOrEmpty(Content) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AttachmentVisibility => AttachmentUrl is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditedTagVisibility => IsEdited ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EditBoxVisibility => IsEditing ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class UserSearchResultItem
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
}

public class DmConversationListItem : INotifyPropertyChanged
{
    public int OtherUserId { get; set; }
    public string OtherUsername { get; set; } = string.Empty;
    public string? OtherUserAvatarUrl { get; set; }
    public string LastMessagePreview { get; set; } = string.Empty;
    public DateTime LastMessageAt { get; set; }

    public string PreviewDisplay => LastMessagePreview;

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (_unreadCount == value) return;
            _unreadCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUnread)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCountDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadBadgeVisibility)));
        }
    }

    public bool HasUnread => UnreadCount > 0;
    public string UnreadCountDisplay => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public Visibility UnreadBadgeVisibility => UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class FriendListItem : INotifyPropertyChanged
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }

    private string _presenceState = "Offline";
    public string PresenceState
    {
        get => _presenceState;
        set
        {
            if (_presenceState == value) return;
            _presenceState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresenceState)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OnlineDotVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AwayDotVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OfflineDotVisibility)));
        }
    }

    public Visibility OnlineDotVisibility => PresenceState == "Online" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AwayDotVisibility => PresenceState == "Away" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OfflineDotVisibility => PresenceState == "Offline" ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class FriendRequestListItem
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string Direction { get; set; } = string.Empty; // "Incoming" or "Outgoing"

    public Visibility AcceptButtonVisibility => Direction == "Incoming" ? Visibility.Visible : Visibility.Collapsed;
    public string SecondaryActionLabel => Direction == "Incoming" ? "Decline" : "Cancel";
}

public class MemberListItem : INotifyPropertyChanged
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string Role { get; set; } = string.Empty;
    public bool IsSelf { get; set; }

    // Only the owner can promote/demote (matches the old Members popup);
    // owner and moderator can both kick (matches KickMember's own
    // CanManageServerAsync check) - neither ever applies to the owner's own
    // row, nor to your own row regardless of your role (a moderator
    // right-clicking themselves shouldn't see "Kick" just because they can
    // kick everyone else who isn't the owner).
    public bool CanChangeRole { get; set; }
    public bool CanKick { get; set; }

    public string RoleButtonLabel => Role == "Moderator" ? "Demote" : "Promote";
    public string NextRole => Role == "Moderator" ? "Member" : "Moderator";
    public Visibility RoleButtonVisibility => CanChangeRole ? Visibility.Visible : Visibility.Collapsed;
    public Visibility KickButtonVisibility => CanKick ? Visibility.Visible : Visibility.Collapsed;

    private string _presenceState = "Offline";
    public string PresenceState
    {
        get => _presenceState;
        set
        {
            if (_presenceState == value) return;
            _presenceState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresenceState)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OnlineDotVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AwayDotVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OfflineDotVisibility)));
        }
    }

    public Visibility OnlineDotVisibility => PresenceState == "Online" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AwayDotVisibility => PresenceState == "Away" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OfflineDotVisibility => PresenceState == "Offline" ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : FluentWindow
{
    private readonly ApiService _api;
    private readonly SignalRService _hub = new();
    private VoiceService? _voice;
    private readonly IdleDetector _idleDetector = new();

    private int? _currentServerId;
    private int? _currentChannelId;
    private int? _currentVoiceChannelId;
    private int? _dmActiveUserId;
    private string? _dmActiveUsername;

    // BulkObservableCollection where a Clear()+per-item-Add() reload pattern
    // is common (see ReplaceAll callers below) - one Reset notification
    // instead of N+1 individual ones. Plain ObservableCollection for the
    // rest (search results etc.), which only ever grow/shrink by a couple
    // of items at a time and don't reload wholesale.
    private readonly BulkObservableCollection<ServerListItem> _servers = new();
    private readonly BulkObservableCollection<MemberListItem> _members = new();
    private readonly BulkObservableCollection<ChannelListItem> _textChannels = new();
    private readonly BulkObservableCollection<ChannelListItem> _voiceChannels = new();
    private readonly BulkObservableCollection<MessageListItem> _messages = new();
    private readonly BulkObservableCollection<DmConversationListItem> _dmConversations = new();
    private readonly ObservableCollection<UserSearchResultItem> _dmSearchResults = new();
    private readonly BulkObservableCollection<FriendListItem> _friends = new();
    private readonly BulkObservableCollection<FriendRequestListItem> _friendRequests = new();
    private readonly ObservableCollection<UserSearchResultItem> _friendSearchResults = new();

    // Source of truth for text-channel unread counts, kept independent of
    // _textChannels - a message can arrive for a channel before that
    // channel's server has ever been opened (so no ChannelListItem exists
    // yet to mark), unlike DM conversations, which get created fresh on
    // arrival regardless of prior UI state. channelId -> unread count.
    private readonly Dictionary<int, int> _unreadTextChannelCounts = new();

    // channelId -> the server it belongs to - needed to decrypt a channel
    // message's E2EE content (the per-server key is looked up by server id,
    // not channel id) for messages that arrive for a server other than the
    // one currently open, same reasoning as _unreadTextChannelCounts above.
    // Populated in LoadServersAsync (every server up front) and
    // RefreshChannelsAsync (safety net for channels created afterward).
    private readonly Dictionary<int, int> _channelServerIds = new();

    // Channels this device has already joined the SignalR group for -
    // JoinChannelAsync is idempotent server-side either way, but tracking
    // this client-side avoids re-issuing a hub call for every text channel
    // on every single server switch (see RefreshChannelsAsync).
    private readonly HashSet<int> _joinedChannelIds = new();

    private DateTime _lastTypingNotify = DateTime.MinValue;

    public MainWindow(ApiService api)
    {
        InitializeComponent();
        _api = api;

        MyAvatarView.DisplayName = _api.CurrentUsername ?? "?";
        MyAvatarView.ImageUrl = _api.CurrentUserAvatarUrl;

        ServerList.ItemsSource = _servers;
        MemberList.ItemsSource = _members;
        TextChannelList.ItemsSource = _textChannels;
        VoiceChannelList.ItemsSource = _voiceChannels;
        MessageList.ItemsSource = _messages;
        DmConversationList.ItemsSource = _dmConversations;
        DmSearchResultsList.ItemsSource = _dmSearchResults;
        FriendList.ItemsSource = _friends;
        FriendRequestList.ItemsSource = _friendRequests;
        FriendSearchResultsList.ItemsSource = _friendSearchResults;

        _hub.MessageReceived += OnMessageReceived;
        _hub.MessageEdited += OnMessageEdited;
        _hub.MessageDeleted += OnMessageDeleted;
        _hub.DirectMessageReceived += OnDirectMessageReceived;
        _hub.DirectMessageEdited += OnDirectMessageEdited;
        _hub.DirectMessageDeleted += OnDirectMessageDeleted;
        _hub.ServerKeyRequested += OnServerKeyRequested;
        _hub.ServerKeyProvisioned += OnServerKeyProvisioned;
        _hub.UserTyping += OnUserTyping;
        _hub.VoiceUserJoined += OnVoiceUserJoined;
        _hub.VoiceUserLeft += OnVoiceUserLeft;
        _hub.UserSpeaking += OnUserSpeaking;
        _hub.UserMuted += OnUserMuted;
        _hub.UserDeafened += OnUserDeafened;
        _hub.FriendRequestReceived += OnFriendRequestReceived;
        _hub.FriendRequestAccepted += OnFriendRequestAccepted;
        _hub.Reconnecting += () => Dispatcher.Invoke(() => ConnectionStatusText.Text = "Reconnecting...");
        _hub.Reconnected += OnReconnected;
        _hub.ConnectionClosed += () => Dispatcher.Invoke(() => ConnectionStatusText.Text = "Disconnected");

        // Fires if the refresh token turns out to be dead (expired past its
        // 30-day life, or revoked - e.g. a "log out everywhere" from another
        // device) the next time ApiService tries to use it, not just at
        // startup - see ApiService.RefreshAccessTokenAsync.
        _api.SessionExpired += () => Dispatcher.Invoke(OnSessionExpired);

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _idleDetector.Dispose();
        if (_voice is not null)
            await _voice.DisposeAsync();
        await _hub.DisconnectAsync();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _hub.ConnectAsync(App.HubUrl, _api.GetFreshAccessTokenAsync);
        _voice = new VoiceService(_hub, _api.CurrentUserId!.Value);
        _voice.PeerConnected += userId => Dispatcher.Invoke(() => ConnectionStatusText.Text = "Voice connected");
        _voice.PeerDisconnected += userId => Dispatcher.Invoke(() => ConnectionStatusText.Text = "");
        _voice.LocalSpeakingChanged += isSpeaking => _ = OnLocalSpeakingChangedAsync(isSpeaking);
        // Mute can change from places that don't have a handle on this
        // button - Voice Settings' input mode switch, the PTT/push-to-mute
        // hotkey - so this is the one place that keeps it in sync regardless
        // of where the change came from (see VoiceService.MicMutedChanged).
        _voice.MicMutedChanged += isMuted =>
        {
            Dispatcher.Invoke(UpdateMuteButtonVisual);
            _ = OnLocalMutedChangedAsync(isMuted);

            // Audio feedback for your own mute state - the only reliable way
            // to know push-to-mute/talk actually registered while alt-tabbed
            // into a game with no UI visible at all.
            if (isMuted) NotificationService.PlayMuteSound();
            else NotificationService.PlayUnmuteSound();
        };
        _voice.DeafenedChanged += isDeafened => _ = OnLocalDeafenedChangedAsync(isDeafened);

        _hub.PresenceChanged += (userId, state) => Dispatcher.Invoke(() => OnPresenceChanged(userId, state));
        _idleDetector.IdleChanged += isIdle => _ = OnIdleChangedAsync(isIdle);
        _idleDetector.Start();

        // Explicit and awaited, rather than relying on the server's own
        // OnConnectedAsync having already finished by the time StartAsync()
        // returns - that's not a guarantee, and without this, clicking into
        // a server fast enough after login could read your own presence
        // back as still Offline (GetMembers/GetFriends are one-shot reads
        // of PresenceService, not something that waits for the flag to land).
        await SetPresenceStateSafeAsync("Online");

        await LoadServersAsync();
    }

    // Presence reporting is best-effort - if the hub call fails for any
    // reason (an older/mismatched server that doesn't have this method
    // yet, a transient network issue), the app must keep working normally
    // rather than surfacing an error dialog for what's a non-critical
    // background update.
    private async Task SetPresenceStateSafeAsync(string state)
    {
        try
        {
            await _hub.SetPresenceStateAsync(state);
        }
        catch
        {
            // Best-effort - see comment above.
        }
    }

    // Away is suppressed while actively in a voice channel - being
    // mid-conversation shouldn't flip you to "away" just because you
    // haven't touched the mouse. _currentVoiceChannelId is cleared on every
    // leave path, so it's a more reliable "in a call" signal than anything
    // on VoiceService itself.
    private async Task OnIdleChangedAsync(bool isIdle)
    {
        if (isIdle && _currentVoiceChannelId is not null) return;
        await SetPresenceStateSafeAsync(isIdle ? "Away" : "Online");
    }

    private void OnPresenceChanged(int userId, string state)
    {
        var member = _members.FirstOrDefault(m => m.UserId == userId);
        if (member is not null) member.PresenceState = state;

        var friend = _friends.FirstOrDefault(f => f.UserId == userId);
        if (friend is not null) friend.PresenceState = state;
    }

    private async Task LoadServersAsync()
    {
        var servers = await _api.GetMyServersAsync();
        _servers.ReplaceAll(servers.Select(s => new ServerListItem { Id = s.Id, Name = s.Name, IconUrl = App.ResolveUploadUrl(s.IconUrl), OwnerId = s.OwnerId }));

        // Join every text channel's SignalR group across every server the
        // user belongs to - not just whichever one happens to be open right
        // now. ReceiveMessage only reaches clients that are in a channel's
        // own group, so without this, unread dots could only ever work while
        // browsing the exact server a message landed in - unlike DMs, which
        // reach you regardless of what you're looking at (Clients.User, not
        // a group).
        //
        // The per-server channel list fetches are independent HTTP calls, so
        // they run concurrently instead of one at a time (HttpClient is safe
        // for concurrent use). The actual SignalR joins below stay
        // sequential - HubConnection isn't documented as safe for concurrent
        // invocation from multiple threads - but only for channels this
        // device hasn't already joined (_joinedChannelIds), so a later
        // reload (e.g. RefreshChannelsAsync) doesn't redo them.
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

        var textItems = new List<ChannelListItem>();
        var voiceItems = new List<ChannelListItem>();
        foreach (var c in channels)
        {
            _channelServerIds[c.Id] = c.GuildServerId;

            var item = new ChannelListItem
            {
                Id = c.Id,
                DisplayName = c.Type == "Text" ? $"# {c.Name}" : $"🔊 {c.Name}",
                UnreadCount = c.Type == "Text" ? _unreadTextChannelCounts.GetValueOrDefault(c.Id) : 0
            };
            if (c.Type == "Text") textItems.Add(item);
            else voiceItems.Add(item);
        }
        _textChannels.ReplaceAll(textItems);
        _voiceChannels.ReplaceAll(voiceItems);

        // Safety net for channels created after the initial LoadServersAsync
        // sweep (e.g. someone else added one) - only joins ones this device
        // hasn't already joined, instead of unconditionally rejoining every
        // text channel on every single server switch.
        foreach (var c in textItems.Where(c => _joinedChannelIds.Add(c.Id)))
            await _hub.JoinChannelAsync(c.Id);
    }

    private async void ServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int serverId }) return;

        ShowServerSidebar();

        // Note: text channel SignalR groups are deliberately NOT left here -
        // the app stays joined to every text channel across every server (see
        // LoadServersAsync) so unread dots keep working no matter which
        // server or view is currently open, same as DMs already do.
        if (_currentServerId.HasValue && _currentServerId.Value != serverId)
            await _hub.LeaveServerPresenceAsync(_currentServerId.Value);

        _currentServerId = serverId;
        await _hub.JoinServerPresenceAsync(serverId);

        // _servers is already populated (LoadServersAsync runs at login/
        // reconnect) - no need to refetch the whole list just to read one
        // server's name back out of it.
        var server = _servers.FirstOrDefault(s => s.Id == serverId);
        ServerNameText.Text = server?.Name ?? "Server";

        await RefreshChannelsAsync(serverId);
        await LoadVoiceRostersAsync(serverId);
        await LoadMembersPanelAsync(serverId);
    }

    private async Task LoadMembersPanelAsync(int serverId)
    {
        var members = await _api.GetMembersAsync(serverId);
        var self = members.FirstOrDefault(m => m.UserId == _api.CurrentUserId);
        var isOwner = self?.Role == "Owner";
        var canManageServer = isOwner || self?.Role == "Moderator";

        _members.ReplaceAll(members.Select(m =>
        {
            var isSelf = m.UserId == _api.CurrentUserId;
            return new MemberListItem
            {
                UserId = m.UserId,
                Username = m.Username,
                AvatarUrl = App.ResolveUploadUrl(m.AvatarUrl),
                Role = m.Role,
                IsSelf = isSelf,
                CanChangeRole = isOwner && m.Role != "Owner" && !isSelf,
                CanKick = canManageServer && m.Role != "Owner" && !isSelf,
                PresenceState = m.PresenceState
            };
        }));
    }

    // Even with both menu items hidden, an empty ContextMenu would still
    // pop open as a bare sliver - cancel it outright for your own row so
    // right-clicking yourself truly does nothing.
    private void MemberRow_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MemberListItem { IsSelf: true } })
            e.Handled = true;
    }

    private async void MemberRoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int userId } || _currentServerId is null) return;

        var item = _members.FirstOrDefault(m => m.UserId == userId);
        if (item is null) return;

        var success = await _api.ChangeRoleAsync(_currentServerId.Value, userId, item.NextRole);
        if (!success)
        {
            MessageBox.Show("Could not change this member's role.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await LoadMembersPanelAsync(_currentServerId.Value);
    }

    private async void MemberKickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int userId } || _currentServerId is null) return;

        var confirm = MessageBox.Show("Remove this member from the server?", "Confirm Kick",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var success = await _api.KickMemberAsync(_currentServerId.Value, userId);
        if (!success)
        {
            MessageBox.Show("Could not kick this member (you may lack permission, or they're the owner).",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await LoadMembersPanelAsync(_currentServerId.Value);
    }

    // Populates each voice channel's member list from a server-wide snapshot,
    // so anyone who opens a server sees who's currently in voice without
    // having joined anything themselves. Uses the same idempotent-add pattern
    // as OnVoiceUserJoined below, so this doesn't fight with a live event
    // arriving for the same person around the same time.
    private async Task LoadVoiceRostersAsync(int serverId)
    {
        var rosters = await _hub.GetVoiceRostersForServerAsync(serverId);
        foreach (var roster in rosters)
        {
            var item = FindVoiceChannelItem(roster.ChannelId);
            if (item is null) continue;

            foreach (var member in roster.Members)
            {
                if (!item.Members.Any(m => m.UserId == member.UserId))
                    item.Members.Add(new VoiceMemberItem { UserId = member.UserId, Username = member.Username, AvatarUrl = App.ResolveUploadUrl(member.AvatarUrl), IsSelf = member.UserId == _api.CurrentUserId, Volume = SavedVolumePercent(member.UserId) });
            }
        }
    }

    private async void ChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId }) return;

        // Deliberately doesn't leave the previous channel's group - all text
        // channels in the open server stay joined (see RefreshChannelsAsync)
        // so unread dots keep working for channels you switch away from too.
        _currentChannelId = channelId;
        await _hub.JoinChannelAsync(channelId);

        _unreadTextChannelCounts.Remove(channelId);
        var thisChannelItem = FindTextChannelItem(channelId);
        if (thisChannelItem is not null) thisChannelItem.UnreadCount = 0;

        var channelItem = FindChannelDisplayName(channelId);
        ChannelNameText.Text = channelItem ?? "# channel";

        await LoadChannelHistoryAsync(channelId);
    }

    // Split out from ChannelButton_Click so OnServerKeyProvisioned can
    // re-run it once this device receives a key it didn't have yet -
    // messages that showed the "waiting for access" placeholder resolve to
    // their real content without needing the user to reopen the channel.
    private async Task LoadChannelHistoryAsync(int channelId)
    {
        if (_currentServerId is not { } serverId) return;

        if (await _api.E2ee.GetServerKeyAsync(serverId) is null)
        {
            // If this device is the server's owner, this could be a server
            // that predates the E2EE feature (or the moment right after
            // creation, if creation-time generation somehow didn't run) -
            // try self-bootstrapping. The server only accepts this when
            // truly no key exists anywhere yet for the server (see
            // ServersController.SetServerKey), so it's a safe no-op if a
            // real key already exists among other members - this device
            // just falls through to asking them instead. Otherwise (or if
            // bootstrap wasn't accepted), ask peers - the response, if
            // any, arrives asynchronously via OnServerKeyProvisioned,
            // which reloads this same history.
            var isOwner = _servers.FirstOrDefault(s => s.Id == serverId)?.OwnerId == _api.CurrentUserId;
            if (!isOwner || !await _api.E2ee.GenerateAndUploadServerKeyAsync(serverId))
                await _hub.RequestServerKeyAsync(serverId);
        }

        // Decrypted in parallel, not one at a time - Task.WhenAll preserves
        // the input order (still oldest-first, matching what the server
        // returned), same fix already applied to
        // ApiService.GetDmConversationsAsync this session.
        var history = await _api.GetMessageHistoryAsync(channelId);
        var items = await Task.WhenAll(history.Select(async m =>
            ToListItem(m, await _api.E2ee.DecryptForServerAsync(serverId, m.Content))));
        _messages.ReplaceAll(items);

        ScrollToBottom();
    }

    private async void VoiceChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId }) return;
        if (_voice is null) return;

        // Re-clicking the channel you're already in used to tear down and
        // re-establish the whole voice connection for no reason - harmless
        // most of the time, but a rapid double-click could land the leave
        // and the rejoin close enough together to leave the audio endpoint
        // (and everyone else's view of this connection) in a half-torn-down
        // state, breaking capture/playback for the whole channel.
        if (_currentVoiceChannelId == channelId) return;

        if (_currentVoiceChannelId.HasValue)
        {
            await _hub.LeaveVoiceChannelAsync(_currentVoiceChannelId.Value);
            await _voice.LeaveAllAsync();
            RemoveSelfFromVoiceRoster(_currentVoiceChannelId.Value);
        }

        _currentVoiceChannelId = channelId;

        // Show ourselves locally right away, styled as "joining" (see
        // VoiceMemberItem.IsJoining), instead of waiting for the LiveKit
        // connection below - that takes a few seconds, and without this
        // there'd be no feedback at all that the click registered. Other
        // clients don't get told about the join at all until it actually
        // succeeds (see JoinVoiceChannelAsync further down), so nobody else
        // ever sees this placeholder.
        var item = FindVoiceChannelItem(channelId);
        VoiceMemberItem? selfItem = null;
        if (item is not null && _api.CurrentUserId is not null && _api.CurrentUsername is not null)
        {
            selfItem = new VoiceMemberItem { UserId = _api.CurrentUserId.Value, Username = _api.CurrentUsername, AvatarUrl = _api.CurrentUserAvatarUrl, IsSelf = true, IsJoining = true };
            item.Members.Add(selfItem);
        }
        ConnectionStatusText.Text = "Joining voice...";

        try
        {
            await _voice.JoinChannelAsync(channelId);
        }
        catch
        {
            if (item is not null && selfItem is not null) item.Members.Remove(selfItem);
            _currentVoiceChannelId = null;
            ConnectionStatusText.Text = "";
            MessageBox.Show("Could not join voice - check your connection and try again.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Presence/roster bookkeeping (still SignalR, unchanged from the mesh
        // days) - deliberately called only now, after the audio connection
        // above actually succeeded, so other clients' rosters only pick up
        // this join once it's real rather than a few seconds early.
        var existingMembers = await _hub.JoinVoiceChannelAsync(channelId);

        if (item is not null)
        {
            item.Members.Clear();
            foreach (var m in existingMembers)
                item.Members.Add(new VoiceMemberItem { UserId = m.UserId, Username = m.Username, AvatarUrl = App.ResolveUploadUrl(m.AvatarUrl), IsSelf = m.UserId == _api.CurrentUserId, Volume = SavedVolumePercent(m.UserId) });
            if (_api.CurrentUserId is not null && _api.CurrentUsername is not null)
                item.Members.Add(new VoiceMemberItem { UserId = _api.CurrentUserId.Value, Username = _api.CurrentUsername, AvatarUrl = _api.CurrentUserAvatarUrl, IsSelf = true });
        }

        LeaveVoiceButton.Visibility = Visibility.Visible;
        MuteMicButton.Visibility = Visibility.Visible;
        DeafenButton.Visibility = Visibility.Visible;
        UpdateMuteButtonVisual();
        UpdateDeafenButtonVisual();
        ConnectionStatusText.Text = "Joined voice";

        // Unconditional (not gated on window focus like OnVoiceUserJoined's
        // toast/sound for other people) - this is direct feedback that your
        // own join went through, so it needs to play even if you're the
        // only one in the channel and even while the app is focused.
        NotificationService.PlayVoiceJoinSound();
    }

    private void MuteMicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;

        _voice.IsMicMuted = !_voice.IsMicMuted;
        UpdateMuteButtonVisual();
    }

    private void UpdateMuteButtonVisual()
    {
        if (_voice is null) return;

        MuteMicButton.Content = _voice.IsMicMuted ? "🔇 Unmute Mic" : "🎤 Mute Mic";
        MuteMicButton.Foreground = _voice.IsMicMuted
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0x3F, 0x42))
            : (System.Windows.Media.Brush)FindResource("TextMuted");
    }

    private void DeafenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;

        // Deafen forces mute to match (see VoiceService.IsDeafened), so
        // both buttons' visuals need refreshing, not just Deafen's own.
        _voice.IsDeafened = !_voice.IsDeafened;
        UpdateDeafenButtonVisual();
        UpdateMuteButtonVisual();
    }

    private void UpdateDeafenButtonVisual()
    {
        if (_voice is null) return;

        // Icon stays 🎧 in both states (color already signals active/inactive) -
        // reusing 🔇 here would make it indistinguishable from the Mute Mic
        // button's own active-state icon, which is a genuinely different
        // action (can't be heard vs. can't hear anyone).
        DeafenButton.Content = _voice.IsDeafened ? "🎧 Undeafen" : "🎧 Deafen";
        DeafenButton.Foreground = _voice.IsDeafened
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0x3F, 0x42))
            : (System.Windows.Media.Brush)FindResource("TextMuted");
    }

    private void VoiceMemberVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_voice is null) return;
        if (sender is not FrameworkElement { DataContext: VoiceMemberItem member }) return;

        var volume = (float)(e.NewValue / 100.0);
        _voice.SetRemoteVolume(member.UserId, volume);
        UserVolumeStorage.SaveVolume(member.UserId, volume);
    }

    private async void AddServerButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateOrJoinDialog { Owner = this };
        dialog.ShowDialog();

        if (dialog.CreateSelected == true)
        {
            var name = PromptForText("Create a Server", "Server name:");
            if (string.IsNullOrWhiteSpace(name)) return;

            var server = await _api.CreateServerAsync(name);
            if (server is not null)
            {
                // This is the one moment a device can be sure no key exists
                // for the server anywhere yet - see E2eeService.
                // GenerateAndUploadServerKeyAsync for why that certainty
                // matters. Every other member (there are none yet) will
                // request a copy from this device once they join.
                await _api.E2ee.GenerateAndUploadServerKeyAsync(server.Id);
                await LoadServersAsync();
            }
        }
        else if (dialog.CreateSelected == false)
        {
            var code = PromptForText("Join with a Code", "Invite code:");
            if (string.IsNullOrWhiteSpace(code)) return;

            var (success, error) = await _api.JoinByInviteAsync(code.Trim());
            if (!success)
            {
                MessageBox.Show(error ?? "Could not join with that invite code.", "Join Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            await LoadServersAsync();
        }
    }

    private async void AddChannelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int serverId }) return;

        var name = PromptForText("Add Channel", "Channel name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var isVoice = MessageBox.Show("Make this a voice channel?", "Channel Type",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        var created = await _api.CreateChannelAsync(serverId, name, isVoice ? "Voice" : "Text");

        // Join the new channel's group regardless of which server is open -
        // otherwise it wouldn't get unread dots until the next full
        // LoadServersAsync (e.g. next login).
        if (created is not null && !isVoice)
            await _hub.JoinChannelAsync(created.Id);

        // Only the currently-open server's channel list is visible - refresh
        // it if that's the one a channel was just added to.
        if (serverId == _currentServerId)
            await RefreshChannelsAsync(serverId);
    }

    private void InvitesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int serverId }) return;

        new InvitesWindow(_api, serverId) { Owner = this }.ShowDialog();
    }

    private async void LeaveServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int serverId }) return;

        var confirm = MessageBox.Show("Leave this server?", "Confirm Leave",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = await _api.LeaveServerAsync(serverId);
        if (!success)
        {
            MessageBox.Show(error ?? "Could not leave this server.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (serverId == _currentServerId)
        {
            _currentServerId = null;
            _textChannels.Clear();
            _voiceChannels.Clear();
            ServerNameText.Text = "Select a server";
            ChannelNameText.Text = "# select-a-channel";
            _messages.Clear();
        }

        await LoadServersAsync();
    }

    private async void DeleteChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId } || _currentServerId is null) return;

        var confirm = MessageBox.Show("Delete this channel? This cannot be undone.", "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var success = await _api.DeleteChannelAsync(_currentServerId.Value, channelId);
        if (!success)
        {
            MessageBox.Show("Could not delete this channel (you may lack permission).", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (channelId == _currentChannelId)
        {
            await _hub.LeaveChannelAsync(_currentChannelId.Value);
            _currentChannelId = null;
            _messages.Clear();
            ChannelNameText.Text = "# select-a-channel";
        }

        if (_voice is not null && channelId == _currentVoiceChannelId)
        {
            await _hub.LeaveVoiceChannelAsync(_currentVoiceChannelId.Value);
            await _voice.LeaveAllAsync();
            _currentVoiceChannelId = null;
            LeaveVoiceButton.Visibility = Visibility.Collapsed;
            MuteMicButton.Visibility = Visibility.Collapsed;
            DeafenButton.Visibility = Visibility.Collapsed;
            ConnectionStatusText.Text = "";
        }

        await RefreshChannelsAsync(_currentServerId.Value);
    }

    private async void LeaveVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentVoiceChannelId is null || _voice is null) return;

        await _hub.LeaveVoiceChannelAsync(_currentVoiceChannelId.Value);
        await _voice.LeaveAllAsync();
        RemoveSelfFromVoiceRoster(_currentVoiceChannelId.Value);
        _currentVoiceChannelId = null;
        LeaveVoiceButton.Visibility = Visibility.Collapsed;
        MuteMicButton.Visibility = Visibility.Collapsed;
        DeafenButton.Visibility = Visibility.Collapsed;
        ConnectionStatusText.Text = "";
    }

    // Removes just the local user's own entry from a voice channel's roster.
    // The old code called Members.Clear() here, which wiped out everyone
    // else still in the channel too - visible as "the whole list disappears"
    // the moment you leave, even though other people are still in the call.
    // Anyone else's departure is already handled correctly (one at a time)
    // by OnVoiceUserLeft reacting to the server's broadcast; this covers the
    // one case that broadcast doesn't reliably beat the UI update for - our
    // own leave, applied immediately rather than waiting on the round trip.
    private void RemoveSelfFromVoiceRoster(int channelId)
    {
        if (_api.CurrentUserId is null) return;
        var item = FindVoiceChannelItem(channelId);
        var self = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId);
        if (item is not null && self is not null) item.Members.Remove(self);
    }

    // UserVolumeStorage stores the 1.0-scale multiplier (matching
    // PlaybackVolume's own unit); VoiceMemberItem.Volume is the 0-200
    // percent scale the slider uses - this converts and defaults to 100%
    // (unchanged) for a user with no saved volume yet.
    private static double SavedVolumePercent(int userId) =>
        (UserVolumeStorage.GetVolume(userId) ?? 1.0f) * 100.0;

    // The session is already dead server-side by the time this fires (see
    // ApiService.SessionExpired) - just get the user back to a login screen,
    // no server call to make here unlike the normal logout button.
    private void OnSessionExpired()
    {
        SessionStorage.Clear();
        MessageBox.Show("Your session has expired. Please log in again.", "Signed Out",
            MessageBoxButton.OK, MessageBoxImage.Information);
        new LoginWindow().Show();
        Close();
    }

    private async void LogOutButton_Click(object sender, RoutedEventArgs e)
    {
        // MainWindow_Closed already handles leaving voice / disconnecting the hub.
        // Revokes the refresh token server-side (best-effort - see
        // ApiService.LogoutAsync) so it can't be redeemed again even if
        // something copied it, not just wiping the local copy.
        await _api.LogoutAsync();
        SessionStorage.Clear();
        var login = new LoginWindow();
        login.Show();
        Close();
    }

    private void MyAvatarBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        new SettingsWindow(_api, _voice) { Owner = this }.ShowDialog();

        // Settings' My Account tab may have just changed this - refresh our
        // own copy now that the (modal) dialog has closed.
        MyAvatarView.ImageUrl = _api.CurrentUserAvatarUrl;
    }

    private async void MessagesButton_Click(object sender, RoutedEventArgs e)
    {
        await LeaveServerContentAsync();
        ShowMessagesSidebar();

        _dmActiveUserId = null;
        _dmActiveUsername = null;
        _messages.Clear();
        ChannelNameText.Text = "Select a conversation";
        DmSearchBox.Clear();
        _dmSearchResults.Clear();

        // Re-clicking the Messages icon shouldn't silently clear unread
        // counts nobody's actually read yet - carry them over by
        // conversation partner.
        var previouslyUnread = _dmConversations.Where(c => c.HasUnread).ToDictionary(c => c.OtherUserId, c => c.UnreadCount);

        var conversations = await _api.GetDmConversationsAsync();
        _dmConversations.ReplaceAll(conversations.OrderByDescending(c => c.LastMessageAt).Select(c =>
        {
            var item = ToDmConversationItem(c);
            item.UnreadCount = previouslyUnread.GetValueOrDefault(c.OtherUserId);
            return item;
        }));
        UpdateMessagesUnreadBadge();
    }

    private async void FriendsButton_Click(object sender, RoutedEventArgs e)
    {
        await LeaveServerContentAsync();
        ShowFriendsSidebar();

        _dmActiveUserId = null;
        _dmActiveUsername = null;
        _messages.Clear();
        ChannelNameText.Text = "Select a conversation";
        FriendSearchBox.Clear();
        _friendSearchResults.Clear();

        await LoadFriendsAsync();
        await LoadFriendRequestsAsync();
    }

    // Called when leaving the server view entirely (Messages/Friends button
    // clicked). Deliberately does NOT leave text channel SignalR groups - the
    // app stays joined to all of them everywhere (see LoadServersAsync) so
    // unread dots keep working while browsing Messages/Friends, same as DMs.
    private async Task LeaveServerContentAsync()
    {
        _currentChannelId = null;

        if (_currentServerId.HasValue)
            await _hub.LeaveServerPresenceAsync(_currentServerId.Value);
    }

    private void ShowServerSidebar()
    {
        ServerSidebarPanel.Visibility = Visibility.Visible;
        MessagesSidebarPanel.Visibility = Visibility.Collapsed;
        FriendsSidebarPanel.Visibility = Visibility.Collapsed;
        MembersPanel.Visibility = Visibility.Visible;

        if (_dmActiveUserId.HasValue || ChannelNameText.Text != "# select-a-channel")
        {
            _dmActiveUserId = null;
            _dmActiveUsername = null;
            _messages.Clear();
            ChannelNameText.Text = "# select-a-channel";
        }
    }

    private void ShowMessagesSidebar()
    {
        ServerSidebarPanel.Visibility = Visibility.Collapsed;
        MessagesSidebarPanel.Visibility = Visibility.Visible;
        FriendsSidebarPanel.Visibility = Visibility.Collapsed;
        MembersPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowFriendsSidebar()
    {
        ServerSidebarPanel.Visibility = Visibility.Collapsed;
        MessagesSidebarPanel.Visibility = Visibility.Collapsed;
        FriendsSidebarPanel.Visibility = Visibility.Visible;
        MembersPanel.Visibility = Visibility.Collapsed;
    }

    private async void DmSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var query = DmSearchBox.Text.Trim();
        if (query.Length < 2)
        {
            _dmSearchResults.Clear();
            return;
        }

        var results = await _api.SearchUsersAsync(query);
        _dmSearchResults.Clear();
        foreach (var r in results.Where(r => r.Id != _api.CurrentUserId))
            _dmSearchResults.Add(new UserSearchResultItem { Id = r.Id, Username = r.Username, AvatarUrl = App.ResolveUploadUrl(r.AvatarUrl) });
    }

    private async void DmSearchResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int userId } button) return;
        var username = (button.DataContext as UserSearchResultItem)?.Username ?? "user";
        await OpenDmConversation(userId, username);
    }

    private async void DmConversation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int userId }) return;
        var convo = _dmConversations.FirstOrDefault(c => c.OtherUserId == userId);
        await OpenDmConversation(userId, convo?.OtherUsername ?? "user");
    }

    private async Task OpenDmConversation(int userId, string username)
    {
        _dmActiveUserId = userId;
        _dmActiveUsername = username;
        ChannelNameText.Text = $"@{username}";

        var convo = _dmConversations.FirstOrDefault(c => c.OtherUserId == userId);
        if (convo is not null) convo.UnreadCount = 0;
        UpdateMessagesUnreadBadge();

        DmSearchBox.Clear();
        _dmSearchResults.Clear();

        var history = await _api.GetDmHistoryAsync(userId);
        _messages.ReplaceAll(history.Select(m => ToDmListItem(m)));

        ScrollToBottom();
    }

    private async Task LoadFriendsAsync()
    {
        var friends = await _api.GetFriendsAsync();
        _friends.ReplaceAll(friends.Select(f => new FriendListItem { UserId = f.UserId, Username = f.Username, AvatarUrl = App.ResolveUploadUrl(f.AvatarUrl), PresenceState = f.PresenceState }));
    }

    private async Task LoadFriendRequestsAsync()
    {
        var requests = await _api.GetFriendRequestsAsync();
        _friendRequests.ReplaceAll(requests.Select(r => new FriendRequestListItem { Id = r.Id, UserId = r.UserId, Username = r.Username, Direction = r.Direction, AvatarUrl = App.ResolveUploadUrl(r.AvatarUrl) }));
    }

    private async void FriendSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var query = FriendSearchBox.Text.Trim();
        if (query.Length < 2)
        {
            _friendSearchResults.Clear();
            return;
        }

        var results = await _api.SearchUsersAsync(query);
        var existingIds = _friends.Select(f => f.UserId)
            .Concat(_friendRequests.Select(r => r.UserId))
            .ToHashSet();

        _friendSearchResults.Clear();
        foreach (var r in results.Where(r => r.Id != _api.CurrentUserId && !existingIds.Contains(r.Id)))
            _friendSearchResults.Add(new UserSearchResultItem { Id = r.Id, Username = r.Username, AvatarUrl = App.ResolveUploadUrl(r.AvatarUrl) });
    }

    private async void AddFriendButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int userId }) return;

        var (success, error) = await _api.SendFriendRequestAsync(userId);
        if (!success)
        {
            MessageBox.Show(error ?? "Could not send friend request.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        FriendSearchBox.Clear();
        _friendSearchResults.Clear();
        await LoadFriendsAsync();
        await LoadFriendRequestsAsync();
    }

    private async void FriendRequestAcceptButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int friendshipId }) return;

        await _api.AcceptFriendRequestAsync(friendshipId);
        await LoadFriendsAsync();
        await LoadFriendRequestsAsync();
    }

    private async void FriendRequestRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int friendshipId }) return;

        await _api.RemoveFriendshipAsync(friendshipId);
        await LoadFriendRequestsAsync();
    }

    private async void FriendListItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int userId } button) return;
        var username = (button.DataContext as FriendListItem)?.Username ?? "user";
        await OpenDmConversation(userId, username);
    }

    private void OnFriendRequestReceived(int friendshipId, int requesterId, string requesterUsername)
    {
        Dispatcher.Invoke(async () =>
        {
            if (FriendsSidebarPanel.Visibility == Visibility.Visible)
                await LoadFriendRequestsAsync();
        });
    }

    private void OnFriendRequestAccepted(int friendshipId, int accepterId)
    {
        Dispatcher.Invoke(async () =>
        {
            if (FriendsSidebarPanel.Visibility == Visibility.Visible)
            {
                await LoadFriendsAsync();
                await LoadFriendRequestsAsync();
            }
        });
    }

    private async void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dmActiveUserId.HasValue)
        {
            MessageBox.Show("Attachments aren't supported in direct messages yet.");
            return;
        }

        if (_currentChannelId is null)
        {
            MessageBox.Show("Select a channel first.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Supported files (*.png;*.jpg;*.jpeg;*.gif;*.webp;*.pdf;*.txt;*.zip)|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.pdf;*.txt;*.zip"
        };

        if (dialog.ShowDialog() != true) return;

        var (upload, error) = await _api.UploadFileAsync(dialog.FileName);
        if (upload is null)
        {
            MessageBox.Show(error ?? "Upload failed.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (_currentServerId is not { } serverId) return;

        // Content is always encrypted, even when empty (attachment-only) -
        // so the receive/history paths never need to guess whether a given
        // row is ciphertext or genuinely plaintext-empty.
        var encrypted = await _api.E2ee.EncryptForServerAsync(serverId, string.Empty);
        if (encrypted is null)
        {
            MessageBox.Show("Couldn't send - encryption isn't ready for this server yet.", "Encryption unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await _hub.SendMessageAsync(_currentChannelId.Value, encrypted, upload.Url);
    }

    private void AttachmentLink_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string relativeUrl }) return;

        // Attachments are served by the ASP.NET Core server as static files.
        var fullUrl = App.ApiBaseUrl.TrimEnd('/') + relativeUrl;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullUrl) { UseShellExecute = true });
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendCurrentMessage();

    private async void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await SendCurrentMessage();
    }

    private async void MessageInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_currentChannelId is null) return;

        // Throttle typing notifications to at most once every 2 seconds.
        if ((DateTime.UtcNow - _lastTypingNotify).TotalSeconds < 2) return;
        _lastTypingNotify = DateTime.UtcNow;

        await _hub.NotifyTypingAsync(_currentChannelId.Value);
    }

    private async Task SendCurrentMessage()
    {
        if (string.IsNullOrWhiteSpace(MessageInput.Text)) return;

        if (_dmActiveUserId.HasValue)
        {
            // Encrypted client-side before it ever reaches the hub - see
            // E2eeService. A null result means this device's keys aren't
            // unlocked yet or the recipient hasn't set up E2EE (no public
            // key on file) - surfaced instead of silently sending plaintext.
            var encrypted = await _api.E2ee.EncryptAsync(_dmActiveUserId.Value, MessageInput.Text.Trim());
            if (encrypted is null)
            {
                MessageBox.Show(
                    "Couldn't send an encrypted message right now - either your own encryption keys aren't unlocked yet, or this person hasn't logged in since secure messaging was added. Try logging out and back in.",
                    "Encryption unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await _hub.SendDirectMessageAsync(_dmActiveUserId.Value, encrypted);
            MessageInput.Clear();
            return;
        }

        if (_currentChannelId is null || _currentServerId is not { } serverId) return;

        var encryptedChannelMessage = await _api.E2ee.EncryptForServerAsync(serverId, MessageInput.Text.Trim());
        if (encryptedChannelMessage is null)
        {
            MessageBox.Show(
                "Couldn't send an encrypted message right now - this device doesn't have this server's encryption key yet. It's waiting for another online member to grant access.",
                "Encryption unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            await _hub.RequestServerKeyAsync(serverId);
            return;
        }

        await _hub.SendMessageAsync(_currentChannelId.Value, encryptedChannelMessage);
        MessageInput.Clear();
    }

    // Shared by both the right-click context menu items and the hover
    // edit/delete icons on each message row (see MainWindow.xaml) - both
    // are just a FrameworkElement carrying the message id in Tag, whichever
    // one the user actually clicks.
    private void EditMessageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int messageId }) return;

        var item = _messages.FirstOrDefault(m => m.Id == messageId);
        if (item is null) return;

        item.EditingContent = item.Content;
        item.IsEditing = true;
    }

    private async void DeleteMessageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int messageId }) return;

        var confirm = new ConfirmDialog("Confirm Delete", "Delete this message?", "Delete", destructive: true) { Owner = this };
        confirm.ShowDialog();
        if (!confirm.Result) return;

        // Channel delete is shown for every message and left to the server
        // to authorize (author or moderator/owner) - a non-author,
        // non-moderator click lands here and gets a Forbid, surfaced below,
        // rather than being hidden client-side (would need a cached role
        // lookup just to decide visibility). DMs are gated client-side
        // instead (MessageListItem.DeleteMenuVisibility), so this branch is
        // always the current user's own message there.
        bool success;
        if (_currentChannelId.HasValue)
            success = await _api.DeleteMessageAsync(_currentChannelId.Value, messageId);
        else if (_dmActiveUserId.HasValue)
            success = await _api.DeleteDirectMessageAsync(_dmActiveUserId.Value, messageId);
        else
            return;

        if (success)
        {
            var item = _messages.FirstOrDefault(m => m.Id == messageId);
            if (item is not null) _messages.Remove(item);
        }
        else
        {
            MessageBox.Show("Could not delete that message (you may lack permission).", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveMessageEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int messageId }) return;
        var item = _messages.FirstOrDefault(m => m.Id == messageId);
        if (item is null) return;

        var newContent = item.EditingContent.Trim();
        if (string.IsNullOrEmpty(newContent))
        {
            item.IsEditing = false;
            return;
        }

        DateTime? editedAt = null;
        string? newContentFromServer = null;

        if (_currentChannelId.HasValue && _currentServerId is { } serverId)
        {
            var encryptedEdit = await _api.E2ee.EncryptForServerAsync(serverId, newContent);
            if (encryptedEdit is not null)
            {
                var updated = await _api.EditMessageAsync(_currentChannelId.Value, messageId, encryptedEdit);
                newContentFromServer = updated is not null ? await _api.E2ee.DecryptForServerAsync(serverId, updated.Content) : null;
                editedAt = updated?.EditedAt;
            }
        }
        else if (_dmActiveUserId.HasValue)
        {
            var updated = await _api.EditDirectMessageAsync(_dmActiveUserId.Value, messageId, newContent);
            newContentFromServer = updated?.Content;
            editedAt = updated?.EditedAt;
        }

        if (newContentFromServer is not null)
        {
            item.Content = newContentFromServer;
            item.IsEdited = editedAt is not null;
        }

        item.IsEditing = false;
    }

    private void CancelMessageEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int messageId }) return;
        var item = _messages.FirstOrDefault(m => m.Id == messageId);
        if (item is not null) item.IsEditing = false;
    }

    // async void - invoked directly from a SignalR background thread, not
    // the UI thread, so an uncaught exception here would crash the whole
    // process rather than being caught by App.xaml.cs's
    // DispatcherUnhandledException (which only sees exceptions raised on
    // the UI thread's Dispatcher). Every async-void hub handler in this
    // file wraps its body the same way for that reason.
    private async void OnMessageReceived(MessageResponse msg)
    {
        try
        {
            var isOwnMessage = msg.AuthorId == _api.CurrentUserId;

            // Pushed straight from the hub, so still opaque ciphertext (see
            // ChatHub.SendMessage) - decrypt before this touches any UI-bound
            // state. _channelServerIds resolves which server's key to use -
            // this can arrive for a channel far from whatever's open right now
            // (that's the whole point of the unread-count path below).
            var content = _channelServerIds.TryGetValue(msg.ChannelId, out var serverId)
                ? await _api.E2ee.DecryptForServerAsync(serverId, msg.Content)
                : "[Encrypted message]";

            Dispatcher.Invoke(() =>
            {
                var isCurrentlyOpen = msg.ChannelId == _currentChannelId;

                if (isCurrentlyOpen)
                {
                    _messages.Add(ToListItem(msg, content));
                    ScrollToBottom();
                }
                else if (!isOwnMessage)
                {
                    // Someone else's message landed in a channel that isn't open
                    // right now - bump its unread count rather than just
                    // dropping it. _unreadTextChannelCounts is the source of
                    // truth (survives even if that channel's server has never
                    // been opened, so there's no ChannelListItem yet to mark);
                    // also update the item directly if it happens to already be
                    // loaded, for an instant UI update.
                    var newCount = _unreadTextChannelCounts.GetValueOrDefault(msg.ChannelId) + 1;
                    _unreadTextChannelCounts[msg.ChannelId] = newCount;
                    var item = FindTextChannelItem(msg.ChannelId);
                    if (item is not null) item.UnreadCount = newCount;
                }

                // Plays whenever you're not actually looking at this channel
                // right now - either a different channel/view is open, or this
                // one is open but the window itself isn't focused. Being on a
                // different view is the common case (e.g. sitting in Friends or
                // another channel) and was previously silent because this only
                // checked window focus, not which view was open.
                if (!isOwnMessage && (!isCurrentlyOpen || !IsActive))
                {
                    NotificationService.PlayMessageSound();
                    if (!isCurrentlyOpen)
                    {
                        var preview = content.Length > 80 ? content[..80] + "…" : content;
                        NotificationService.ShowToast(FindChannelDisplayName(msg.ChannelId) ?? "New message", preview);
                    }
                }
            });
        }
        catch
        {
            // Best-effort - a dropped/failed live-message update isn't worth
            // taking the app down for (the next history reload will show it).
        }
    }

    private async void OnDirectMessageReceived(DirectMessageResponse dm)
    {
        try
        {
            var otherUserId = dm.SenderId == _api.CurrentUserId ? dm.RecipientId : dm.SenderId;
            var isOwnMessage = dm.SenderId == _api.CurrentUserId;

            // Pushed straight from the hub, so still opaque ciphertext (see
            // ChatHub.SendDirectMessage) - decrypt before this touches any
            // UI-bound state, same as the REST paths in ApiService already do
            // transparently for history/conversation loads.
            var content = await _api.E2ee.DecryptAsync(otherUserId, dm.Content);

            Dispatcher.Invoke(() =>
            {
                // Bump/update the conversation list regardless of whether it's
                // currently visible, so it's accurate next time Messages is opened.
                var existing = _dmConversations.FirstOrDefault(c => c.OtherUserId == otherUserId);
                if (existing is not null) _dmConversations.Remove(existing);

                var isCurrentlyOpen = _dmActiveUserId == otherUserId;
                var newUnreadCount = !isOwnMessage && !isCurrentlyOpen ? (existing?.UnreadCount ?? 0) + 1 : 0;
                _dmConversations.Insert(0, new DmConversationListItem
                {
                    OtherUserId = otherUserId,
                    OtherUsername = existing?.OtherUsername ?? _dmActiveUsername ?? "user",
                    OtherUserAvatarUrl = existing?.OtherUserAvatarUrl,
                    LastMessagePreview = content,
                    LastMessageAt = dm.SentAt,
                    UnreadCount = newUnreadCount
                });

                if (isCurrentlyOpen)
                {
                    _messages.Add(ToDmListItem(dm, content));
                    ScrollToBottom();
                }

                UpdateMessagesUnreadBadge();

                // Same "not actually looking at this conversation" logic as
                // OnMessageReceived - either a different view is open, or this
                // DM is open but the window itself isn't focused.
                if (!isOwnMessage && (!isCurrentlyOpen || !IsActive))
                {
                    NotificationService.PlayMessageSound();
                    var preview = content.Length > 80 ? content[..80] + "…" : content;
                    NotificationService.ShowToast($"{existing?.OtherUsername ?? _dmActiveUsername ?? "New message"}", preview);
                }
            });
        }
        catch
        {
            // Best-effort - see OnMessageReceived.
        }
    }

    private async void OnMessageEdited(MessageResponse msg)
    {
        try
        {
            // Only worth decrypting if this channel is actually open below
            // (matches the early-return this had before) - still has to happen
            // before Dispatcher.Invoke since it's async.
            if (msg.ChannelId != _currentChannelId) return;
            if (!_channelServerIds.TryGetValue(msg.ChannelId, out var serverId)) return;
            var content = await _api.E2ee.DecryptForServerAsync(serverId, msg.Content);

            Dispatcher.Invoke(() =>
            {
                var item = _messages.FirstOrDefault(m => m.Id == msg.Id);
                if (item is null) return;

                item.Content = content;
                item.IsEdited = msg.EditedAt is not null;
            });
        }
        catch
        {
            // Best-effort - see OnMessageReceived.
        }
    }

    private void OnMessageDeleted(int messageId, int channelId)
    {
        Dispatcher.Invoke(() =>
        {
            if (channelId != _currentChannelId) return;
            var item = _messages.FirstOrDefault(m => m.Id == messageId);
            if (item is not null) _messages.Remove(item);
        });
    }

    private async void OnDirectMessageEdited(DirectMessageResponse dm)
    {
        try
        {
            var otherUserId = dm.SenderId == _api.CurrentUserId ? dm.RecipientId : dm.SenderId;

            // Only worth decrypting if this conversation is actually open below
            // (matches the early-return this had before) - still has to happen
            // before Dispatcher.Invoke since it's async.
            if (otherUserId != _dmActiveUserId) return;
            var content = await _api.E2ee.DecryptAsync(otherUserId, dm.Content);

            Dispatcher.Invoke(() =>
            {
                var item = _messages.FirstOrDefault(m => m.Id == dm.Id);
                if (item is null) return;

                item.Content = content;
                item.IsEdited = dm.EditedAt is not null;
            });
        }
        catch
        {
            // Best-effort - see OnMessageReceived.
        }
    }

    private void OnDirectMessageDeleted(int messageId, int senderId, int recipientId)
    {
        var otherUserId = senderId == _api.CurrentUserId ? recipientId : senderId;

        Dispatcher.Invoke(() =>
        {
            if (otherUserId != _dmActiveUserId) return;
            var item = _messages.FirstOrDefault(m => m.Id == messageId);
            if (item is not null) _messages.Remove(item);
        });
    }

    // A fellow member's device just asked for a copy of this server's
    // shared key (ChatHub.RequestServerKey) - if this device already has
    // it unlocked, hand over a copy wrapped for them. Best-effort and
    // silent: if this device doesn't have the key cached either, some
    // other online member may still answer, and if nobody does, the
    // requester just stays locked until someone is (see E2eeService for
    // why that's the standard tradeoff for group E2EE).
    private async void OnServerKeyRequested(int serverId, int requestingUserId)
    {
        try
        {
            await _api.E2ee.ProvisionServerKeyForPeerAsync(serverId, requestingUserId);
        }
        catch
        {
            // Best-effort - see OnMessageReceived. Some other online
            // member may still answer the same request.
        }
    }

    // This device just received a wrapped copy of a server key it didn't
    // have before (see OnServerKeyRequested on whichever peer answered).
    // If the channel that triggered the original request is still the one
    // open, reload it so the "waiting for access" placeholders resolve to
    // real content without the user needing to reopen anything.
    private async void OnServerKeyProvisioned(int serverId)
    {
        try
        {
            if (_currentServerId != serverId || _currentChannelId is not { } channelId) return;
            await LoadChannelHistoryAsync(channelId);
        }
        catch
        {
            // Best-effort - see OnMessageReceived. Worst case the
            // placeholder text just stays until the channel is reopened.
        }
    }

    // The Messages icon itself lights up while any conversation has an
    // unread dot - cleared only once that specific conversation is opened
    // (not just by visiting the Messages view), same as most chat apps.
    private void UpdateMessagesUnreadBadge()
    {
        var total = _dmConversations.Sum(c => c.UnreadCount);
        MessagesUnreadBadge.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        MessagesUnreadBadgeText.Text = total > 99 ? "99+" : total.ToString();
    }

    private void OnUserTyping(string username, int channelId)
    {
        if (channelId != _currentChannelId) return;

        Dispatcher.Invoke(() =>
        {
            ChannelNameText.Text = $"{FindChannelDisplayName(channelId)}   ({username} is typing...)";
        });
    }


    // SignalR's automatic reconnect gives us a fresh connection with no group
    // memberships, so whatever channel/voice channel the user had open needs
    // to be re-joined explicitly - otherwise messages/voice presence silently
    // stop arriving until the user manually switches channels.
    private async void OnReconnected()
    {
        if (_currentChannelId.HasValue)
            await _hub.JoinChannelAsync(_currentChannelId.Value);

        if (_currentVoiceChannelId.HasValue)
            await _hub.JoinVoiceChannelAsync(_currentVoiceChannelId.Value);

        Dispatcher.Invoke(() => ConnectionStatusText.Text = "");
    }

    private void OnVoiceUserJoined(int userId, string username, int channelId, string? avatarUrl)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            if (item is not null && !item.Members.Any(m => m.UserId == userId))
                item.Members.Add(new VoiceMemberItem { UserId = userId, Username = username, AvatarUrl = App.ResolveUploadUrl(avatarUrl), IsSelf = userId == _api.CurrentUserId, Volume = SavedVolumePercent(userId) });

            // Only for the voice channel you're actually sitting in, and
            // only when you're not looking at the app - the icon/roster
            // update above already covers "looking at it" case.
            if (channelId == _currentVoiceChannelId && userId != _api.CurrentUserId && !IsActive)
            {
                NotificationService.PlayVoiceJoinSound();
                NotificationService.ShowToast("Voice", $"{username} joined voice");
            }
        });
    }

    private void OnVoiceUserLeft(int userId, string username, int channelId)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            var existing = item?.Members.FirstOrDefault(m => m.UserId == userId);
            if (item is not null && existing is not null) item.Members.Remove(existing);

            if (channelId == _currentVoiceChannelId && userId != _api.CurrentUserId && !IsActive)
            {
                NotificationService.PlayVoiceLeaveSound();
                NotificationService.ShowToast("Voice", $"{username} left voice");
            }
        });
    }

    private void OnUserSpeaking(int userId, int channelId, bool isSpeaking)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            var member = item?.Members.FirstOrDefault(m => m.UserId == userId);
            if (member is not null) member.IsSpeaking = isSpeaking;
        });
    }

    // Fired from VoiceService's background audio-level detection, so this
    // isn't on the UI thread yet.
    private async Task OnLocalSpeakingChangedAsync(bool isSpeaking)
    {
        try
        {
            if (_currentVoiceChannelId.HasValue)
                await _hub.SendSpeakingAsync(_currentVoiceChannelId.Value, isSpeaking);
        }
        catch
        {
            // Best-effort - a dropped speaking-state update isn't worth surfacing an error for.
        }

        Dispatcher.Invoke(() =>
        {
            if (!_currentVoiceChannelId.HasValue || _api.CurrentUserId is null) return;
            var item = FindVoiceChannelItem(_currentVoiceChannelId.Value);
            var me = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId.Value);
            if (me is not null) me.IsSpeaking = isSpeaking;
        });
    }

    private void OnUserMuted(int userId, int channelId, bool isMuted)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            var member = item?.Members.FirstOrDefault(m => m.UserId == userId);
            if (member is not null) member.IsMuted = isMuted;
        });
    }

    private void OnUserDeafened(int userId, int channelId, bool isDeafened)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            var member = item?.Members.FirstOrDefault(m => m.UserId == userId);
            if (member is not null) member.IsDeafened = isDeafened;
        });
    }

    // Mirrors OnLocalSpeakingChangedAsync - broadcast to whoever else is in
    // the channel, then reflect it on our own row too (the row for "you"
    // needs the icon just as much as everyone else's).
    private async Task OnLocalMutedChangedAsync(bool isMuted)
    {
        try
        {
            if (_currentVoiceChannelId.HasValue)
                await _hub.SendMutedAsync(_currentVoiceChannelId.Value, isMuted);
        }
        catch
        {
            // Best-effort - a dropped mute-state update isn't worth surfacing an error for.
        }

        Dispatcher.Invoke(() =>
        {
            if (!_currentVoiceChannelId.HasValue || _api.CurrentUserId is null) return;
            var item = FindVoiceChannelItem(_currentVoiceChannelId.Value);
            var me = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId.Value);
            if (me is not null) me.IsMuted = isMuted;
        });
    }

    private async Task OnLocalDeafenedChangedAsync(bool isDeafened)
    {
        try
        {
            if (_currentVoiceChannelId.HasValue)
                await _hub.SendDeafenedAsync(_currentVoiceChannelId.Value, isDeafened);
        }
        catch
        {
            // Best-effort - a dropped deafen-state update isn't worth surfacing an error for.
        }

        Dispatcher.Invoke(() =>
        {
            if (!_currentVoiceChannelId.HasValue || _api.CurrentUserId is null) return;
            var item = FindVoiceChannelItem(_currentVoiceChannelId.Value);
            var me = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId.Value);
            if (me is not null) me.IsDeafened = isDeafened;
        });
    }

    // contentOverride lets a caller that already decrypted m.Content itself
    // (OnMessageReceived, working with a raw hub push) supply the plaintext
    // directly - callers going through LoadChannelHistoryAsync already
    // decrypt inline and pass the result the same way.
    private MessageListItem ToListItem(MessageResponse m, string? contentOverride = null) => new()
    {
        Id = m.Id,
        AuthorId = m.AuthorId,
        AuthorUsername = m.AuthorUsername,
        AuthorAvatarUrl = App.ResolveUploadUrl(m.AuthorAvatarUrl),
        Content = contentOverride ?? m.Content,
        TimeDisplay = m.SentAt.ToLocalTime().ToString("t"),
        AttachmentUrl = m.AttachmentUrl,
        IsEdited = m.EditedAt is not null,
        IsOwnMessage = m.AuthorId == _api.CurrentUserId,
        IsChannelMessage = true
    };

    // DirectMessageResponse doesn't carry a sender avatar (1:1 DMs - you
    // already know who you're talking to, unlike a multi-sender channel) -
    // pull it from context instead: our own cached avatar, or the open
    // conversation's cached one for the other side.
    // contentOverride lets a caller that already decrypted dm.Content itself
    // (OnDirectMessageReceived, working with a raw hub push that never went
    // through ApiService's transparent decrypt) supply the plaintext
    // directly - callers going through ApiService.GetDmHistoryAsync already
    // get plaintext in dm.Content and don't need it.
    private MessageListItem ToDmListItem(DirectMessageResponse dm, string? contentOverride = null) => new()
    {
        Id = dm.Id,
        AuthorId = dm.SenderId,
        AuthorUsername = dm.SenderId == _api.CurrentUserId ? "You" : (_dmActiveUsername ?? "them"),
        AuthorAvatarUrl = dm.SenderId == _api.CurrentUserId
            ? _api.CurrentUserAvatarUrl
            : _dmConversations.FirstOrDefault(c => c.OtherUserId == _dmActiveUserId)?.OtherUserAvatarUrl,
        Content = contentOverride ?? dm.Content,
        TimeDisplay = dm.SentAt.ToLocalTime().ToString("t"),
        IsEdited = dm.EditedAt is not null,
        IsOwnMessage = dm.SenderId == _api.CurrentUserId,
        IsChannelMessage = false
    };

    private static DmConversationListItem ToDmConversationItem(DmConversationResponse c) => new()
    {
        OtherUserId = c.OtherUserId,
        OtherUsername = c.OtherUsername,
        OtherUserAvatarUrl = App.ResolveUploadUrl(c.OtherUserAvatarUrl),
        LastMessagePreview = c.LastMessagePreview,
        LastMessageAt = c.LastMessageAt
    };

    private string? FindChannelDisplayName(int channelId)
    {
        foreach (var c in _textChannels) if (c.Id == channelId) return c.DisplayName;
        foreach (var c in _voiceChannels) if (c.Id == channelId) return c.DisplayName;
        return null;
    }

    private ChannelListItem? FindVoiceChannelItem(int channelId)
    {
        foreach (var c in _voiceChannels) if (c.Id == channelId) return c;
        return null;
    }

    private ChannelListItem? FindTextChannelItem(int channelId)
    {
        foreach (var c in _textChannels) if (c.Id == channelId) return c;
        return null;
    }

    private void ScrollToBottom() => MessageScroll.ScrollToEnd();

    private string? PromptForText(string title, string label)
    {
        var dialog = new TextInputDialog(title, label) { Owner = this };
        dialog.ShowDialog();
        return dialog.Result;
    }
}
