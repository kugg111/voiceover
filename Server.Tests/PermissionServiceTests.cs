using Microsoft.EntityFrameworkCore;
using Voiceover.Server.Data;
using Voiceover.Server.Models;
using Voiceover.Server.Services;

namespace Server.Tests;

// Each test gets its own isolated in-memory database (a fresh Guid-named
// instance) rather than sharing one across the class, so tests can run in
// parallel/any order without one test's rows leaking into another's.
public class PermissionServiceTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<(AppDbContext Db, PermissionService Permissions)> SeedAsync(
        int serverId, int userId, MemberRole role, ServerPermission permissions = ServerPermission.All)
    {
        var db = CreateDb();
        db.Memberships.Add(new Membership
        {
            UserId = userId,
            GuildServerId = serverId,
            Role = role,
            Permissions = permissions
        });
        await db.SaveChangesAsync();
        return (db, new PermissionService(db));
    }

    [Fact]
    public async Task IsMemberAsync_ReturnsTrue_ForExistingMembership()
    {
        var (_, permissions) = await SeedAsync(serverId: 1, userId: 1, MemberRole.Member);

        Assert.True(await permissions.IsMemberAsync(1, 1));
    }

    [Fact]
    public async Task IsMemberAsync_ReturnsFalse_ForNonMember()
    {
        var (_, permissions) = await SeedAsync(serverId: 1, userId: 1, MemberRole.Member);

        Assert.False(await permissions.IsMemberAsync(999, 1));
    }

    [Fact]
    public async Task IsOwnerAsync_ReturnsTrue_OnlyForOwner()
    {
        var (db, permissions) = await SeedAsync(serverId: 1, userId: 1, MemberRole.Owner);
        db.Memberships.Add(new Membership { UserId = 2, GuildServerId = 1, Role = MemberRole.Moderator });
        await db.SaveChangesAsync();

        Assert.True(await permissions.IsOwnerAsync(1, 1));
        Assert.False(await permissions.IsOwnerAsync(2, 1));
    }

    [Theory]
    [InlineData(MemberRole.Owner, ServerPermission.All, true)]
    [InlineData(MemberRole.Moderator, ServerPermission.All, true)]
    [InlineData(MemberRole.Moderator, ServerPermission.None, false)] // an Owner can strip a Moderator down to no powers without demoting them
    [InlineData(MemberRole.Member, ServerPermission.All, false)] // Permissions is only ever consulted for Moderator rows
    public async Task CanManageServerAsync_MatchesExpectedCoarseRule(MemberRole role, ServerPermission permissions, bool expected)
    {
        var (_, permissionService) = await SeedAsync(serverId: 1, userId: 1, role, permissions);

        Assert.Equal(expected, await permissionService.CanManageServerAsync(1, 1));
    }

    [Fact]
    public async Task CanManageServerAsync_ReturnsFalse_ForNonMember()
    {
        var (_, permissions) = await SeedAsync(serverId: 1, userId: 1, MemberRole.Owner);

        Assert.False(await permissions.CanManageServerAsync(999, 1));
    }

    [Theory]
    [InlineData(ServerPermission.None)]
    [InlineData(ServerPermission.KickMembers)]
    [InlineData(ServerPermission.ManageMessages)]
    public async Task HasPermissionAsync_OwnerAlwaysHasEveryPermission_RegardlessOfStoredBitmask(ServerPermission storedOnOwnerRow)
    {
        // Owner's Permissions column is never actually read - it's
        // implicitly "All", computed rather than stored (see
        // PermissionService.HasPermissionAsync). This seeds an
        // intentionally-wrong stored value to prove that.
        var (_, permissions) = await SeedAsync(serverId: 1, userId: 1, MemberRole.Owner, storedOnOwnerRow);

        Assert.True(await permissions.HasPermissionAsync(1, 1, ServerPermission.ManageChannels));
        Assert.True(await permissions.HasPermissionAsync(1, 1, ServerPermission.KickMembers));
        Assert.True(await permissions.HasPermissionAsync(1, 1, ServerPermission.ManageMessages));
        Assert.True(await permissions.HasPermissionAsync(1, 1, ServerPermission.MuteMembers));
    }

    [Fact]
    public async Task HasPermissionAsync_Member_NeverHasAnyPermission()
    {
        var (_, permissions) = await SeedAsync(serverId: 1, userId: 1, MemberRole.Member, ServerPermission.All);

        Assert.False(await permissions.HasPermissionAsync(1, 1, ServerPermission.ManageChannels));
    }

    [Fact]
    public async Task HasPermissionAsync_Moderator_ChecksOnlyTheStoredBit()
    {
        var (_, permissions) = await SeedAsync(serverId: 1, userId: 1, MemberRole.Moderator,
            ServerPermission.KickMembers | ServerPermission.ManageMessages);

        Assert.True(await permissions.HasPermissionAsync(1, 1, ServerPermission.KickMembers));
        Assert.True(await permissions.HasPermissionAsync(1, 1, ServerPermission.ManageMessages));
        Assert.False(await permissions.HasPermissionAsync(1, 1, ServerPermission.ManageChannels));
        Assert.False(await permissions.HasPermissionAsync(1, 1, ServerPermission.MuteMembers));
    }

    [Fact]
    public async Task HasPermissionAsync_ReturnsFalse_ForNonMember()
    {
        var (_, permissions) = await SeedAsync(serverId: 1, userId: 1, MemberRole.Owner);

        Assert.False(await permissions.HasPermissionAsync(999, 1, ServerPermission.ManageChannels));
    }
}
