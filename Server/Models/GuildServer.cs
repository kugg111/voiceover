namespace Voiceover.Server.Models;

// Named "GuildServer" (Discord internally calls servers "guilds") to avoid
// any naming collision with ASP.NET Core's own "Server" concepts.
public class GuildServer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? IconUrl { get; set; }
    public int OwnerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Owner opt-in - lists this server in the discovery directory
    // (ServersController.Discover) instead of requiring an invite to join.
    public bool IsPublic { get; set; }
    // Shown in the discovery listing. Only meaningful when IsPublic - not
    // cleared if the server is later made private again, so re-enabling
    // discovery doesn't lose it.
    public string? Description { get; set; }

    public User? Owner { get; set; }
    public List<Channel> Channels { get; set; } = new();
    public List<Membership> Memberships { get; set; } = new();
    public List<Invite> Invites { get; set; } = new();
}
