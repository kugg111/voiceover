namespace Voiceover.Client.Models;

public record AuthResponse(string Token, DateTime ExpiresAtUtc, string RefreshToken, int UserId, string Username, string? AvatarUrl = null, string? CustomStatus = null);
public record GuildServerResponse(int Id, string Name, string? IconUrl, int OwnerId);
public record SetIconRequest(string Url);
public record RenameServerRequest(string Name);
public record ChannelResponse(int Id, string Name, string Type, int GuildServerId, int Position, int SlowModeSeconds = 0);
public record RenameChannelRequest(string Name);
public record ReorderChannelsRequest(List<int> OrderedChannelIds);
public record ReactionSummaryResponse(string Emoji, int Count, bool ReactedByMe);
public record MessageResponse(int Id, string Content, int ChannelId, int AuthorId, string AuthorUsername, DateTime SentAt, string? AttachmentUrl = null, string? AuthorAvatarUrl = null, DateTime? EditedAt = null, List<ReactionSummaryResponse>? Reactions = null, DateTime? PinnedAt = null);
public record EditMessageRequest(string Content);
public record DirectMessageResponse(int Id, string Content, int SenderId, int RecipientId, DateTime SentAt, DateTime? EditedAt = null, DateTime? ReadAt = null, List<ReactionSummaryResponse>? Reactions = null);
public record DmConversationResponse(int OtherUserId, string OtherUsername, string LastMessagePreview, DateTime LastMessageAt, string? OtherUserAvatarUrl = null);
public record CallRecordResponse(int Id, int OtherUserId, string OtherUsername, bool WasIncoming, string Outcome, DateTime StartedAt, DateTime EndedAt, int? DurationSeconds, string? OtherUserAvatarUrl = null);
public record SetKeyMaterialRequest(string PublicKey, string WrappedPrivateKey, string PrivateKeySalt);
public record PublicKeyResponse(int UserId, string? PublicKey);
public record OwnKeyMaterialResponse(string? PublicKey, string? WrappedPrivateKey, string? PrivateKeySalt);
public record SetServerKeyRequest(string WrappedKey);
public record ServerKeyResponse(string? WrappedKey, int? WrappedByUserId);
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
    All = ManageChannels | KickMembers | ManageMessages | MuteMembers
}
public record UploadResponse(string Url);
public record VoiceParticipant(int UserId, string Username, string? AvatarUrl = null);
public record ChannelVoiceRoster(int ChannelId, List<VoiceParticipant> Members);
public record SetAvatarRequest(string Url);
public record SetCustomStatusRequest(string? Status);
public record FriendResponse(int UserId, string Username, string? AvatarUrl = null, string PresenceState = "Offline", string? CustomStatus = null);
public record FriendRequestResponse(int Id, int UserId, string Username, string Direction, string? AvatarUrl = null);
public record LiveKitJoinResponse(string Token, string ServerUrl);
public record VersionInfo(string Version, string InstallerUrl, string PortableUrl);
