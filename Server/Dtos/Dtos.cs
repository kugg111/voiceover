namespace Voiceover.Server.Dtos;

public record RegisterRequest(string Username, string Password);
public record LoginRequest(string Username, string Password);
public record AuthResponse(string Token, int UserId, string Username);

public record CreateServerRequest(string Name);
public record GuildServerResponse(int Id, string Name, string? IconUrl, int OwnerId);

public record CreateChannelRequest(string Name, string Type); // "Text" or "Voice"
public record ChannelResponse(int Id, string Name, string Type, int GuildServerId, int Position);

public record SendMessageRequest(string Content);
public record MessageResponse(int Id, string Content, int ChannelId, int AuthorId, string AuthorUsername, DateTime SentAt, string? AttachmentUrl = null);
public record UploadResponse(string Url);

public record DirectMessageResponse(int Id, string Content, int SenderId, int RecipientId, DateTime SentAt);
public record DmConversationResponse(int OtherUserId, string OtherUsername, string LastMessagePreview, DateTime LastMessageAt);
public record UserSummaryResponse(int Id, string Username);
public record VoiceParticipant(int UserId, string Username);

public record FriendResponse(int UserId, string Username);
public record FriendRequestResponse(int Id, int UserId, string Username, string Direction); // Direction: "Incoming" or "Outgoing"
