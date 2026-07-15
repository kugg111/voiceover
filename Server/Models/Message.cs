namespace Voiceover.Server.Models;

public class Message
{
    public int Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public int ChannelId { get; set; }
    public int AuthorId { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }
    public string? AttachmentUrl { get; set; }
    public DateTime? PinnedAt { get; set; }

    // No FK - self-referencing FKs on a table that's already cascade-deleted
    // by channel/server deletion risk multiple-cascade-path errors in some
    // database families, and it's simpler to just store the id and resolve
    // it in application code (same no-FK convention this codebase already
    // uses for Friendship/BannedUser/etc.). A dangling value (the original
    // was since deleted) is resolved to a "message not found" placeholder
    // client-side rather than needing cleanup here.
    public int? ReplyToMessageId { get; set; }

    public Channel? Channel { get; set; }
    public User? Author { get; set; }
}
