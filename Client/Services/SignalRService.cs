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
    public event Action<string, int>? UserTyping;
    public event Action<int, string, int, string?>? VoiceUserJoined;
    public event Action<int, string, int>? VoiceUserLeft;
    public event Action<int, int, bool>? UserSpeaking;
    public event Action<int, int, bool>? UserMuted;
    public event Action<int, int, bool>? UserDeafened;
    public event Action<int, int, string>? FriendRequestReceived;
    public event Action<int, int>? FriendRequestAccepted;
    public event Action<int, string>? PresenceChanged;
    public event Action<int, int>? ServerKeyRequested; // serverId, requestingUserId
    public event Action<int>? ServerKeyProvisioned; // serverId

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
        _connection.On<string, int>("UserTyping", (username, channelId) => UserTyping?.Invoke(username, channelId));
        _connection.On<int, string, int, string?>("VoiceUserJoined", (userId, username, channelId, avatarUrl) => VoiceUserJoined?.Invoke(userId, username, channelId, avatarUrl));
        _connection.On<int, string, int>("VoiceUserLeft", (userId, username, channelId) => VoiceUserLeft?.Invoke(userId, username, channelId));
        _connection.On<int, int, bool>("UserSpeaking", (userId, channelId, isSpeaking) => UserSpeaking?.Invoke(userId, channelId, isSpeaking));
        _connection.On<int, int, bool>("UserMuted", (userId, channelId, isMuted) => UserMuted?.Invoke(userId, channelId, isMuted));
        _connection.On<int, int, bool>("UserDeafened", (userId, channelId, isDeafened) => UserDeafened?.Invoke(userId, channelId, isDeafened));
        _connection.On<int, int, string>("FriendRequestReceived", (friendshipId, requesterId, requesterUsername) => FriendRequestReceived?.Invoke(friendshipId, requesterId, requesterUsername));
        _connection.On<int, int>("FriendRequestAccepted", (friendshipId, accepterId) => FriendRequestAccepted?.Invoke(friendshipId, accepterId));
        _connection.On<int, string>("PresenceChanged", (userId, state) => PresenceChanged?.Invoke(userId, state));
        _connection.On<int, int>("ServerKeyRequested", (serverId, requestingUserId) => ServerKeyRequested?.Invoke(serverId, requestingUserId));
        _connection.On<int>("ServerKeyProvisioned", serverId => ServerKeyProvisioned?.Invoke(serverId));

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
    public Task<List<VoiceParticipant>> JoinVoiceChannelAsync(int channelId) => _connection!.InvokeAsync<List<VoiceParticipant>>("JoinVoiceChannel", channelId);
    public Task LeaveVoiceChannelAsync(int channelId) => _connection!.InvokeAsync("LeaveVoiceChannel", channelId);
    public Task<LiveKitJoinResponse> GetLiveKitTokenAsync(int channelId) => _connection!.InvokeAsync<LiveKitJoinResponse>("GetLiveKitToken", channelId);
    public Task SendSpeakingAsync(int channelId, bool isSpeaking) => _connection!.InvokeAsync("NotifySpeaking", channelId, isSpeaking);
    public Task SendMutedAsync(int channelId, bool isMuted) => _connection!.InvokeAsync("NotifyMuted", channelId, isMuted);
    public Task SendDeafenedAsync(int channelId, bool isDeafened) => _connection!.InvokeAsync("NotifyDeafened", channelId, isDeafened);
    public Task JoinServerPresenceAsync(int serverId) => _connection!.InvokeAsync("JoinServerPresence", serverId);
    public Task LeaveServerPresenceAsync(int serverId) => _connection!.InvokeAsync("LeaveServerPresence", serverId);
    public Task<List<ChannelVoiceRoster>> GetVoiceRostersForServerAsync(int serverId) => _connection!.InvokeAsync<List<ChannelVoiceRoster>>("GetVoiceRostersForServer", serverId);
    public Task SetPresenceStateAsync(string state) => _connection!.InvokeAsync("SetPresenceState", state);
    public Task RequestServerKeyAsync(int serverId) => _connection!.InvokeAsync("RequestServerKey", serverId);

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task DisconnectAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
