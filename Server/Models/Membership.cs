namespace Voiceover.Server.Models;

public enum MemberRole
{
    Member,
    Moderator,
    Owner
}

// Granular powers a Moderator can be given individually - the Owner
// implicitly has all of these (see PermissionService.HasPermissionAsync),
// so this bitmask is only ever meaningful for MemberRole.Moderator rows.
// Stored as a plain int column on Membership rather than a separate table -
// one row per member already exists, and a member's permission set has no
// independent lifecycle worth a join.
[Flags]
public enum ServerPermission
{
    None = 0,
    ManageChannels = 1,
    KickMembers = 2,
    ManageMessages = 4,
    MuteMembers = 8,
    All = ManageChannels | KickMembers | ManageMessages | MuteMembers
}

public class Membership
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int GuildServerId { get; set; }
    public MemberRole Role { get; set; } = MemberRole.Member;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    // Only consulted for Moderator rows - defaults to All so every
    // Moderator promoted before this feature existed keeps full power
    // after the migration, rather than being silently locked out.
    public ServerPermission Permissions { get; set; } = ServerPermission.All;

    public User? User { get; set; }
    public GuildServer? GuildServer { get; set; }
}
