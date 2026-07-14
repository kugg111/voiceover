using Voiceover.Server.Data;
using Voiceover.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Services;

public class PermissionService
{
    private readonly AppDbContext _db;
    public PermissionService(AppDbContext db) => _db = db;

    public async Task<Membership?> GetMembershipAsync(int userId, int serverId)
        => await _db.Memberships.FirstOrDefaultAsync(m => m.UserId == userId && m.GuildServerId == serverId);

    public async Task<bool> IsMemberAsync(int userId, int serverId)
        => await _db.Memberships.AnyAsync(m => m.UserId == userId && m.GuildServerId == serverId);

    // Coarse "has any management power at all" check - still fine for
    // gates that don't care which specific power (e.g. viewing the
    // moderation log). Owner always qualifies; a Moderator only if they
    // have at least one permission bit set (an Owner could strip a
    // Moderator down to ServerPermission.None without demoting them).
    public async Task<bool> CanManageServerAsync(int userId, int serverId)
    {
        var membership = await GetMembershipAsync(userId, serverId);
        if (membership is null) return false;
        return membership.Role == MemberRole.Owner
            || (membership.Role == MemberRole.Moderator && membership.Permissions != ServerPermission.None);
    }

    public async Task<bool> IsOwnerAsync(int userId, int serverId)
    {
        var membership = await GetMembershipAsync(userId, serverId);
        return membership is not null && membership.Role == MemberRole.Owner;
    }

    // The granular check every specific moderation action should gate on -
    // Owner implicitly has every permission (never stored, computed here),
    // Member never has any, Moderator checks the stored bitmask.
    public async Task<bool> HasPermissionAsync(int userId, int serverId, ServerPermission permission)
    {
        var membership = await GetMembershipAsync(userId, serverId);
        if (membership is null) return false;
        if (membership.Role == MemberRole.Owner) return true;
        if (membership.Role != MemberRole.Moderator) return false;
        return (membership.Permissions & permission) == permission;
    }
}
