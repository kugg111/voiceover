namespace Voiceover.Server.Dtos;

public record RegisterRequest(string Username, string Password);
public record LoginRequest(string Username, string Password);
public record AuthResponse(string Token, DateTime ExpiresAtUtc, string RefreshToken, int UserId, string Username, string? AvatarUrl = null, string? CustomStatus = null, bool IsAdmin = false, bool TwoFactorEnabled = false);
public record RefreshRequest(string RefreshToken);
public record LogoutRequest(string RefreshToken);

// Login's actual response shape - either a completed login (the
// AuthResponse-shaped fields below are set, RequiresTwoFactor false) or a
// 2FA challenge to complete via POST /api/auth/login/totp (ChallengeToken
// set, those fields null). Register always returns a plain AuthResponse -
// a brand-new account can't have 2FA enabled yet.
//
// Deliberately flat (not AuthResponse nested under its own property) -
// this shape shipped as a nested { requiresTwoFactor, challengeToken, auth }
// object at first, which broke every client built before 2FA existed:
// they deserialize this response directly as AuthResponse and only ever
// looked at top-level fields, so Token/RefreshToken/etc came back null and
// every subsequent authenticated call 401'd. Flat keeps those fields at
// the top level, so an old client keeps working exactly as before,
// silently ignoring the two RequiresTwoFactor/ChallengeToken fields it
// doesn't know about.
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
    bool IsAdmin = false,
    bool TwoFactorEnabled = false);
public record TotpLoginRequest(string ChallengeToken, string? Code, string? RecoveryCode);
public record TotpSetupResponse(string Secret, string QrCodePngBase64);
public record TotpConfirmRequest(string Code);
public record TotpConfirmResponse(List<string> RecoveryCodes);
public record TotpDisableRequest(string Password);

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
public record GuildServerResponse(int Id, string Name, string? IconUrl, int OwnerId, bool IsPublic = false, string? Description = null);
public record SetIconRequest(string Url);
public record RenameServerRequest(string Name);

public record CreateChannelRequest(string Name, string Type); // "Text" or "Voice"
public record ChannelResponse(int Id, string Name, string Type, int GuildServerId, int Position, int SlowModeSeconds = 0, int? CategoryId = null);
public record SetSlowModeRequest(int Seconds);
public record RenameChannelRequest(string Name);
public record ReorderChannelsRequest(List<int> OrderedChannelIds);
public record SetChannelCategoryRequest(int? CategoryId);

public record CreateCategoryRequest(string Name);
public record CategoryResponse(int Id, string Name, int GuildServerId, int Position);
public record RenameCategoryRequest(string Name);
public record ReorderCategoriesRequest(List<int> OrderedCategoryIds);

// Aggregated per (message, emoji) - ReactedByMe lets the client render the
// "you reacted" highlight without shipping every individual reactor's id.
public record ReactionSummaryResponse(string Emoji, int Count, bool ReactedByMe);

// Url is a relative /uploads/... path already returned by POST /api/upload,
// same two-step upload-then-attach flow as SetIconRequest/SetAvatarRequest.
public record CreateEmojiRequest(string Name, string Url);
public record EmojiResponse(int Id, int GuildServerId, string Name, string ImageUrl, DateTime CreatedAt);

// Content is opaque E2EE ciphertext, encrypted client-side under a fresh
// one-time key generated for this message alone - see
// Client/Services/E2eeService.cs and MessageRecipientKey. The server never
// has a usable key for this either.
public record SendMessageRequest(string Content);

// WrappedKeyForMe is this specific caller's own envelope-wrapped copy of the
// message's one-time key (see MessageRecipientKey) - never any other
// recipient's copy, so a response never leaks who else can read a message
// beyond what channel membership already implies.
public record MessageResponse(int Id, string Content, int ChannelId, int AuthorId, string AuthorUsername, DateTime SentAt, string? AttachmentUrl = null, string? AuthorAvatarUrl = null, DateTime? EditedAt = null, List<ReactionSummaryResponse>? Reactions = null, DateTime? PinnedAt = null, int? ReplyToMessageId = null, int? ReplyToAuthorId = null, string? ForwardedFromAuthorUsername = null, string? WrappedKeyForMe = null);
public record MessageKeyEnvelope(int UserId, string WrappedKey);

// Shared by both MessagesController.Edit and DirectMessagesController.Edit -
// RecipientKeys is only ever populated (and only ever read) for channel
// messages, since a DM's key is derivable on demand from the two
// participants' public keys and never needs an envelope table at all (see
// MessageRecipientKey). Left as one nullable-list DTO rather than two
// separate request records so both editable-message controllers keep
// sharing this type, matching how they already share EditMessageRequest.
public record EditMessageRequest(string Content, List<MessageKeyEnvelope>? RecipientKeys = null);
public record UploadResponse(string Url);

// Content/LastMessagePreview are opaque E2EE ciphertext - the client
// decrypts them itself (see Client/Services/E2eeService.cs); the server
// never has a usable key.
public record DirectMessageResponse(int Id, string Content, int SenderId, int RecipientId, DateTime SentAt, DateTime? EditedAt = null, DateTime? ReadAt = null, List<ReactionSummaryResponse>? Reactions = null, int? ReplyToMessageId = null, int? ReplyToAuthorId = null, string? ForwardedFromAuthorUsername = null);
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

public record VoiceParticipant(int UserId, string Username, string? AvatarUrl = null);
public record ChannelVoiceRoster(int ChannelId, List<VoiceParticipant> Members);
public record SetAvatarRequest(string Url);

public record FriendResponse(int UserId, string Username, string? AvatarUrl = null, string PresenceState = "Offline", string? CustomStatus = null);
public record SetCustomStatusRequest(string? Status);
public record FriendRequestResponse(int Id, int UserId, string Username, string Direction, string? AvatarUrl = null); // Direction: "Incoming" or "Outgoing"
public record BlockedUserResponse(int UserId, string Username, string? AvatarUrl = null);

public record LiveKitJoinResponse(string Token, string ServerUrl);
