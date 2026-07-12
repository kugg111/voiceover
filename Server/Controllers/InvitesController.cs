using System.Security.Claims;
using System.Security.Cryptography;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Models;
using Voiceover.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Controllers;

public record CreateInviteRequest(int? ExpiresInHours, int? MaxUses);
public record InviteResponse(string Code, DateTime? ExpiresAt, int? MaxUses, int UseCount);

[ApiController]
[Authorize]
[Route("api/servers/{serverId}/invites")]
public class InvitesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PermissionService _permissions;

    public InvitesController(AppDbContext db, PermissionService permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost]
    [EnableRateLimiting("invites")]
    public async Task<ActionResult<InviteResponse>> Create(int serverId, CreateInviteRequest req)
    {
        // Any member can invite people in and see the invite list - not
        // just owner/moderator (that restriction still applies to
        // kicking/roles, just not invites).
        if (!await _permissions.IsMemberAsync(CurrentUserId, serverId))
            return Forbid();

        var invite = new Invite
        {
            Code = GenerateCode(),
            GuildServerId = serverId,
            CreatedByUserId = CurrentUserId,
            ExpiresAt = req.ExpiresInHours.HasValue ? DateTime.UtcNow.AddHours(req.ExpiresInHours.Value) : null,
            MaxUses = req.MaxUses
        };

        _db.Invites.Add(invite);
        await _db.SaveChangesAsync();

        return Ok(new InviteResponse(invite.Code, invite.ExpiresAt, invite.MaxUses, invite.UseCount));
    }

    [HttpGet]
    public async Task<ActionResult<List<InviteResponse>>> List(int serverId)
    {
        if (!await _permissions.IsMemberAsync(CurrentUserId, serverId))
            return Forbid();

        var invites = await _db.Invites
            .Where(i => i.GuildServerId == serverId)
            .Select(i => new InviteResponse(i.Code, i.ExpiresAt, i.MaxUses, i.UseCount))
            .ToListAsync();

        return Ok(invites);
    }

    private static string GenerateCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789"; // no ambiguous chars
        var bytes = RandomNumberGenerator.GetBytes(8);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }
}

// Separate top-level controller since joining isn't scoped to a known serverId
// (the invite code is what identifies the server).
[ApiController]
[Authorize]
[Route("api/invites")]
public class InviteJoinController : ControllerBase
{
    private readonly AppDbContext _db;
    public InviteJoinController(AppDbContext db) => _db = db;

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("{code}/join")]
    public async Task<ActionResult<GuildServerResponse>> Join(string code)
    {
        var invite = await _db.Invites.Include(i => i.GuildServer)
            .FirstOrDefaultAsync(i => i.Code == code);

        if (invite is null || invite.GuildServer is null) return NotFound("Invite not found.");
        if (!invite.IsValid()) return BadRequest("This invite has expired or reached its use limit.");

        var alreadyMember = await _db.Memberships.AnyAsync(m => m.UserId == CurrentUserId && m.GuildServerId == invite.GuildServerId);
        if (!alreadyMember)
        {
            _db.Memberships.Add(new Membership { UserId = CurrentUserId, GuildServerId = invite.GuildServerId });
            invite.UseCount++;
            await _db.SaveChangesAsync();
        }

        var server = invite.GuildServer;
        return Ok(new GuildServerResponse(server.Id, server.Name, server.IconUrl, server.OwnerId));
    }
}
