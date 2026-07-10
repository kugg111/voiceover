namespace Voiceover.Server.Models;

public enum MemberRole
{
    Member,
    Moderator,
    Owner
}

public class Membership
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int GuildServerId { get; set; }
    public MemberRole Role { get; set; } = MemberRole.Member;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
    public GuildServer? GuildServer { get; set; }
}
