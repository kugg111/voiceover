namespace Voiceover.Server.Models;

// One row per (message, user, emoji) - reacting again with the same emoji
// removes it (toggle), so there's never more than one row for a given
// triple (enforced by a unique index, see AppDbContext). Emoji itself is
// never sensitive/E2EE content - just one of a small fixed picker set (see
// Client/Views/MainWindow.xaml.cs's reaction picker) - so unlike message
// content this needs no encryption.
public class MessageReaction
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public int UserId { get; set; }
    public string Emoji { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
