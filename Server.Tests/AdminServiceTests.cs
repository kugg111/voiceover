using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Voiceover.Server.Data;
using Voiceover.Server.Hubs;
using Voiceover.Server.Models;
using Voiceover.Server.Services;

namespace Server.Tests;

public class AdminServiceTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    // No Moq in this project (see PermissionServiceTests/
    // ServerDeletionServiceTests - every existing test constructs real
    // dependencies against the InMemory provider instead of mocking). A
    // real SignalR hub isn't available in a unit test, so this is a
    // minimal hand-rolled no-op standing in for IHubContext<ChatHub> -
    // AdminService only ever fires a best-effort broadcast after the DB
    // write already succeeded, so a no-op here doesn't hide anything the
    // tests below actually need to verify.
    private static AdminService CreateAdminService(AppDbContext db, string? uploadsDir = null) =>
        new(db, new ServerDeletionService(db), new NoOpHubContext(),
            new UploadsPathOptions(uploadsDir ?? Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid())));

    private sealed class NoOpHubContext : IHubContext<ChatHub>
    {
        public IHubClients Clients { get; } = new NoOpHubClients();
        public IGroupManager Groups { get; } = new NoOpGroupManager();
    }

    private sealed class NoOpHubClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new NoOpClientProxy();
        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class NoOpClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task IsAdminAsync_ReturnsTrue_ForAdminUser()
    {
        var db = CreateDb();
        db.Users.Add(new User { Id = 1, Username = "dev", IsAdmin = true });
        await db.SaveChangesAsync();

        Assert.True(await CreateAdminService(db).IsAdminAsync(1));
    }

    [Fact]
    public async Task IsAdminAsync_ReturnsFalse_ForNonAdminUser()
    {
        var db = CreateDb();
        db.Users.Add(new User { Id = 1, Username = "regular", IsAdmin = false });
        await db.SaveChangesAsync();

        Assert.False(await CreateAdminService(db).IsAdminAsync(1));
    }

    [Fact]
    public async Task IsAdminAsync_ReturnsFalse_ForNonexistentUser()
    {
        var db = CreateDb();

        Assert.False(await CreateAdminService(db).IsAdminAsync(999));
    }

    [Fact]
    public async Task GetServersAsync_ReturnsCorrectMemberAndChannelCounts()
    {
        var db = CreateDb();
        var owner = new User { Username = "owner" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var server = new GuildServer { Name = "Test Server", OwnerId = owner.Id };
        db.GuildServers.Add(server);
        await db.SaveChangesAsync();

        db.Memberships.Add(new Membership { UserId = owner.Id, GuildServerId = server.Id, Role = MemberRole.Owner });
        db.Memberships.Add(new Membership { UserId = 2, GuildServerId = server.Id, Role = MemberRole.Member });
        db.Channels.Add(new Channel { Name = "general", GuildServerId = server.Id, Type = ChannelType.Text });
        db.Channels.Add(new Channel { Name = "voice", GuildServerId = server.Id, Type = ChannelType.Voice });
        db.Channels.Add(new Channel { Name = "random", GuildServerId = server.Id, Type = ChannelType.Text });
        await db.SaveChangesAsync();

        var result = await CreateAdminService(db).GetServersAsync(null, null);

        var summary = Assert.Single(result);
        Assert.Equal(2, summary.MemberCount);
        Assert.Equal(3, summary.ChannelCount);
        Assert.Equal("owner", summary.OwnerUsername);
    }

    [Fact]
    public async Task GetServerDetailAsync_ReturnsNull_ForNonexistentServer()
    {
        var db = CreateDb();

        Assert.Null(await CreateAdminService(db).GetServerDetailAsync(999));
    }

    [Fact]
    public async Task GetServerDetailAsync_ComputesMessageCountsPerTextChannel_AndNullForVoiceChannels()
    {
        var db = CreateDb();
        var owner = new User { Username = "owner" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var server = new GuildServer { Name = "Test Server", OwnerId = owner.Id };
        db.GuildServers.Add(server);
        await db.SaveChangesAsync();

        var textChannel = new Channel { Name = "general", GuildServerId = server.Id, Type = ChannelType.Text };
        var voiceChannel = new Channel { Name = "voice", GuildServerId = server.Id, Type = ChannelType.Voice };
        db.Channels.AddRange(textChannel, voiceChannel);
        await db.SaveChangesAsync();

        db.Messages.Add(new Message { Content = "a", ChannelId = textChannel.Id, AuthorId = owner.Id });
        db.Messages.Add(new Message { Content = "b", ChannelId = textChannel.Id, AuthorId = owner.Id });
        db.Messages.Add(new Message { Content = "c", ChannelId = textChannel.Id, AuthorId = owner.Id });
        await db.SaveChangesAsync();

        var detail = await CreateAdminService(db).GetServerDetailAsync(server.Id);

        Assert.NotNull(detail);
        var textResult = Assert.Single(detail!.Channels, c => c.Id == textChannel.Id);
        var voiceResult = Assert.Single(detail.Channels, c => c.Id == voiceChannel.Id);
        Assert.Equal(3, textResult.MessageCount);
        Assert.Null(voiceResult.MessageCount);
    }

    [Fact]
    public async Task GetUsersAsync_NoFilter_ReturnsAllUsers_PaginatedAndOrderedByUsername()
    {
        var db = CreateDb();
        db.Users.Add(new User { Username = "charlie" });
        db.Users.Add(new User { Username = "alice" });
        db.Users.Add(new User { Username = "bob" });
        await db.SaveChangesAsync();

        var page1 = await CreateAdminService(db).GetUsersAsync(null, take: 2, skip: 0);
        var page2 = await CreateAdminService(db).GetUsersAsync(null, take: 2, skip: 2);

        Assert.Equal(new[] { "alice", "bob" }, page1.Select(u => u.Username));
        Assert.Equal(new[] { "charlie" }, page2.Select(u => u.Username));
    }

    [Fact]
    public async Task GetUsersAsync_WithFilter_OnlyReturnsMatchingUsers_EvenForASingleCharacter()
    {
        var db = CreateDb();
        db.Users.Add(new User { Username = "alice" });
        db.Users.Add(new User { Username = "bob" });
        await db.SaveChangesAsync();

        // No minimum filter length - unlike UsersController.Search, this is
        // a browse-with-optional-filter admin page, not an autocomplete
        // firing on every keystroke, and pagination already bounds result
        // size regardless of filter breadth.
        var result = await CreateAdminService(db).GetUsersAsync("a", take: 20, skip: 0);

        Assert.Equal(new[] { "alice" }, result.Select(u => u.Username));
    }

    [Fact]
    public async Task RenameUserAsync_Success_UpdatesUsername_AndWritesAuditLog()
    {
        var db = CreateDb();
        var user = new User { Username = "oldname" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await CreateAdminService(db).RenameUserAsync(1, "admin", user.Id, "newname");

        Assert.Equal(AdminActionResult.Success, result);
        Assert.Equal("newname", (await db.Users.FindAsync(user.Id))!.Username);
        var log = Assert.Single(db.AdminAuditLogEntries);
        Assert.Equal("RenameUser", log.Action);
        Assert.Equal(user.Id, log.TargetUserId);
        Assert.Equal("admin", log.ActorUsername);
    }

    [Fact]
    public async Task RenameUserAsync_ReturnsUsernameTaken_WhenNewNameAlreadyExists()
    {
        var db = CreateDb();
        db.Users.Add(new User { Username = "taken" });
        var target = new User { Username = "target" };
        db.Users.Add(target);
        await db.SaveChangesAsync();

        var result = await CreateAdminService(db).RenameUserAsync(1, "admin", target.Id, "taken");

        Assert.Equal(AdminActionResult.UsernameTaken, result);
    }

    [Fact]
    public async Task RenameUserAsync_ReturnsTargetNotFound_ForMissingUser()
    {
        var db = CreateDb();

        var result = await CreateAdminService(db).RenameUserAsync(1, "admin", 999, "newname");

        Assert.Equal(AdminActionResult.TargetNotFound, result);
    }

    [Fact]
    public async Task ResetPasswordAsync_ChangesPasswordHash_AndNullsE2eeKeyMaterial()
    {
        var db = CreateDb();
        var user = new User
        {
            Username = "target",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("oldpassword"),
            PublicKey = "pubkey",
            WrappedPrivateKey = "wrapped",
            PrivateKeySalt = "salt"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await CreateAdminService(db).ResetPasswordAsync(1, "admin", user.Id, "newpassword123");

        Assert.Equal(AdminActionResult.Success, result);
        var updated = await db.Users.FindAsync(user.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify("newpassword123", updated!.PasswordHash));
        Assert.Null(updated.PublicKey);
        Assert.Null(updated.WrappedPrivateKey);
        Assert.Null(updated.PrivateKeySalt);
    }

    [Fact]
    public async Task ResetPasswordAsync_RevokesAllActiveRefreshTokens_ButLeavesAlreadyRevokedAlone()
    {
        var db = CreateDb();
        var user = new User { Username = "target", PasswordHash = "x" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var alreadyRevokedAt = DateTime.UtcNow.AddDays(-1);
        var active1 = new RefreshToken { UserId = user.Id, TokenHash = "h1", ExpiresAt = DateTime.UtcNow.AddDays(30) };
        var active2 = new RefreshToken { UserId = user.Id, TokenHash = "h2", ExpiresAt = DateTime.UtcNow.AddDays(30) };
        var preRevoked = new RefreshToken { UserId = user.Id, TokenHash = "h3", ExpiresAt = DateTime.UtcNow.AddDays(30), RevokedAt = alreadyRevokedAt };
        db.RefreshTokens.AddRange(active1, active2, preRevoked);
        await db.SaveChangesAsync();

        await CreateAdminService(db).ResetPasswordAsync(1, "admin", user.Id, "newpassword123");

        Assert.NotNull((await db.RefreshTokens.FindAsync(active1.Id))!.RevokedAt);
        Assert.NotNull((await db.RefreshTokens.FindAsync(active2.Id))!.RevokedAt);
        Assert.Equal(alreadyRevokedAt, (await db.RefreshTokens.FindAsync(preRevoked.Id))!.RevokedAt);
    }

    [Fact]
    public async Task ResetPasswordAsync_WritesAdminAuditLogEntry_WithActorAndTargetInfo()
    {
        var db = CreateDb();
        var user = new User { Username = "target", PasswordHash = "x" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        await CreateAdminService(db).ResetPasswordAsync(1, "admin", user.Id, "newpassword123");

        var log = Assert.Single(db.AdminAuditLogEntries);
        Assert.Equal("ResetPassword", log.Action);
        Assert.Equal(1, log.ActorUserId);
        Assert.Equal("admin", log.ActorUsername);
        Assert.Equal(user.Id, log.TargetUserId);
        Assert.Equal("target", log.TargetUsername);
    }

    [Fact]
    public async Task ResetPasswordAsync_ReturnsInvalidInput_ForShortPassword()
    {
        var db = CreateDb();
        var user = new User { Username = "target", PasswordHash = "x" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await CreateAdminService(db).ResetPasswordAsync(1, "admin", user.Id, "short");

        Assert.Equal(AdminActionResult.InvalidInput, result);
    }

    [Fact]
    public async Task ResetPasswordAsync_DeletesDirectMessagesInvolvingTheUser_AndTheirReactions_ButLeavesOtherDMsAlone()
    {
        var db = CreateDb();
        var target = new User { Username = "target", PasswordHash = "x" };
        var other = new User { Username = "other", PasswordHash = "x" };
        var bystander1 = new User { Username = "bystander1", PasswordHash = "x" };
        var bystander2 = new User { Username = "bystander2", PasswordHash = "x" };
        db.Users.AddRange(target, other, bystander1, bystander2);
        await db.SaveChangesAsync();

        // Both directions of a conversation involving the target - both
        // must go, since the shared secret for this whole conversation is
        // gone the moment target's keypair changes, regardless of who sent
        // which message.
        var sentByTarget = new DirectMessage { SenderId = target.Id, RecipientId = other.Id, Content = "hi" };
        var sentToTarget = new DirectMessage { SenderId = other.Id, RecipientId = target.Id, Content = "hey" };
        // Unrelated conversation - target isn't a party to this one at all.
        var unrelated = new DirectMessage { SenderId = bystander1.Id, RecipientId = bystander2.Id, Content = "unrelated" };
        db.DirectMessages.AddRange(sentByTarget, sentToTarget, unrelated);
        await db.SaveChangesAsync();

        db.DirectMessageReactions.Add(new DirectMessageReaction { DirectMessageId = sentByTarget.Id, UserId = other.Id, Emoji = "👍" });
        db.DirectMessageReactions.Add(new DirectMessageReaction { DirectMessageId = unrelated.Id, UserId = bystander2.Id, Emoji = "👍" });
        await db.SaveChangesAsync();

        await CreateAdminService(db).ResetPasswordAsync(1, "admin", target.Id, "newpassword123");

        Assert.False(await db.DirectMessages.AnyAsync(dm => dm.Id == sentByTarget.Id));
        Assert.False(await db.DirectMessages.AnyAsync(dm => dm.Id == sentToTarget.Id));
        Assert.False(await db.DirectMessageReactions.AnyAsync(r => r.DirectMessageId == sentByTarget.Id));
        Assert.True(await db.DirectMessages.AnyAsync(dm => dm.Id == unrelated.Id));
        Assert.True(await db.DirectMessageReactions.AnyAsync(r => r.DirectMessageId == unrelated.Id));
    }

    [Fact]
    public async Task ResetPasswordAsync_DoesNotTouchChannelMessages()
    {
        // Channel messages use a per-server key wrapped individually per
        // member (ServerMemberKey), not derived from the target's own
        // keypair like DMs are - resetting the target's password doesn't
        // affect any OTHER member's copy of that key, and the target's own
        // client already self-heals via the existing RequestServerKey
        // flow once they have a new keypair. So unlike DMs, these must
        // never be deleted.
        var db = CreateDb();
        var target = new User { Username = "target", PasswordHash = "x" };
        db.Users.Add(target);
        await db.SaveChangesAsync();

        var server = new GuildServer { Name = "Test Server", OwnerId = target.Id };
        db.GuildServers.Add(server);
        await db.SaveChangesAsync();

        var channel = new Channel { Name = "general", GuildServerId = server.Id, Type = ChannelType.Text };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var message = new Message { Content = "hi", ChannelId = channel.Id, AuthorId = target.Id };
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        await CreateAdminService(db).ResetPasswordAsync(1, "admin", target.Id, "newpassword123");

        Assert.True(await db.Messages.AnyAsync(m => m.Id == message.Id));
    }

    [Fact]
    public async Task DeleteServerAsync_RemovesServerAndCascadesCleanup_AndWritesAuditLog()
    {
        var db = CreateDb();
        var owner = new User { Username = "owner" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var server = new GuildServer { Name = "Doomed Server", OwnerId = owner.Id };
        db.GuildServers.Add(server);
        await db.SaveChangesAsync();

        var channel = new Channel { Name = "general", GuildServerId = server.Id };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var message = new Message { Content = "hi", ChannelId = channel.Id, AuthorId = owner.Id };
        db.Messages.Add(message);
        db.Memberships.Add(new Membership { UserId = owner.Id, GuildServerId = server.Id, Role = MemberRole.Owner });
        await db.SaveChangesAsync();

        db.MessageReactions.Add(new MessageReaction { MessageId = message.Id, UserId = owner.Id, Emoji = "👍" });
        await db.SaveChangesAsync();

        var result = await CreateAdminService(db).DeleteServerAsync(1, "admin", server.Id);

        Assert.Equal(AdminActionResult.Success, result);
        Assert.False(await db.GuildServers.AnyAsync(s => s.Id == server.Id));
        Assert.False(await db.Channels.AnyAsync());
        Assert.False(await db.Messages.AnyAsync());
        Assert.False(await db.MessageReactions.AnyAsync());
        Assert.False(await db.Memberships.AnyAsync());

        var log = Assert.Single(db.AdminAuditLogEntries);
        Assert.Equal("DeleteServer", log.Action);
        Assert.Equal(1, log.ActorUserId);
        Assert.Contains("Doomed Server", log.Details);
    }

    [Fact]
    public async Task DeleteServerAsync_ReturnsTargetNotFound_ForMissingServer()
    {
        var db = CreateDb();

        var result = await CreateAdminService(db).DeleteServerAsync(1, "admin", 999);

        Assert.Equal(AdminActionResult.TargetNotFound, result);
    }

    [Fact]
    public async Task MigrateUploadsFromDiskAsync_ReturnsZero_WhenDirectoryDoesNotExist()
    {
        var db = CreateDb();

        var result = await CreateAdminService(db).MigrateUploadsFromDiskAsync();

        Assert.Equal(0, result.Imported);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public async Task MigrateUploadsFromDiskAsync_ImportsNewFiles_AndSkipsAlreadyPresentOnes()
    {
        var db = CreateDb();
        db.StoredFiles.Add(new StoredFile { FileName = "already-imported.png", ContentType = "image/png", Data = [1, 2, 3], Size = 3 });
        await db.SaveChangesAsync();

        var dir = Path.Combine(Path.GetTempPath(), "uploads-migration-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "already-imported.png"), [9, 9, 9]);
            var newFileBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
            await File.WriteAllBytesAsync(Path.Combine(dir, "brand-new.png"), newFileBytes);

            var result = await CreateAdminService(db, dir).MigrateUploadsFromDiskAsync();

            Assert.Equal(1, result.Imported);
            Assert.Equal(1, result.Skipped);

            // The pre-existing DB row must win - not overwritten by the file on disk.
            var untouched = await db.StoredFiles.FindAsync("already-imported.png");
            Assert.Equal(new byte[] { 1, 2, 3 }, untouched!.Data);

            var imported = await db.StoredFiles.FindAsync("brand-new.png");
            Assert.NotNull(imported);
            Assert.Equal(newFileBytes, imported!.Data);
            Assert.Equal("image/png", imported.ContentType);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
