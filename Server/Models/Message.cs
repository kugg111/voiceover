namespace DiscordClone.Server.Models;

public class Message
{
    public int Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public int ChannelId { get; set; }
    public int AuthorId { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }
    public string? AttachmentUrl { get; set; }

    public Channel? Channel { get; set; }
    public User? Author { get; set; }
}
