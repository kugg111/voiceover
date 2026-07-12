namespace Voiceover.Server.Dtos;

public record RegisterRequest(string Username, string Password);
public record LoginRequest(string Username, string Password);
public record AuthResponse(string Token, DateTime ExpiresAtUtc, string RefreshToken, int UserId, string Username, string? AvatarUrl = null);
public record RefreshRequest(string RefreshToken);
public record LogoutRequest(string RefreshToken);

public record CreateServerRequest(string Name);
public record GuildServerResponse(int Id, string Name, string? IconUrl, int OwnerId);
public record SetIconRequest(string Url);

public record CreateChannelRequest(string Name, string Type); // "Text" or "Voice"
public record ChannelResponse(int Id, string Name, string Type, int GuildServerId, int Position);

// Content is opaque E2EE ciphertext, encrypted client-side under the
// sending server's shared key - see Client/Services/E2eeService.cs and
// ServerMemberKey. The server never has a usable key for this either.
public record SendMessageRequest(string Content);
public record MessageResponse(int Id, string Content, int ChannelId, int AuthorId, string AuthorUsername, DateTime SentAt, string? AttachmentUrl = null, string? AuthorAvatarUrl = null, DateTime? EditedAt = null);
public record EditMessageRequest(string Content);
public record UploadResponse(string Url);

// Content/LastMessagePreview are opaque E2EE ciphertext - the client
// decrypts them itself (see Client/Services/E2eeService.cs); the server
// never has a usable key.
public record DirectMessageResponse(int Id, string Content, int SenderId, int RecipientId, DateTime SentAt, DateTime? EditedAt = null);
public record DmConversationResponse(int OtherUserId, string OtherUsername, string LastMessagePreview, DateTime LastMessageAt, string? OtherUserAvatarUrl = null);
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

public record FriendResponse(int UserId, string Username, string? AvatarUrl = null, string PresenceState = "Offline");
public record FriendRequestResponse(int Id, int UserId, string Username, string Direction, string? AvatarUrl = null); // Direction: "Incoming" or "Outgoing"

public record LiveKitJoinResponse(string Token, string ServerUrl);
