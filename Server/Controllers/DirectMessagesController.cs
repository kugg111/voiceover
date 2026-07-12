using System.Security.Claims;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Controllers;

// DM content is always opaque E2EE ciphertext (see DirectMessage.Content) -
// this controller just stores/relays it, it never decrypts.
[ApiController]
[Authorize]
[Route("api/dm")]
public class DirectMessagesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHubContext<ChatHub> _hub;

    public DirectMessagesController(AppDbContext db, IHubContext<ChatHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/dm/conversations -> one row per person the current user has
    // exchanged DMs with, most recent conversation first.
    //
    // Uses raw SQL (Postgres's DISTINCT ON) rather than EF Core LINQ,
    // deliberately - the natural LINQ expression for "latest row per group"
    // (GroupBy(computed key).Select(g => g.OrderByDescending(...).First()))
    // either fails to translate or falls back to loading every group's full
    // contents into memory, depending on EF Core version/provider maturity;
    // testing that blindly is exactly the kind of thing that quietly
    // regresses. DISTINCT ON is the standard, well-understood Postgres
    // idiom for this and lets the query planner use the SenderId/
    // RecipientId indexes (see AppDbContext) directly, so this scales with
    // conversation count, not total message count - the previous version
    // pulled every DM the user ever sent or received into memory before
    // grouping in C#.
    [HttpGet("conversations")]
    public async Task<ActionResult<List<DmConversationResponse>>> GetConversations()
    {
        var currentUserId = CurrentUserId;

        // DISTINCT ON's expression has to be textually identical to ORDER
        // BY's leading expression - Npgsql turns each {currentUserId}
        // interpolation into its own separate parameter, so repeating the
        // CASE expression in both clauses (each with a different parameter
        // reference) fails Postgres's check even though the value is the
        // same. Computing other_user_id once in an inner subquery and
        // referencing that plain column in DISTINCT ON/ORDER BY avoids the
        // duplication entirely.
        var latestMessages = await _db.DirectMessages
            .FromSqlInterpolated($@"
                SELECT ""Id"", ""Content"", ""SenderId"", ""RecipientId"", ""SentAt"", ""EditedAt""
                FROM (
                    SELECT DISTINCT ON (other_user_id)
                        ""Id"", ""Content"", ""SenderId"", ""RecipientId"", ""SentAt"", ""EditedAt"", other_user_id
                    FROM (
                        SELECT ""Id"", ""Content"", ""SenderId"", ""RecipientId"", ""SentAt"", ""EditedAt"",
                            CASE WHEN ""SenderId"" = {currentUserId} THEN ""RecipientId"" ELSE ""SenderId"" END AS other_user_id
                        FROM ""DirectMessages""
                        WHERE ""SenderId"" = {currentUserId} OR ""RecipientId"" = {currentUserId}
                    ) tagged
                    ORDER BY other_user_id, ""SentAt"" DESC
                ) latest")
            .ToListAsync();

        var otherUserIds = latestMessages.Select(m => m.SenderId == currentUserId ? m.RecipientId : m.SenderId).ToList();
        var userInfo = await _db.Users
            .Where(u => otherUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new { u.Username, u.AvatarUrl });

        var result = latestMessages
            .Select(m =>
            {
                var otherUserId = m.SenderId == currentUserId ? m.RecipientId : m.SenderId;
                var info = userInfo.GetValueOrDefault(otherUserId);
                return new DmConversationResponse(otherUserId, info?.Username ?? "Unknown", m.Content, m.SentAt, info?.AvatarUrl);
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

        var response = messages
            .Select(m => new DirectMessageResponse(m.Id, m.Content, m.SenderId, m.RecipientId, m.SentAt, m.EditedAt))
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

        // request.Content is already E2EE ciphertext by the time it gets
        // here - the client always encrypts before calling this (see
        // ApiService.EditDirectMessageAsync).
        message.Content = request.Content;
        message.EditedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Broadcasts exactly what was stored - both sides decrypt it
        // themselves client-side (see ChatHub.SendDirectMessage for the
        // same reasoning on the send path).
        var response = new DirectMessageResponse(message.Id, message.Content, message.SenderId, message.RecipientId, message.SentAt, message.EditedAt);

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
