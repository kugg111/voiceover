using System.Security.Claims;
using System.Text.RegularExpressions;
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
public class EmojisController : ControllerBase
{
    // Same 100-per-server ceiling Discord itself starts free servers at -
    // arbitrary, just here so one server can't accumulate an unbounded
    // number of StoredFiles rows purely through this endpoint.
    private const int MaxEmojisPerServer = 100;
    private static readonly Regex NamePattern = new("^[a-zA-Z0-9_]{2,32}$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly PermissionService _permissions;
    private readonly IHubContext<ChatHub> _hub;

    public EmojisController(AppDbContext db, PermissionService permissions, IHubContext<ChatHub> hub)
    {
        _db = db;
        _permissions = permissions;
        _hub = hub;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<List<EmojiResponse>>> GetEmojis(int serverId)
    {
        if (!await _permissions.IsMemberAsync(CurrentUserId, serverId))
            return Forbid();

        var emojis = await _db.Emojis.Where(em => em.GuildServerId == serverId)
            .OrderBy(em => em.Name)
            .Select(em => new EmojiResponse(em.Id, em.GuildServerId, em.Name, em.ImageUrl, em.CreatedAt))
            .ToListAsync();

        return Ok(emojis);
    }

    // ManageChannels-gated - the closest existing permission bucket to
    // "customize server appearance" (see ChannelsController for the same
    // gate on channel create/delete/rename).
    [HttpPost]
    public async Task<ActionResult<EmojiResponse>> Create(int serverId, CreateEmojiRequest req)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageChannels))
            return Forbid();

        var name = req.Name.Trim();
        if (!NamePattern.IsMatch(name))
            return BadRequest("Name must be 2-32 letters, numbers, or underscores.");
        if (string.IsNullOrWhiteSpace(req.Url))
            return BadRequest("Missing image url.");

        if (await _db.Emojis.CountAsync(em => em.GuildServerId == serverId) >= MaxEmojisPerServer)
            return BadRequest($"This server already has the maximum of {MaxEmojisPerServer} custom emoji.");

        var emoji = new Emoji { GuildServerId = serverId, Name = name, ImageUrl = req.Url };
        _db.Emojis.Add(emoji);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Unique (GuildServerId, Name) index - same race-tolerance
            // pattern as ServersController.SetServerKey.
            return BadRequest("An emoji with that name already exists on this server.");
        }

        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ServerEmojisChanged", serverId);
        return Ok(new EmojiResponse(emoji.Id, emoji.GuildServerId, emoji.Name, emoji.ImageUrl, emoji.CreatedAt));
    }

    [HttpDelete("{emojiId}")]
    public async Task<ActionResult> Delete(int serverId, int emojiId)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageChannels))
            return Forbid();

        var emoji = await _db.Emojis.FirstOrDefaultAsync(em => em.Id == emojiId && em.GuildServerId == serverId);
        if (emoji is null) return NotFound();

        _db.Emojis.Remove(emoji);
        await _db.SaveChangesAsync();

        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ServerEmojisChanged", serverId);
        return Ok();
    }
}
