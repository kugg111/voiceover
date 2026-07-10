using DiscordClone.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace DiscordClone.Server.Data;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

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
    }
}
