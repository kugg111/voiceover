using System.Security.Claims;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    public UsersController(AppDbContext db) => _db = db;

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/users/search?username=alice
    [HttpGet("search")]
    public async Task<ActionResult<List<UserSummaryResponse>>> Search(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return Ok(new List<UserSummaryResponse>());

        var users = await _db.Users
            .Where(u => u.Username.Contains(username))
            .Take(10)
            .Select(u => new UserSummaryResponse(u.Id, u.Username, u.AvatarUrl))
            .ToListAsync();

        return Ok(users);
    }

    // Url is expected to already be an uploaded file's path (from POST
    // /api/upload) - this endpoint just persists that URL against the
    // caller's own account, it doesn't handle the upload itself.
    [HttpPut("me/avatar")]
    public async Task<ActionResult> SetAvatar(SetAvatarRequest req)
    {
        var user = await _db.Users.FindAsync(CurrentUserId);
        if (user is null) return NotFound();

        user.AvatarUrl = req.Url;
        await _db.SaveChangesAsync();
        return Ok();
    }
}
