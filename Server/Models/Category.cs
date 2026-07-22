namespace Voiceover.Server.Models;

// A named group a channel can optionally belong to (see Channel.CategoryId) -
// purely organizational, no permission/behavior implications of its own.
// Deliberately has no "type" (text/voice) - a category can hold either, same
// as Discord's own model; the client groups its two separate Text/Voice
// sections by this same shared category list independently (see
// MainWindow.RefreshChannelsAsync).
public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int GuildServerId { get; set; }
    public int Position { get; set; }

    public GuildServer? GuildServer { get; set; }
    public List<Channel> Channels { get; set; } = new();
}
