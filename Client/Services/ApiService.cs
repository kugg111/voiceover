using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Voiceover.Client.Models;
using Voiceover.Client;

namespace Voiceover.Client.Services;

public class ApiService
{
    private readonly HttpClient _http;

    public string? Token { get; private set; }
    public int? CurrentUserId { get; private set; }
    public string? CurrentUsername { get; private set; }
    public string? CurrentUserAvatarUrl { get; set; }

    public ApiService(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task<bool> RegisterAsync(string username, string password)
        => await AuthenticateAsync("api/auth/register", username, password);

    public async Task<bool> LoginAsync(string username, string password)
        => await AuthenticateAsync("api/auth/login", username, password);

    private async Task<bool> AuthenticateAsync(string endpoint, string username, string password)
    {
        var response = await _http.PostAsJsonAsync(endpoint, new { Username = username, Password = password });
        if (!response.IsSuccessStatusCode) return false;

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        if (auth is null) return false;

        Token = auth.Token;
        CurrentUserId = auth.UserId;
        CurrentUsername = auth.Username;
        CurrentUserAvatarUrl = App.ResolveUploadUrl(auth.AvatarUrl);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return true;
    }

    // Reconstitutes an authenticated session from a previously-saved token
    // (see SessionStorage), without a fresh login/register round-trip.
    // avatarUrl here is already a fully-resolved URL (SessionStorage just
    // round-trips whatever AuthenticateAsync last put in
    // CurrentUserAvatarUrl) - unlike everywhere else, don't resolve it again.
    public void RestoreSession(string token, int userId, string username, string? avatarUrl = null)
    {
        Token = token;
        CurrentUserId = userId;
        CurrentUsername = username;
        CurrentUserAvatarUrl = avatarUrl;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task<List<GuildServerResponse>> GetMyServersAsync()
        => await _http.GetFromJsonAsync<List<GuildServerResponse>>("api/servers") ?? new();

    public async Task<(bool Success, string? Error)> LeaveServerAsync(int serverId)
    {
        var response = await _http.DeleteAsync($"api/servers/{serverId}/leave");
        if (response.IsSuccessStatusCode) return (true, null);
        return (false, await response.Content.ReadAsStringAsync());
    }

    public async Task<GuildServerResponse?> CreateServerAsync(string name)
    {
        var response = await _http.PostAsJsonAsync("api/servers", new { Name = name });
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<GuildServerResponse>()
            : null;
    }

    // Owner-only server-side (see ServersController.SetIcon) - url is a
    // relative /uploads/... path already returned by UploadFileAsync.
    public async Task<GuildServerResponse?> SetServerIconAsync(int serverId, string url)
    {
        var response = await _http.PutAsJsonAsync($"api/servers/{serverId}/icon", new SetIconRequest(url));
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<GuildServerResponse>()
            : null;
    }

    public async Task<List<ChannelResponse>> GetChannelsAsync(int serverId)
        => await _http.GetFromJsonAsync<List<ChannelResponse>>($"api/servers/{serverId}/channels") ?? new();

    public async Task<ChannelResponse?> CreateChannelAsync(int serverId, string name, string type)
    {
        var response = await _http.PostAsJsonAsync($"api/servers/{serverId}/channels", new { Name = name, Type = type });
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ChannelResponse>()
            : null;
    }

    public async Task<List<MessageResponse>> GetMessageHistoryAsync(int channelId)
        => await _http.GetFromJsonAsync<List<MessageResponse>>($"api/channels/{channelId}/messages") ?? new();

    // --- Invites ---
    public async Task<InviteResponse?> CreateInviteAsync(int serverId, int? expiresInHours = null, int? maxUses = null)
    {
        var response = await _http.PostAsJsonAsync($"api/servers/{serverId}/invites",
            new { ExpiresInHours = expiresInHours, MaxUses = maxUses });
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<InviteResponse>() : null;
    }

    public async Task<(bool Success, string? Error)> JoinByInviteAsync(string code)
    {
        var response = await _http.PostAsync($"api/invites/{code}/join", null);
        if (response.IsSuccessStatusCode) return (true, null);
        return (false, await response.Content.ReadAsStringAsync());
    }

    public async Task<List<InviteResponse>> ListInvitesAsync(int serverId)
        => await _http.GetFromJsonAsync<List<InviteResponse>>($"api/servers/{serverId}/invites") ?? new();

    // --- Members ---
    public async Task<List<MemberResponse>> GetMembersAsync(int serverId)
        => await _http.GetFromJsonAsync<List<MemberResponse>>($"api/servers/{serverId}/members") ?? new();

    public async Task<bool> KickMemberAsync(int serverId, int userId)
        => (await _http.DeleteAsync($"api/servers/{serverId}/members/{userId}")).IsSuccessStatusCode;

    public async Task<bool> ChangeRoleAsync(int serverId, int userId, string role)
    {
        var response = await _http.PutAsJsonAsync($"api/servers/{serverId}/members/{userId}/role", new { Role = role });
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteChannelAsync(int serverId, int channelId)
        => (await _http.DeleteAsync($"api/servers/{serverId}/channels/{channelId}")).IsSuccessStatusCode;

    // --- Direct messages ---
    public async Task<List<UserSummaryResponse>> SearchUsersAsync(string query)
        => await _http.GetFromJsonAsync<List<UserSummaryResponse>>($"api/users/search?username={Uri.EscapeDataString(query)}") ?? new();

    public async Task<List<DirectMessageResponse>> GetDmHistoryAsync(int otherUserId)
        => await _http.GetFromJsonAsync<List<DirectMessageResponse>>($"api/dm/{otherUserId}") ?? new();

    public async Task<List<DmConversationResponse>> GetDmConversationsAsync()
        => await _http.GetFromJsonAsync<List<DmConversationResponse>>("api/dm/conversations") ?? new();

    // --- Friends ---
    public async Task<List<FriendResponse>> GetFriendsAsync()
        => await _http.GetFromJsonAsync<List<FriendResponse>>("api/friends") ?? new();

    public async Task<List<FriendRequestResponse>> GetFriendRequestsAsync()
        => await _http.GetFromJsonAsync<List<FriendRequestResponse>>("api/friends/requests") ?? new();

    public async Task<(bool Success, string? Error)> SendFriendRequestAsync(int userId)
    {
        var response = await _http.PostAsync($"api/friends/request/{userId}", null);
        if (response.IsSuccessStatusCode) return (true, null);
        return (false, await response.Content.ReadAsStringAsync());
    }

    public async Task<bool> AcceptFriendRequestAsync(int friendshipId)
        => (await _http.PostAsync($"api/friends/{friendshipId}/accept", null)).IsSuccessStatusCode;

    public async Task<bool> RemoveFriendshipAsync(int friendshipId)
        => (await _http.DeleteAsync($"api/friends/{friendshipId}")).IsSuccessStatusCode;

    // url is a relative /uploads/... path already returned by UploadFileAsync.
    public async Task<bool> SetMyAvatarAsync(string url)
        => (await _http.PutAsJsonAsync("api/users/me/avatar", new SetAvatarRequest(url))).IsSuccessStatusCode;

    // --- File upload ---
    private const long MaxUploadSizeBytes = 8 * 1024 * 1024; // matches UploadController server-side

    public async Task<(UploadResponse? Result, string? Error)> UploadFileAsync(string filePath)
    {
        // Fails fast client-side instead of uploading a doomed multi-MB
        // request only to have the server reject it with the same limit.
        if (new FileInfo(filePath).Length > MaxUploadSizeBytes)
            return (null, "File too large (8 MB max).");

        await using var stream = File.OpenRead(filePath);
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(stream);
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        var response = await _http.PostAsync("api/upload", content);
        if (response.IsSuccessStatusCode)
            return (await response.Content.ReadFromJsonAsync<UploadResponse>(), null);

        var error = (await response.Content.ReadAsStringAsync()).Trim('"');
        return (null, string.IsNullOrWhiteSpace(error) ? "Upload failed." : error);
    }
}
