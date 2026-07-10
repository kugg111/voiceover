namespace DiscordClone.Server.Models;

public class Invite
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty; // short shareable code, e.g. "aB3xQ9"
    public int GuildServerId { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }        // null = never expires
    public int? MaxUses { get; set; }                // null = unlimited
    public int UseCount { get; set; }

    public GuildServer? GuildServer { get; set; }

    public bool IsValid()
    {
        if (ExpiresAt.HasValue && ExpiresAt.Value < DateTime.UtcNow) return false;
        if (MaxUses.HasValue && UseCount >= MaxUses.Value) return false;
        return true;
    }
}
