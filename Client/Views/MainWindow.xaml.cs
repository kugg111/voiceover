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

public class MessageListItem
{
    public string AuthorUsername { get; set; } = string.Empty;
    public string? AuthorAvatarUrl { get; set; }
    public string Content { get; set; } = string.Empty;
    public string TimeDisplay { get; set; } = string.Empty;
    public string? AttachmentUrl { get; set; }

    public string AttachmentDisplay => AttachmentUrl is null ? "" : $"📎 {System.IO.Path.GetFileName(AttachmentUrl)}";
    public Visibility ContentVisibility => string.IsNullOrEmpty(Content) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AttachmentVisibility => AttachmentUrl is null ? Visibility.Collapsed : Visibility.Visible;
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

public class FriendListItem
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
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

public partial class MainWindow : FluentWindow
{
    private readonly ApiService _api;
    private readonly SignalRService _hub = new();
    private VoiceService? _voice;

    private int? _currentServerId;
    private int? _currentChannelId;
    private int? _currentVoiceChannelId;
    private int? _dmActiveUserId;
    private string? _dmActiveUsername;

    private readonly ObservableCollection<ServerListItem> _servers = new();
    private readonly ObservableCollection<ChannelListItem> _textChannels = new();
    private readonly ObservableCollection<ChannelListItem> _voiceChannels = new();
    private readonly ObservableCollection<MessageListItem> _messages = new();
    private readonly ObservableCollection<DmConversationListItem> _dmConversations = new();
    private readonly ObservableCollection<UserSearchResultItem> _dmSearchResults = new();
    private readonly ObservableCollection<FriendListItem> _friends = new();
    private readonly ObservableCollection<FriendRequestListItem> _friendRequests = new();
    private readonly ObservableCollection<UserSearchResultItem> _friendSearchResults = new();

    // Source of truth for text-channel unread counts, kept independent of
    // _textChannels - a message can arrive for a channel before that
    // channel's server has ever been opened (so no ChannelListItem exists
    // yet to mark), unlike DM conversations, which get created fresh on
    // arrival regardless of prior UI state. channelId -> unread count.
    private readonly Dictionary<int, int> _unreadTextChannelCounts = new();

    private DateTime _lastTypingNotify = DateTime.MinValue;

    public MainWindow(ApiService api)
    {
        InitializeComponent();
        _api = api;

        MyAvatarView.DisplayName = _api.CurrentUsername ?? "?";
        MyAvatarView.ImageUrl = _api.CurrentUserAvatarUrl;

        ServerList.ItemsSource = _servers;
        TextChannelList.ItemsSource = _textChannels;
        VoiceChannelList.ItemsSource = _voiceChannels;
        MessageList.ItemsSource = _messages;
        DmConversationList.ItemsSource = _dmConversations;
        DmSearchResultsList.ItemsSource = _dmSearchResults;
        FriendList.ItemsSource = _friends;
        FriendRequestList.ItemsSource = _friendRequests;
        FriendSearchResultsList.ItemsSource = _friendSearchResults;

        _hub.MessageReceived += OnMessageReceived;
        _hub.DirectMessageReceived += OnDirectMessageReceived;
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

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_voice is not null)
            await _voice.DisposeAsync();
        await _hub.DisconnectAsync();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _hub.ConnectAsync(App.HubUrl, _api.Token!);
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
        await LoadServersAsync();
    }

    private async Task LoadServersAsync()
    {
        var servers = await _api.GetMyServersAsync();
        _servers.Clear();
        foreach (var s in servers)
            _servers.Add(new ServerListItem { Id = s.Id, Name = s.Name, IconUrl = App.ResolveUploadUrl(s.IconUrl) });

        // Join every text channel's SignalR group across every server the
        // user belongs to - not just whichever one happens to be open right
        // now. ReceiveMessage only reaches clients that are in a channel's
        // own group, so without this, unread dots could only ever work while
        // browsing the exact server a message landed in - unlike DMs, which
        // reach you regardless of what you're looking at (Clients.User, not
        // a group). A handful of servers/channels for a small friends app,
        // so the extra join calls up front are cheap.
        foreach (var server in servers)
        {
            var channels = await _api.GetChannelsAsync(server.Id);
            foreach (var c in channels.Where(c => c.Type == "Text"))
                await _hub.JoinChannelAsync(c.Id);
        }
    }

    private async Task RefreshChannelsAsync(int serverId)
    {
        var channels = await _api.GetChannelsAsync(serverId);
        _textChannels.Clear();
        _voiceChannels.Clear();
        foreach (var c in channels)
        {
            var item = new ChannelListItem
            {
                Id = c.Id,
                DisplayName = c.Type == "Text" ? $"# {c.Name}" : $"🔊 {c.Name}",
                UnreadCount = c.Type == "Text" ? _unreadTextChannelCounts.GetValueOrDefault(c.Id) : 0
            };
            if (c.Type == "Text") _textChannels.Add(item);
            else _voiceChannels.Add(item);
        }

        // Safety net for channels created after the initial LoadServersAsync
        // sweep (e.g. someone else added one) - harmless/idempotent if
        // already joined.
        foreach (var c in _textChannels)
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

        var servers = await _api.GetMyServersAsync();
        var server = servers.Find(s => s.Id == serverId);
        ServerNameText.Text = server?.Name ?? "Server";

        await RefreshChannelsAsync(serverId);
        await LoadVoiceRostersAsync(serverId);
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

        var history = await _api.GetMessageHistoryAsync(channelId);
        _messages.Clear();
        foreach (var m in history)
            _messages.Add(ToListItem(m));

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
        var existingMembers = await _hub.JoinVoiceChannelAsync(channelId);

        // JoinVoiceChannelAsync above is presence/roster bookkeeping only
        // (unchanged from the mesh days - still SignalR, still drives the
        // member list below); this is the actual audio connection, to the
        // separate LiveKit deployment.
        await _voice.JoinChannelAsync(channelId);

        var item = FindVoiceChannelItem(channelId);
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

        DeafenButton.Content = _voice.IsDeafened ? "🔇 Undeafen" : "🎧 Deafen";
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
            var name = PromptForText("Server name:");
            if (string.IsNullOrWhiteSpace(name)) return;

            var server = await _api.CreateServerAsync(name);
            if (server is not null)
                await LoadServersAsync();
        }
        else if (dialog.CreateSelected == false)
        {
            var code = PromptForText("Invite code:");
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

        var name = PromptForText("Channel name:");
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

    private void LogOutButton_Click(object sender, RoutedEventArgs e)
    {
        // MainWindow_Closed already handles leaving voice / disconnecting the hub.
        SessionStorage.Clear();
        var login = new LoginWindow();
        login.Show();
        Close();
    }

    private async void MembersButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServerId is null)
        {
            MessageBox.Show("Select a server first.");
            return;
        }

        var window = new MembersWindow(_api, _currentServerId.Value);
        window.Owner = this;
        window.ShowDialog();
    }

    private void VoiceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;

        var window = new VoiceSettingsWindow(_voice);
        window.Owner = this;
        window.ShowDialog();
    }

    private void MyAvatarBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        new SettingsWindow(_api, _voice) { Owner = this }.ShowDialog();
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
        _dmConversations.Clear();
        foreach (var c in conversations.OrderByDescending(c => c.LastMessageAt))
        {
            var item = ToDmConversationItem(c);
            item.UnreadCount = previouslyUnread.GetValueOrDefault(c.OtherUserId);
            _dmConversations.Add(item);
        }
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
        MembersNavButton.Visibility = Visibility.Visible;
        VoiceSettingsNavButton.Visibility = Visibility.Visible;

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
        MembersNavButton.Visibility = Visibility.Collapsed;
        VoiceSettingsNavButton.Visibility = Visibility.Collapsed;
    }

    private void ShowFriendsSidebar()
    {
        ServerSidebarPanel.Visibility = Visibility.Collapsed;
        MessagesSidebarPanel.Visibility = Visibility.Collapsed;
        FriendsSidebarPanel.Visibility = Visibility.Visible;
        MembersNavButton.Visibility = Visibility.Collapsed;
        VoiceSettingsNavButton.Visibility = Visibility.Collapsed;
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
        _messages.Clear();
        foreach (var m in history)
            _messages.Add(ToDmListItem(m));

        ScrollToBottom();
    }

    private async Task LoadFriendsAsync()
    {
        var friends = await _api.GetFriendsAsync();
        _friends.Clear();
        foreach (var f in friends)
            _friends.Add(new FriendListItem { UserId = f.UserId, Username = f.Username, AvatarUrl = App.ResolveUploadUrl(f.AvatarUrl) });
    }

    private async Task LoadFriendRequestsAsync()
    {
        var requests = await _api.GetFriendRequestsAsync();
        _friendRequests.Clear();
        foreach (var r in requests)
            _friendRequests.Add(new FriendRequestListItem { Id = r.Id, UserId = r.UserId, Username = r.Username, Direction = r.Direction, AvatarUrl = App.ResolveUploadUrl(r.AvatarUrl) });
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

        await _hub.SendMessageAsync(_currentChannelId.Value, string.Empty, upload.Url);
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
            await _hub.SendDirectMessageAsync(_dmActiveUserId.Value, MessageInput.Text.Trim());
            MessageInput.Clear();
            return;
        }

        if (_currentChannelId is null) return;

        await _hub.SendMessageAsync(_currentChannelId.Value, MessageInput.Text.Trim());
        MessageInput.Clear();
    }

    private void OnMessageReceived(MessageResponse msg)
    {
        Dispatcher.Invoke(() =>
        {
            var isOwnMessage = msg.AuthorId == _api.CurrentUserId;
            var isCurrentlyOpen = msg.ChannelId == _currentChannelId;

            if (isCurrentlyOpen)
            {
                _messages.Add(ToListItem(msg));
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
                    var preview = msg.Content.Length > 80 ? msg.Content[..80] + "…" : msg.Content;
                    NotificationService.ShowToast(FindChannelDisplayName(msg.ChannelId) ?? "New message", preview);
                }
            }
        });
    }

    private void OnDirectMessageReceived(DirectMessageResponse dm)
    {
        var otherUserId = dm.SenderId == _api.CurrentUserId ? dm.RecipientId : dm.SenderId;
        var isOwnMessage = dm.SenderId == _api.CurrentUserId;

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
                LastMessagePreview = dm.Content,
                LastMessageAt = dm.SentAt,
                UnreadCount = newUnreadCount
            });

            if (isCurrentlyOpen)
            {
                _messages.Add(ToDmListItem(dm));
                ScrollToBottom();
            }

            UpdateMessagesUnreadBadge();

            // Same "not actually looking at this conversation" logic as
            // OnMessageReceived - either a different view is open, or this
            // DM is open but the window itself isn't focused.
            if (!isOwnMessage && (!isCurrentlyOpen || !IsActive))
            {
                NotificationService.PlayMessageSound();
                var preview = dm.Content.Length > 80 ? dm.Content[..80] + "…" : dm.Content;
                NotificationService.ShowToast($"{existing?.OtherUsername ?? _dmActiveUsername ?? "New message"}", preview);
            }
        });
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

    private static MessageListItem ToListItem(MessageResponse m) => new()
    {
        AuthorUsername = m.AuthorUsername,
        AuthorAvatarUrl = App.ResolveUploadUrl(m.AuthorAvatarUrl),
        Content = m.Content,
        TimeDisplay = m.SentAt.ToLocalTime().ToString("t"),
        AttachmentUrl = m.AttachmentUrl
    };

    // DirectMessageResponse doesn't carry a sender avatar (1:1 DMs - you
    // already know who you're talking to, unlike a multi-sender channel) -
    // pull it from context instead: our own cached avatar, or the open
    // conversation's cached one for the other side.
    private MessageListItem ToDmListItem(DirectMessageResponse dm) => new()
    {
        AuthorUsername = dm.SenderId == _api.CurrentUserId ? "You" : (_dmActiveUsername ?? "them"),
        AuthorAvatarUrl = dm.SenderId == _api.CurrentUserId
            ? _api.CurrentUserAvatarUrl
            : _dmConversations.FirstOrDefault(c => c.OtherUserId == _dmActiveUserId)?.OtherUserAvatarUrl,
        Content = dm.Content,
        TimeDisplay = dm.SentAt.ToLocalTime().ToString("t")
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

    private static string? PromptForText(string label)
    {
        // Simple inline input dialog to keep this scaffold dependency-free.
        // Swap for a proper WPF dialog/UserControl as the app grows.
        var window = new Window
        {
            Width = 320,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Title = label,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["BgDark"]
        };

        var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        var textBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 8, 0, 8) };
        var okButton = new System.Windows.Controls.Button { Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };

        string? result = null;
        okButton.Click += (_, _) => { result = textBox.Text; window.Close(); };

        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = label,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextNormal"],
            Margin = new Thickness(0, 0, 0, 4)
        });
        stack.Children.Add(textBox);
        stack.Children.Add(okButton);
        window.Content = stack;

        window.ShowDialog();
        return result;
    }
}
