using System.Security.Claims;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Controllers;

// Call history is unencrypted metadata only (see CallRecord) - no call
// content exists server-side to protect either way.
[ApiController]
[Authorize]
[Route("api/calls")]
public class CallsController : ControllerBase
{
    private readonly AppDbContext _db;

    public CallsController(AppDbContext db)
    {
        _db = db;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/calls/history?take=50                -> most recent calls first, both
    //     outgoing and incoming, across every friend (not scoped to one
    //     conversation - see CallRecord for why this needs its own table).
    // GET /api/calls/history?take=50&beforeId=X      -> the 50 calls immediately
    //     before record X ("load more" - Id is used as the cursor rather than
    //     EndedAt since it's collision-free and, because rows are inserted at
    //     the moment a call ends, Id order and EndedAt order always agree).
    [HttpGet("history")]
    public async Task<ActionResult<List<CallRecordResponse>>> GetHistory(int take = 50, int? beforeId = null)
    {
        var query = _db.CallRecords.Where(c => c.CallerId == CurrentUserId || c.CalleeId == CurrentUserId);
        if (beforeId.HasValue) query = query.Where(c => c.Id < beforeId.Value);

        var records = await query
            .OrderByDescending(c => c.Id)
            .Take(take)
            .ToListAsync();

        var otherUserIds = records
            .Select(c => c.CallerId == CurrentUserId ? c.CalleeId : c.CallerId)
            .Distinct()
            .ToList();

        var otherUsers = await _db.Users
            .Where(u => otherUserIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Username, u.AvatarUrl })
            .ToDictionaryAsync(u => u.Id);

        var response = records.Select(c =>
        {
            var wasIncoming = c.CalleeId == CurrentUserId;
            var otherUserId = wasIncoming ? c.CallerId : c.CalleeId;
            otherUsers.TryGetValue(otherUserId, out var other);
            var durationSeconds = c.Outcome == CallOutcome.Completed && c.ConnectedAt.HasValue
                ? (int)(c.EndedAt - c.ConnectedAt.Value).TotalSeconds
                : (int?)null;

            return new CallRecordResponse(
                c.Id, otherUserId, other?.Username ?? "Unknown", wasIncoming, c.Outcome.ToString(),
                c.StartedAt, c.EndedAt, durationSeconds, other?.AvatarUrl);
        }).ToList();

        return Ok(response);
    }
}
