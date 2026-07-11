using System.Security.Claims;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Hubs;
using Voiceover.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/friends")]
public class FriendsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHubContext<ChatHub> _hub;

    public FriendsController(AppDbContext db, IHubContext<ChatHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/friends -> accepted friendships, returned as the other user
    [HttpGet]
    public async Task<ActionResult<List<FriendResponse>>> GetFriends()
    {
        var friendships = await _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == CurrentUserId || f.AddresseeId == CurrentUserId))
            .ToListAsync();

        var otherUserIds = friendships
            .Select(f => f.RequesterId == CurrentUserId ? f.AddresseeId : f.RequesterId)
            .ToList();

        var userInfo = await _db.Users
            .Where(u => otherUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new { u.Username, u.AvatarUrl });

        var result = otherUserIds
            .Select(id =>
            {
                var info = userInfo.GetValueOrDefault(id);
                return new FriendResponse(id, info?.Username ?? "Unknown", info?.AvatarUrl);
            })
            .ToList();

        return Ok(result);
    }

    // GET /api/friends/requests -> pending requests involving the current user
    [HttpGet("requests")]
    public async Task<ActionResult<List<FriendRequestResponse>>> GetRequests()
    {
        var pending = await _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Pending &&
                        (f.RequesterId == CurrentUserId || f.AddresseeId == CurrentUserId))
            .ToListAsync();

        var otherUserIds = pending
            .Select(f => f.RequesterId == CurrentUserId ? f.AddresseeId : f.RequesterId)
            .ToList();

        var userInfo = await _db.Users
            .Where(u => otherUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new { u.Username, u.AvatarUrl });

        var result = pending
            .Select(f =>
            {
                var otherUserId = f.RequesterId == CurrentUserId ? f.AddresseeId : f.RequesterId;
                var direction = f.RequesterId == CurrentUserId ? "Outgoing" : "Incoming";
                var info = userInfo.GetValueOrDefault(otherUserId);
                return new FriendRequestResponse(f.Id, otherUserId, info?.Username ?? "Unknown", direction, info?.AvatarUrl);
            })
            .ToList();

        return Ok(result);
    }

    // POST /api/friends/request/5
    [HttpPost("request/{userId}")]
    public async Task<ActionResult> SendRequest(int userId)
    {
        if (userId == CurrentUserId) return BadRequest("Can't friend yourself.");
        if (!await _db.Users.AnyAsync(u => u.Id == userId)) return NotFound();

        var existing = await _db.Friendships.FirstOrDefaultAsync(f =>
            (f.RequesterId == CurrentUserId && f.AddresseeId == userId) ||
            (f.RequesterId == userId && f.AddresseeId == CurrentUserId));

        if (existing is not null)
        {
            if (existing.Status == FriendshipStatus.Accepted)
                return BadRequest("Already friends.");

            // A reverse pending request already exists (mutual request) -
            // accept it instead of creating a duplicate row.
            if (existing.RequesterId == userId)
            {
                existing.Status = FriendshipStatus.Accepted;
                await _db.SaveChangesAsync();
                await _hub.Clients.User(userId.ToString()).SendAsync("FriendRequestAccepted", existing.Id, CurrentUserId);
                return Ok();
            }

            return BadRequest("Friend request already sent.");
        }

        var friendship = new Friendship { RequesterId = CurrentUserId, AddresseeId = userId };
        _db.Friendships.Add(friendship);
        await _db.SaveChangesAsync();

        var requesterUsername = User.FindFirstValue(ClaimTypes.Name)!;
        await _hub.Clients.User(userId.ToString()).SendAsync("FriendRequestReceived", friendship.Id, CurrentUserId, requesterUsername);

        return Ok();
    }

    // POST /api/friends/12/accept
    [HttpPost("{friendshipId}/accept")]
    public async Task<ActionResult> Accept(int friendshipId)
    {
        var friendship = await _db.Friendships.FirstOrDefaultAsync(f => f.Id == friendshipId);
        if (friendship is null) return NotFound();
        if (friendship.AddresseeId != CurrentUserId) return Forbid();
        if (friendship.Status == FriendshipStatus.Accepted) return Ok();

        friendship.Status = FriendshipStatus.Accepted;
        await _db.SaveChangesAsync();

        await _hub.Clients.User(friendship.RequesterId.ToString()).SendAsync("FriendRequestAccepted", friendship.Id, CurrentUserId);

        return Ok();
    }

    // DELETE /api/friends/12 -> covers declining, cancelling, and unfriending
    [HttpDelete("{friendshipId}")]
    public async Task<ActionResult> Remove(int friendshipId)
    {
        var friendship = await _db.Friendships.FirstOrDefaultAsync(f => f.Id == friendshipId);
        if (friendship is null) return NotFound();
        if (friendship.RequesterId != CurrentUserId && friendship.AddresseeId != CurrentUserId) return Forbid();

        _db.Friendships.Remove(friendship);
        await _db.SaveChangesAsync();
        return Ok();
    }
}
