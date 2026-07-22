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

// Channel categories/folders - purely organizational grouping, see
// Models/Category.cs and Channel.CategoryId. Structurally mirrors
// ChannelsController (same ManageChannels gate, same route shape, same
// "reuse ChannelCreated as a refetch signal" broadcast convention) since
// category changes always need the client to refetch its channel list too.
[ApiController]
[Authorize]
[Route("api/servers/{serverId}/[controller]")]
public class CategoriesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PermissionService _permissions;
    private readonly IHubContext<ChatHub> _hub;

    public CategoriesController(AppDbContext db, PermissionService permissions, IHubContext<ChatHub> hub)
    {
        _db = db;
        _permissions = permissions;
        _hub = hub;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<List<CategoryResponse>>> GetCategories(int serverId)
    {
        if (!await _permissions.IsMemberAsync(CurrentUserId, serverId))
            return Forbid();

        var categories = await _db.Categories
            .Where(c => c.GuildServerId == serverId)
            .OrderBy(c => c.Position)
            .Select(c => new CategoryResponse(c.Id, c.Name, c.GuildServerId, c.Position))
            .ToListAsync();

        return Ok(categories);
    }

    [HttpPost]
    public async Task<ActionResult<CategoryResponse>> Create(int serverId, CreateCategoryRequest req)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageChannels))
            return Forbid();

        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name cannot be empty.");

        var maxPosition = await _db.Categories.Where(c => c.GuildServerId == serverId)
            .Select(c => (int?)c.Position).MaxAsync() ?? -1;

        var category = new Category
        {
            Name = req.Name.Trim(),
            GuildServerId = serverId,
            Position = maxPosition + 1
        };

        _db.Categories.Add(category);
        await _db.SaveChangesAsync();

        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ChannelCreated", serverId);
        return Ok(new CategoryResponse(category.Id, category.Name, category.GuildServerId, category.Position));
    }

    [HttpPut("{categoryId}/rename")]
    public async Task<ActionResult> Rename(int serverId, int categoryId, RenameCategoryRequest req)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageChannels))
            return Forbid();

        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name cannot be empty.");

        var category = await _db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId && c.GuildServerId == serverId);
        if (category is null) return NotFound();

        category.Name = req.Name.Trim();
        await _db.SaveChangesAsync();

        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ChannelCreated", serverId);
        return Ok();
    }

    // Channels in this category are uncategorized (CategoryId set to null),
    // not deleted - see AppDbContext's SetNull configuration on that FK.
    [HttpDelete("{categoryId}")]
    public async Task<ActionResult> Delete(int serverId, int categoryId)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageChannels))
            return Forbid();

        var category = await _db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId && c.GuildServerId == serverId);
        if (category is null) return NotFound();

        _db.Categories.Remove(category);
        await _db.SaveChangesAsync();

        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ChannelCreated", serverId);
        return Ok();
    }

    [HttpPut("reorder")]
    public async Task<ActionResult> Reorder(int serverId, ReorderCategoriesRequest req)
    {
        if (!await _permissions.HasPermissionAsync(CurrentUserId, serverId, ServerPermission.ManageChannels))
            return Forbid();

        var categories = await _db.Categories
            .Where(c => c.GuildServerId == serverId && req.OrderedCategoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);
        if (categories.Count != req.OrderedCategoryIds.Distinct().Count())
            return BadRequest("One or more category ids don't belong to this server.");

        for (var i = 0; i < req.OrderedCategoryIds.Count; i++)
            categories[req.OrderedCategoryIds[i]].Position = i;

        await _db.SaveChangesAsync();
        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ChannelCreated", serverId);
        return Ok();
    }
}
