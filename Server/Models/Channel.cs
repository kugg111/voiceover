namespace Voiceover.Server.Models;

public enum ChannelType
{
    Text,
    Voice
}

public class Channel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ChannelType Type { get; set; } = ChannelType.Text;
    public int GuildServerId { get; set; }
    public int Position { get; set; }

    // 0 = off. Only enforced for MemberRole.Member senders (see
    // ChatHub.SendMessage/SlowModeLimiter) - Moderators/Owners are exempt,
    // the standard convention for this feature.
    public int SlowModeSeconds { get; set; }

    public GuildServer? GuildServer { get; set; }
    public List<Message> Messages { get; set; } = new();
}
