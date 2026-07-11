using System.Security.Claims;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/dm")]
public class DirectMessagesController : ControllerBase
{
    private readonly AppDbContext _db;
    public DirectMessagesController(AppDbContext db) => _db = db;

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
                    c.Last.Content,
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
            .Select(m => new DirectMessageResponse(m.Id, m.Content, m.SenderId, m.RecipientId, m.SentAt))
            .ToListAsync();

        return Ok(messages);
    }
}
