namespace DiscordClone.Client.Models;

public record AuthResponse(string Token, int UserId, string Username);
public record GuildServerResponse(int Id, string Name, string? IconUrl, int OwnerId);
public record ChannelResponse(int Id, string Name, string Type, int GuildServerId, int Position);
public record MessageResponse(int Id, string Content, int ChannelId, int AuthorId, string AuthorUsername, DateTime SentAt, string? AttachmentUrl = null);
public record DirectMessageResponse(int Id, string Content, int SenderId, int RecipientId, DateTime SentAt);
public record UserSummaryResponse(int Id, string Username);
public record InviteResponse(string Code, DateTime? ExpiresAt, int? MaxUses, int UseCount);
public record MemberResponse(int UserId, string Username, string Role);
public record UploadResponse(string Url);
