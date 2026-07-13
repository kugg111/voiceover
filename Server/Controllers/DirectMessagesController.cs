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
    public async Task<ActionResult<List<DmConversationResponse>>> GetConversations(int take = 100)
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
                SELECT ""Id"", ""Content"", ""SenderId"", ""RecipientId"", ""SentAt"", ""EditedAt"", ""ReadAt""
                FROM (
                    SELECT DISTINCT ON (other_user_id)
                        ""Id"", ""Content"", ""SenderId"", ""RecipientId"", ""SentAt"", ""EditedAt"", ""ReadAt"", other_user_id
                    FROM (
                        SELECT ""Id"", ""Content"", ""SenderId"", ""RecipientId"", ""SentAt"", ""EditedAt"", ""ReadAt"",
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

        // Bounds what actually goes out over the wire (and gets decrypted
        // client-side) to the most recently active conversations - the DISTINCT
        // ON query above still has to touch every partner to find each one's
        // latest message, but a user with hundreds of DM partners doesn't need
        // all of them re-sent and re-decrypted on every Messages-tab open.
        var result = latestMessages
            .Select(m =>
            {
                var otherUserId = m.SenderId == currentUserId ? m.RecipientId : m.SenderId;
                var info = userInfo.GetValueOrDefault(otherUserId);
                return new DmConversationResponse(otherUserId, info?.Username ?? "Unknown", m.Content, m.SentAt, info?.AvatarUrl);
            })
            .OrderByDescending(c => c.LastMessageAt)
            .Take(take)
            .ToList();

        return Ok(result);
    }

    // GET /api/dm/5                    -> history with user 5, oldest first
    // GET /api/dm/5?take=50&beforeId=X  -> the 50 messages immediately before message X
    //     ("load older" - see MessagesController.GetHistory for why Id, not SentAt, is the cursor)
    [HttpGet("{otherUserId:int}")]
    public async Task<ActionResult<List<DirectMessageResponse>>> GetHistory(int otherUserId, int take = 50, int? beforeId = null)
    {
        var query = _db.DirectMessages.Where(m =>
            (m.SenderId == CurrentUserId && m.RecipientId == otherUserId) ||
            (m.SenderId == otherUserId && m.RecipientId == CurrentUserId));
        if (beforeId.HasValue) query = query.Where(m => m.Id < beforeId.Value);

        var messages = await query
            .OrderByDescending(m => m.Id)
            .Take(take)
            .OrderBy(m => m.Id)
            .ToListAsync();

        var reactionsByMessage = await LoadReactionsAsync(messages.Select(m => m.Id).ToList());

        var response = messages
            .Select(m => new DirectMessageResponse(m.Id, m.Content, m.SenderId, m.RecipientId, m.SentAt, m.EditedAt, m.ReadAt, reactionsByMessage.GetValueOrDefault(m.Id)))
            .ToList();

        return Ok(response);
    }

    // Same aggregation shape as MessagesController.LoadReactionsAsync, over
    // DirectMessageReaction instead of MessageReaction.
    private async Task<Dictionary<int, List<ReactionSummaryResponse>>> LoadReactionsAsync(List<int> messageIds)
    {
        if (messageIds.Count == 0) return new();

        var rows = await _db.DirectMessageReactions
            .Where(r => messageIds.Contains(r.DirectMessageId))
            .ToListAsync();

        return rows
            .GroupBy(r => r.DirectMessageId)
            .ToDictionary(g => g.Key, g => g
                .GroupBy(r => r.Emoji)
                .Select(eg => new ReactionSummaryResponse(eg.Key, eg.Count(), eg.Any(r => r.UserId == CurrentUserId)))
                .ToList());
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
        var reactions = (await LoadReactionsAsync(new List<int> { messageId })).GetValueOrDefault(messageId);
        var response = new DirectMessageResponse(message.Id, message.Content, message.SenderId, message.RecipientId, message.SentAt, message.EditedAt, message.ReadAt, reactions);

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

        // No FK/cascade configured between DirectMessageReaction and
        // DirectMessage (see AppDbContext) - clean these up explicitly, same
        // reasoning as MessagesController.Delete.
        var reactions = await _db.DirectMessageReactions.Where(r => r.DirectMessageId == messageId).ToListAsync();
        _db.DirectMessageReactions.RemoveRange(reactions);

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
