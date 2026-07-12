using System.Security.Claims;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Services;
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
    private readonly UserAvatarCache _avatarCache;

    public UsersController(AppDbContext db, UserAvatarCache avatarCache)
    {
        _db = db;
        _avatarCache = avatarCache;
    }

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

        // The one and only place an avatar can change - keep
        // ChatHub.SendMessage's cache (which exists specifically to avoid
        // a DB round trip per message) from serving a stale URL.
        _avatarCache.Set(CurrentUserId, user.AvatarUrl);

        return Ok();
    }

    // Only the public key - safe for anyone to read, it's what a sender
    // needs to derive a shared DM key with this user (see E2eeService).
    // Null PublicKey means this account hasn't set up E2EE yet (hasn't
    // logged in since it shipped) - callers should treat that as "can't
    // send an encrypted DM to this person yet", not retry/error.
    [HttpGet("{id:int}/public-key")]
    public async Task<ActionResult<PublicKeyResponse>> GetPublicKey(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return NotFound();

        return Ok(new PublicKeyResponse(user.Id, user.PublicKey));
    }

    // Lets a newly-logged-in device fetch this account's wrapped private
    // key so it can unwrap it locally with the just-entered password (see
    // E2eeService.UnlockAsync) - the only way a second device gets access
    // to full DM history.
    [HttpGet("me/key-material")]
    public async Task<ActionResult<OwnKeyMaterialResponse>> GetMyKeyMaterial()
    {
        var user = await _db.Users.FindAsync(CurrentUserId);
        if (user is null) return NotFound();

        return Ok(new OwnKeyMaterialResponse(user.PublicKey, user.WrappedPrivateKey, user.PrivateKeySalt));
    }

    // Called once per account, either right after registration or on the
    // first login after E2EE shipped for a pre-existing account - uploads
    // freshly generated key material. Overwriting existing key material
    // isn't reachable from the client today (would orphan every other
    // device's ability to unwrap the old private key) - this endpoint just
    // persists whatever the client hands it, same trust model as any other
    // [Authorize]'d "update my own account" endpoint.
    [HttpPut("me/keys")]
    public async Task<ActionResult> SetMyKeys(SetKeyMaterialRequest req)
    {
        var user = await _db.Users.FindAsync(CurrentUserId);
        if (user is null) return NotFound();

        user.PublicKey = req.PublicKey;
        user.WrappedPrivateKey = req.WrappedPrivateKey;
        user.PrivateKeySalt = req.PrivateKeySalt;
        await _db.SaveChangesAsync();
        return Ok();
    }
}
