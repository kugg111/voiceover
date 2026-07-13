using System.Security.Claims;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Models;
using Voiceover.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/servers/{serverId}/[controller]")]
public class ChannelsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PermissionService _permissions;

    public ChannelsController(AppDbContext db, PermissionService permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // take/skip are optional and unbounded by default (existing callers get
    // today's "return everything" behavior).
    [HttpGet]
    public async Task<ActionResult<List<ChannelResponse>>> GetChannels(int serverId, int? take = null, int? skip = null)
    {
        if (!await _permissions.IsMemberAsync(CurrentUserId, serverId))
            return Forbid();

        var query = _db.Channels
            .Where(c => c.GuildServerId == serverId)
            .OrderBy(c => c.Position)
            .Skip(skip ?? 0);
        if (take.HasValue) query = query.Take(take.Value);

        var channels = await query
            .Select(c => new ChannelResponse(c.Id, c.Name, c.Type.ToString(), c.GuildServerId, c.Position))
            .ToListAsync();

        return Ok(channels);
    }

    [HttpPost]
    public async Task<ActionResult<ChannelResponse>> Create(int serverId, CreateChannelRequest req)
    {
        // Only owners/moderators can create channels.
        if (!await _permissions.CanManageServerAsync(CurrentUserId, serverId))
            return Forbid();

        var type = string.Equals(req.Type, "Voice", StringComparison.OrdinalIgnoreCase) ? ChannelType.Voice : ChannelType.Text;
        var maxPosition = await _db.Channels.Where(c => c.GuildServerId == serverId)
            .Select(c => (int?)c.Position).MaxAsync() ?? -1;

        var channel = new Channel
        {
            Name = req.Name,
            Type = type,
            GuildServerId = serverId,
            Position = maxPosition + 1
        };

        _db.Channels.Add(channel);
        await _db.SaveChangesAsync();

        return Ok(new ChannelResponse(channel.Id, channel.Name, channel.Type.ToString(), channel.GuildServerId, channel.Position));
    }

    [HttpDelete("{channelId}")]
    public async Task<ActionResult> Delete(int serverId, int channelId)
    {
        if (!await _permissions.CanManageServerAsync(CurrentUserId, serverId))
            return Forbid();

        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.GuildServerId == serverId);
        if (channel is null) return NotFound();

        _db.Channels.Remove(channel);
        await _db.SaveChangesAsync();
        return Ok();
    }
}
