using System.Security.Claims;
using DiscordClone.Server.Data;
using DiscordClone.Server.Dtos;
using DiscordClone.Server.Models;
using DiscordClone.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DiscordClone.Server.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly AppDbContext _db;
    private readonly VoicePresenceService _voicePresence;

    public ChatHub(AppDbContext db, VoicePresenceService voicePresence)
    {
        _db = db;
        _voicePresence = voicePresence;
    }

    private int CurrentUserId => int.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string CurrentUsername => Context.User!.FindFirstValue(ClaimTypes.Name)!;

    // Client calls this after opening a channel so it starts receiving
    // messages/typing events for that channel via SignalR groups.
    public async Task JoinChannel(int channelId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(channelId));
    }

    public async Task LeaveChannel(int channelId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(channelId));
    }

    public async Task SendMessage(int channelId, string content, string? attachmentUrl = null)
    {
        if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(attachmentUrl)) return;

        var message = new Message
        {
            ChannelId = channelId,
            AuthorId = CurrentUserId,
            Content = content ?? string.Empty,
            AttachmentUrl = attachmentUrl,
            SentAt = DateTime.UtcNow
        };

        _db.Messages.Add(message);
        await _db.SaveChangesAsync();

        var response = new MessageResponse(message.Id, message.Content, channelId, CurrentUserId, CurrentUsername, message.SentAt, message.AttachmentUrl);
        await Clients.Group(GroupName(channelId)).SendAsync("ReceiveMessage", response);
    }

    public async Task NotifyTyping(int channelId)
    {
        await Clients.OthersInGroup(GroupName(channelId)).SendAsync("UserTyping", CurrentUsername, channelId);
    }

    // --- Direct messages ---
    // Relies on SignalR's default IUserIdProvider, which maps connections to
    // users via the ClaimTypes.NameIdentifier claim baked into the JWT - so
    // Clients.User(id) reaches every connection (device/window) that user has open.
    public async Task SendDirectMessage(int recipientId, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        var dm = new Models.DirectMessage
        {
            SenderId = CurrentUserId,
            RecipientId = recipientId,
            Content = content,
            SentAt = DateTime.UtcNow
        };

        _db.DirectMessages.Add(dm);
        await _db.SaveChangesAsync();

        var response = new Dtos.DirectMessageResponse(dm.Id, dm.Content, dm.SenderId, dm.RecipientId, dm.SentAt);

        await Clients.User(recipientId.ToString()).SendAsync("ReceiveDirectMessage", response);
        await Clients.User(CurrentUserId.ToString()).SendAsync("ReceiveDirectMessage", response);
    }

    // --- Voice channel presence (signaling only; audio itself is handled
    // separately, see notes on SIPSorcery/WebRTC integration) ---

    // Returns the roster of who was already in the channel, so the joining
    // client can display the full member list immediately instead of waiting
    // for future VoiceUserJoined events.
    public async Task<List<VoiceParticipant>> JoinVoiceChannel(int channelId)
    {
        var existingMembers = _voicePresence.Join(Context.ConnectionId, channelId, CurrentUserId, CurrentUsername);
        await Groups.AddToGroupAsync(Context.ConnectionId, VoiceGroupName(channelId));
        await Clients.OthersInGroup(VoiceGroupName(channelId)).SendAsync("VoiceUserJoined", CurrentUserId, CurrentUsername, channelId);
        return existingMembers;
    }

    public async Task LeaveVoiceChannel(int channelId)
    {
        _voicePresence.Leave(Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, VoiceGroupName(channelId));
        await Clients.Group(VoiceGroupName(channelId)).SendAsync("VoiceUserLeft", CurrentUserId, CurrentUsername, channelId);
    }

    // Relays WebRTC SDP offers/answers and ICE candidates between two specific
    // peers in a voice channel (mesh topology - each pair negotiates directly).
    // signalType is one of: "offer", "answer", "ice-candidate".
    public async Task SendVoiceSignal(int targetUserId, int channelId, string signalType, string payload)
    {
        await Clients.User(targetUserId.ToString())
            .SendAsync("VoiceSignal", CurrentUserId, channelId, signalType, payload);
    }

    // Client-side voice-activity detection pushes state changes here (not raw
    // audio levels) so this stays a cheap, infrequent broadcast rather than a
    // per-frame one.
    public async Task NotifySpeaking(int channelId, bool isSpeaking)
    {
        await Clients.OthersInGroup(VoiceGroupName(channelId)).SendAsync("UserSpeaking", CurrentUserId, channelId, isSpeaking);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var left = _voicePresence.Leave(Context.ConnectionId);
        if (left is not null)
        {
            var (channelId, userId, username) = left.Value;
            await Clients.Group(VoiceGroupName(channelId)).SendAsync("VoiceUserLeft", userId, username, channelId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    private static string GroupName(int channelId) => $"channel-{channelId}";
    private static string VoiceGroupName(int channelId) => $"voice-{channelId}";
}
