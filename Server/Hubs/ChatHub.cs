using System.Security.Claims;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Models;
using Voiceover.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly AppDbContext _db;
    private readonly VoicePresenceService _voicePresence;
    private readonly LiveKitTokenService _liveKitTokens;
    private readonly PresenceService _presence;

    public ChatHub(AppDbContext db, VoicePresenceService voicePresence, LiveKitTokenService liveKitTokens, PresenceService presence)
    {
        _db = db;
        _voicePresence = voicePresence;
        _liveKitTokens = liveKitTokens;
        _presence = presence;
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

        // CurrentUserId/CurrentUsername come straight from JWT claims, but
        // avatar isn't (and shouldn't be - it changes more often than a
        // token gets refreshed) baked into the token, so it needs an actual
        // lookup here rather than reusing those.
        var authorAvatarUrl = (await _db.Users.FindAsync(CurrentUserId))?.AvatarUrl;

        var response = new MessageResponse(message.Id, message.Content, channelId, CurrentUserId, CurrentUsername, message.SentAt, message.AttachmentUrl, authorAvatarUrl);
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

    // --- Voice channel presence (roster/mute/deafen/speaking signaling only -
    // audio itself flows through a separate self-hosted LiveKit deployment,
    // see LiveKitTokenService/GetLiveKitToken below; this server never
    // touches the media plane) ---

    // Returns the roster of who was already in the channel, so the joining
    // client can display the full member list immediately instead of waiting
    // for future VoiceUserJoined events.
    public async Task<List<VoiceParticipant>> JoinVoiceChannel(int channelId)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return new List<VoiceParticipant>();

        var avatarUrl = (await _db.Users.FindAsync(CurrentUserId))?.AvatarUrl;
        var existingMembers = _voicePresence.Join(Context.ConnectionId, channelId, channel.GuildServerId, CurrentUserId, CurrentUsername, avatarUrl);
        await Groups.AddToGroupAsync(Context.ConnectionId, VoiceGroupName(channelId));
        await Clients.OthersInGroup(VoiceGroupName(channelId)).SendAsync("VoiceUserJoined", CurrentUserId, CurrentUsername, channelId, avatarUrl);

        // Also notify anyone just viewing the server (not necessarily in this
        // voice channel) so their sidebar roster updates live too. Someone in
        // both groups gets this event twice - harmless, the client-side
        // handler is idempotent (checks the member isn't already listed).
        await Clients.OthersInGroup(ServerPresenceGroupName(channel.GuildServerId)).SendAsync("VoiceUserJoined", CurrentUserId, CurrentUsername, channelId, avatarUrl);

        return existingMembers;
    }

    // Mints a LiveKit join token for the SFU deployment - a separate,
    // additive path alongside the existing mesh presence/signaling above
    // while the client-side rewrite to actually use it is still pending
    // (see LiveKitTokenService for why this doesn't require configuration
    // at server startup). Room name mirrors VoiceGroupName's convention.
    public Task<LiveKitJoinResponse> GetLiveKitToken(int channelId)
    {
        var token = _liveKitTokens.CreateJoinToken(CurrentUserId, CurrentUsername, channelId);
        return Task.FromResult(new LiveKitJoinResponse(token, _liveKitTokens.ServerUrl ?? string.Empty));
    }

    public async Task LeaveVoiceChannel(int channelId)
    {
        var left = _voicePresence.Leave(Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, VoiceGroupName(channelId));
        await Clients.Group(VoiceGroupName(channelId)).SendAsync("VoiceUserLeft", CurrentUserId, CurrentUsername, channelId);

        if (left is not null)
            await Clients.Group(ServerPresenceGroupName(left.Value.ServerId)).SendAsync("VoiceUserLeft", CurrentUserId, CurrentUsername, channelId);
    }

    // Client-side voice-activity detection pushes state changes here (not raw
    // audio levels) so this stays a cheap, infrequent broadcast rather than a
    // per-frame one.
    public async Task NotifySpeaking(int channelId, bool isSpeaking)
    {
        await Clients.OthersInGroup(VoiceGroupName(channelId)).SendAsync("UserSpeaking", CurrentUserId, channelId, isSpeaking);

        var serverId = _voicePresence.GetServerId(Context.ConnectionId);
        if (serverId is not null)
            await Clients.OthersInGroup(ServerPresenceGroupName(serverId.Value)).SendAsync("UserSpeaking", CurrentUserId, channelId, isSpeaking);
    }

    // Mirrors NotifySpeaking - same live-broadcast-only shape (not part of
    // the roster snapshot GetVoiceRostersForServer returns), so someone who
    // opens a voice channel after another participant already muted won't
    // see the icon until that participant's mute state next changes. Same
    // tradeoff the speaking indicator already accepts.
    public async Task NotifyMuted(int channelId, bool isMuted)
    {
        await Clients.OthersInGroup(VoiceGroupName(channelId)).SendAsync("UserMuted", CurrentUserId, channelId, isMuted);

        var serverId = _voicePresence.GetServerId(Context.ConnectionId);
        if (serverId is not null)
            await Clients.OthersInGroup(ServerPresenceGroupName(serverId.Value)).SendAsync("UserMuted", CurrentUserId, channelId, isMuted);
    }

    public async Task NotifyDeafened(int channelId, bool isDeafened)
    {
        await Clients.OthersInGroup(VoiceGroupName(channelId)).SendAsync("UserDeafened", CurrentUserId, channelId, isDeafened);

        var serverId = _voicePresence.GetServerId(Context.ConnectionId);
        if (serverId is not null)
            await Clients.OthersInGroup(ServerPresenceGroupName(serverId.Value)).SendAsync("UserDeafened", CurrentUserId, channelId, isDeafened);
    }

    // --- Online/away/offline presence. Online is set on connect (see
    // OnConnectedAsync); Offline is always server-derived from the last
    // connection dropping (see OnDisconnectedAsync), never client-reported.
    // Away is the only state the client actively reports, when its own
    // idle detection crosses the threshold (see IdleDetector client-side). ---

    // Client calls this only with "Online" or "Away" - Offline is never
    // accepted from here, it can only happen via disconnection.
    public async Task SetPresenceState(string state)
    {
        if (state is not ("Online" or "Away")) return;

        _presence.SetState(CurrentUserId, state);
        await BroadcastPresenceChangeAsync(CurrentUserId, state);
    }

    // Fans a presence change out to the same audience that would ever want
    // to see it: accepted friends (wherever they're looking, same as
    // FriendRequestReceived) and fellow members of any server this user
    // belongs to who currently have that server open (ServerPresenceGroupName,
    // the same group VoiceUserJoined/UserSpeaking etc. already broadcast to).
    private async Task BroadcastPresenceChangeAsync(int userId, string state)
    {
        var friendIds = await _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == userId || f.AddresseeId == userId))
            .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId)
            .ToListAsync();

        if (friendIds.Count > 0)
            await Clients.Users(friendIds.Select(id => id.ToString())).SendAsync("PresenceChanged", userId, state);

        var serverIds = await _db.Memberships
            .Where(m => m.UserId == userId)
            .Select(m => m.GuildServerId)
            .ToListAsync();

        foreach (var serverId in serverIds)
            await Clients.Group(ServerPresenceGroupName(serverId)).SendAsync("PresenceChanged", userId, state);
    }

    public override async Task OnConnectedAsync()
    {
        if (_presence.Connect(CurrentUserId, Context.ConnectionId))
            await BroadcastPresenceChangeAsync(CurrentUserId, "Online");

        await base.OnConnectedAsync();
    }

    // --- Server presence (joined whenever a client selects a server in the
    // UI, independent of the per-voice-channel group above) ---

    public async Task JoinServerPresence(int serverId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, ServerPresenceGroupName(serverId));
    }

    public async Task LeaveServerPresence(int serverId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ServerPresenceGroupName(serverId));
    }

    // Lets a client that just opened a server (without joining any voice
    // channel) populate its sidebar with who's currently in each voice
    // channel, without joining anything itself.
    public async Task<List<ChannelVoiceRoster>> GetVoiceRostersForServer(int serverId)
    {
        var voiceChannelIds = await _db.Channels
            .Where(c => c.GuildServerId == serverId && c.Type == ChannelType.Voice)
            .Select(c => c.Id)
            .ToListAsync();

        return voiceChannelIds
            .Select(id => new ChannelVoiceRoster(id, _voicePresence.GetRoster(id)))
            .ToList();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var left = _voicePresence.Leave(Context.ConnectionId);
        if (left is not null)
        {
            var (channelId, serverId, userId, username) = left.Value;
            await Clients.Group(VoiceGroupName(channelId)).SendAsync("VoiceUserLeft", userId, username, channelId);
            await Clients.Group(ServerPresenceGroupName(serverId)).SendAsync("VoiceUserLeft", userId, username, channelId);
        }

        var disconnected = _presence.Disconnect(Context.ConnectionId);
        if (disconnected is { WasLastConnection: true } d)
            await BroadcastPresenceChangeAsync(d.UserId, "Offline");

        await base.OnDisconnectedAsync(exception);
    }

    private static string GroupName(int channelId) => $"channel-{channelId}";
    private static string VoiceGroupName(int channelId) => $"voice-{channelId}";
    private static string ServerPresenceGroupName(int serverId) => $"server-presence-{serverId}";
}
