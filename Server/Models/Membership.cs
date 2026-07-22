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

    // Gates typing @everyone/@here as a "real" ping rather than inert text -
    // see MainWindow.SendCurrentMessage client-side, where this is actually
    // enforced. Message content is E2EE ciphertext (see Message.Content),
    // so the server itself can never inspect whether a given message
    // contains "@everyone" - this permission can only ever be a client-side
    // compose-time gate, same documented tradeoff as the existing @mention
    // highlighting/notification-title heuristics (see
    // MessageContentRenderer's own class comment).
    MentionEveryone = 16,

    // Promote/demote Member<->Moderator (ServersController.ChangeRole).
    // Deliberately does NOT also cover editing another Moderator's specific
    // permission bitmask (SetPermissions stays owner-only, see that
    // endpoint's own comment) - a freshly-promoted Moderator starts with
    // ServerPermission.All, so letting a ManageRoles-holder promote freely
    // is already a meaningful power; also letting them hand-tune a peer's
    // exact bits (including granting ManageRoles/All itself) is a
    // materially bigger escalation path this app isn't taking on.
    ManageRoles = 32,

    // Server branding/identity - rename, icon, discoverability
    // (ServersController.SetIcon/Rename/SetDiscoverable). These were
    // owner-only before this flag existed; matches real Discord's "Manage
    // Server" permission.
    ManageServer = 64,

    // View-only access to the ban list and moderation log
    // (ServersController.GetBans/GetModerationLog) without granting any
    // actual moderation power - previously bundled into "has any
    // permission at all" (any non-None bitmask, checked inline at each
    // call site before this flag existed).
    ViewAuditLog = 128,

    All = ManageChannels | KickMembers | ManageMessages | MuteMembers | MentionEveryone | ManageRoles | ManageServer | ViewAuditLog
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
