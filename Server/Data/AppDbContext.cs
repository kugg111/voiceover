using Voiceover.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<GuildServer> GuildServers => Set<GuildServer>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<DirectMessage> DirectMessages => Set<DirectMessage>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ServerMemberKey> ServerMemberKeys => Set<ServerMemberKey>();
    public DbSet<CallRecord> CallRecords => Set<CallRecord>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
    public DbSet<DirectMessageReaction> DirectMessageReactions => Set<DirectMessageReaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Needed for the trigram (substring-search) index below.
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        // UsersController.Search does Username.Contains(...) (a leading-
        // wildcard ILIKE) for friend/DM search - the unique btree index
        // above can't serve that at all, so this is a second, separate
        // index on the same column using Postgres's trigram extension,
        // which the query planner picks for substring matches. The name
        // has to be passed into HasIndex itself (not chained on after) -
        // otherwise EF Core treats a second HasIndex call on the same
        // property as reconfiguring the SAME index rather than creating a
        // new one, which would silently replace the unique constraint
        // above with a (invalid) "unique GIN" index instead of adding a
        // second index.
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username, "IX_Users_Username_Trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        modelBuilder.Entity<Membership>()
            .HasIndex(m => new { m.UserId, m.GuildServerId })
            .IsUnique();

        modelBuilder.Entity<Membership>()
            .HasOne(m => m.User)
            .WithMany(u => u.Memberships)
            .HasForeignKey(m => m.UserId);

        modelBuilder.Entity<Membership>()
            .HasOne(m => m.GuildServer)
            .WithMany(s => s.Memberships)
            .HasForeignKey(m => m.GuildServerId);

        modelBuilder.Entity<Channel>()
            .HasOne(c => c.GuildServer)
            .WithMany(s => s.Channels)
            .HasForeignKey(c => c.GuildServerId);

        modelBuilder.Entity<Message>()
            .HasOne(m => m.Channel)
            .WithMany(c => c.Messages)
            .HasForeignKey(m => m.ChannelId);

        modelBuilder.Entity<Message>()
            .HasOne(m => m.Author)
            .WithMany(u => u.Messages)
            .HasForeignKey(m => m.AuthorId);

        // MessagesController.GetHistory filters by ChannelId and orders by
        // SentAt on every load - EF's FK convention already indexes
        // ChannelId alone, but a composite index lets Postgres satisfy both
        // the filter and the sort from the index directly instead of
        // sorting the matched rows separately once the channel's history
        // grows large.
        modelBuilder.Entity<Message>()
            .HasIndex(m => new { m.ChannelId, m.SentAt });

        modelBuilder.Entity<Invite>()
            .HasIndex(i => i.Code)
            .IsUnique();

        modelBuilder.Entity<Invite>()
            .HasOne(i => i.GuildServer)
            .WithMany(s => s.Invites)
            .HasForeignKey(i => i.GuildServerId);

        // DirectMessage doesn't need a FK-backed navigation on User (would create
        // two conflicting relationships); look up participants by id instead.
        modelBuilder.Entity<DirectMessage>()
            .HasIndex(dm => new { dm.SenderId, dm.RecipientId, dm.SentAt });

        // The composite index above leads with SenderId, so it doesn't serve
        // "messages sent TO me" (RecipientId == X) on its own - every DM
        // query filters SenderId == X OR RecipientId == X
        // (DirectMessagesController.GetConversations/GetHistory), and
        // without this, that side of the OR falls back to a full table
        // scan. Postgres combines the two single-column indexes with a
        // bitmap OR for exactly this kind of query.
        modelBuilder.Entity<DirectMessage>()
            .HasIndex(dm => dm.RecipientId);

        // Same reasoning as DirectMessage above - no FK navigation on User,
        // look participants up by id.
        modelBuilder.Entity<Friendship>()
            .HasIndex(f => new { f.RequesterId, f.AddresseeId })
            .IsUnique();

        // Same "OR across two columns needs both sides indexed" reasoning as
        // DirectMessage.RecipientId above - the composite unique index only
        // leads with RequesterId, but every friendship query filters
        // RequesterId == X OR AddresseeId == X (FriendsController,
        // ChatHub.BroadcastPresenceChangeAsync - the latter on every single
        // online/away/offline transition for every user).
        modelBuilder.Entity<Friendship>()
            .HasIndex(f => f.AddresseeId);

        // Unique so a hash collision (or a bug that generates the same token
        // twice) fails loudly at the DB level instead of silently letting
        // two sessions share one row. UserId indexed for the "revoke every
        // session for this user" logout-all query.
        modelBuilder.Entity<RefreshToken>()
            .HasIndex(r => r.TokenHash)
            .IsUnique();

        modelBuilder.Entity<RefreshToken>()
            .HasIndex(r => r.UserId);

        modelBuilder.Entity<RefreshToken>()
            .HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId);

        // One wrapped copy of the server key per member - GetMyServerKey/
        // SetServerKey both look up by this pair.
        modelBuilder.Entity<ServerMemberKey>()
            .HasIndex(k => new { k.GuildServerId, k.UserId })
            .IsUnique();

        // "Recent Calls" (CallsController.GetHistory) filters CallerId == X
        // OR CalleeId == X, ordered by EndedAt - same "both sides of an OR
        // need their own index" reasoning as DirectMessage/Friendship above.
        modelBuilder.Entity<CallRecord>()
            .HasIndex(c => new { c.CallerId, c.EndedAt });

        modelBuilder.Entity<CallRecord>()
            .HasIndex(c => new { c.CalleeId, c.EndedAt });

        // Unique so "react again with the same emoji removes it" (see
        // ChatHub.ToggleMessageReaction) can never end up with two rows for
        // the same (message, user, emoji) triple even under a race.
        modelBuilder.Entity<MessageReaction>()
            .HasIndex(r => new { r.MessageId, r.UserId, r.Emoji })
            .IsUnique();

        modelBuilder.Entity<DirectMessageReaction>()
            .HasIndex(r => new { r.DirectMessageId, r.UserId, r.Emoji })
            .IsUnique();
    }
}
