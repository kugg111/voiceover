using Voiceover.Server.Data;
using Voiceover.Server.Hubs;
using Voiceover.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Services;

public enum AdminActionResult { Success, TargetNotFound, UsernameTaken, InvalidInput }

// Backs AdminController - the developer-only dashboard under Server/Site/
// admin/. Deliberately a plain scoped service (same shape as
// PermissionService/ServerDeletionService) rather than logic living
// directly in the controller, so it can be unit-tested without needing to
// fake a ClaimsPrincipal/ControllerContext - see AdminServiceTests.
public class AdminService
{
    private readonly AppDbContext _db;
    private readonly ServerDeletionService _serverDeletion;
    private readonly IHubContext<ChatHub> _hub;
    private readonly UploadsPathOptions _uploadsPath;

    public AdminService(AppDbContext db, ServerDeletionService serverDeletion, IHubContext<ChatHub> hub, UploadsPathOptions uploadsPath)
    {
        _db = db;
        _serverDeletion = serverDeletion;
        _hub = hub;
        _uploadsPath = uploadsPath;
    }

    public async Task<bool> IsAdminAsync(int userId) =>
        (await _db.Users.FindAsync(userId))?.IsAdmin == true;

    public async Task<List<AdminServerSummaryResponse>> GetServersAsync(int? take, int? skip)
    {
        var query = _db.GuildServers
            .OrderBy(s => s.Id)
            .Skip(skip ?? 0);
        if (PaginationLimits.Clamp(take) is { } clampedTake) query = query.Take(clampedTake);

        return await query
            .Select(s => new AdminServerSummaryResponse(
                s.Id, s.Name, s.IconUrl, s.OwnerId, s.Owner!.Username,
                s.Memberships.Count, s.Channels.Count, s.CreatedAt))
            .ToListAsync();
    }

    public async Task<AdminServerDetailResponse?> GetServerDetailAsync(int serverId)
    {
        var server = await _db.GuildServers
            .Where(s => s.Id == serverId)
            .Select(s => new AdminServerSummaryResponse(
                s.Id, s.Name, s.IconUrl, s.OwnerId, s.Owner!.Username,
                s.Memberships.Count, s.Channels.Count, s.CreatedAt))
            .FirstOrDefaultAsync();
        if (server is null) return null;

        var channels = await _db.Channels
            .Where(c => c.GuildServerId == serverId)
            .OrderBy(c => c.Position)
            .Select(c => new { c.Id, c.Name, Type = c.Type.ToString(), c.Position, c.SlowModeSeconds })
            .ToListAsync();

        // Direct SQL-translated aggregate rather than loading messages to
        // memory first (see MessagesController's reaction-count code for
        // where that pattern is actually needed - a nested double-groupby,
        // not the case here) - matters since this scans every message in
        // the server's channels, not a handful of reactions on one page.
        var channelIds = channels.Select(c => c.Id).ToList();
        var messageCounts = await _db.Messages
            .Where(m => channelIds.Contains(m.ChannelId))
            .GroupBy(m => m.ChannelId)
            .Select(g => new { ChannelId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ChannelId, g => g.Count);

        var channelResponses = channels
            .Select(c => new AdminChannelResponse(
                c.Id, c.Name, c.Type, c.Position, c.SlowModeSeconds,
                // Message counting is meaningless for Voice channels.
                c.Type == nameof(ChannelType.Text) ? messageCounts.GetValueOrDefault(c.Id, 0) : null))
            .ToList();

        var members = await _db.Memberships
            .Where(m => m.GuildServerId == serverId)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new AdminMemberResponse(m.UserId, m.User!.Username, m.Role.ToString(), m.JoinedAt))
            .ToListAsync();

        return new AdminServerDetailResponse(server, channelResponses, members);
    }

    // Owner-only in ServersController; here it's admin-only instead,
    // reusing the exact same cascade-safe cleanup (ServerDeletionService)
    // and the same "ServerDeleted" broadcast so any WPF client with the
    // server open updates live, identically to a real owner deleting it.
    public async Task<AdminActionResult> DeleteServerAsync(int actorId, string actorUsername, int serverId)
    {
        var server = await _db.GuildServers.FindAsync(serverId);
        if (server is null) return AdminActionResult.TargetNotFound;

        var serverName = server.Name;
        await _serverDeletion.QueueDeleteAsync(server);

        LogAdminAction(actorId, actorUsername, "DeleteServer", null, null,
            $"Deleted server '{serverName}' (id {serverId}).");

        await _db.SaveChangesAsync();
        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ServerDeleted", serverId);
        return AdminActionResult.Success;
    }

    // Browses all users, optionally narrowed by a username substring filter
    // (backed by the same pg_trgm index UsersController.Search uses),
    // paginated like every other list endpoint (PaginationLimits.Clamp,
    // ordered for stable Skip/Take). Unlike that endpoint this has no
    // minimum filter length - it's a browse-with-optional-filter admin
    // page, not an autocomplete firing on every keystroke, and pagination
    // already bounds each page's result size regardless of how broad the
    // filter is.
    public async Task<List<AdminUserSearchResponse>> GetUsersAsync(string? usernameFilter, int? take, int? skip)
    {
        var query = _db.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(usernameFilter))
            query = query.Where(u => u.Username.Contains(usernameFilter));

        query = query.OrderBy(u => u.Username).Skip(skip ?? 0);
        if (PaginationLimits.Clamp(take) is { } clampedTake) query = query.Take(clampedTake);

        return await query
            .Select(u => new AdminUserSearchResponse(u.Id, u.Username, u.CreatedAt, u.IsAdmin))
            .ToListAsync();
    }

    public async Task<AdminActionResult> RenameUserAsync(int actorId, string actorUsername, int targetUserId, string newUsername)
    {
        newUsername = newUsername.Trim();
        if (newUsername.Length is < 2 or > 32) return AdminActionResult.InvalidInput;

        var user = await _db.Users.FindAsync(targetUserId);
        if (user is null) return AdminActionResult.TargetNotFound;

        if (await _db.Users.AnyAsync(u => u.Username == newUsername && u.Id != targetUserId))
            return AdminActionResult.UsernameTaken;

        var oldUsername = user.Username;
        user.Username = newUsername;

        LogAdminAction(actorId, actorUsername, "RenameUser", targetUserId, oldUsername,
            $"Renamed from '{oldUsername}' to '{newUsername}'.");

        await _db.SaveChangesAsync();
        return AdminActionResult.Success;
    }

    public async Task<AdminActionResult> ResetPasswordAsync(int actorId, string actorUsername, int targetUserId, string newPassword)
    {
        if (newPassword.Length < 8) return AdminActionResult.InvalidInput;

        var user = await _db.Users.FindAsync(targetUserId);
        if (user is null) return AdminActionResult.TargetNotFound;

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

        // Critical: this user's E2EE private key is wrapped under a key
        // derived from their OLD password (see Client/Services/
        // E2eeService.cs). Overwriting PasswordHash alone would leave that
        // wrapped key permanently undecryptable (a clean but silent
        // "wrong password" failure, forever) instead of failing usefully.
        // Nulling the key material here routes the client's next unlock
        // into its existing "no key material yet" fallback
        // (GenerateAndUploadNewKeysAsync), which cleanly issues a fresh
        // keypair under the new password instead.
        user.PublicKey = null;
        user.WrappedPrivateKey = null;
        user.PrivateKeySalt = null;

        // DMs are genuinely, permanently unrecoverable once this happens -
        // a DM's key is HKDF(ECDH(sender_priv, recipient_pub)), derived
        // fresh from both participants' CURRENT public keys every time,
        // never stored. Once this user's keypair changes, that shared
        // secret can never be reproduced by anyone again (not even the
        // other participant, who still has their original key but not
        // this user's original one) - so every DM this user was ever a
        // party to becomes silent garbage for both sides forever. Deleting
        // them outright (rather than leaving unreadable rows behind) is
        // the same cleanup UsersController.DeleteMyAccount already does
        // for a deleted account, applied here for the same underlying
        // reason - the ciphertext is unrecoverable either way.
        //
        // Channel messages hit the same fate for the same reason - each
        // recipient's copy of a message's one-time key (see
        // MessageRecipientKey) is wrapped via ECDH against THIS user's
        // identity keypair too, so it stops being derivable the moment that
        // keypair changes. Unlike DMs, though, only this user's own wrapped
        // copies are affected - every other recipient's independent copy of
        // the same message is untouched, and this user's client
        // transparently gets working copies of every message sent AFTER
        // they have a new keypair (senders always wrap fresh against
        // whatever public key is on file at send time) - only pre-reset
        // channel history is lost for this user alone, not the message
        // itself.
        var dmIds = await _db.DirectMessages
            .Where(dm => dm.SenderId == targetUserId || dm.RecipientId == targetUserId)
            .Select(dm => dm.Id)
            .ToListAsync();
        _db.DirectMessageReactions.RemoveRange(_db.DirectMessageReactions.Where(r => dmIds.Contains(r.DirectMessageId)));
        _db.DirectMessages.RemoveRange(_db.DirectMessages.Where(dm => dmIds.Contains(dm.Id)));
        _db.MessageRecipientKeys.RemoveRange(_db.MessageRecipientKeys.Where(k => k.UserId == targetUserId));

        // Force re-login everywhere - same loop AuthController's logout-
        // everywhere endpoint uses.
        var activeTokens = await _db.RefreshTokens
            .Where(r => r.UserId == targetUserId && r.RevokedAt == null)
            .ToListAsync();
        foreach (var token in activeTokens) token.RevokedAt = DateTime.UtcNow;

        LogAdminAction(actorId, actorUsername, "ResetPassword", targetUserId, user.Username,
            $"Password reset; E2EE key material cleared; {dmIds.Count} unrecoverable DM(s) deleted; all sessions revoked.");

        await _db.SaveChangesAsync();
        return AdminActionResult.Success;
    }

    // One-time backfill for whatever's still sitting on the old Railway
    // volume (see Program.cs's uploadsDir/DATA_DIR comment) from before
    // uploads moved into the StoredFiles table. Safe to call more than
    // once - already-imported files are skipped by FileName, so re-running
    // this after the volume is fully migrated (or with no volume at all,
    // locally) is just a no-op. Not exposed to regular users; triggered
    // once from the admin dashboard after deploying this change.
    public async Task<AdminUploadMigrationResult> MigrateUploadsFromDiskAsync()
    {
        if (!Directory.Exists(_uploadsPath.Path))
            return new AdminUploadMigrationResult(0, 0);

        var existingNames = await _db.StoredFiles.Select(f => f.FileName).ToListAsync();
        var existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        int imported = 0, skipped = 0;
        foreach (var path in Directory.EnumerateFiles(_uploadsPath.Path))
        {
            var fileName = Path.GetFileName(path);
            if (existing.Contains(fileName)) { skipped++; continue; }

            var bytes = await File.ReadAllBytesAsync(path);
            _db.StoredFiles.Add(new StoredFile
            {
                FileName = fileName,
                ContentType = UploadContentTypes.Resolve(Path.GetExtension(fileName)),
                Data = bytes,
                Size = bytes.Length
            });
            imported++;
        }

        await _db.SaveChangesAsync();
        return new AdminUploadMigrationResult(imported, skipped);
    }

    private void LogAdminAction(int actorId, string actorUsername, string action,
        int? targetId, string? targetUsername, string? details) =>
        _db.AdminAuditLogEntries.Add(new AdminAuditLogEntry
        {
            ActorUserId = actorId,
            ActorUsername = actorUsername,
            Action = action,
            TargetUserId = targetId,
            TargetUsername = targetUsername,
            Details = details
        });
}

// Response shapes live here (not Dtos/Dtos.cs) since AdminService is their
// only producer and AdminController their only consumer - matches
// ServersController's precedent of keeping single-consumer DTOs local
// rather than growing the shared file, just anchored to the service that
// actually builds them instead of the controller that passes them through.
public record AdminServerSummaryResponse(int Id, string Name, string? IconUrl, int OwnerId, string OwnerUsername, int MemberCount, int ChannelCount, DateTime CreatedAt);
public record AdminChannelResponse(int Id, string Name, string Type, int Position, int SlowModeSeconds, int? MessageCount);
public record AdminMemberResponse(int UserId, string Username, string Role, DateTime JoinedAt);
public record AdminServerDetailResponse(AdminServerSummaryResponse Server, List<AdminChannelResponse> Channels, List<AdminMemberResponse> Members);
public record AdminUserSearchResponse(int Id, string Username, DateTime CreatedAt, bool IsAdmin);
public record AdminUploadMigrationResult(int Imported, int Skipped);
