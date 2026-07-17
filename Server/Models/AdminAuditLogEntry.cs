namespace Voiceover.Server.Models;

// Global counterpart to ModerationLogEntry (which is per-server) - records
// sensitive AdminController actions (password resets, username changes)
// that aren't scoped to any one GuildServer. Denormalizes
// ActorUsername/TargetUsername for the same reason ModerationLogEntry
// does: the log stays readable even after the actor or target account is
// later deleted/renamed.
public class AdminAuditLogEntry
{
    public int Id { get; set; }
    public int ActorUserId { get; set; }
    public string ActorUsername { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public int? TargetUserId { get; set; }
    public string? TargetUsername { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
