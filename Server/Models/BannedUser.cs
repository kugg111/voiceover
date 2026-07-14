namespace Voiceover.Server.Models;

// No FK navigation on GuildServer/User - matches MessageReaction/
// DirectMessage's deliberate no-FK pattern elsewhere in this codebase, so a
// server or user being deleted later doesn't need explicit ban cleanup to
// avoid a broken relationship (an orphaned ban row for an already-deleted
// server/user is simply never matched at join time again).
public class BannedUser
{
    public int Id { get; set; }
    public int GuildServerId { get; set; }
    public int UserId { get; set; }
    public int BannedByUserId { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
