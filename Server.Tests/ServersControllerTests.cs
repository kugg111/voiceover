using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Voiceover.Server.Controllers;
using Voiceover.Server.Data;
using Voiceover.Server.Hubs;
using Voiceover.Server.Models;
using Voiceover.Server.Services;

namespace Server.Tests;

public class ServersControllerTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    // Same no-mocking-framework convention as AdminServiceTests - real
    // service instances wired to the InMemory DbContext, plus a minimal
    // hand-rolled no-op standing in for IHubContext<ChatHub> (this
    // controller only ever fires best-effort broadcasts after the DB write
    // already succeeded).
    private static ServersController CreateController(AppDbContext db, int currentUserId, string currentUsername = "caller")
    {
        var controller = new ServersController(
            db,
            new PermissionService(db),
            new PresenceService(),
            new ModerationLogService(db, new NoOpHubContext()),
            new ServerDeletionService(db),
            new NoOpHubContext());

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString()),
            new Claim(ClaimTypes.Name, currentUsername)
        }, "TestAuth");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return controller;
    }

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
    public async Task Join_RejectsNonPublicServer_ForNonMember()
    {
        var db = CreateDb();
        var owner = new User { Username = "owner" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var server = new GuildServer { Name = "Private Server", OwnerId = owner.Id, IsPublic = false };
        db.GuildServers.Add(server);
        await db.SaveChangesAsync();

        var joiner = new User { Username = "joiner" };
        db.Users.Add(joiner);
        await db.SaveChangesAsync();

        var result = await CreateController(db, joiner.Id).Join(server.Id);

        Assert.IsType<ForbidResult>(result);
        Assert.False(await db.Memberships.AnyAsync(m => m.UserId == joiner.Id && m.GuildServerId == server.Id));
    }

    [Fact]
    public async Task Join_Succeeds_ForPublicServer()
    {
        var db = CreateDb();
        var owner = new User { Username = "owner" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var server = new GuildServer { Name = "Public Server", OwnerId = owner.Id, IsPublic = true };
        db.GuildServers.Add(server);
        await db.SaveChangesAsync();

        var joiner = new User { Username = "joiner" };
        db.Users.Add(joiner);
        await db.SaveChangesAsync();

        var result = await CreateController(db, joiner.Id).Join(server.Id);

        Assert.IsType<OkResult>(result);
        Assert.True(await db.Memberships.AnyAsync(m => m.UserId == joiner.Id && m.GuildServerId == server.Id));
    }

    [Fact]
    public async Task Discover_ReturnsOnlyPublicServers_AndRespectsPagination()
    {
        var db = CreateDb();
        var owner = new User { Username = "owner" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        db.GuildServers.Add(new GuildServer { Name = "Public A", OwnerId = owner.Id, IsPublic = true });
        db.GuildServers.Add(new GuildServer { Name = "Public B", OwnerId = owner.Id, IsPublic = true });
        db.GuildServers.Add(new GuildServer { Name = "Private C", OwnerId = owner.Id, IsPublic = false });
        await db.SaveChangesAsync();

        var result = await CreateController(db, owner.Id).Discover(q: null, take: 1, skip: 0);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var servers = Assert.IsAssignableFrom<List<DiscoverServerResponse>>(ok.Value);
        var single = Assert.Single(servers);
        Assert.Equal("Public A", single.Name);
    }

    [Fact]
    public async Task SetDiscoverable_RequiresOwnership()
    {
        var db = CreateDb();
        var owner = new User { Username = "owner" };
        var notOwner = new User { Username = "notowner" };
        db.Users.AddRange(owner, notOwner);
        await db.SaveChangesAsync();

        var server = new GuildServer { Name = "Test Server", OwnerId = owner.Id };
        db.GuildServers.Add(server);
        await db.SaveChangesAsync();

        db.Memberships.Add(new Membership { UserId = notOwner.Id, GuildServerId = server.Id, Role = MemberRole.Member });
        await db.SaveChangesAsync();

        var result = await CreateController(db, notOwner.Id).SetDiscoverable(server.Id, new SetDiscoverableRequest(true, "desc"));

        Assert.IsType<ForbidResult>(result.Result);
        Assert.False((await db.GuildServers.FindAsync(server.Id))!.IsPublic);
    }

    [Fact]
    public async Task SetDiscoverable_Succeeds_ForOwner()
    {
        var db = CreateDb();
        var owner = new User { Username = "owner" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var server = new GuildServer { Name = "Test Server", OwnerId = owner.Id };
        db.GuildServers.Add(server);
        await db.SaveChangesAsync();

        db.Memberships.Add(new Membership { UserId = owner.Id, GuildServerId = server.Id, Role = MemberRole.Owner });
        await db.SaveChangesAsync();

        var result = await CreateController(db, owner.Id).SetDiscoverable(server.Id, new SetDiscoverableRequest(true, "Come join us"));

        Assert.IsType<OkObjectResult>(result.Result);
        var updated = await db.GuildServers.FindAsync(server.Id);
        Assert.True(updated!.IsPublic);
        Assert.Equal("Come join us", updated.Description);
    }
}
