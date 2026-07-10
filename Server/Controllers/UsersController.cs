using DiscordClone.Server.Data;
using DiscordClone.Server.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DiscordClone.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    public UsersController(AppDbContext db) => _db = db;

    // GET /api/users/search?username=alice
    [HttpGet("search")]
    public async Task<ActionResult<List<UserSummaryResponse>>> Search(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return Ok(new List<UserSummaryResponse>());

        var users = await _db.Users
            .Where(u => u.Username.Contains(username))
            .Take(10)
            .Select(u => new UserSummaryResponse(u.Id, u.Username))
            .ToListAsync();

        return Ok(users);
    }
}
