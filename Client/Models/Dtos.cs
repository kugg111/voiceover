namespace Voiceover.Client.Models;

public record AuthResponse(string Token, DateTime ExpiresAtUtc, string RefreshToken, int UserId, string Username, string? AvatarUrl = null, string? CustomStatus = null, bool TwoFactorEnabled = false);
// Flat, not nested under Auth - matches the server's LoginResponse shape
// (see Server/Dtos/Dtos.cs) so a client built before 2FA existed keeps
// working against a current server too, and vice versa if this client
// ever talks to an older server (it'll just see RequiresTwoFactor=false
// and the token fields already populated at the top level either way).
public record LoginResponse(
    bool RequiresTwoFactor,
    string? ChallengeToken,
    string? Token = null,
    DateTime? ExpiresAtUtc = null,
    string? RefreshToken = null,
    int? UserId = null,
    string? Username = null,
    string? AvatarUrl = null,
    string? CustomStatus = null,
    bool TwoFactorEnabled = false);
public record TotpLoginRequest(string ChallengeToken, string? Code, string? RecoveryCode);
public record TotpSetupResponse(string Secret, string QrCodePngBase64);
public record TotpConfirmRequest(string Code);
public record TotpConfirmResponse(List<string> RecoveryCodes);
public record TotpDisableRequest(string Password);
public record GuildServerResponse(int Id, string Name, string? IconUrl, int OwnerId, bool IsPublic = false, string? Description = null);
public record DiscoverServerResponse(int Id, string Name, string? IconUrl, string? Description, int MemberCount);
public record SetDiscoverableRequest(bool IsPublic, string? Description);
public record SetIconRequest(string Url);
public record RenameServerRequest(string Name);
public record ChannelResponse(int Id, string Name, string Type, int GuildServerId, int Position, int SlowModeSeconds = 0, int? CategoryId = null);
public record RenameChannelRequest(string Name);
public record ReorderChannelsRequest(List<int> OrderedChannelIds);
public record SetChannelCategoryRequest(int? CategoryId);
public record CreateCategoryRequest(string Name);
public record CategoryResponse(int Id, string Name, int GuildServerId, int Position);
public record RenameCategoryRequest(string Name);
public record ReorderCategoriesRequest(List<int> OrderedCategoryIds);
public record ReactionSummaryResponse(string Emoji, int Count, bool ReactedByMe);
public record CreateEmojiRequest(string Name, string Url);
public record EmojiResponse(int Id, int GuildServerId, string Name, string ImageUrl, DateTime CreatedAt);
public record MessageResponse(int Id, string Content, int ChannelId, int AuthorId, string AuthorUsername, DateTime SentAt, string? AttachmentUrl = null, string? AuthorAvatarUrl = null, DateTime? EditedAt = null, List<ReactionSummaryResponse>? Reactions = null, DateTime? PinnedAt = null, int? ReplyToMessageId = null, int? ReplyToAuthorId = null, string? ForwardedFromAuthorUsername = null, string? WrappedKeyForMe = null);
public record MessageKeyEnvelope(int UserId, string WrappedKey);

// Shared by both channel and DM message edits - RecipientKeys is only ever
// populated for channel messages (see EditMessageAsync/EditDirectMessageAsync
// in ApiService.cs and the server-side mirror of this record).
public record EditMessageRequest(string Content, List<MessageKeyEnvelope>? RecipientKeys = null);
public record DirectMessageResponse(int Id, string Content, int SenderId, int RecipientId, DateTime SentAt, DateTime? EditedAt = null, DateTime? ReadAt = null, List<ReactionSummaryResponse>? Reactions = null, int? ReplyToMessageId = null, int? ReplyToAuthorId = null, string? ForwardedFromAuthorUsername = null);
public record DmConversationResponse(int OtherUserId, string OtherUsername, string LastMessagePreview, DateTime LastMessageAt, string? OtherUserAvatarUrl = null);
public record CallRecordResponse(int Id, int OtherUserId, string OtherUsername, bool WasIncoming, string Outcome, DateTime StartedAt, DateTime EndedAt, int? DurationSeconds, string? OtherUserAvatarUrl = null);
public record SetKeyMaterialRequest(string PublicKey, string WrappedPrivateKey, string PrivateKeySalt);
public record PublicKeyResponse(int UserId, string? PublicKey);
public record OwnKeyMaterialResponse(string? PublicKey, string? WrappedPrivateKey, string? PrivateKeySalt);
public record UserSummaryResponse(int Id, string Username, string? AvatarUrl = null);
public record InviteResponse(string Code, DateTime? ExpiresAt, int? MaxUses, int UseCount);
public record MemberResponse(int UserId, string Username, string Role, string? AvatarUrl = null, string PresenceState = "Offline", string? CustomStatus = null, int Permissions = 0);
public record SetPermissionsRequest(int Permissions);
public record BanRequest(string? Reason);
public record BannedUserResponse(int UserId, string Username, string? Reason, DateTime CreatedAt, int BannedByUserId, string BannedByUsername);
public record ModerationLogEntryResponse(int Id, string ActorUsername, string Action, string? TargetUsername, string? Details, DateTime CreatedAt);
public record SetSlowModeRequest(int Seconds);
public record ExportedMembership(string ServerName, string Role);
public record UserDataExportResponse(string Username, DateTime CreatedAt, string? CustomStatus, List<ExportedMembership> Servers, List<string> Friends);
public record OwnershipCandidate(int UserId, string Username, string? AvatarUrl);
public record OwnedServerNeedingTransferResponse(int ServerId, string ServerName, List<OwnershipCandidate> Candidates);
public record OwnershipTransfer(int ServerId, int NewOwnerUserId);
public record DeleteAccountRequest(List<OwnershipTransfer>? Transfers);

// Mirrors Server's ServerPermission [Flags] enum exactly - bit values must
// stay in lockstep, see Server/Models/Membership.cs.
[Flags]
public enum ServerPermission
{
    None = 0,
    ManageChannels = 1,
    KickMembers = 2,
    ManageMessages = 4,
    MuteMembers = 8,
    MentionEveryone = 16,
    ManageRoles = 32,
    ManageServer = 64,
    ViewAuditLog = 128,
    All = ManageChannels | KickMembers | ManageMessages | MuteMembers | MentionEveryone | ManageRoles | ManageServer | ViewAuditLog
}
public record UploadResponse(string Url);
public record VoiceParticipant(int UserId, string Username, string? AvatarUrl = null);
public record ChannelVoiceRoster(int ChannelId, List<VoiceParticipant> Members);
public record SetAvatarRequest(string Url);
public record SetCustomStatusRequest(string? Status);
public record FriendResponse(int UserId, string Username, string? AvatarUrl = null, string PresenceState = "Offline", string? CustomStatus = null);
public record FriendRequestResponse(int Id, int UserId, string Username, string Direction, string? AvatarUrl = null);
public record BlockedUserResponse(int UserId, string Username, string? AvatarUrl = null);
public record LiveKitJoinResponse(string Token, string ServerUrl);
// Mandatory is the developer's manual "force everyone off older builds"
// switch - flipped by hand in Server/Site/downloads/version.json per
// release (see DEPLOYMENT.txt / this repo's release workflow), never set
// automatically. Defaults false so older cached responses or a
// hand-edited file that omits the field never accidentally block login.
public record VersionInfo(string Version, string InstallerUrl, string PortableUrl, bool Mandatory = false);
