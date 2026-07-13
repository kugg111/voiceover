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

    // GET /api/channels/5/messages?take=50           -> most recent 50 messages, oldest first
    // GET /api/channels/5/messages?take=50&beforeId=X -> the 50 messages immediately before
    //     message X (for "load older" - Id is a strictly monotonic cursor, safer than SentAt
    //     since two messages can share a timestamp but never an Id).
    [HttpGet]
    public async Task<ActionResult<List<MessageResponse>>> GetHistory(int channelId, int take = 50, int? beforeId = null)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();

        if (!await _permissions.IsMemberAsync(CurrentUserId, channel.GuildServerId))
            return Forbid();

        var query = _db.Messages.Where(m => m.ChannelId == channelId);
        if (beforeId.HasValue) query = query.Where(m => m.Id < beforeId.Value);

        var messages = await query
            .OrderByDescending(m => m.Id)
            .Take(take)
            .OrderBy(m => m.Id)
            .Include(m => m.Author)
            .ToListAsync();

        var reactionsByMessage = await LoadReactionsAsync(messages.Select(m => m.Id).ToList());

        var response = messages
            .Select(m => new MessageResponse(m.Id, m.Content, m.ChannelId, m.AuthorId, m.Author!.Username, m.SentAt, m.AttachmentUrl, m.Author!.AvatarUrl, m.EditedAt, reactionsByMessage.GetValueOrDefault(m.Id), m.PinnedAt))
            .ToList();

        return Ok(response);
    }

    // GET /api/channels/5/messages/pinned -> every pinned message in this
    // channel, most recently pinned first (not paginated - pin lists are
    // expected to stay small in practice, same assumption Discord itself makes).
    [HttpGet("pinned")]
    public async Task<ActionResult<List<MessageResponse>>> GetPinned(int channelId)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();
        if (!await _permissions.IsMemberAsync(CurrentUserId, channel.GuildServerId))
            return Forbid();

        var messages = await _db.Messages
            .Where(m => m.ChannelId == channelId && m.PinnedAt != null)
            .OrderByDescending(m => m.PinnedAt)
            .Include(m => m.Author)
            .ToListAsync();

        var reactionsByMessage = await LoadReactionsAsync(messages.Select(m => m.Id).ToList());

        var response = messages
            .Select(m => new MessageResponse(m.Id, m.Content, m.ChannelId, m.AuthorId, m.Author!.Username, m.SentAt, m.AttachmentUrl, m.Author!.AvatarUrl, m.EditedAt, reactionsByMessage.GetValueOrDefault(m.Id), m.PinnedAt))
            .ToList();

        return Ok(response);
    }

    // Pinning requires being able to manage the server (owner/moderator) -
    // unlike Delete, authorship alone doesn't grant it, matching Discord's
    // own "Manage Messages" permission rather than this app's simpler
    // author-or-moderator rule used elsewhere.
    [HttpPut("{messageId}/pin")]
    public async Task<ActionResult> Pin(int channelId, int messageId)
    {
        var message = await _db.Messages
            .Include(m => m.Channel)
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ChannelId == channelId);
        if (message is null) return NotFound();

        if (!await _permissions.CanManageServerAsync(CurrentUserId, message.Channel!.GuildServerId))
            return Forbid();

        message.PinnedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _hub.Clients.Group(GroupName(channelId)).SendAsync("MessagePinned", channelId, messageId, message.PinnedAt);

        return Ok();
    }

    [HttpDelete("{messageId}/pin")]
    public async Task<ActionResult> Unpin(int channelId, int messageId)
    {
        var message = await _db.Messages
            .Include(m => m.Channel)
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ChannelId == channelId);
        if (message is null) return NotFound();

        if (!await _permissions.CanManageServerAsync(CurrentUserId, message.Channel!.GuildServerId))
            return Forbid();

        message.PinnedAt = null;
        await _db.SaveChangesAsync();
        await _hub.Clients.Group(GroupName(channelId)).SendAsync("MessageUnpinned", channelId, messageId);

        return Ok();
    }

    // Aggregated per (message, emoji) rather than shipping every individual
    // reaction row - ReactionSummaryResponse.ReactedByMe is computed against
    // CurrentUserId here so the client never needs to know every reactor's
    // identity just to render its own "you reacted" state.
    private async Task<Dictionary<int, List<ReactionSummaryResponse>>> LoadReactionsAsync(List<int> messageIds)
    {
        if (messageIds.Count == 0) return new();

        var rows = await _db.MessageReactions
            .Where(r => messageIds.Contains(r.MessageId))
            .ToListAsync();

        return rows
            .GroupBy(r => r.MessageId)
            .ToDictionary(g => g.Key, g => g
                .GroupBy(r => r.Emoji)
                .Select(eg => new ReactionSummaryResponse(eg.Key, eg.Count(), eg.Any(r => r.UserId == CurrentUserId)))
                .ToList());
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

        var reactions = (await LoadReactionsAsync(new List<int> { messageId })).GetValueOrDefault(messageId);
        var response = new MessageResponse(message.Id, message.Content, channelId, message.AuthorId, message.Author!.Username, message.SentAt, message.AttachmentUrl, message.Author.AvatarUrl, message.EditedAt, reactions, message.PinnedAt);
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

        // No FK/cascade configured between MessageReaction and Message (see
        // AppDbContext) - clean these up explicitly so deleting a reacted-to
        // message doesn't leave orphaned reaction rows behind forever.
        var reactions = await _db.MessageReactions.Where(r => r.MessageId == messageId).ToListAsync();
        _db.MessageReactions.RemoveRange(reactions);

        _db.Messages.Remove(message);
        await _db.SaveChangesAsync();
        await _hub.Clients.Group(GroupName(channelId)).SendAsync("MessageDeleted", messageId, channelId);

        return Ok();
    }
}
