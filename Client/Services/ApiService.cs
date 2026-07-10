using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Voiceover.Client.Models;

namespace Voiceover.Client.Services;

public class ApiService
{
    private readonly HttpClient _http;

    public string? Token { get; private set; }
    public int? CurrentUserId { get; private set; }
    public string? CurrentUsername { get; private set; }

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
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return true;
    }

    // Reconstitutes an authenticated session from a previously-saved token
    // (see SessionStorage), without a fresh login/register round-trip.
    public void RestoreSession(string token, int userId, string username)
    {
        Token = token;
        CurrentUserId = userId;
        CurrentUsername = username;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task<List<GuildServerResponse>> GetMyServersAsync()
        => await _http.GetFromJsonAsync<List<GuildServerResponse>>("api/servers") ?? new();

    public async Task<GuildServerResponse?> CreateServerAsync(string name)
    {
        var response = await _http.PostAsJsonAsync("api/servers", new { Name = name });
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

    // --- File upload ---
    public async Task<UploadResponse?> UploadFileAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(stream);
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        var response = await _http.PostAsync("api/upload", content);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<UploadResponse>() : null;
    }
}
