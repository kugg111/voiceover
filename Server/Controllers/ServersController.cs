using System.Security.Claims;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Hubs;
using Voiceover.Server.Models;
using Voiceover.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Controllers;

public record MemberResponse(int UserId, string Username, string Role, string? AvatarUrl = null, string PresenceState = "Offline");
public record ChangeRoleRequest(string Role); // "Member" or "Moderator"

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class ServersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PermissionService _permissions;
    private readonly PresenceService _presence;
    private readonly IHubContext<ChatHub> _hub;

    public ServersController(AppDbContext db, PermissionService permissions, PresenceService presence, IHubContext<ChatHub> hub)
    {
        _db = db;
        _permissions = permissions;
        _presence = presence;
        _hub = hub;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/servers  -> all servers the current user belongs to
    [HttpGet]
    public async Task<ActionResult<List<GuildServerResponse>>> GetMyServers()
    {
        var servers = await _db.Memberships
            .Where(m => m.UserId == CurrentUserId)
            .Select(m => m.GuildServer!)
            .Select(s => new GuildServerResponse(s.Id, s.Name, s.IconUrl, s.OwnerId))
            .ToListAsync();

        return Ok(servers);
    }

    [HttpPost]
    public async Task<ActionResult<GuildServerResponse>> Create(CreateServerRequest req)
    {
        var server = new GuildServer { Name = req.Name, OwnerId = CurrentUserId };
        _db.GuildServers.Add(server);
        await _db.SaveChangesAsync();

        _db.Memberships.Add(new Membership
        {
            UserId = CurrentUserId,
            GuildServerId = server.Id,
            Role = MemberRole.Owner
        });

        _db.Channels.Add(new Channel { Name = "general", Type = ChannelType.Text, GuildServerId = server.Id, Position = 0 });
        _db.Channels.Add(new Channel { Name = "General Voice", Type = ChannelType.Voice, GuildServerId = server.Id, Position = 1 });

        await _db.SaveChangesAsync();

        return Ok(new GuildServerResponse(server.Id, server.Name, server.IconUrl, server.OwnerId));
    }

    // Direct join by server id is kept for convenience/dev use; in practice
    // most people will join via POST /api/invites/{code}/join instead.
    [HttpPost("{serverId}/join")]
    public async Task<ActionResult> Join(int serverId)
    {
        var exists = await _db.GuildServers.AnyAsync(s => s.Id == serverId);
        if (!exists) return NotFound();

        var already = await _db.Memberships.AnyAsync(m => m.UserId == CurrentUserId && m.GuildServerId == serverId);
        if (already) return Ok();

        _db.Memberships.Add(new Membership { UserId = CurrentUserId, GuildServerId = serverId });
        await _db.SaveChangesAsync();
        return Ok();
    }

    // take/skip are optional and unbounded by default (existing callers get
    // today's "return everything" behavior) - added so member lists don't
    // need a breaking change later if a server's membership grows large.
    [HttpGet("{serverId}/members")]
    public async Task<ActionResult<List<MemberResponse>>> GetMembers(int serverId, int? take = null, int? skip = null)
    {
        if (!await _permissions.IsMemberAsync(CurrentUserId, serverId))
            return Forbid();

        // PresenceService is in-memory (not queryable in SQL), so the
        // presence lookup happens after materializing the DB rows rather
        // than inside the EF projection above. Ordered by Id for stable
        // pagination (Skip/Take need a deterministic order).
        var query = _db.Memberships
            .Where(m => m.GuildServerId == serverId)
            .OrderBy(m => m.Id)
            .Skip(skip ?? 0);
        if (take.HasValue) query = query.Take(take.Value);

        var members = await query
            .Select(m => new { m.UserId, m.User!.Username, Role = m.Role.ToString(), m.User!.AvatarUrl })
            .ToListAsync();

        var result = members
            .Select(m => new MemberResponse(m.UserId, m.Username, m.Role, m.AvatarUrl, _presence.GetState(m.UserId)))
            .ToList();

        return Ok(result);
    }

    // Self-removal, distinct from KickMember below (which targets someone
    // else and requires manage permission). Owners can't leave since there's
    // no ownership-transfer mechanism - they'd have to delete the server.
    [HttpDelete("{serverId}/leave")]
    public async Task<ActionResult> Leave(int serverId)
    {
        var membership = await _db.Memberships.FirstOrDefaultAsync(m => m.UserId == CurrentUserId && m.GuildServerId == serverId);
        if (membership is null) return NotFound();
        if (membership.Role == MemberRole.Owner) return BadRequest("Owners can't leave their own server.");

        _db.Memberships.Remove(membership);
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpDelete("{serverId}/members/{userId}")]
    public async Task<ActionResult> KickMember(int serverId, int userId)
    {
        if (!await _permissions.CanManageServerAsync(CurrentUserId, serverId))
            return Forbid();

        var target = await _db.Memberships.FirstOrDefaultAsync(m => m.UserId == userId && m.GuildServerId == serverId);
        if (target is null) return NotFound();
        if (target.Role == MemberRole.Owner) return BadRequest("Cannot kick the server owner.");

        _db.Memberships.Remove(target);
        await _db.SaveChangesAsync();
        return Ok();
    }

    // Url is expected to already be an uploaded file's path (from POST
    // /api/upload). Owner-only, same as the rest of a server's identity
    // (renaming isn't even exposed yet) - matches real Discord where
    // moderators can manage members/channels but not the server's branding.
    [HttpPut("{serverId}/icon")]
    public async Task<ActionResult<GuildServerResponse>> SetIcon(int serverId, SetIconRequest req)
    {
        if (!await _permissions.IsOwnerAsync(CurrentUserId, serverId))
            return Forbid();

        var server = await _db.GuildServers.FirstOrDefaultAsync(s => s.Id == serverId);
        if (server is null) return NotFound();

        server.IconUrl = req.Url;
        await _db.SaveChangesAsync();
        return Ok(new GuildServerResponse(server.Id, server.Name, server.IconUrl, server.OwnerId));
    }

    [HttpPut("{serverId}/members/{userId}/role")]
    public async Task<ActionResult> ChangeRole(int serverId, int userId, ChangeRoleRequest req)
    {
        // Only the owner can promote/demote moderators.
        if (!await _permissions.IsOwnerAsync(CurrentUserId, serverId))
            return Forbid();

        var target = await _db.Memberships.FirstOrDefaultAsync(m => m.UserId == userId && m.GuildServerId == serverId);
        if (target is null) return NotFound();
        if (target.Role == MemberRole.Owner) return BadRequest("Cannot change the owner's role.");

        target.Role = req.Role.Equals("Moderator", StringComparison.OrdinalIgnoreCase)
            ? MemberRole.Moderator
            : MemberRole.Member;

        await _db.SaveChangesAsync();
        return Ok();
    }

    // --- E2EE server key (shared, wrapped per member - see ServerMemberKey) ---

    // GET /api/servers/{serverId}/keys/me -> this member's own wrapped copy,
    // or a null WrappedKey if nobody's granted them one yet (brand new
    // member - the client falls back to ChatHub.RequestServerKey to ask an
    // online peer to onboard them).
    [HttpGet("{serverId}/keys/me")]
    public async Task<ActionResult<ServerKeyResponse>> GetMyServerKey(int serverId)
    {
        if (!await _permissions.IsMemberAsync(CurrentUserId, serverId)) return Forbid();

        var row = await _db.ServerMemberKeys.FirstOrDefaultAsync(k => k.GuildServerId == serverId && k.UserId == CurrentUserId);
        return Ok(new ServerKeyResponse(row?.WrappedKey, row?.WrappedByUserId));
    }

    // PUT /api/servers/{serverId}/keys/{targetUserId} - either a member
    // wrapping the key for themselves (targetUserId == caller) or an
    // existing member who already has the key wrapping a copy for a
    // fellow member who doesn't yet (onboarding a new joiner, or the
    // requester of ChatHub.RequestServerKey). WrappedByUserId is always the
    // caller, never client-supplied, so the unwrapping side always redoes
    // ECDH against a public key it can actually trust came from the caller.
    [HttpPut("{serverId}/keys/{targetUserId}")]
    public async Task<ActionResult> SetServerKey(int serverId, int targetUserId, SetServerKeyRequest req)
    {
        if (!await _permissions.IsMemberAsync(CurrentUserId, serverId)) return Forbid();
        if (!await _permissions.IsMemberAsync(targetUserId, serverId)) return NotFound();

        // Immutable once set - same reasoning as user identity key material
        // (UsersController.SetMyKeys): a bad/foreign overwrite would just
        // strand that one member, so simplest to disallow overwriting
        // entirely rather than trying to authenticate "is this a legitimate
        // correction."
        if (await _db.ServerMemberKeys.AnyAsync(k => k.GuildServerId == serverId && k.UserId == targetUserId))
            return Conflict("A key is already set for this member.");

        var anyKeyExistsForServer = await _db.ServerMemberKeys.AnyAsync(k => k.GuildServerId == serverId);

        if (targetUserId == CurrentUserId)
        {
            // Self-bootstrap - only ever the very first key for a server
            // (no member, including this one, has one yet). Restricted to
            // the owner so two members can't each notice "no key exists"
            // at the same moment and generate two different keys,
            // permanently splitting the server's history in two. If keys
            // already exist for OTHER members but not this caller (e.g. a
            // pre-E2EE server where onboarding hasn't reached them yet),
            // this correctly refuses rather than letting them self-generate
            // an incompatible key - they need a copy of the real one from
            // an existing member (ChatHub.RequestServerKey), not a new one.
            if (anyKeyExistsForServer)
                return Conflict("A key already exists for this server - request a copy from an existing member instead of self-generating one.");
            if (!await _permissions.IsOwnerAsync(CurrentUserId, serverId))
                return Forbid();
        }
        // Wrapping a copy for a fellow member (onboarding) is open to any
        // existing member, same trust level already granted for creating
        // invites - the "target has no row yet" check above already
        // prevents clobbering a working key.

        _db.ServerMemberKeys.Add(new ServerMemberKey
        {
            GuildServerId = serverId,
            UserId = targetUserId,
            WrappedByUserId = CurrentUserId,
            WrappedKey = req.WrappedKey
        });
        await _db.SaveChangesAsync();

        // Lets the target's client (if it's the one that just asked via
        // RequestServerKey and is sitting on a locked/placeholder view)
        // pick this up immediately instead of waiting for its next poll.
        if (targetUserId != CurrentUserId)
            await _hub.Clients.User(targetUserId.ToString()).SendAsync("ServerKeyProvisioned", serverId);

        return Ok();
    }
}
