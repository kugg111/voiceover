using Voiceover.Client.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace Voiceover.Client.Services;

public class SignalRService
{
    private HubConnection? _connection;

    public event Action<MessageResponse>? MessageReceived;
    public event Action<MessageResponse>? MessageEdited;
    public event Action<int, int>? MessageDeleted; // messageId, channelId
    public event Action<DirectMessageResponse>? DirectMessageReceived;
    public event Action<DirectMessageResponse>? DirectMessageEdited;
    public event Action<int, int, int>? DirectMessageDeleted; // messageId, senderId, recipientId
    public event Action<int, int, DateTime>? DirectMessagesRead; // readerId, otherUserId, readAtUtc
    public event Action<int, int, string, int, bool>? MessageReactionToggled; // channelId, messageId, emoji, userId, added
    public event Action<int, string, int, bool>? DirectMessageReactionToggled; // messageId, emoji, userId, added
    public event Action<int, int, DateTime>? MessagePinned; // channelId, messageId, pinnedAt
    public event Action<int, int>? MessageUnpinned; // channelId, messageId
    public event Action<int, int>? MessagesBulkDeletedByUser; // channelId, userId
    public event Action<int>? YouWereBanned; // serverId
    public event Action<int>? YouWereKicked; // serverId
    public event Action<int>? ForceMuted; // channelId
    // Bystander-facing moderation/channel events - unlike YouWereBanned/
    // YouWereKicked above (targeted at the affected user only), these go to
    // everyone in the server's server-presence group so open member lists,
    // ban lists, and the moderation log window can refresh live instead of
    // going stale until manually reopened.
    public event Action<int, int>? MemberKicked; // serverId, userId
    public event Action<int, int>? MemberBanned; // serverId, userId
    public event Action<int, int>? MemberUnbanned; // serverId, userId
    public event Action<int, int>? MemberRoleChanged; // serverId, userId
    public event Action<int>? ModerationLogChanged; // serverId
    public event Action<int>? ChannelCreated; // serverId
    public event Action<int>? ChannelDeleted; // serverId
    public event Action<int>? ServerDeleted; // serverId
    public event Action<int>? ServerRenamed; // serverId
    public event Action<string, int>? UserTyping;
    public event Action<int, string, int, string?>? VoiceUserJoined;
    public event Action<int, string, int>? VoiceUserLeft;
    public event Action<int, int, bool>? UserSpeaking;
    public event Action<int, int, bool>? UserMuted;
    public event Action<int, int, bool>? UserDeafened;
    public event Action<int, int, string>? FriendRequestReceived;
    public event Action<int, int>? FriendRequestAccepted;
    public event Action<int, string>? PresenceChanged;
    public event Action<int, string?>? CustomStatusChanged;
    public event Action<int, int>? ServerKeyRequested; // serverId, requestingUserId
    public event Action<int>? ServerKeyProvisioned; // serverId
    public event Action<string, int, string, string?>? IncomingCall; // callId, callerId, callerUsername, callerAvatarUrl
    public event Action<string>? CallAccepted; // callId
    public event Action<string>? CallDeclined; // callId
    public event Action<string>? CallEnded; // callId

    // Connection lifecycle events, surfaced so the UI can show a status banner
    // instead of silently failing when the connection drops.
    public event Action? Reconnecting;
    public event Action? Reconnected;
    public event Action? ConnectionClosed;

    // accessTokenProvider is called on the initial connect AND on every
    // automatic-reconnect attempt (SignalR re-invokes it each time, not just
    // once) - passing ApiService.GetFreshAccessTokenAsync here means a
    // reconnect that happens to land after the access token expired still
    // gets a live one instead of retrying with a stale token forever.
    public async Task ConnectAsync(string hubUrl, Func<Task<string?>> accessTokenProvider)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = accessTokenProvider;
            })
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        _connection.On<MessageResponse>("ReceiveMessage", msg => MessageReceived?.Invoke(msg));
        _connection.On<MessageResponse>("MessageEdited", msg => MessageEdited?.Invoke(msg));
        _connection.On<int, int>("MessageDeleted", (messageId, channelId) => MessageDeleted?.Invoke(messageId, channelId));
        _connection.On<DirectMessageResponse>("ReceiveDirectMessage", dm => DirectMessageReceived?.Invoke(dm));
        _connection.On<DirectMessageResponse>("DirectMessageEdited", dm => DirectMessageEdited?.Invoke(dm));
        _connection.On<int, int, int>("DirectMessageDeleted", (messageId, senderId, recipientId) => DirectMessageDeleted?.Invoke(messageId, senderId, recipientId));
        _connection.On<int, int, DateTime>("DirectMessagesRead", (readerId, otherUserId, readAtUtc) => DirectMessagesRead?.Invoke(readerId, otherUserId, readAtUtc));
        _connection.On<int, int, string, int, bool>("MessageReactionToggled", (channelId, messageId, emoji, userId, added) => MessageReactionToggled?.Invoke(channelId, messageId, emoji, userId, added));
        _connection.On<int, string, int, bool>("DirectMessageReactionToggled", (messageId, emoji, userId, added) => DirectMessageReactionToggled?.Invoke(messageId, emoji, userId, added));
        _connection.On<int, int, DateTime>("MessagePinned", (channelId, messageId, pinnedAt) => MessagePinned?.Invoke(channelId, messageId, pinnedAt));
        _connection.On<int, int>("MessageUnpinned", (channelId, messageId) => MessageUnpinned?.Invoke(channelId, messageId));
        _connection.On<int, int>("MessagesBulkDeletedByUser", (channelId, userId) => MessagesBulkDeletedByUser?.Invoke(channelId, userId));
        _connection.On<int>("YouWereBanned", serverId => YouWereBanned?.Invoke(serverId));
        _connection.On<int>("YouWereKicked", serverId => YouWereKicked?.Invoke(serverId));
        _connection.On<int>("ForceMuted", channelId => ForceMuted?.Invoke(channelId));
        _connection.On<int, int>("MemberKicked", (serverId, userId) => MemberKicked?.Invoke(serverId, userId));
        _connection.On<int, int>("MemberBanned", (serverId, userId) => MemberBanned?.Invoke(serverId, userId));
        _connection.On<int, int>("MemberUnbanned", (serverId, userId) => MemberUnbanned?.Invoke(serverId, userId));
        _connection.On<int, int>("MemberRoleChanged", (serverId, userId) => MemberRoleChanged?.Invoke(serverId, userId));
        _connection.On<int>("ModerationLogChanged", serverId => ModerationLogChanged?.Invoke(serverId));
        _connection.On<int>("ChannelCreated", serverId => ChannelCreated?.Invoke(serverId));
        _connection.On<int>("ChannelDeleted", serverId => ChannelDeleted?.Invoke(serverId));
        _connection.On<int>("ServerDeleted", serverId => ServerDeleted?.Invoke(serverId));
        _connection.On<int>("ServerRenamed", serverId => ServerRenamed?.Invoke(serverId));
        _connection.On<string, int>("UserTyping", (username, channelId) => UserTyping?.Invoke(username, channelId));
        _connection.On<int, string, int, string?>("VoiceUserJoined", (userId, username, channelId, avatarUrl) => VoiceUserJoined?.Invoke(userId, username, channelId, avatarUrl));
        _connection.On<int, string, int>("VoiceUserLeft", (userId, username, channelId) => VoiceUserLeft?.Invoke(userId, username, channelId));
        _connection.On<int, int, bool>("UserSpeaking", (userId, channelId, isSpeaking) => UserSpeaking?.Invoke(userId, channelId, isSpeaking));
        _connection.On<int, int, bool>("UserMuted", (userId, channelId, isMuted) => UserMuted?.Invoke(userId, channelId, isMuted));
        _connection.On<int, int, bool>("UserDeafened", (userId, channelId, isDeafened) => UserDeafened?.Invoke(userId, channelId, isDeafened));
        _connection.On<int, int, string>("FriendRequestReceived", (friendshipId, requesterId, requesterUsername) => FriendRequestReceived?.Invoke(friendshipId, requesterId, requesterUsername));
        _connection.On<int, int>("FriendRequestAccepted", (friendshipId, accepterId) => FriendRequestAccepted?.Invoke(friendshipId, accepterId));
        _connection.On<int, string>("PresenceChanged", (userId, state) => PresenceChanged?.Invoke(userId, state));
        _connection.On<int, string?>("CustomStatusChanged", (userId, status) => CustomStatusChanged?.Invoke(userId, status));
        _connection.On<int, int>("ServerKeyRequested", (serverId, requestingUserId) => ServerKeyRequested?.Invoke(serverId, requestingUserId));
        _connection.On<int>("ServerKeyProvisioned", serverId => ServerKeyProvisioned?.Invoke(serverId));
        _connection.On<string, int, string, string?>("IncomingCall", (callId, callerId, callerUsername, callerAvatarUrl) => IncomingCall?.Invoke(callId, callerId, callerUsername, callerAvatarUrl));
        _connection.On<string>("CallAccepted", callId => CallAccepted?.Invoke(callId));
        _connection.On<string>("CallDeclined", callId => CallDeclined?.Invoke(callId));
        _connection.On<string>("CallEnded", callId => CallEnded?.Invoke(callId));

        _connection.Reconnecting += _ => { Reconnecting?.Invoke(); return Task.CompletedTask; };
        _connection.Reconnected += _ => { Reconnected?.Invoke(); return Task.CompletedTask; };
        _connection.Closed += _ => { ConnectionClosed?.Invoke(); return Task.CompletedTask; };

        await _connection.StartAsync();
    }

    public Task JoinChannelAsync(int channelId) => _connection!.InvokeAsync("JoinChannel", channelId);
    public Task LeaveChannelAsync(int channelId) => _connection!.InvokeAsync("LeaveChannel", channelId);
    public Task SendMessageAsync(int channelId, string content, string? attachmentUrl = null)
        => _connection!.InvokeAsync("SendMessage", channelId, content, attachmentUrl);
    public Task NotifyTypingAsync(int channelId) => _connection!.InvokeAsync("NotifyTyping", channelId);
    public Task SendDirectMessageAsync(int recipientId, string content) => _connection!.InvokeAsync("SendDirectMessage", recipientId, content);
    public Task MarkDmReadAsync(int otherUserId) => _connection!.InvokeAsync("MarkDmRead", otherUserId);
    public Task ToggleMessageReactionAsync(int messageId, string emoji) => _connection!.InvokeAsync("ToggleMessageReaction", messageId, emoji);
    public Task ToggleDirectMessageReactionAsync(int messageId, string emoji) => _connection!.InvokeAsync("ToggleDirectMessageReaction", messageId, emoji);
    public Task<List<VoiceParticipant>> JoinVoiceChannelAsync(int channelId) => _connection!.InvokeAsync<List<VoiceParticipant>>("JoinVoiceChannel", channelId);
    public Task LeaveVoiceChannelAsync(int channelId) => _connection!.InvokeAsync("LeaveVoiceChannel", channelId);
    public Task<LiveKitJoinResponse> GetLiveKitTokenAsync(int channelId) => _connection!.InvokeAsync<LiveKitJoinResponse>("GetLiveKitToken", channelId);
    public Task SendSpeakingAsync(int channelId, bool isSpeaking) => _connection!.InvokeAsync("NotifySpeaking", channelId, isSpeaking);
    public Task SendMutedAsync(int channelId, bool isMuted) => _connection!.InvokeAsync("NotifyMuted", channelId, isMuted);
    public Task SendDeafenedAsync(int channelId, bool isDeafened) => _connection!.InvokeAsync("NotifyDeafened", channelId, isDeafened);
    public Task ForceMuteUserAsync(int channelId, int targetUserId) => _connection!.InvokeAsync("ForceMuteUser", channelId, targetUserId);
    public Task JoinServerPresenceAsync(int serverId) => _connection!.InvokeAsync("JoinServerPresence", serverId);
    public Task LeaveServerPresenceAsync(int serverId) => _connection!.InvokeAsync("LeaveServerPresence", serverId);
    public Task<List<ChannelVoiceRoster>> GetVoiceRostersForServerAsync(int serverId) => _connection!.InvokeAsync<List<ChannelVoiceRoster>>("GetVoiceRostersForServer", serverId);
    public Task SetPresenceStateAsync(string state) => _connection!.InvokeAsync("SetPresenceState", state);
    public Task RequestServerKeyAsync(int serverId) => _connection!.InvokeAsync("RequestServerKey", serverId);

    // --- Private calls (1:1, friends-only - see ChatHub's call signaling
    // methods server-side). Returns null from InitiateCallAsync if the two
    // aren't friends or either is already in a call. ---
    public Task<string?> InitiateCallAsync(int calleeId) => _connection!.InvokeAsync<string?>("InitiateCall", calleeId);
    public Task<bool> AcceptCallAsync(string callId) => _connection!.InvokeAsync<bool>("AcceptCall", callId);
    public Task DeclineCallAsync(string callId) => _connection!.InvokeAsync("DeclineCall", callId);
    public Task EndCallAsync(string callId) => _connection!.InvokeAsync("EndCall", callId);
    public Task<LiveKitJoinResponse> GetCallTokenAsync(string callId) => _connection!.InvokeAsync<LiveKitJoinResponse>("GetCallToken", callId);

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task DisconnectAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
