namespace Voiceover.Server.Models;

// A custom per-server emoji, uploaded once (via /api/upload, same as
// avatars/icons/attachments) and then usable as a message reaction by any
// member - see ChatHub.ToggleMessageReaction's "custom:{Id}" token format
// and Client/Services/CustomEmojiRegistry.cs for how the client resolves
// that token back to ImageUrl.
public class Emoji
{
    public int Id { get; set; }
    public int GuildServerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public GuildServer? GuildServer { get; set; }
}
