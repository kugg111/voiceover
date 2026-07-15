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
    private readonly MessageRateLimiter _messageRateLimiter;
    private readonly UserAvatarCache _avatarCache;
    private readonly CallSignalingService _callSignaling;
    private readonly CallRateLimiter _callRateLimiter;
    private readonly PresenceAudienceCache _presenceAudience;
    private readonly SlowModeLimiter _slowMode;
    private readonly PermissionService _permissions;

    public ChatHub(AppDbContext db, VoicePresenceService voicePresence, LiveKitTokenService liveKitTokens, PresenceService presence, MessageRateLimiter messageRateLimiter, UserAvatarCache avatarCache, CallSignalingService callSignaling, CallRateLimiter callRateLimiter, PresenceAudienceCache presenceAudience, SlowModeLimiter slowMode, PermissionService permissions)
    {
        _db = db;
        _voicePresence = voicePresence;
        _liveKitTokens = liveKitTokens;
        _presence = presence;
        _messageRateLimiter = messageRateLimiter;
        _avatarCache = avatarCache;
        _callSignaling = callSignaling;
        _callRateLimiter = callRateLimiter;
        _presenceAudience = presenceAudience;
        _slowMode = slowMode;
        _permissions = permissions;
    }

    private int CurrentUserId => int.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string CurrentUsername => Context.User!.FindFirstValue(ClaimTypes.Name)!;

    // Client calls this after opening a channel so it starts receiving
    // messages/typing events for that channel via SignalR groups. Membership-
    // gated - previously anyone authenticated could join any channel's group
    // regardless of whether they belonged to that server. A banned user
    // never has a Membership row to begin with (ServersController.Ban
    // removes it atomically in the same save as adding the ban), so this
    // check alone also covers "and isn't banned" without a second query.
    public async Task JoinChannel(int channelId)
    {
        var channel = await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null || !await _permissions.IsMemberAsync(CurrentUserId, channel.GuildServerId)) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Channel(channelId));
    }

    public async Task LeaveChannel(int channelId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Channel(channelId));
    }

    // content is already E2EE ciphertext by the time it gets here - the
    // client encrypts client-side under that server's shared key before
    // calling this (see Client/Services/E2eeService.cs and
    // ServerMemberKey). This hub relays opaque bytes; it can't decrypt
    // channel messages even if it wanted to.
    public async Task SendMessage(int channelId, string content, string? attachmentUrl = null)
    {
        if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(attachmentUrl)) return;
        if (content.Length > ContentLimits.MaxMessageLength) return;

        // Silently dropped rather than throwing - same treatment as the
        // empty-content no-op above. This is anti-spam, not a security
        // boundary, so a flooding client just sees its excess messages
        // never arrive rather than a HubException the client isn't set up
        // to show useful feedback for (see SetPresenceState's history with
        // exactly that failure mode).
        if (!_messageRateLimiter.TryAcquire(CurrentUserId)) return;

        // Slow-mode only applies to plain Members - Moderators/Owners are
        // exempt (standard convention). Skips the extra queries entirely
        // for the common case of a channel with slow-mode off.
        var channel = await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return;

        // Membership check - same reasoning as JoinChannel above (a banned
        // user has no Membership row, so this alone also excludes them).
        if (!await _permissions.IsMemberAsync(CurrentUserId, channel.GuildServerId)) return;

        if (channel.SlowModeSeconds > 0)
        {
            var membership = await _permissions.GetMembershipAsync(CurrentUserId, channel.GuildServerId);
            var isExempt = membership is not null && membership.Role is MemberRole.Owner or MemberRole.Moderator;
            if (!isExempt && !_slowMode.TryAcquire(channelId, CurrentUserId, channel.SlowModeSeconds)) return;
        }

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
        // token gets refreshed) baked into the token. UserAvatarCache
        // avoids a DB round trip for this on every single message send -
        // on a cache miss (first message from this user since the server
        // started), fall back to the DB once and cache the result.
        string? authorAvatarUrl;
        if (!_avatarCache.TryGet(CurrentUserId, out authorAvatarUrl))
        {
            authorAvatarUrl = (await _db.Users.FindAsync(CurrentUserId))?.AvatarUrl;
            _avatarCache.Set(CurrentUserId, authorAvatarUrl);
        }

        var response = new MessageResponse(message.Id, message.Content, channelId, CurrentUserId, CurrentUsername, message.SentAt, message.AttachmentUrl, authorAvatarUrl);
        await Clients.Group(HubGroups.Channel(channelId)).SendAsync("ReceiveMessage", response);
    }

    public async Task NotifyTyping(int channelId)
    {
        await Clients.OthersInGroup(HubGroups.Channel(channelId)).SendAsync("UserTyping", CurrentUsername, channelId);
    }

    // Emoji reactions - a small fixed picker client-side (see MainWindow's
    // reaction button), never sensitive/E2EE content, so this (and its DM
    // equivalent below) stores the emoji as plain text. Reacting again with
    // the same emoji removes it - one method covering both add and remove,
    // same shape EndCallInternalAsync already uses for decline/hangup.
    // Broadcasts only the delta (not a recomputed full reaction list) so
    // clients aggregate locally, same as UserSpeaking/UserMuted etc.
    public async Task ToggleMessageReaction(int messageId, string emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji) || emoji.Length > 8) return;
        if (!_messageRateLimiter.TryAcquire(CurrentUserId)) return;

        var message = await _db.Messages.Include(m => m.Channel).FirstOrDefaultAsync(m => m.Id == messageId);
        if (message is null) return;

        // Membership check - same reasoning as JoinChannel/SendMessage above.
        if (!await _permissions.IsMemberAsync(CurrentUserId, message.Channel!.GuildServerId)) return;

        var existing = await _db.MessageReactions.FirstOrDefaultAsync(r =>
            r.MessageId == messageId && r.UserId == CurrentUserId && r.Emoji == emoji);

        bool added;
        if (existing is not null)
        {
            _db.MessageReactions.Remove(existing);
            added = false;
        }
        else
        {
            _db.MessageReactions.Add(new MessageReaction { MessageId = messageId, UserId = CurrentUserId, Emoji = emoji });
            added = true;
        }

        await _db.SaveChangesAsync();

        await Clients.Group(HubGroups.Channel(message.ChannelId))
            .SendAsync("MessageReactionToggled", message.ChannelId, messageId, emoji, CurrentUserId, added);
    }

    public async Task ToggleDirectMessageReaction(int messageId, string emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji) || emoji.Length > 8) return;
        if (!_messageRateLimiter.TryAcquire(CurrentUserId)) return;

        var message = await _db.DirectMessages.FirstOrDefaultAsync(m => m.Id == messageId);
        if (message is null || (message.SenderId != CurrentUserId && message.RecipientId != CurrentUserId)) return;

        var existing = await _db.DirectMessageReactions.FirstOrDefaultAsync(r =>
            r.DirectMessageId == messageId && r.UserId == CurrentUserId && r.Emoji == emoji);

        bool added;
        if (existing is not null)
        {
            _db.DirectMessageReactions.Remove(existing);
            added = false;
        }
        else
        {
            _db.DirectMessageReactions.Add(new DirectMessageReaction { DirectMessageId = messageId, UserId = CurrentUserId, Emoji = emoji });
            added = true;
        }

        await _db.SaveChangesAsync();

        var otherUserId = message.SenderId == CurrentUserId ? message.RecipientId : message.SenderId;
        await Clients.Users(new[] { CurrentUserId.ToString(), otherUserId.ToString() })
            .SendAsync("DirectMessageReactionToggled", messageId, emoji, CurrentUserId, added);
    }

    // Called by a client that just joined a server (or opened one for the
    // first time since E2EE shipped) and doesn't have a wrapped copy of its
    // shared message key yet. Fans the request out to every other member's
    // currently-connected sessions (not just whoever has the server "open"
    // right now - Clients.Users reaches every connection that member has),
    // so whichever of them already has the key unlocked in memory can wrap
    // a copy for the requester and PUT it via ServersController.SetServerKey.
    // If nobody who has the key happens to be online right now, the
    // requester just stays locked until someone is - the same tradeoff
    // every group-E2EE app (Signal, Matrix) makes, since nothing server-side
    // can hand out a key it never has.
    public async Task RequestServerKey(int serverId)
    {
        var otherMemberIds = await _db.Memberships
            .Where(m => m.GuildServerId == serverId && m.UserId != CurrentUserId)
            .Select(m => m.UserId)
            .ToListAsync();

        if (otherMemberIds.Count == 0) return;

        await Clients.Users(otherMemberIds.Select(id => id.ToString())).SendAsync("ServerKeyRequested", serverId, CurrentUserId);
    }

    // --- Direct messages ---
    // Relies on SignalR's default IUserIdProvider, which maps connections to
    // users via the ClaimTypes.NameIdentifier claim baked into the JWT - so
    // Clients.User(id) reaches every connection (device/window) that user has open.
    // content is already E2EE ciphertext by the time it gets here - the
    // client encrypts client-side before calling this (see
    // Client/Services/E2eeService.cs) using a key derived from both
    // participants' ECDH keypairs that the server never has. This hub
    // relays opaque bytes; it can't decrypt DMs even if it wanted to.
    public async Task SendDirectMessage(int recipientId, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        if (content.Length > ContentLimits.MaxMessageLength) return;

        // Shares the same per-user budget as SendMessage above - one spam
        // allowance across channels and DMs, not double the throughput by
        // splitting between the two.
        if (!_messageRateLimiter.TryAcquire(CurrentUserId)) return;

        // A bad/stale/typo'd recipient id would otherwise silently create a
        // permanent, undeliverable row (DirectMessage has no FK on
        // RecipientId - see AppDbContext).
        if (!await _db.Users.AnyAsync(u => u.Id == recipientId)) return;

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

    // Called when the current user opens/refreshes a DM conversation - marks
    // every unread message the *other* user sent them as read, and tells the
    // other side's own connections so their UI can show read receipts live.
    public async Task MarkDmRead(int otherUserId)
    {
        var unread = await _db.DirectMessages
            .Where(m => m.SenderId == otherUserId && m.RecipientId == CurrentUserId && m.ReadAt == null)
            .ToListAsync();
        if (unread.Count == 0) return;

        var readAt = DateTime.UtcNow;
        foreach (var m in unread) m.ReadAt = readAt;
        await _db.SaveChangesAsync();

        await Clients.User(otherUserId.ToString()).SendAsync("DirectMessagesRead", CurrentUserId, otherUserId, readAt);
    }

    // --- Private calls (1:1, friends-only, outside any server/channel -
    // see CallSignalingService for the in-memory ringing/active tracking
    // this all sits on top of). Audio itself flows through the same
    // self-hosted LiveKit deployment as server voice channels, just under
    // a room named after the generated call id instead of a channel id -
    // this hub only ever brokers who's calling whom, never touches media. ---

    // Returns null if the two users aren't friends, or either is already in
    // a call - the client should treat null as "can't start this call"
    // rather than assuming it's ringing.
    public async Task<string?> InitiateCall(int calleeId)
    {
        if (!_callRateLimiter.TryAcquire(CurrentUserId)) return null;

        var areFriends = await _db.Friendships.AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == CurrentUserId && f.AddresseeId == calleeId) ||
             (f.RequesterId == calleeId && f.AddresseeId == CurrentUserId)));
        if (!areFriends) return null;

        var session = _callSignaling.Create(CurrentUserId, calleeId);
        if (session is null) return null;

        if (!_avatarCache.TryGet(CurrentUserId, out var avatarUrl))
        {
            avatarUrl = (await _db.Users.FindAsync(CurrentUserId))?.AvatarUrl;
            _avatarCache.Set(CurrentUserId, avatarUrl);
        }

        await Clients.User(calleeId.ToString()).SendAsync("IncomingCall", session.CallId, CurrentUserId, CurrentUsername, avatarUrl);
        return session.CallId;
    }

    public async Task<bool> AcceptCall(string callId)
    {
        var session = _callSignaling.Get(callId);
        if (session is null || session.CalleeId != CurrentUserId) return false;

        _callSignaling.Accept(callId);
        await Clients.User(session.CallerId.ToString()).SendAsync("CallAccepted", callId);
        return true;
    }

    // Covers both "I'm declining an incoming call" and "I'm hanging up an
    // active one" - same single-endpoint-covers-multiple-intents shape
    // FriendsController's DELETE already uses for decline/cancel/unfriend.
    public Task DeclineCall(string callId) => EndCallInternalAsync(callId, "CallDeclined");
    public Task EndCall(string callId) => EndCallInternalAsync(callId, "CallEnded");

    private async Task EndCallInternalAsync(string callId, string eventName)
    {
        var session = _callSignaling.Get(callId);
        if (session is null) return;
        if (session.CallerId != CurrentUserId && session.CalleeId != CurrentUserId) return;

        _callSignaling.Remove(callId);
        await RecordCallEndedAsync(session, eventName, CurrentUserId);
        var otherUserId = session.CallerId == CurrentUserId ? session.CalleeId : session.CallerId;
        await Clients.User(otherUserId.ToString()).SendAsync(eventName, callId);
    }

    // Persists unencrypted call-history metadata (see CallRecord for why
    // this is separate from the E2EE call-event chat messages) - called
    // from both the normal decline/hangup path above and the
    // OnDisconnectedAsync cleanup path below, so every way a call can end
    // gets exactly one row.
    private async Task RecordCallEndedAsync(CallSession session, string eventName, int endedByUserId)
    {
        var outcome = eventName == "CallDeclined"
            ? CallOutcome.Declined
            : session.State == CallState.Active
                ? CallOutcome.Completed
                : endedByUserId == session.CallerId
                    ? CallOutcome.Cancelled
                    : CallOutcome.Missed;

        _db.CallRecords.Add(new CallRecord
        {
            CallerId = session.CallerId,
            CalleeId = session.CalleeId,
            StartedAt = session.StartedAt,
            ConnectedAt = session.ConnectedAt,
            EndedAt = DateTime.UtcNow,
            Outcome = outcome
        });
        await _db.SaveChangesAsync();
    }

    // Mints a LiveKit token for this specific call's room - only callable by
    // one of the call's two participants.
    public Task<LiveKitJoinResponse> GetCallToken(string callId)
    {
        var session = _callSignaling.Get(callId);
        if (session is null || (session.CallerId != CurrentUserId && session.CalleeId != CurrentUserId))
            throw new HubException("Not a participant of this call.");

        var token = _liveKitTokens.CreateJoinToken(CurrentUserId, CurrentUsername, callId);
        return Task.FromResult(new LiveKitJoinResponse(token, _liveKitTokens.ServerUrl ?? string.Empty));
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

        // Same cache SendMessage uses - avoids a DB round trip for a value
        // that rarely changes.
        if (!_avatarCache.TryGet(CurrentUserId, out var avatarUrl))
        {
            avatarUrl = (await _db.Users.FindAsync(CurrentUserId))?.AvatarUrl;
            _avatarCache.Set(CurrentUserId, avatarUrl);
        }

        var existingMembers = _voicePresence.Join(Context.ConnectionId, channelId, channel.GuildServerId, CurrentUserId, CurrentUsername, avatarUrl);
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Voice(channelId));
        await Clients.OthersInGroup(HubGroups.Voice(channelId)).SendAsync("VoiceUserJoined", CurrentUserId, CurrentUsername, channelId, avatarUrl);

        // Also notify anyone just viewing the server (not necessarily in this
        // voice channel) so their sidebar roster updates live too. Someone in
        // both groups gets this event twice - harmless, the client-side
        // handler is idempotent (checks the member isn't already listed).
        await Clients.OthersInGroup(HubGroups.ServerPresence(channel.GuildServerId)).SendAsync("VoiceUserJoined", CurrentUserId, CurrentUsername, channelId, avatarUrl);

        return existingMembers;
    }

    // Mints a LiveKit join token for the SFU deployment - a separate,
    // additive path alongside the existing mesh presence/signaling above
    // while the client-side rewrite to actually use it is still pending
    // (see LiveKitTokenService for why this doesn't require configuration
    // at server startup). Room name mirrors VoiceGroupName's convention.
    // Membership-gated, same reasoning as JoinChannel above - this
    // previously had no check at all, letting anyone authenticated mint a
    // token to join the live audio of any voice channel in any server.
    public async Task<LiveKitJoinResponse> GetLiveKitToken(int channelId)
    {
        var channel = await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null || !await _permissions.IsMemberAsync(CurrentUserId, channel.GuildServerId))
            throw new HubException("Not a member of this channel's server.");

        var token = _liveKitTokens.CreateJoinToken(CurrentUserId, CurrentUsername, channelId);
        return new LiveKitJoinResponse(token, _liveKitTokens.ServerUrl ?? string.Empty);
    }

    public async Task LeaveVoiceChannel(int channelId)
    {
        var left = _voicePresence.Leave(Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Voice(channelId));
        await Clients.Group(HubGroups.Voice(channelId)).SendAsync("VoiceUserLeft", CurrentUserId, CurrentUsername, channelId);

        if (left is not null)
            await Clients.Group(HubGroups.ServerPresence(left.Value.ServerId)).SendAsync("VoiceUserLeft", CurrentUserId, CurrentUsername, channelId);
    }

    // Client-side voice-activity detection pushes state changes here (not raw
    // audio levels) so this stays a cheap, infrequent broadcast rather than a
    // per-frame one.
    public async Task NotifySpeaking(int channelId, bool isSpeaking)
    {
        await Clients.OthersInGroup(HubGroups.Voice(channelId)).SendAsync("UserSpeaking", CurrentUserId, channelId, isSpeaking);

        var serverId = _voicePresence.GetServerId(Context.ConnectionId);
        if (serverId is not null)
            await Clients.OthersInGroup(HubGroups.ServerPresence(serverId.Value)).SendAsync("UserSpeaking", CurrentUserId, channelId, isSpeaking);
    }

    // Mirrors NotifySpeaking - same live-broadcast-only shape (not part of
    // the roster snapshot GetVoiceRostersForServer returns), so someone who
    // opens a voice channel after another participant already muted won't
    // see the icon until that participant's mute state next changes. Same
    // tradeoff the speaking indicator already accepts.
    public async Task NotifyMuted(int channelId, bool isMuted)
    {
        await Clients.OthersInGroup(HubGroups.Voice(channelId)).SendAsync("UserMuted", CurrentUserId, channelId, isMuted);

        var serverId = _voicePresence.GetServerId(Context.ConnectionId);
        if (serverId is not null)
            await Clients.OthersInGroup(HubGroups.ServerPresence(serverId.Value)).SendAsync("UserMuted", CurrentUserId, channelId, isMuted);
    }

    public async Task NotifyDeafened(int channelId, bool isDeafened)
    {
        await Clients.OthersInGroup(HubGroups.Voice(channelId)).SendAsync("UserDeafened", CurrentUserId, channelId, isDeafened);

        var serverId = _voicePresence.GetServerId(Context.ConnectionId);
        if (serverId is not null)
            await Clients.OthersInGroup(HubGroups.ServerPresence(serverId.Value)).SendAsync("UserDeafened", CurrentUserId, channelId, isDeafened);
    }

    // Pushes a forced-mute instruction to a specific other user's client(s)
    // via Clients.User(...) (reaches every connection that user has open,
    // regardless of SignalR group membership - same mechanism already used
    // for ServerKeyProvisioned/YouWereBanned) - VoicePresenceService has no
    // userId-to-connectionId index to add a specific member's connection to
    // a group directly. The client sets its own IsMicMuted and re-broadcasts
    // via its own NotifyMuted call, so other participants see the mute icon
    // update through the already-working broadcast path. One-time push, not
    // a persisted "can't self-unmute" state - see the plan's disclosed scope
    // cut for why.
    public async Task ForceMuteUser(int channelId, int targetUserId)
    {
        var channel = await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return;

        if (!await _permissions.HasPermissionAsync(CurrentUserId, channel.GuildServerId, ServerPermission.MuteMembers))
            return;

        if (!_voicePresence.GetRoster(channelId).Any(p => p.UserId == targetUserId))
            return;

        await Clients.User(targetUserId.ToString()).SendAsync("ForceMuted", channelId);
    }

    // --- Online/away/offline presence. Online is set on connect (see
    // OnConnectedAsync); Offline is always server-derived from the last
    // connection dropping (see OnDisconnectedAsync), never client-reported.
    // Away is the only state the client actively reports, when its own
    // idle detection crosses the threshold (see IdleDetector client-side). ---

    // Client calls this with "Online"/"Away" (idle detection) or "InCall"
    // (entering/leaving a voice channel or private call) - Offline is never
    // accepted from here, it can only happen via disconnection.
    public async Task SetPresenceState(string state)
    {
        if (state is not ("Online" or "Away" or "InCall")) return;

        // The client always confirms "Online" once right after connecting
        // (closes a race with this hub's own OnConnectedAsync - see
        // MainWindow.MainWindow_Loaded), which would otherwise re-broadcast
        // a no-op change on every single login.
        if (_presence.GetState(CurrentUserId) == state) return;

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
        if (!_presenceAudience.TryGet(userId, out var friendIds, out var serverIds))
        {
            friendIds = await _db.Friendships
                .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == userId || f.AddresseeId == userId))
                .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId)
                .ToListAsync();

            serverIds = await _db.Memberships
                .Where(m => m.UserId == userId)
                .Select(m => m.GuildServerId)
                .ToListAsync();

            _presenceAudience.Set(userId, friendIds, serverIds);
        }

        if (friendIds.Count > 0)
            await Clients.Users(friendIds.Select(id => id.ToString())).SendAsync("PresenceChanged", userId, state);

        foreach (var serverId in serverIds)
            await Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("PresenceChanged", userId, state);
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
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.ServerPresence(serverId));
    }

    public async Task LeaveServerPresence(int serverId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.ServerPresence(serverId));
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
        // A dropped connection (crash, network loss) counts as hanging up -
        // notify whoever was on the other end of any call this user was
        // in, ringing or active, the same way JoinVoiceChannel's cleanup
        // below doesn't wait for an explicit LeaveVoiceChannel either.
        var activeCall = _callSignaling.GetActiveCallForUser(CurrentUserId);
        if (activeCall is not null)
        {
            _callSignaling.Remove(activeCall.CallId);
            await RecordCallEndedAsync(activeCall, "CallEnded", CurrentUserId);
            var otherUserId = activeCall.CallerId == CurrentUserId ? activeCall.CalleeId : activeCall.CallerId;
            await Clients.User(otherUserId.ToString()).SendAsync("CallEnded", activeCall.CallId);
        }

        var left = _voicePresence.Leave(Context.ConnectionId);
        if (left is not null)
        {
            var (channelId, serverId, userId, username) = left.Value;
            await Clients.Group(HubGroups.Voice(channelId)).SendAsync("VoiceUserLeft", userId, username, channelId);
            await Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("VoiceUserLeft", userId, username, channelId);
        }

        var disconnected = _presence.Disconnect(Context.ConnectionId);
        if (disconnected is { WasLastConnection: true } d)
            await BroadcastPresenceChangeAsync(d.UserId, "Offline");

        await base.OnDisconnectedAsync(exception);
    }
}
