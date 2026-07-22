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
[EnableRateLimiting("channel-management")]
[Route("api/servers/{serverId}/[controller]")]
public class ChannelsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PermissionService _permissions;
    private readonly IHubContext<ChatHub> _hub;

    public ChannelsController(AppDbContext db, PermissionService permissions, IHubContext<ChatHub> hub)
    {
        _db = db;
        _permissions = permissions;
        _hub = hub;
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
        if (PaginationLimits.Clamp(take) is { } clampedTake) query = query.Take(clampedTake);

        var channels = await query
            .Select(c => new ChannelResponse(c.Id, c.Name, c.Type.ToString(), c.GuildServerId, c.Position, c.SlowModeSeconds, c.CategoryId))
            .ToListAsync();

        return Ok(channels);
    }

    // ManageChannels-gated, same as the other channel-management endpoints.
    // categoryId null moves the channel back to "uncategorized". A non-null
    // value must belong to this same server - otherwise a moderator could
    // point a channel at another server's category id with no visible effect
    // beyond a confusing orphaned reference.
    [HttpPut("{channelId}/category")]
    public async Task<ActionResult> SetCategory(int serverId, int channelId, SetChannelCategoryRequest req)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageChannels))
            return Forbid();

        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.GuildServerId == serverId);
        if (channel is null) return NotFound();

        if (req.CategoryId is { } categoryId &&
            !await _db.Categories.AnyAsync(c => c.Id == categoryId && c.GuildServerId == serverId))
            return BadRequest("That category doesn't belong to this server.");

        channel.CategoryId = req.CategoryId;
        await _db.SaveChangesAsync();

        // Reuses ChannelCreated purely as a "refetch the channel list"
        // signal, same precedent as Rename above.
        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ChannelCreated", serverId);
        return Ok();
    }

    [HttpPost]
    public async Task<ActionResult<ChannelResponse>> Create(int serverId, CreateChannelRequest req)
    {
        // Only owners/moderators with ManageChannels can create channels.
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageChannels))
            return Forbid();

        // Previously unchecked entirely - unlike Rename below, Create never
        // validated req.Name at all (null/empty/whitespace or unbounded
        // length all passed through).
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name cannot be empty.");
        if (req.Name.Trim().Length > ContentLimits.MaxNameLength) return BadRequest("Name is too long.");

        var type = string.Equals(req.Type, "Voice", StringComparison.OrdinalIgnoreCase) ? ChannelType.Voice : ChannelType.Text;
        var maxPosition = await _db.Channels.Where(c => c.GuildServerId == serverId)
            .Select(c => (int?)c.Position).MaxAsync() ?? -1;

        var channel = new Channel
        {
            Name = req.Name.Trim(),
            Type = type,
            GuildServerId = serverId,
            Position = maxPosition + 1
        };

        _db.Channels.Add(channel);
        await _db.SaveChangesAsync();

        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ChannelCreated", serverId);
        return Ok(new ChannelResponse(channel.Id, channel.Name, channel.Type.ToString(), channel.GuildServerId, channel.Position, channel.SlowModeSeconds));
    }

    [HttpDelete("{channelId}")]
    public async Task<ActionResult> Delete(int serverId, int channelId)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageChannels))
            return Forbid();

        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.GuildServerId == serverId);
        if (channel is null) return NotFound();

        _db.Channels.Remove(channel);
        await _db.SaveChangesAsync();

        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ChannelDeleted", serverId);
        return Ok();
    }

    // 0 = off. ManageChannels-gated, same as Create/Delete above.
    [HttpPut("{channelId}/slowmode")]
    public async Task<ActionResult> SetSlowMode(int serverId, int channelId, SetSlowModeRequest req)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageChannels))
            return Forbid();

        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.GuildServerId == serverId);
        if (channel is null) return NotFound();

        channel.SlowModeSeconds = Math.Max(0, req.Seconds);
        await _db.SaveChangesAsync();
        return Ok();
    }

    // ManageChannels-gated, same as Create/Delete/SetSlowMode above.
    [HttpPut("{channelId}/rename")]
    public async Task<ActionResult> Rename(int serverId, int channelId, RenameChannelRequest req)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageChannels))
            return Forbid();

        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name cannot be empty.");
        if (req.Name.Trim().Length > ContentLimits.MaxNameLength) return BadRequest("Name is too long.");

        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.GuildServerId == serverId);
        if (channel is null) return NotFound();

        channel.Name = req.Name.Trim();
        await _db.SaveChangesAsync();

        // Reuses ChannelCreated purely as a "refetch the channel list"
        // signal - no dedicated event needed since the new name travels via
        // the refetch, not over the wire.
        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ChannelCreated", serverId);
        return Ok();
    }

    // ManageChannels-gated, same as the other channel-management endpoints.
    // Only the given ids get repositioned (0..count-1, in list order) - the
    // client sends one type's channels at a time (text and voice render as
    // two separate lists), which is fine since GetChannels only relies on
    // Position for ordering *within* a type after it's split client-side,
    // not for any cross-type meaning.
    [HttpPut("reorder")]
    public async Task<ActionResult> Reorder(int serverId, ReorderChannelsRequest req)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageChannels))
            return Forbid();

        var channels = await _db.Channels
            .Where(c => c.GuildServerId == serverId && req.OrderedChannelIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);
        if (channels.Count != req.OrderedChannelIds.Distinct().Count())
            return BadRequest("One or more channel ids don't belong to this server.");

        for (var i = 0; i < req.OrderedChannelIds.Count; i++)
            channels[req.OrderedChannelIds[i]].Position = i;

        await _db.SaveChangesAsync();
        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ChannelCreated", serverId);
        return Ok();
    }
}
