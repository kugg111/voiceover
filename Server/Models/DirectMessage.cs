namespace Voiceover.Server.Models;

public class DirectMessage
{
    public int Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public int SenderId { get; set; }
    public int RecipientId { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }
}
