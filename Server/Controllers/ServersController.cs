using System.Security.Claims;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Models;
using Voiceover.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Controllers;

public record MemberResponse(int UserId, string Username, string Role, string? AvatarUrl = null);
public record ChangeRoleRequest(string Role); // "Member" or "Moderator"

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class ServersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PermissionService _permissions;

    public ServersController(AppDbContext db, PermissionService permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/servers  -> all servers the current user belongs to
    [HttpGet]
    public async Task<ActionResult<List<GuildServerResponse>>> GetMyServers()
    {
        var servers = await _db.Memberships
            .Where(m => m.UserId == CurrentUserId)
            .Select(m => new GuildServerResponse(
                m.GuildServer!.Id, m.GuildServer!.Name, m.GuildServer!.IconUrl, m.GuildServer!.OwnerId,
                m.Role == MemberRole.Owner || m.Role == MemberRole.Moderator))
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

        return Ok(new GuildServerResponse(server.Id, server.Name, server.IconUrl, server.OwnerId, CanManageInvites: true));
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

    [HttpGet("{serverId}/members")]
    public async Task<ActionResult<List<MemberResponse>>> GetMembers(int serverId)
    {
        if (!await _permissions.IsMemberAsync(CurrentUserId, serverId))
            return Forbid();

        var members = await _db.Memberships
            .Where(m => m.GuildServerId == serverId)
            .Select(m => new MemberResponse(m.UserId, m.User!.Username, m.Role.ToString(), m.User!.AvatarUrl))
            .ToListAsync();

        return Ok(members);
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
        return Ok(new GuildServerResponse(server.Id, server.Name, server.IconUrl, server.OwnerId, CanManageInvites: true));
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
}
