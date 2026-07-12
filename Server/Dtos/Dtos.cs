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

public record SendMessageRequest(string Content);
public record MessageResponse(int Id, string Content, int ChannelId, int AuthorId, string AuthorUsername, DateTime SentAt, string? AttachmentUrl = null, string? AuthorAvatarUrl = null, DateTime? EditedAt = null);
public record EditMessageRequest(string Content);
public record UploadResponse(string Url);

public record DirectMessageResponse(int Id, string Content, int SenderId, int RecipientId, DateTime SentAt, DateTime? EditedAt = null);
public record DmConversationResponse(int OtherUserId, string OtherUsername, string LastMessagePreview, DateTime LastMessageAt, string? OtherUserAvatarUrl = null);
public record UserSummaryResponse(int Id, string Username, string? AvatarUrl = null);
public record VoiceParticipant(int UserId, string Username, string? AvatarUrl = null);
public record ChannelVoiceRoster(int ChannelId, List<VoiceParticipant> Members);
public record SetAvatarRequest(string Url);

public record FriendResponse(int UserId, string Username, string? AvatarUrl = null, string PresenceState = "Offline");
public record FriendRequestResponse(int Id, int UserId, string Username, string Direction, string? AvatarUrl = null); // Direction: "Incoming" or "Outgoing"

public record LiveKitJoinResponse(string Token, string ServerUrl);
