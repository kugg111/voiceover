namespace Voiceover.Server.Models;

// Denormalizes ActorUsername/TargetUsername (rather than a FK navigation)
// so the log entry stays readable even after the actor or target account
// is later deleted - same reasoning as BannedUser's lack of FKs. Action is
// a plain string (matches how every other enum crosses this app's wire,
// e.g. CallRecordResponse.Outcome) rather than a dedicated enum, since new
// action types are expected to be added over time without a migration.
public class ModerationLogEntry
{
    public int Id { get; set; }
    public int GuildServerId { get; set; }
    public int ActorUserId { get; set; }
    public string ActorUsername { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public int? TargetUserId { get; set; }
    public string? TargetUsername { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
