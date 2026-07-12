using System.Security.Claims;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Hubs;
using Voiceover.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Controllers;

// Message content is always opaque E2EE ciphertext (see Dtos.MessageResponse
// and ServerMemberKey) - this controller just stores/relays it, it never
// decrypts.
[ApiController]
[Authorize]
[Route("api/channels/{channelId}/[controller]")]
public class MessagesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PermissionService _permissions;
    private readonly IHubContext<ChatHub> _hub;

    public MessagesController(AppDbContext db, PermissionService permissions, IHubContext<ChatHub> hub)
    {
        _db = db;
        _permissions = permissions;
        _hub = hub;
    }

    // Must match ChatHub's private GroupName(channelId) - every text
    // channel's members are already in this group via JoinChannel, same one
    // SendMessage broadcasts new messages to.
    private static string GroupName(int channelId) => $"channel-{channelId}";

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
            .Include(m => m.Author)
            .ToListAsync();

        var response = messages
            .Select(m => new MessageResponse(m.Id, m.Content, m.ChannelId, m.AuthorId, m.Author!.Username, m.SentAt, m.AttachmentUrl, m.Author!.AvatarUrl, m.EditedAt))
            .ToList();

        return Ok(response);
    }

    // Author-only - unlike Delete, moderators can remove someone else's
    // message but shouldn't be able to rewrite their words.
    [HttpPut("{messageId}")]
    public async Task<ActionResult<MessageResponse>> Edit(int channelId, int messageId, EditMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content)) return BadRequest();

        var message = await _db.Messages
            .Include(m => m.Author)
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ChannelId == channelId);

        if (message is null) return NotFound();
        if (message.AuthorId != CurrentUserId) return Forbid();

        // request.Content is already E2EE ciphertext by the time it gets
        // here - the client always encrypts before calling this (see
        // ApiService/MainWindow's channel-edit path).
        message.Content = request.Content;
        message.EditedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var response = new MessageResponse(message.Id, message.Content, channelId, message.AuthorId, message.Author!.Username, message.SentAt, message.AttachmentUrl, message.Author.AvatarUrl, message.EditedAt);
        await _hub.Clients.Group(GroupName(channelId)).SendAsync("MessageEdited", response);

        return Ok(response);
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
        await _hub.Clients.Group(GroupName(channelId)).SendAsync("MessageDeleted", messageId, channelId);

        return Ok();
    }
}
