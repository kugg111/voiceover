namespace Voiceover.Client.Models;

public record AuthResponse(string Token, int UserId, string Username, string? AvatarUrl = null);
public record GuildServerResponse(int Id, string Name, string? IconUrl, int OwnerId);
public record SetIconRequest(string Url);
public record ChannelResponse(int Id, string Name, string Type, int GuildServerId, int Position);
public record MessageResponse(int Id, string Content, int ChannelId, int AuthorId, string AuthorUsername, DateTime SentAt, string? AttachmentUrl = null, string? AuthorAvatarUrl = null);
public record DirectMessageResponse(int Id, string Content, int SenderId, int RecipientId, DateTime SentAt);
public record DmConversationResponse(int OtherUserId, string OtherUsername, string LastMessagePreview, DateTime LastMessageAt, string? OtherUserAvatarUrl = null);
public record UserSummaryResponse(int Id, string Username, string? AvatarUrl = null);
public record InviteResponse(string Code, DateTime? ExpiresAt, int? MaxUses, int UseCount);
public record MemberResponse(int UserId, string Username, string Role, string? AvatarUrl = null);
public record UploadResponse(string Url);
public record VoiceParticipant(int UserId, string Username, string? AvatarUrl = null);
public record ChannelVoiceRoster(int ChannelId, List<VoiceParticipant> Members);
public record SetAvatarRequest(string Url);
public record FriendResponse(int UserId, string Username, string? AvatarUrl = null);
public record FriendRequestResponse(int Id, int UserId, string Username, string Direction, string? AvatarUrl = null);
