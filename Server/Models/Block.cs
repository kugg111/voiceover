namespace Voiceover.Server.Models;

// Directional - BlockerId blocked BlockedId. No FK navigation, same
// deliberate no-FK pattern as Friendship/BannedUser (see their own class
// comments) - a blocked user being deleted later doesn't need explicit
// cleanup, an orphaned row for an already-deleted user is simply never
// matched at query time again.
public class Block
{
    public int Id { get; set; }
    public int BlockerId { get; set; }
    public int BlockedId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
