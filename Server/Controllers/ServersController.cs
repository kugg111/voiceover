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

public record MemberResponse(int UserId, string Username, string Role, string? AvatarUrl = null, string PresenceState = "Offline", string? CustomStatus = null, int Permissions = 0);
public record ChangeRoleRequest(string Role); // "Member" or "Moderator"
public record SetPermissionsRequest(int Permissions); // ServerPermission bitmask
public record BanRequest(string? Reason);
public record BannedUserResponse(int UserId, string Username, string? Reason, DateTime CreatedAt, int BannedByUserId, string BannedByUsername);
public record ModerationLogEntryResponse(int Id, string ActorUsername, string Action, string? TargetUsername, string? Details, DateTime CreatedAt);
public record DiscoverServerResponse(int Id, string Name, string? IconUrl, string? Description, int MemberCount);
public record SetDiscoverableRequest(bool IsPublic, string? Description);

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class ServersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PermissionService _permissions;
    private readonly PresenceService _presence;
    private readonly ModerationLogService _modLog;
    private readonly ServerDeletionService _serverDeletion;
    private readonly IHubContext<ChatHub> _hub;

    public ServersController(AppDbContext db, PermissionService permissions, PresenceService presence, ModerationLogService modLog, ServerDeletionService serverDeletion, IHubContext<ChatHub> hub)
    {
        _db = db;
        _permissions = permissions;
        _presence = presence;
        _modLog = modLog;
        _serverDeletion = serverDeletion;
        _hub = hub;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string CurrentUsername => User.FindFirstValue(ClaimTypes.Name)!;

    // GET /api/servers  -> all servers the current user belongs to. take/skip
    // optional and unbounded by default, same convention as GetMembers below.
    [HttpGet]
    public async Task<ActionResult<List<GuildServerResponse>>> GetMyServers(int? take = null, int? skip = null)
    {
        var query = _db.Memberships
            .Where(m => m.UserId == CurrentUserId)
            .OrderBy(m => m.Id)
            .Skip(skip ?? 0);
        if (PaginationLimits.Clamp(take) is { } clampedTake) query = query.Take(clampedTake);

        var servers = await query
            .Select(m => m.GuildServer!)
            .Select(s => new GuildServerResponse(s.Id, s.Name, s.IconUrl, s.OwnerId, s.IsPublic, s.Description))
            .ToListAsync();

        return Ok(servers);
    }

    // GET /api/servers/discover -> servers anyone can browse and join
    // without an invite (IsPublic == true, owner opt-in via
    // SetDiscoverable below). No membership check, unlike every other
    // per-server endpoint here - [Authorize] on the class still requires a
    // logged-in app user, just not a member of the specific server.
    [HttpGet("discover")]
    public async Task<ActionResult<List<DiscoverServerResponse>>> Discover(string? q, int? take = null, int? skip = null)
    {
        var query = _db.GuildServers.Where(s => s.IsPublic);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(s => s.Name.Contains(q));

        query = query.OrderBy(s => s.Id).Skip(skip ?? 0);
        if (PaginationLimits.Clamp(take) is { } clampedTake) query = query.Take(clampedTake);

        var servers = await query
            .Select(s => new DiscoverServerResponse(s.Id, s.Name, s.IconUrl, s.Description, s.Memberships.Count))
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

        return Ok(new GuildServerResponse(server.Id, server.Name, server.IconUrl, server.OwnerId, server.IsPublic, server.Description));
    }

    // Direct join by server id - only for servers listed in Discover above
    // (IsPublic == true). A private server can only be joined via its own
    // invite code (POST /api/invites/{code}/join) - without this check,
    // any authenticated user could join any server just by guessing/
    // incrementing ids, invite or not.
    [HttpPost("{serverId}/join")]
    public async Task<ActionResult> Join(int serverId)
    {
        var server = await _db.GuildServers.FirstOrDefaultAsync(s => s.Id == serverId);
        if (server is null) return NotFound();
        if (!server.IsPublic) return Forbid();

        if (await _db.BannedUsers.AnyAsync(b => b.GuildServerId == serverId && b.UserId == CurrentUserId))
            return Forbid();

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
        if (PaginationLimits.Clamp(take) is { } clampedTake) query = query.Take(clampedTake);

        var members = await query
            .Select(m => new { m.UserId, m.User!.Username, Role = m.Role.ToString(), m.User!.AvatarUrl, m.User!.CustomStatus, m.Permissions })
            .ToListAsync();

        var result = members
            .Select(m => new MemberResponse(m.UserId, m.Username, m.Role, m.AvatarUrl, _presence.GetState(m.UserId), m.CustomStatus, (int)m.Permissions))
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
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.KickMembers))
            return Forbid();

        var target = await _db.Memberships.Include(m => m.User).FirstOrDefaultAsync(m => m.UserId == userId && m.GuildServerId == serverId);
        if (target is null) return NotFound();
        if (target.Role == MemberRole.Owner) return BadRequest("Cannot kick the server owner.");

        _db.Memberships.Remove(target);
        await _db.SaveChangesAsync();
        await _modLog.LogAsync(serverId, CurrentUserId, CurrentUsername, "Kick", userId, target.User?.Username);

        // Without this, a kicked user's already-open client keeps showing
        // the server until their next full reload (next login/restart) -
        // they'd click into it and just get a 403 on whatever they try.
        // Same Clients.User(...) mechanism already used for YouWereBanned.
        await _hub.Clients.User(userId.ToString()).SendAsync("YouWereKicked", serverId);

        // Separate from the targeted push above - this reaches everyone else
        // who has this server's member list open, so their view doesn't go
        // stale until they manually switch away and back.
        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("MemberKicked", serverId, userId);
        return Ok();
    }

    // Kicks (if currently a member) and records a ban - blocks rejoining via
    // any invite link until unbanned. Reuses KickMember's own permission
    // gate (KickMembers), matching how banning is a strictly stronger form
    // of the same "remove this person" power in this app's flat permission
    // model.
    [HttpPost("{serverId}/bans/{userId}")]
    public async Task<ActionResult> Ban(int serverId, int userId, BanRequest req)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.KickMembers))
            return Forbid();

        var target = await _db.Memberships.Include(m => m.User).FirstOrDefaultAsync(m => m.UserId == userId && m.GuildServerId == serverId);
        if (target?.Role == MemberRole.Owner) return BadRequest("Cannot ban the server owner.");

        if (await _db.BannedUsers.AnyAsync(b => b.GuildServerId == serverId && b.UserId == userId))
            return Conflict("Already banned.");

        var targetUser = target?.User ?? await _db.Users.FindAsync(userId);
        if (targetUser is null) return NotFound();

        if (target is not null) _db.Memberships.Remove(target);

        _db.BannedUsers.Add(new BannedUser
        {
            GuildServerId = serverId,
            UserId = userId,
            BannedByUserId = CurrentUserId,
            Reason = req.Reason
        });

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Backstop for the rare case two ban requests for the same user
            // race past the AnyAsync check above simultaneously - the
            // unique index on (GuildServerId, UserId) catches it at the DB
            // level, which would otherwise surface as an unhandled 500.
            return Conflict("Already banned.");
        }
        await _modLog.LogAsync(serverId, CurrentUserId, CurrentUsername, "Ban", userId, targetUser.Username, req.Reason);

        await _hub.Clients.User(userId.ToString()).SendAsync("YouWereBanned", serverId);

        // Separate from the targeted push above - lets a mod with the ban
        // list or member panel open see this without switching away and back.
        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("MemberBanned", serverId, userId);
        return Ok();
    }

    [HttpDelete("{serverId}/bans/{userId}")]
    public async Task<ActionResult> Unban(int serverId, int userId)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.KickMembers))
            return Forbid();

        var ban = await _db.BannedUsers.FirstOrDefaultAsync(b => b.GuildServerId == serverId && b.UserId == userId);
        if (ban is null) return NotFound();

        var targetUsername = (await _db.Users.FindAsync(userId))?.Username;

        _db.BannedUsers.Remove(ban);
        await _db.SaveChangesAsync();
        await _modLog.LogAsync(serverId, CurrentUserId, CurrentUsername, "Unban", userId, targetUsername);

        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("MemberUnbanned", serverId, userId);
        return Ok();
    }

    // Same optional take/skip convention as GetMembers/GetModerationLog -
    // omitting take still returns everything (ban lists are expected to
    // stay small), a caller-supplied take just gets clamped to MaxPageSize.
    [HttpGet("{serverId}/bans")]
    public async Task<ActionResult<List<BannedUserResponse>>> GetBans(int serverId, int? take = null, int? skip = null)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ViewAuditLog))
            return Forbid();

        var query = _db.BannedUsers
            .Where(b => b.GuildServerId == serverId)
            .OrderByDescending(b => b.CreatedAt)
            .Skip(skip ?? 0);
        if (PaginationLimits.Clamp(take) is { } clampedTake) query = query.Take(clampedTake);

        var bans = await query.ToListAsync();

        // Usernames looked up separately (BannedUser has no FK navigation -
        // see that class's own comment for why) rather than a join, since
        // ban lists are expected to stay small.
        var userIds = bans.Select(b => b.UserId).Concat(bans.Select(b => b.BannedByUserId)).Distinct().ToList();
        var usernames = await _db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username);

        var result = bans
            .Select(b => new BannedUserResponse(
                b.UserId,
                usernames.GetValueOrDefault(b.UserId, "Unknown"),
                b.Reason,
                b.CreatedAt,
                b.BannedByUserId,
                usernames.GetValueOrDefault(b.BannedByUserId, "Unknown")))
            .ToList();

        return Ok(result);
    }

    // Newest-first, same optional take/skip convention as GetMembers.
    [HttpGet("{serverId}/moderation-log")]
    public async Task<ActionResult<List<ModerationLogEntryResponse>>> GetModerationLog(int serverId, int? take = null, int? skip = null)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ViewAuditLog))
            return Forbid();

        var query = _db.ModerationLogEntries
            .Where(m => m.GuildServerId == serverId)
            .OrderByDescending(m => m.CreatedAt)
            .Skip(skip ?? 0);
        if (PaginationLimits.Clamp(take) is { } clampedTake) query = query.Take(clampedTake);

        var result = await query
            .Select(m => new ModerationLogEntryResponse(m.Id, m.ActorUsername, m.Action, m.TargetUsername, m.Details, m.CreatedAt))
            .ToListAsync();

        return Ok(result);
    }

    // Owner-only, permanent - deletes the server and everything in it.
    // Channels/Messages/Memberships/Invites cascade automatically (real,
    // required FKs); ServerDeletionService cleans up the rest (see its own
    // comment for exactly what has no FK/cascade configured and why).
    [HttpDelete("{serverId}")]
    public async Task<ActionResult> Delete(int serverId)
    {
        if (!await _permissions.IsOwnerAsync(CurrentUserId, serverId))
            return Forbid();

        var server = await _db.GuildServers.FirstOrDefaultAsync(s => s.Id == serverId);
        if (server is null) return NotFound();

        await _serverDeletion.QueueDeleteAsync(server);
        await _db.SaveChangesAsync();

        // Every member either has this server's presence group joined
        // (currently viewing it) or will simply stop seeing it on their next
        // GetMyServers - no per-user targeted push needed like YouWereKicked,
        // since deleting your own server isn't something the caller (the
        // one who just did it) needs to be separately notified of.
        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ServerDeleted", serverId);
        return Ok();
    }

    // Url is expected to already be an uploaded file's path (from POST
    // /api/upload). ManageServer-gated (Owner implicitly passes, see
    // HasPermissionAsync) - matches real Discord's "Manage Server"
    // permission, delegable to a Moderator instead of owner-only.
    [HttpPut("{serverId}/icon")]
    public async Task<ActionResult<GuildServerResponse>> SetIcon(int serverId, SetIconRequest req)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageServer))
            return Forbid();

        var server = await _db.GuildServers.FirstOrDefaultAsync(s => s.Id == serverId);
        if (server is null) return NotFound();

        server.IconUrl = req.Url;
        await _db.SaveChangesAsync();
        return Ok(new GuildServerResponse(server.Id, server.Name, server.IconUrl, server.OwnerId, server.IsPublic, server.Description));
    }

    // Same gate as SetIcon above.
    [HttpPut("{serverId}/rename")]
    public async Task<ActionResult<GuildServerResponse>> Rename(int serverId, RenameServerRequest req)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageServer))
            return Forbid();

        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name cannot be empty.");

        var server = await _db.GuildServers.FirstOrDefaultAsync(s => s.Id == serverId);
        if (server is null) return NotFound();

        server.Name = req.Name.Trim();
        await _db.SaveChangesAsync();

        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ServerRenamed", serverId);
        return Ok(new GuildServerResponse(server.Id, server.Name, server.IconUrl, server.OwnerId, server.IsPublic, server.Description));
    }

    // Same gate as SetIcon/Rename above - controls whether this server shows
    // up in Discover and can be joined without an invite.
    [HttpPut("{serverId}/discoverable")]
    public async Task<ActionResult<GuildServerResponse>> SetDiscoverable(int serverId, SetDiscoverableRequest req)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageServer))
            return Forbid();

        if (req.Description?.Length > 300) return BadRequest("Description must be 300 characters or fewer.");

        var server = await _db.GuildServers.FirstOrDefaultAsync(s => s.Id == serverId);
        if (server is null) return NotFound();

        server.IsPublic = req.IsPublic;
        server.Description = req.Description;
        await _db.SaveChangesAsync();
        return Ok(new GuildServerResponse(server.Id, server.Name, server.IconUrl, server.OwnerId, server.IsPublic, server.Description));
    }

    [HttpPut("{serverId}/members/{userId}/role")]
    public async Task<ActionResult> ChangeRole(int serverId, int userId, ChangeRoleRequest req)
    {
        // ManageRoles-gated (Owner implicitly passes) - see
        // ServerPermission.ManageRoles for why this is split from
        // SetPermissions below rather than sharing one gate.
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageRoles))
            return Forbid();

        var target = await _db.Memberships.Include(m => m.User).FirstOrDefaultAsync(m => m.UserId == userId && m.GuildServerId == serverId);
        if (target is null) return NotFound();
        if (target.Role == MemberRole.Owner) return BadRequest("Cannot change the owner's role.");

        target.Role = req.Role.Equals("Moderator", StringComparison.OrdinalIgnoreCase)
            ? MemberRole.Moderator
            : MemberRole.Member;
        // Freshly-promoted Moderators start with every permission - the
        // Owner can then dial specific ones back via SetPermissions below.
        if (target.Role == MemberRole.Moderator) target.Permissions = ServerPermission.All;

        await _db.SaveChangesAsync();
        await _modLog.LogAsync(serverId, CurrentUserId, CurrentUsername, "RoleChange", userId, target.User?.Username, target.Role.ToString());

        // No other live signal exists for a role change today - this both
        // refreshes bystanders' member lists and lets the demoted/promoted
        // member's own client re-fetch its capability buttons.
        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("MemberRoleChanged", serverId, userId);
        return Ok();
    }

    // Deliberately stays owner-only (unlike ChangeRole above, which accepts
    // ManageRoles) - hand-tuning another Moderator's exact permission bits,
    // including granting them ManageRoles/All, is a materially bigger
    // escalation path than just promoting someone with the default full
    // grant, so this one isn't delegable. See ServerPermission.ManageRoles.
    [HttpPut("{serverId}/members/{userId}/permissions")]
    public async Task<ActionResult> SetPermissions(int serverId, int userId, SetPermissionsRequest req)
    {
        if (!await _permissions.IsOwnerAsync(CurrentUserId, serverId))
            return Forbid();

        var target = await _db.Memberships.Include(m => m.User).FirstOrDefaultAsync(m => m.UserId == userId && m.GuildServerId == serverId);
        if (target is null) return NotFound();
        if (target.Role != MemberRole.Moderator) return BadRequest("Permissions only apply to Moderators.");

        target.Permissions = (ServerPermission)req.Permissions & ServerPermission.All;
        await _db.SaveChangesAsync();
        await _modLog.LogAsync(serverId, CurrentUserId, CurrentUsername, "PermissionsChanged", userId, target.User?.Username, target.Permissions.ToString());

        // Reuses the same event as ChangeRole - both change what the
        // affected member's client should refetch (its own capability
        // buttons) and what bystanders' member panels show.
        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("MemberRoleChanged", serverId, userId);
        return Ok();
    }

}
