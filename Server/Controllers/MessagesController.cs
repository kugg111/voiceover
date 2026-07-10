using System.Security.Claims;
using DiscordClone.Server.Data;
using DiscordClone.Server.Dtos;
using DiscordClone.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DiscordClone.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/channels/{channelId}/[controller]")]
public class MessagesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PermissionService _permissions;

    public MessagesController(AppDbContext db, PermissionService permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/channels/5/messages?take=50  -> most recent 50 messages, oldest first
    [HttpGet]
    public async Task<ActionResult<List<MessageResponse>>> GetHistory(int channelId, int take = 50)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();

        if (!await _permissions.IsMemberAsync(CurrentUserId, channel.GuildServerId))
            return Forbid();

        var messages = await _db.Messages
            .Where(m => m.ChannelId == channelId)
            .OrderByDescending(m => m.SentAt)
            .Take(take)
            .OrderBy(m => m.SentAt)
            .Select(m => new MessageResponse(m.Id, m.Content, m.ChannelId, m.AuthorId, m.Author!.Username, m.SentAt, m.AttachmentUrl))
            .ToListAsync();

        return Ok(messages);
    }

    // Only the author, or an owner/moderator, may delete a message.
    [HttpDelete("{messageId}")]
    public async Task<ActionResult> Delete(int channelId, int messageId)
    {
        var message = await _db.Messages
            .Include(m => m.Channel)
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ChannelId == channelId);

        if (message is null) return NotFound();

        var isAuthor = message.AuthorId == CurrentUserId;
        var canManage = await _permissions.CanManageServerAsync(CurrentUserId, message.Channel!.GuildServerId);

        if (!isAuthor && !canManage) return Forbid();

        _db.Messages.Remove(message);
        await _db.SaveChangesAsync();
        return Ok();
    }
}
