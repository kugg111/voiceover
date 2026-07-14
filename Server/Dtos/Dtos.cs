namespace Voiceover.Server.Dtos;

public record RegisterRequest(string Username, string Password);
public record LoginRequest(string Username, string Password);
public record AuthResponse(string Token, DateTime ExpiresAtUtc, string RefreshToken, int UserId, string Username, string? AvatarUrl = null, string? CustomStatus = null);
public record RefreshRequest(string RefreshToken);
public record LogoutRequest(string RefreshToken);

// Account profile + membership/friend metadata only - never message
// content, since the server only ever holds E2EE ciphertext for messages
// (same limitation already disclosed for in-app search, which had to be
// client-side for the same reason).
public record ExportedMembership(string ServerName, string Role);
public record UserDataExportResponse(string Username, DateTime CreatedAt, string? CustomStatus, List<ExportedMembership> Servers, List<string> Friends);

// Returned by GET /api/users/me/owned-servers-needing-transfer before a
// delete-account call - only includes servers where the caller is Owner
// and has 2+ other members, since with 0 the server is just deleted and
// with exactly 1 that member is auto-promoted, no picker needed either way.
public record OwnershipCandidate(int UserId, string Username, string? AvatarUrl);
public record OwnedServerNeedingTransferResponse(int ServerId, string ServerName, List<OwnershipCandidate> Candidates);

// Sent with DELETE /api/users/me - only required for servers that came back
// from the endpoint above; servers with 0 or 1 other member are resolved
// automatically without needing an entry here.
public record OwnershipTransfer(int ServerId, int NewOwnerUserId);
public record DeleteAccountRequest(List<OwnershipTransfer>? Transfers);

public record CreateServerRequest(string Name);
public record GuildServerResponse(int Id, string Name, string? IconUrl, int OwnerId);
public record SetIconRequest(string Url);

public record CreateChannelRequest(string Name, string Type); // "Text" or "Voice"
public record ChannelResponse(int Id, string Name, string Type, int GuildServerId, int Position, int SlowModeSeconds = 0);
public record SetSlowModeRequest(int Seconds);

// Aggregated per (message, emoji) - ReactedByMe lets the client render the
// "you reacted" highlight without shipping every individual reactor's id.
public record ReactionSummaryResponse(string Emoji, int Count, bool ReactedByMe);

// Content is opaque E2EE ciphertext, encrypted client-side under the
// sending server's shared key - see Client/Services/E2eeService.cs and
// ServerMemberKey. The server never has a usable key for this either.
public record SendMessageRequest(string Content);
public record MessageResponse(int Id, string Content, int ChannelId, int AuthorId, string AuthorUsername, DateTime SentAt, string? AttachmentUrl = null, string? AuthorAvatarUrl = null, DateTime? EditedAt = null, List<ReactionSummaryResponse>? Reactions = null, DateTime? PinnedAt = null);
public record EditMessageRequest(string Content);
public record UploadResponse(string Url);

// Content/LastMessagePreview are opaque E2EE ciphertext - the client
// decrypts them itself (see Client/Services/E2eeService.cs); the server
// never has a usable key.
public record DirectMessageResponse(int Id, string Content, int SenderId, int RecipientId, DateTime SentAt, DateTime? EditedAt = null, DateTime? ReadAt = null, List<ReactionSummaryResponse>? Reactions = null);
public record DmConversationResponse(int OtherUserId, string OtherUsername, string LastMessagePreview, DateTime LastMessageAt, string? OtherUserAvatarUrl = null);

// "Recent Calls" history (see CallsController/CallRecord) - Outcome is one
// of "Completed"/"Missed"/"Declined"/"Cancelled". DurationSeconds is only
// meaningful (non-null) for Completed calls.
public record CallRecordResponse(int Id, int OtherUserId, string OtherUsername, bool WasIncoming, string Outcome, DateTime StartedAt, DateTime EndedAt, int? DurationSeconds, string? OtherUserAvatarUrl = null);
public record UserSummaryResponse(int Id, string Username, string? AvatarUrl = null);

// --- E2EE key material (identity keys, DM-oriented - see Client/Services/E2eeService.cs) ---
public record SetKeyMaterialRequest(string PublicKey, string WrappedPrivateKey, string PrivateKeySalt);
public record PublicKeyResponse(int UserId, string? PublicKey);
public record OwnKeyMaterialResponse(string? PublicKey, string? WrappedPrivateKey, string? PrivateKeySalt);

// --- E2EE server (channel-message) key - one shared key per GuildServer,
// asymmetrically wrapped per member (see ServerMemberKey) ---
public record SetServerKeyRequest(string WrappedKey);
public record ServerKeyResponse(string? WrappedKey, int? WrappedByUserId);
public record VoiceParticipant(int UserId, string Username, string? AvatarUrl = null);
public record ChannelVoiceRoster(int ChannelId, List<VoiceParticipant> Members);
public record SetAvatarRequest(string Url);

public record FriendResponse(int UserId, string Username, string? AvatarUrl = null, string PresenceState = "Offline", string? CustomStatus = null);
public record SetCustomStatusRequest(string? Status);
public record FriendRequestResponse(int Id, int UserId, string Username, string Direction, string? AvatarUrl = null); // Direction: "Incoming" or "Outgoing"

public record LiveKitJoinResponse(string Token, string ServerUrl);
