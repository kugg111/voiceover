namespace DiscordClone.Server.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Membership> Memberships { get; set; } = new();
    public List<Message> Messages { get; set; } = new();
}
