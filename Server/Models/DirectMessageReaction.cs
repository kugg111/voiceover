namespace Voiceover.Server.Models;

// DM equivalent of MessageReaction - kept as its own table rather than a
// generic polymorphic "reactable" design, matching how DirectMessage/Message
// are already handled as separate parallel tables everywhere else in this
// codebase (separate Edit/Delete endpoints, separate DTOs, etc.).
public class DirectMessageReaction
{
    public int Id { get; set; }
    public int DirectMessageId { get; set; }
    public int UserId { get; set; }
    public string Emoji { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
