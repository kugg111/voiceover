using System.Security.Claims;
using Voiceover.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Voiceover.Server.Controllers;

public record AdminRenameUserRequest(string Username);
public record AdminResetPasswordRequest(string NewPassword);

// Backs the developer-only dashboard at Server/Site/admin/index.html - not
// linked anywhere in the public app, reached only by someone who already
// knows the URL and has an IsAdmin account. Every action starts with the
// same IsAdminAsync check (checked fresh from the DB each request, not
// baked into the JWT, so revoking access takes effect immediately) rather
// than a policy-based [Authorize] requirement - matches this codebase's
// existing convention of manual per-action checks (see every other
// controller's use of PermissionService) instead of ASP.NET Core's
// declarative authorization policies, which aren't used anywhere here.
[ApiController]
[Authorize]
[Route("api/[controller]")]
[EnableRateLimiting("admin")]
public class AdminController : ControllerBase
{
    private readonly AdminService _admin;

    public AdminController(AdminService admin) => _admin = admin;

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string CurrentUsername => User.FindFirstValue(ClaimTypes.Name)!;

    [HttpGet("servers")]
    public async Task<ActionResult<List<AdminServerSummaryResponse>>> GetServers(int? take = null, int? skip = null)
    {
        if (!await _admin.IsAdminAsync(CurrentUserId)) return Forbid();
        return Ok(await _admin.GetServersAsync(take, skip));
    }

    [HttpGet("servers/{id:int}")]
    public async Task<ActionResult<AdminServerDetailResponse>> GetServerDetail(int id)
    {
        if (!await _admin.IsAdminAsync(CurrentUserId)) return Forbid();
        var detail = await _admin.GetServerDetailAsync(id);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpDelete("servers/{id:int}")]
    public async Task<ActionResult> DeleteServer(int id)
    {
        if (!await _admin.IsAdminAsync(CurrentUserId)) return Forbid();

        var result = await _admin.DeleteServerAsync(CurrentUserId, CurrentUsername, id);
        return result == AdminActionResult.TargetNotFound ? NotFound() : Ok();
    }

    [HttpGet("users/search")]
    public async Task<ActionResult<List<AdminUserSearchResponse>>> SearchUsers(string username)
    {
        if (!await _admin.IsAdminAsync(CurrentUserId)) return Forbid();
        return Ok(await _admin.SearchUsersAsync(username));
    }

    [HttpPut("users/{id:int}/username")]
    public async Task<ActionResult> RenameUser(int id, AdminRenameUserRequest req)
    {
        if (!await _admin.IsAdminAsync(CurrentUserId)) return Forbid();

        var result = await _admin.RenameUserAsync(CurrentUserId, CurrentUsername, id, req.Username);
        return result switch
        {
            AdminActionResult.TargetNotFound => NotFound(),
            AdminActionResult.UsernameTaken => Conflict("Username already taken."),
            AdminActionResult.InvalidInput => BadRequest("Username must be 2-32 characters."),
            _ => Ok()
        };
    }

    [HttpPost("users/{id:int}/reset-password")]
    public async Task<ActionResult> ResetPassword(int id, AdminResetPasswordRequest req)
    {
        if (!await _admin.IsAdminAsync(CurrentUserId)) return Forbid();

        var result = await _admin.ResetPasswordAsync(CurrentUserId, CurrentUsername, id, req.NewPassword);
        return result switch
        {
            AdminActionResult.TargetNotFound => NotFound(),
            AdminActionResult.InvalidInput => BadRequest("Password must be at least 8 characters."),
            _ => Ok()
        };
    }
}
