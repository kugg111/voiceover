using DiscordClone.Server.Data;
using DiscordClone.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace DiscordClone.Server.Services;

public class PermissionService
{
    private readonly AppDbContext _db;
    public PermissionService(AppDbContext db) => _db = db;

    public async Task<Membership?> GetMembershipAsync(int userId, int serverId)
        => await _db.Memberships.FirstOrDefaultAsync(m => m.UserId == userId && m.GuildServerId == serverId);

    public async Task<bool> IsMemberAsync(int userId, int serverId)
        => await _db.Memberships.AnyAsync(m => m.UserId == userId && m.GuildServerId == serverId);

    // Moderators and Owners can manage channels, kick members, delete messages, etc.
    public async Task<bool> CanManageServerAsync(int userId, int serverId)
    {
        var membership = await GetMembershipAsync(userId, serverId);
        return membership is not null && membership.Role is MemberRole.Owner or MemberRole.Moderator;
    }

    public async Task<bool> IsOwnerAsync(int userId, int serverId)
    {
        var membership = await GetMembershipAsync(userId, serverId);
        return membership is not null && membership.Role == MemberRole.Owner;
    }
}
