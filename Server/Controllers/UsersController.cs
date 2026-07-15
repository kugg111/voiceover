using System.Security.Claims;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Hubs;
using Voiceover.Server.Models;
using Voiceover.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly UserAvatarCache _avatarCache;
    private readonly IHubContext<ChatHub> _hub;

    public UsersController(AppDbContext db, UserAvatarCache avatarCache, IHubContext<ChatHub> hub)
    {
        _db = db;
        _avatarCache = avatarCache;
        _hub = hub;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/users/search?username=alice
    [HttpGet("search")]
    [EnableRateLimiting("search")]
    public async Task<ActionResult<List<UserSummaryResponse>>> Search(string username)
    {
        // A 1-char query still triggers a trigram-index scan across every
        // username with no floor - the rate limit above bounds how often,
        // this bounds how cheap each individual call has to stay.
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length < 2)
            return Ok(new List<UserSummaryResponse>());

        var users = await _db.Users
            .Where(u => u.Username.Contains(username))
            .Take(10)
            .Select(u => new UserSummaryResponse(u.Id, u.Username, u.AvatarUrl))
            .ToListAsync();

        return Ok(users);
    }

    // Url is expected to already be an uploaded file's path (from POST
    // /api/upload) - this endpoint just persists that URL against the
    // caller's own account, it doesn't handle the upload itself.
    [HttpPut("me/avatar")]
    public async Task<ActionResult> SetAvatar(SetAvatarRequest req)
    {
        var user = await _db.Users.FindAsync(CurrentUserId);
        if (user is null) return NotFound();

        user.AvatarUrl = req.Url;
        await _db.SaveChangesAsync();

        // The one and only place an avatar can change - keep
        // ChatHub.SendMessage's cache (which exists specifically to avoid
        // a DB round trip per message) from serving a stale URL.
        _avatarCache.Set(CurrentUserId, user.AvatarUrl);

        return Ok();
    }

    // Free-text custom status ("brb", "working", etc.) - persisted (see
    // User.CustomStatus), unlike PresenceState which is entirely in-memory.
    // Broadcast to the same audience BroadcastPresenceChangeAsync uses
    // (friends + fellow members of servers this user belongs to) so it
    // updates live without a refresh, same reasoning as presence itself.
    [HttpPut("me/status")]
    public async Task<ActionResult> SetCustomStatus(SetCustomStatusRequest req)
    {
        var user = await _db.Users.FindAsync(CurrentUserId);
        if (user is null) return NotFound();

        var status = req.Status?.Trim();
        if (status is { Length: > 128 }) status = status[..128];
        user.CustomStatus = string.IsNullOrEmpty(status) ? null : status;
        await _db.SaveChangesAsync();

        var friendIds = await _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == CurrentUserId || f.AddresseeId == CurrentUserId))
            .Select(f => f.RequesterId == CurrentUserId ? f.AddresseeId : f.RequesterId)
            .ToListAsync();
        if (friendIds.Count > 0)
            await _hub.Clients.Users(friendIds.Select(id => id.ToString())).SendAsync("CustomStatusChanged", CurrentUserId, user.CustomStatus);

        var serverIds = await _db.Memberships
            .Where(m => m.UserId == CurrentUserId)
            .Select(m => m.GuildServerId)
            .ToListAsync();
        foreach (var serverId in serverIds)
            await _hub.Clients.Group($"server-presence-{serverId}").SendAsync("CustomStatusChanged", CurrentUserId, user.CustomStatus);

        return Ok();
    }

    // Only the public key - safe for anyone to read, it's what a sender
    // needs to derive a shared DM key with this user (see E2eeService).
    // Null PublicKey means this account hasn't set up E2EE yet (hasn't
    // logged in since it shipped) - callers should treat that as "can't
    // send an encrypted DM to this person yet", not retry/error.
    [HttpGet("{id:int}/public-key")]
    public async Task<ActionResult<PublicKeyResponse>> GetPublicKey(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return NotFound();

        return Ok(new PublicKeyResponse(user.Id, user.PublicKey));
    }

    // Lets a newly-logged-in device fetch this account's wrapped private
    // key so it can unwrap it locally with the just-entered password (see
    // E2eeService.UnlockAsync) - the only way a second device gets access
    // to full DM history.
    [HttpGet("me/key-material")]
    public async Task<ActionResult<OwnKeyMaterialResponse>> GetMyKeyMaterial()
    {
        var user = await _db.Users.FindAsync(CurrentUserId);
        if (user is null) return NotFound();

        return Ok(new OwnKeyMaterialResponse(user.PublicKey, user.WrappedPrivateKey, user.PrivateKeySalt));
    }

    // Called once per account, either right after registration or on the
    // first login after E2EE shipped for a pre-existing account - uploads
    // freshly generated key material. Overwriting existing key material
    // isn't reachable from the client today (would orphan every other
    // device's ability to unwrap the old private key) - this endpoint just
    // persists whatever the client hands it, same trust model as any other
    // [Authorize]'d "update my own account" endpoint.
    [HttpPut("me/keys")]
    public async Task<ActionResult> SetMyKeys(SetKeyMaterialRequest req)
    {
        var user = await _db.Users.FindAsync(CurrentUserId);
        if (user is null) return NotFound();

        user.PublicKey = req.PublicKey;
        user.WrappedPrivateKey = req.WrappedPrivateKey;
        user.PrivateKeySalt = req.PrivateKeySalt;
        await _db.SaveChangesAsync();
        return Ok();
    }

    // Account profile + membership/friend metadata only - explicitly no
    // message content (server only ever holds E2EE ciphertext). A full
    // client-side history export (walking decrypted history to a file) is
    // out of scope for this batch - a disclosed scope cut, same kind
    // MessageSearchPage's own doc comment already makes for the same
    // E2EE reason.
    [HttpGet("me/export")]
    public async Task<ActionResult<UserDataExportResponse>> ExportMyData()
    {
        var user = await _db.Users.FindAsync(CurrentUserId);
        if (user is null) return NotFound();

        var servers = await _db.Memberships
            .Where(m => m.UserId == CurrentUserId)
            .Select(m => new ExportedMembership(m.GuildServer!.Name, m.Role.ToString()))
            .ToListAsync();

        // Friendship has no FK navigation on User (see AppDbContext) - look
        // friend ids up first, then usernames in a second query.
        var friendIds = await _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == CurrentUserId || f.AddresseeId == CurrentUserId))
            .Select(f => f.RequesterId == CurrentUserId ? f.AddresseeId : f.RequesterId)
            .ToListAsync();
        var friends = await _db.Users.Where(u => friendIds.Contains(u.Id)).Select(u => u.Username).ToListAsync();

        return Ok(new UserDataExportResponse(user.Username, user.CreatedAt, user.CustomStatus, servers, friends));
    }

    // Client calls this right after confirming account deletion, before
    // actually calling DELETE me, so it knows which owned servers need a
    // "pick the new owner" popup - servers with 0 other members are just
    // deleted, and with exactly 1 that member is auto-promoted, neither
    // needs the caller to choose anything.
    [HttpGet("me/owned-servers-needing-transfer")]
    public async Task<ActionResult<List<OwnedServerNeedingTransferResponse>>> GetOwnedServersNeedingTransfer()
    {
        var owned = await _db.GuildServers.Where(g => g.OwnerId == CurrentUserId).ToListAsync();
        var result = new List<OwnedServerNeedingTransferResponse>();

        foreach (var server in owned)
        {
            var others = await _db.Memberships.Include(m => m.User)
                .Where(m => m.GuildServerId == server.Id && m.UserId != CurrentUserId)
                .ToListAsync();

            if (others.Count > 1)
            {
                result.Add(new OwnedServerNeedingTransferResponse(server.Id, server.Name,
                    others.Select(m => new OwnershipCandidate(m.UserId, m.User!.Username, m.User.AvatarUrl)).ToList()));
            }
        }

        return Ok(result);
    }

    // Deletion must always succeed, even for a user who owns servers: User
    // has no explicit OnDelete configured anywhere (see AppDbContext), so
    // EF Core's default cascade behavior means deleting a User who owns a
    // GuildServer would otherwise cascade-delete that entire server -
    // channels, everyone's messages in it, everything - as an unintended
    // side effect. So each owned server is resolved first, below, before
    // the User row itself is ever removed. For a server with 2+ other
    // members, the caller must have already picked a successor via the
    // GetOwnedServersNeedingTransfer flow above - request.Transfers carries
    // that choice back.
    [HttpDelete("me")]
    public async Task<ActionResult> DeleteMyAccount([FromBody] DeleteAccountRequest? request)
    {
        var user = await _db.Users.FindAsync(CurrentUserId);
        if (user is null) return NotFound();

        var chosenTransfers = request?.Transfers?.ToDictionary(t => t.ServerId, t => t.NewOwnerUserId) ?? new Dictionary<int, int>();

        var ownedServers = await _db.GuildServers.Where(g => g.OwnerId == CurrentUserId).ToListAsync();
        foreach (var server in ownedServers)
        {
            var others = await _db.Memberships.Where(m => m.GuildServerId == server.Id && m.UserId != CurrentUserId).ToListAsync();

            if (others.Count == 0)
            {
                // No one left to inherit it - delete the server outright.
                // Channels/Messages/Invites cascade automatically (real,
                // required FKs - see AppDbContext), but MessageReaction and
                // ServerMemberKey have no FK/cascade configured, so clean
                // those up explicitly first (same reasoning
                // MessagesController.Delete already documents for
                // MessageReactions).
                var messageIds = await _db.Messages.Where(m => m.Channel!.GuildServerId == server.Id).Select(m => m.Id).ToListAsync();
                _db.MessageReactions.RemoveRange(_db.MessageReactions.Where(r => messageIds.Contains(r.MessageId)));
                _db.ServerMemberKeys.RemoveRange(_db.ServerMemberKeys.Where(k => k.GuildServerId == server.Id));
                _db.GuildServers.Remove(server);
                continue;
            }

            Membership successor;
            if (others.Count == 1)
            {
                successor = others[0];
            }
            else
            {
                if (!chosenTransfers.TryGetValue(server.Id, out var newOwnerId))
                    return BadRequest($"Select a new owner for '{server.Name}' before deleting your account.");

                var match = others.FirstOrDefault(m => m.UserId == newOwnerId);
                if (match is null)
                    return BadRequest($"Invalid owner selection for '{server.Name}'.");
                successor = match;
            }

            successor.Role = MemberRole.Owner;
            server.OwnerId = successor.UserId;
        }

        // Every one of these columns has no FK/cascade configured (see
        // AppDbContext's own comments on DirectMessage/Friendship/
        // MessageReaction) - clean them up explicitly, same pattern
        // MessagesController.Delete already uses for MessageReactions, so
        // deleting this account doesn't leave dangling rows pointing at a
        // user id that no longer exists.
        _db.Invites.RemoveRange(_db.Invites.Where(i => i.CreatedByUserId == CurrentUserId));
        _db.DirectMessages.RemoveRange(_db.DirectMessages.Where(dm => dm.SenderId == CurrentUserId || dm.RecipientId == CurrentUserId));
        _db.Friendships.RemoveRange(_db.Friendships.Where(f => f.RequesterId == CurrentUserId || f.AddresseeId == CurrentUserId));
        _db.CallRecords.RemoveRange(_db.CallRecords.Where(c => c.CallerId == CurrentUserId || c.CalleeId == CurrentUserId));
        _db.MessageReactions.RemoveRange(_db.MessageReactions.Where(r => r.UserId == CurrentUserId));
        _db.DirectMessageReactions.RemoveRange(_db.DirectMessageReactions.Where(r => r.UserId == CurrentUserId));
        _db.ServerMemberKeys.RemoveRange(_db.ServerMemberKeys.Where(k => k.UserId == CurrentUserId || k.WrappedByUserId == CurrentUserId));

        // Cascades Memberships, authored channel Messages, and RefreshTokens
        // automatically - all three are real, required FKs (confirmed via
        // AppDbContext's fluent config), unlike everything cleaned up above.
        _db.Users.Remove(user);
        await _db.SaveChangesAsync();

        _avatarCache.Remove(CurrentUserId);
        return Ok();
    }
}
