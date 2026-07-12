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

[ApiController]
[Authorize]
[Route("api/dm")]
public class DirectMessagesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MessageEncryptionService _messageEncryption;
    private readonly IHubContext<ChatHub> _hub;

    public DirectMessagesController(AppDbContext db, MessageEncryptionService messageEncryption, IHubContext<ChatHub> hub)
    {
        _db = db;
        _messageEncryption = messageEncryption;
        _hub = hub;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/dm/conversations -> one row per person the current user has
    // exchanged DMs with, most recent conversation first. Grouping is done
    // in-memory after fetching (small scale, avoids fighting EF's SQL
    // translation for "latest row per group").
    [HttpGet("conversations")]
    public async Task<ActionResult<List<DmConversationResponse>>> GetConversations()
    {
        var messages = await _db.DirectMessages
            .Where(m => m.SenderId == CurrentUserId || m.RecipientId == CurrentUserId)
            .OrderByDescending(m => m.SentAt)
            .ToListAsync();

        var latestPerOtherUser = messages
            .GroupBy(m => m.SenderId == CurrentUserId ? m.RecipientId : m.SenderId)
            .Select(g => new { OtherUserId = g.Key, Last = g.First() })
            .ToList();

        var otherUserIds = latestPerOtherUser.Select(c => c.OtherUserId).ToList();
        var userInfo = await _db.Users
            .Where(u => otherUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new { u.Username, u.AvatarUrl });

        var result = latestPerOtherUser
            .Select(c =>
            {
                var info = userInfo.GetValueOrDefault(c.OtherUserId);
                return new DmConversationResponse(
                    c.OtherUserId,
                    info?.Username ?? "Unknown",
                    _messageEncryption.Decrypt(c.Last.Content),
                    c.Last.SentAt,
                    info?.AvatarUrl);
            })
            .OrderByDescending(c => c.LastMessageAt)
            .ToList();

        return Ok(result);
    }

    // GET /api/dm/5  -> history between the current user and user 5, oldest first
    [HttpGet("{otherUserId:int}")]
    public async Task<ActionResult<List<DirectMessageResponse>>> GetHistory(int otherUserId, int take = 50)
    {
        var messages = await _db.DirectMessages
            .Where(m =>
                (m.SenderId == CurrentUserId && m.RecipientId == otherUserId) ||
                (m.SenderId == otherUserId && m.RecipientId == CurrentUserId))
            .OrderByDescending(m => m.SentAt)
            .Take(take)
            .OrderBy(m => m.SentAt)
            .ToListAsync();

        // Decryption can't happen inside the EF query above (it'd try to
        // translate MessageEncryptionService.Decrypt into SQL) - projecting to
        // the response DTO happens here, in memory, after materializing.
        var response = messages
            .Select(m => new DirectMessageResponse(m.Id, _messageEncryption.Decrypt(m.Content), m.SenderId, m.RecipientId, m.SentAt, m.EditedAt))
            .ToList();

        return Ok(response);
    }

    // Author-only, always - DMs have no moderator concept the way server
    // channels do.
    [HttpPut("{otherUserId:int}/{messageId:int}")]
    public async Task<ActionResult<DirectMessageResponse>> Edit(int otherUserId, int messageId, EditMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content)) return BadRequest();

        var message = await _db.DirectMessages.FirstOrDefaultAsync(m => m.Id == messageId);
        if (message is null) return NotFound();
        if (message.SenderId != CurrentUserId) return Forbid();

        message.Content = _messageEncryption.Encrypt(request.Content);
        message.EditedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Broadcasts the plaintext we were handed, not message.Content - see
        // ChatHub.SendDirectMessage for why (recipients only ever see
        // plaintext, the stored ciphertext never leaves the server).
        var response = new DirectMessageResponse(message.Id, request.Content, message.SenderId, message.RecipientId, message.SentAt, message.EditedAt);

        // Same dual-send pattern SendDirectMessage already uses - both
        // participants' own connections need the update, there's no shared
        // group for a 1:1 DM the way channel messages have.
        await _hub.Clients.User(message.RecipientId.ToString()).SendAsync("DirectMessageEdited", response);
        await _hub.Clients.User(message.SenderId.ToString()).SendAsync("DirectMessageEdited", response);

        return Ok(response);
    }

    [HttpDelete("{otherUserId:int}/{messageId:int}")]
    public async Task<ActionResult> Delete(int otherUserId, int messageId)
    {
        var message = await _db.DirectMessages.FirstOrDefaultAsync(m => m.Id == messageId);
        if (message is null) return NotFound();
        if (message.SenderId != CurrentUserId) return Forbid();

        var senderId = message.SenderId;
        var recipientId = message.RecipientId;

        _db.DirectMessages.Remove(message);
        await _db.SaveChangesAsync();

        // Sends both ids (not just "the other user") so each side can work
        // out which conversation this belongs to the same way a live
        // DirectMessageResponse would - whichever id isn't their own.
        await _hub.Clients.User(recipientId.ToString()).SendAsync("DirectMessageDeleted", messageId, senderId, recipientId);
        await _hub.Clients.User(senderId.ToString()).SendAsync("DirectMessageDeleted", messageId, senderId, recipientId);

        return Ok();
    }
}
