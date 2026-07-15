using Microsoft.EntityFrameworkCore;
using Voiceover.Server.Data;
using Voiceover.Server.Models;
using Voiceover.Server.Services;

namespace Server.Tests;

public class ServerDeletionServiceTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task QueueDeleteAsync_LeavesNoOrphanedRows_InAnyRelatedTable()
    {
        var db = CreateDb();

        var server = new GuildServer { Name = "Doomed Server", OwnerId = 1 };
        db.GuildServers.Add(server);
        await db.SaveChangesAsync();

        var channel = new Channel { Name = "general", GuildServerId = server.Id };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var message = new Message { Content = "hi", ChannelId = channel.Id, AuthorId = 1 };
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        db.MessageReactions.Add(new MessageReaction { MessageId = message.Id, UserId = 2, Emoji = "👍" });
        db.Memberships.Add(new Membership { UserId = 1, GuildServerId = server.Id, Role = MemberRole.Owner });
        db.ServerMemberKeys.Add(new ServerMemberKey { GuildServerId = server.Id, UserId = 1, WrappedByUserId = 1, WrappedKey = "key" });
        db.BannedUsers.Add(new BannedUser { GuildServerId = server.Id, UserId = 3, BannedByUserId = 1 });
        db.ModerationLogEntries.Add(new ModerationLogEntry { GuildServerId = server.Id, ActorUserId = 1, ActorUsername = "owner", Action = "ban" });
        await db.SaveChangesAsync();

        var deletion = new ServerDeletionService(db);
        await deletion.QueueDeleteAsync(server);
        await db.SaveChangesAsync();

        Assert.False(await db.GuildServers.AnyAsync());
        Assert.False(await db.Channels.AnyAsync());
        Assert.False(await db.Messages.AnyAsync());
        Assert.False(await db.MessageReactions.AnyAsync());
        Assert.False(await db.Memberships.AnyAsync());
        Assert.False(await db.ServerMemberKeys.AnyAsync());
        Assert.False(await db.BannedUsers.AnyAsync());
        Assert.False(await db.ModerationLogEntries.AnyAsync());
    }

    [Fact]
    public async Task QueueDeleteAsync_DoesNotTouchOtherServersData()
    {
        var db = CreateDb();

        var doomed = new GuildServer { Name = "Doomed Server", OwnerId = 1 };
        var survivor = new GuildServer { Name = "Survivor Server", OwnerId = 1 };
        db.GuildServers.AddRange(doomed, survivor);
        await db.SaveChangesAsync();

        var doomedChannel = new Channel { Name = "general", GuildServerId = doomed.Id };
        var survivorChannel = new Channel { Name = "general", GuildServerId = survivor.Id };
        db.Channels.AddRange(doomedChannel, survivorChannel);
        await db.SaveChangesAsync();

        var survivorMessage = new Message { Content = "still here", ChannelId = survivorChannel.Id, AuthorId = 1 };
        db.Messages.Add(survivorMessage);
        db.BannedUsers.Add(new BannedUser { GuildServerId = survivor.Id, UserId = 3, BannedByUserId = 1 });
        await db.SaveChangesAsync();

        var deletion = new ServerDeletionService(db);
        await deletion.QueueDeleteAsync(doomed);
        await db.SaveChangesAsync();

        Assert.True(await db.GuildServers.AnyAsync(s => s.Id == survivor.Id));
        Assert.True(await db.Channels.AnyAsync(c => c.Id == survivorChannel.Id));
        Assert.True(await db.Messages.AnyAsync(m => m.Id == survivorMessage.Id));
        Assert.True(await db.BannedUsers.AnyAsync(b => b.GuildServerId == survivor.Id));
    }
}
