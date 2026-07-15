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
    private readonly ModerationLogService _modLog;
    private readonly IHubContext<ChatHub> _hub;

    public MessagesController(AppDbContext db, PermissionService permissions, ModerationLogService modLog, IHubContext<ChatHub> hub)
    {
        _db = db;
        _permissions = permissions;
        _modLog = modLog;
        _hub = hub;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string CurrentUsername => User.FindFirstValue(ClaimTypes.Name)!;

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
        var replyAuthors = await LoadReplyAuthorsAsync(messages);

        var response = messages
            .Select(m => new MessageResponse(m.Id, m.Content, m.ChannelId, m.AuthorId, m.Author!.Username, m.SentAt, m.AttachmentUrl, m.Author!.AvatarUrl, m.EditedAt, reactionsByMessage.GetValueOrDefault(m.Id), m.PinnedAt, m.ReplyToMessageId, m.ReplyToMessageId is null ? null : replyAuthors.GetValueOrDefault(m.ReplyToMessageId.Value)))
            .ToList();

        return Ok(response);
    }

    // GET /api/channels/5/messages/pinned -> pinned messages in this
    // channel, most recently pinned first. take/skip are optional and
    // unbounded by default (existing callers get today's "return
    // everything" behavior) - added as a cap against a pathologically large
    // pin list rather than an assumption pin lists always stay small.
    [HttpGet("pinned")]
    public async Task<ActionResult<List<MessageResponse>>> GetPinned(int channelId, int? take = null, int? skip = null)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();
        if (!await _permissions.IsMemberAsync(CurrentUserId, channel.GuildServerId))
            return Forbid();

        var query = _db.Messages
            .Where(m => m.ChannelId == channelId && m.PinnedAt != null)
            .OrderByDescending(m => m.PinnedAt)
            .Skip(skip ?? 0);
        if (PaginationLimits.Clamp(take) is { } clampedTake) query = query.Take(clampedTake);

        var messages = await query
            .Include(m => m.Author)
            .ToListAsync();

        var reactionsByMessage = await LoadReactionsAsync(messages.Select(m => m.Id).ToList());
        var replyAuthors = await LoadReplyAuthorsAsync(messages);

        var response = messages
            .Select(m => new MessageResponse(m.Id, m.Content, m.ChannelId, m.AuthorId, m.Author!.Username, m.SentAt, m.AttachmentUrl, m.Author!.AvatarUrl, m.EditedAt, reactionsByMessage.GetValueOrDefault(m.Id), m.PinnedAt, m.ReplyToMessageId, m.ReplyToMessageId is null ? null : replyAuthors.GetValueOrDefault(m.ReplyToMessageId.Value)))
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

        if (!await _permissions.HasPermissionAsync(CurrentUserId, message.Channel!.GuildServerId, ServerPermission.ManageMessages))
            return Forbid();

        message.PinnedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _hub.Clients.Group(HubGroups.Channel(channelId)).SendAsync("MessagePinned", channelId, messageId, message.PinnedAt);
        await _modLog.LogAsync(message.Channel.GuildServerId, CurrentUserId, CurrentUsername, "Pin", details: $"message #{messageId} in channel #{channelId}");

        return Ok();
    }

    [HttpDelete("{messageId}/pin")]
    public async Task<ActionResult> Unpin(int channelId, int messageId)
    {
        var message = await _db.Messages
            .Include(m => m.Channel)
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ChannelId == channelId);
        if (message is null) return NotFound();

        if (!await _permissions.HasPermissionAsync(CurrentUserId, message.Channel!.GuildServerId, ServerPermission.ManageMessages))
            return Forbid();

        message.PinnedAt = null;
        await _db.SaveChangesAsync();
        await _hub.Clients.Group(HubGroups.Channel(channelId)).SendAsync("MessageUnpinned", channelId, messageId);
        await _modLog.LogAsync(message.Channel.GuildServerId, CurrentUserId, CurrentUsername, "Unpin", details: $"message #{messageId} in channel #{channelId}");

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

    // Batches the "who wrote the message being replied to" lookup that
    // MessageResponse.ReplyToAuthorId needs - content itself is E2EE
    // ciphertext the server can't preview, so this is the only extra piece
    // of context worth shipping alongside the raw ReplyToMessageId.
    private async Task<Dictionary<int, int>> LoadReplyAuthorsAsync(List<Message> messages)
    {
        var replyToIds = messages.Where(m => m.ReplyToMessageId.HasValue).Select(m => m.ReplyToMessageId!.Value).Distinct().ToList();
        if (replyToIds.Count == 0) return new();

        return await _db.Messages
            .Where(m => replyToIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.AuthorId);
    }

    // Author-only - unlike Delete, moderators can remove someone else's
    // message but shouldn't be able to rewrite their words.
    [HttpPut("{messageId}")]
    public async Task<ActionResult<MessageResponse>> Edit(int channelId, int messageId, EditMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content)) return BadRequest();
        if (request.Content.Length > ContentLimits.MaxMessageLength) return BadRequest("Message is too long.");

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
        var replyAuthors = await LoadReplyAuthorsAsync(new List<Message> { message });
        var response = new MessageResponse(message.Id, message.Content, channelId, message.AuthorId, message.Author!.Username, message.SentAt, message.AttachmentUrl, message.Author.AvatarUrl, message.EditedAt, reactions, message.PinnedAt, message.ReplyToMessageId, message.ReplyToMessageId is null ? null : replyAuthors.GetValueOrDefault(message.ReplyToMessageId.Value));
        await _hub.Clients.Group(HubGroups.Channel(channelId)).SendAsync("MessageEdited", response);

        return Ok(response);
    }

    // Only the author, or an owner/moderator with ManageMessages, may delete
    // a message.
    [HttpDelete("{messageId}")]
    public async Task<ActionResult> Delete(int channelId, int messageId)
    {
        var message = await _db.Messages
            .Include(m => m.Channel)
            .Include(m => m.Author)
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ChannelId == channelId);

        if (message is null) return NotFound();

        var isAuthor = message.AuthorId == CurrentUserId;
        var canManage = await _permissions.HasPermissionAsync(CurrentUserId, message.Channel!.GuildServerId, ServerPermission.ManageMessages);

        if (!isAuthor && !canManage) return Forbid();

        // No FK/cascade configured between MessageReaction and Message (see
        // AppDbContext) - clean these up explicitly so deleting a reacted-to
        // message doesn't leave orphaned reaction rows behind forever.
        var reactions = await _db.MessageReactions.Where(r => r.MessageId == messageId).ToListAsync();
        _db.MessageReactions.RemoveRange(reactions);

        _db.Messages.Remove(message);
        await _db.SaveChangesAsync();
        await _hub.Clients.Group(HubGroups.Channel(channelId)).SendAsync("MessageDeleted", messageId, channelId);

        // Only log when it wasn't a routine self-delete - avoids logging
        // every user deleting their own typo.
        if (!isAuthor)
            await _modLog.LogAsync(message.Channel.GuildServerId, CurrentUserId, CurrentUsername, "MessageDelete", message.AuthorId, message.Author?.Username, $"message #{messageId} in channel #{channelId}");

        return Ok();
    }

    // "Purge" - deletes every message by a specific user in this channel, a
    // single practical moderation action for a spammer/troll rather than a
    // multi-select UI. ManageMessages-gated, same as single-message delete's
    // moderator path.
    [HttpDelete("from/{userId}")]
    public async Task<ActionResult> DeleteAllFromUser(int channelId, int userId)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();

        if (!await _permissions.HasPermissionAsync(CurrentUserId, channel.GuildServerId, ServerPermission.ManageMessages))
            return Forbid();

        var messages = await _db.Messages.Where(m => m.ChannelId == channelId && m.AuthorId == userId).ToListAsync();
        if (messages.Count == 0) return Ok();

        var messageIds = messages.Select(m => m.Id).ToList();
        var reactions = await _db.MessageReactions.Where(r => messageIds.Contains(r.MessageId)).ToListAsync();
        _db.MessageReactions.RemoveRange(reactions);
        _db.Messages.RemoveRange(messages);
        await _db.SaveChangesAsync();

        await _hub.Clients.Group(HubGroups.Channel(channelId)).SendAsync("MessagesBulkDeletedByUser", channelId, userId);

        var targetUsername = (await _db.Users.FindAsync(userId))?.Username;
        await _modLog.LogAsync(channel.GuildServerId, CurrentUserId, CurrentUsername, "BulkDelete", userId, targetUsername, $"{messages.Count} message(s) in channel #{channelId}");

        return Ok();
    }
}
