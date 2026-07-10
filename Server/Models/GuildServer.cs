namespace DiscordClone.Server.Models;

// Named "GuildServer" (Discord internally calls servers "guilds") to avoid
// any naming collision with ASP.NET Core's own "Server" concepts.
public class GuildServer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? IconUrl { get; set; }
    public int OwnerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User? Owner { get; set; }
    public List<Channel> Channels { get; set; } = new();
    public List<Membership> Memberships { get; set; } = new();
    public List<Invite> Invites { get; set; } = new();
}
