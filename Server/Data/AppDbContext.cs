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
    public DbSet<MessageRecipientKey> MessageRecipientKeys => Set<MessageRecipientKey>();
    public DbSet<CallRecord> CallRecords => Set<CallRecord>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
    public DbSet<DirectMessageReaction> DirectMessageReactions => Set<DirectMessageReaction>();
    public DbSet<BannedUser> BannedUsers => Set<BannedUser>();
    public DbSet<ModerationLogEntry> ModerationLogEntries => Set<ModerationLogEntry>();
    public DbSet<Block> Blocks => Set<Block>();
    public DbSet<AdminAuditLogEntry> AdminAuditLogEntries => Set<AdminAuditLogEntry>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<TotpRecoveryCode> TotpRecoveryCodes => Set<TotpRecoveryCode>();
    public DbSet<Emoji> Emojis => Set<Emoji>();
    public DbSet<Category> Categories => Set<Category>();

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

        // Nullable FK, SetNull on delete - removing a Category uncategorizes
        // its channels rather than deleting them (see CategoriesController.Delete).
        modelBuilder.Entity<Channel>()
            .HasOne(c => c.Category)
            .WithMany(cat => cat.Channels)
            .HasForeignKey(c => c.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // Real, required FK - same as Channel/Emoji above, so it cascades
        // automatically on server deletion.
        modelBuilder.Entity<Category>()
            .HasOne(c => c.GuildServer)
            .WithMany(s => s.Categories)
            .HasForeignKey(c => c.GuildServerId);

        modelBuilder.Entity<Category>()
            .HasIndex(c => c.GuildServerId);

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

        // One wrapped copy of a message's one-time key per recipient - a real
        // FK (unlike MessageReaction below) since both rows are always
        // created/deleted together in application code, so letting Postgres
        // cascade-delete these when their Message is removed is simpler and
        // more reliable than remembering to clean them up by hand everywhere
        // a Message gets deleted (Delete/DeleteAllFromUser/data-only wipe).
        modelBuilder.Entity<MessageRecipientKey>()
            .HasOne<Message>()
            .WithMany()
            .HasForeignKey(k => k.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MessageRecipientKey>()
            .HasIndex(k => new { k.MessageId, k.UserId })
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

        // No FK/cascade configured - same deliberate pattern as
        // MessageReaction/DirectMessage above (see BannedUser/
        // ModerationLogEntry's own class comments for why).
        modelBuilder.Entity<BannedUser>()
            .HasIndex(b => new { b.GuildServerId, b.UserId })
            .IsUnique();

        // ServersController.GetModerationLog always orders newest-first for
        // one server at a time.
        modelBuilder.Entity<ModerationLogEntry>()
            .HasIndex(m => new { m.GuildServerId, m.CreatedAt });

        // Unique so blocking the same user twice is a no-op at the DB level
        // rather than piling up duplicate rows. Directional (unlike
        // Friendship) - blocking isn't symmetric, so no reverse-pair index
        // is needed; every check queries BOTH directions explicitly instead
        // (see FriendsController.SendRequest/ChatHub.SendDirectMessage).
        modelBuilder.Entity<Block>()
            .HasIndex(b => new { b.BlockerId, b.BlockedId })
            .IsUnique();

        // AdminController's dashboard lists recent admin actions newest-first
        // (mirrors ModerationLogEntry's own index, minus GuildServerId since
        // this log is global, not per-server).
        modelBuilder.Entity<AdminAuditLogEntry>()
            .HasIndex(a => a.CreatedAt);

        // FileName (the GUID+extension UploadController generates) is the
        // natural key here - every AvatarUrl/IconUrl/AttachmentUrl column
        // already stores "/uploads/{FileName}", so looking rows up by it
        // directly avoids needing a separate surrogate id anywhere.
        modelBuilder.Entity<StoredFile>()
            .HasKey(f => f.FileName);

        // ServersController.Discover filters on this directly.
        modelBuilder.Entity<GuildServer>()
            .HasIndex(s => s.IsPublic);

        // Same trigram substring-search precedent as User.Username above -
        // lets Discover's optional "q" filter use an index instead of a
        // full scan of every public server's name.
        modelBuilder.Entity<GuildServer>()
            .HasIndex(s => s.Name, "IX_GuildServers_Name_Trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        // Real, required FK - same as Channel above, so it cascades
        // automatically on server deletion (see ServerDeletionService's own
        // comment for the full list of what cascades for free this way).
        modelBuilder.Entity<Emoji>()
            .HasOne(em => em.GuildServer)
            .WithMany(s => s.Emojis)
            .HasForeignKey(em => em.GuildServerId);

        // EmojisController.GetEmojis filters by this on every server open.
        modelBuilder.Entity<Emoji>()
            .HasIndex(em => em.GuildServerId);

        // Duplicate names within one server would be confusing in the
        // picker/management page - Id (not Name) is the real identity used
        // by reaction tokens, so this is purely a UX guard, not a lookup key.
        modelBuilder.Entity<Emoji>()
            .HasIndex(em => new { em.GuildServerId, em.Name })
            .IsUnique();

        // Real FK + cascade (same pattern as RefreshToken above) - account-
        // owned security data with no reason to outlive the account.
        modelBuilder.Entity<TotpRecoveryCode>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId);

        modelBuilder.Entity<TotpRecoveryCode>()
            .HasIndex(c => c.UserId);
    }
}
