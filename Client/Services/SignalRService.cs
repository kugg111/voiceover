using Voiceover.Client.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace Voiceover.Client.Services;

public class SignalRService
{
    private HubConnection? _connection;

    public event Action<MessageResponse>? MessageReceived;
    public event Action<DirectMessageResponse>? DirectMessageReceived;
    public event Action<string, int>? UserTyping;
    public event Action<int, string, int>? VoiceUserJoined;
    public event Action<int, string, int>? VoiceUserLeft;
    public event Action<int, int, string, string>? VoiceSignalReceived;
    public event Action<int, int, bool>? UserSpeaking;
    public event Action<int, int, string>? FriendRequestReceived;
    public event Action<int, int>? FriendRequestAccepted;

    // Connection lifecycle events, surfaced so the UI can show a status banner
    // instead of silently failing when the connection drops.
    public event Action? Reconnecting;
    public event Action? Reconnected;
    public event Action? ConnectionClosed;

    public async Task ConnectAsync(string hubUrl, string token)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        _connection.On<MessageResponse>("ReceiveMessage", msg => MessageReceived?.Invoke(msg));
        _connection.On<DirectMessageResponse>("ReceiveDirectMessage", dm => DirectMessageReceived?.Invoke(dm));
        _connection.On<string, int>("UserTyping", (username, channelId) => UserTyping?.Invoke(username, channelId));
        _connection.On<int, string, int>("VoiceUserJoined", (userId, username, channelId) => VoiceUserJoined?.Invoke(userId, username, channelId));
        _connection.On<int, string, int>("VoiceUserLeft", (userId, username, channelId) => VoiceUserLeft?.Invoke(userId, username, channelId));
        _connection.On<int, int, string, string>("VoiceSignal", (fromUserId, channelId, signalType, payload) => VoiceSignalReceived?.Invoke(fromUserId, channelId, signalType, payload));
        _connection.On<int, int, bool>("UserSpeaking", (userId, channelId, isSpeaking) => UserSpeaking?.Invoke(userId, channelId, isSpeaking));
        _connection.On<int, int, string>("FriendRequestReceived", (friendshipId, requesterId, requesterUsername) => FriendRequestReceived?.Invoke(friendshipId, requesterId, requesterUsername));
        _connection.On<int, int>("FriendRequestAccepted", (friendshipId, accepterId) => FriendRequestAccepted?.Invoke(friendshipId, accepterId));

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
    public Task SendVoiceSignalAsync(int targetUserId, int channelId, string signalType, string payload)
        => _connection!.InvokeAsync("SendVoiceSignal", targetUserId, channelId, signalType, payload);
    public Task SendSpeakingAsync(int channelId, bool isSpeaking) => _connection!.InvokeAsync("NotifySpeaking", channelId, isSpeaking);
    public Task JoinServerPresenceAsync(int serverId) => _connection!.InvokeAsync("JoinServerPresence", serverId);
    public Task LeaveServerPresenceAsync(int serverId) => _connection!.InvokeAsync("LeaveServerPresence", serverId);
    public Task<List<ChannelVoiceRoster>> GetVoiceRostersForServerAsync(int serverId) => _connection!.InvokeAsync<List<ChannelVoiceRoster>>("GetVoiceRostersForServer", serverId);

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task DisconnectAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
