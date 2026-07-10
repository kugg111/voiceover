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

    // GET /api/dm/5  -> history between the current user and user 5, oldest first
    [HttpGet("{otherUserId}")]
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
