using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Voiceover.Client.Models;
using Voiceover.Client;

namespace Voiceover.Client.Services;

public class ApiService
{
    private readonly HttpClient _http;

    // Separate, plain client for the /api/auth/* endpoints themselves
    // (register/login/refresh/logout) - deliberately NOT wrapped in
    // AuthRefreshHandler below, since that handler's job is refreshing the
    // token these very calls are the ones producing/consuming. Sharing one
    // handler chain here would risk the refresh call itself triggering
    // another refresh attempt.
    private readonly HttpClient _authHttp;

    public string? Token { get; private set; }
    private DateTime _accessTokenExpiresAtUtc;
    private string? _refreshToken;
    public string? RefreshToken => _refreshToken;
    public int? CurrentUserId { get; private set; }
    public string? CurrentUsername { get; private set; }
    public string? CurrentUserAvatarUrl { get; set; }

    // Fires when the refresh token itself turns out to be invalid/expired/
    // revoked (not just a transient network hiccup) - the session is truly
    // dead and the UI needs to send the user back to the login window.
    public event Action? SessionExpired;

    public ApiService(string baseUrl)
    {
        _authHttp = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _http = new HttpClient(new AuthRefreshHandler(GetFreshAccessTokenAsync) { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri(baseUrl)
        };
    }

    public async Task<bool> RegisterAsync(string username, string password)
        => await AuthenticateAsync("api/auth/register", username, password);

    public async Task<bool> LoginAsync(string username, string password)
        => await AuthenticateAsync("api/auth/login", username, password);

    private async Task<bool> AuthenticateAsync(string endpoint, string username, string password)
    {
        var response = await _authHttp.PostAsJsonAsync(endpoint, new { Username = username, Password = password });
        if (!response.IsSuccessStatusCode) return false;

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        if (auth is null) return false;

        ApplyAuthResponse(auth);
        return true;
    }

    private void ApplyAuthResponse(AuthResponse auth)
    {
        Token = auth.Token;
        _accessTokenExpiresAtUtc = auth.ExpiresAtUtc;
        _refreshToken = auth.RefreshToken;
        CurrentUserId = auth.UserId;
        CurrentUsername = auth.Username;
        CurrentUserAvatarUrl = App.ResolveUploadUrl(auth.AvatarUrl);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    // Reconstitutes an authenticated session from a previously-saved refresh
    // token (see SessionStorage) by exchanging it for a fresh access token -
    // "remember me" sessions can be days old, so there's no point trying to
    // reuse whatever access token was saved last time; just mint a new one
    // up front. Returns false if the refresh token itself is no longer
    // valid, in which case the caller should fall back to LoginWindow.
    // avatarUrl is already a fully-resolved URL (SessionStorage just
    // round-trips whatever AuthenticateAsync last put in
    // CurrentUserAvatarUrl) - unlike everywhere else, don't resolve it again.
    public async Task<bool> RestoreSessionAsync(string refreshToken, int userId, string username, string? avatarUrl = null)
    {
        CurrentUserId = userId;
        CurrentUsername = username;
        CurrentUserAvatarUrl = avatarUrl;
        _refreshToken = refreshToken;
        return await RefreshAccessTokenAsync();
    }

    // Exchanges the current refresh token for a new access+refresh token
    // pair (server-side rotation - see AuthController.Refresh). Only treats
    // an explicit rejection (401: token invalid/expired/revoked) as a dead
    // session; a network-level failure returns false without tearing down
    // local state, since the user might just be offline and could retry.
    public async Task<bool> RefreshAccessTokenAsync()
    {
        if (_refreshToken is null) return false;

        HttpResponseMessage response;
        try
        {
            response = await _authHttp.PostAsJsonAsync("api/auth/refresh", new { RefreshToken = _refreshToken });
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _refreshToken = null;
            Token = null;
            SessionStorage.Clear();
            SessionExpired?.Invoke();
            return false;
        }

        if (!response.IsSuccessStatusCode) return false;

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        if (auth is null) return false;

        ApplyAuthResponse(auth);
        // The server rotates the refresh token on every use (a stale one is
        // revoked immediately) - the locally saved "remember me" file must
        // move to the new one too, or the next app launch would try to
        // redeem an already-revoked token and force a real re-login.
        SessionStorage.UpdateRefreshToken(auth.RefreshToken);
        return true;
    }

    // Used both by AuthRefreshHandler (before every REST request through
    // _http) and by SignalRService's AccessTokenProvider (called on every
    // hub connect/reconnect attempt) - proactively refreshes if the access
    // token is at or near expiry (a minute of buffer to absorb clock skew
    // and in-flight request time) rather than waiting to be rejected first.
    public async Task<string?> GetFreshAccessTokenAsync()
    {
        if (Token is null) return null;
        if (_accessTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(1)) return Token;

        return await RefreshAccessTokenAsync() ? Token : null;
    }

    // Best-effort - revokes just this device's refresh token server-side so
    // it can't be used again, but the local session is cleared regardless
    // of whether the network call actually succeeds (see SessionStorage.Clear
    // callers - logging out has to work even if the server is unreachable).
    public async Task LogoutAsync()
    {
        if (_refreshToken is not null)
        {
            try
            {
                await _authHttp.PostAsJsonAsync("api/auth/logout", new { RefreshToken = _refreshToken });
            }
            catch
            {
                // Best-effort - see comment above.
            }
        }

        Token = null;
        _refreshToken = null;
        _http.DefaultRequestHeaders.Authorization = null;
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

    // --- Auto-update ---

    // Plain unauthenticated GET against the same static-file hosting the
    // downloads themselves already use (Server/Site/downloads/version.json) -
    // no controller/endpoint needed server-side. Swallows every failure
    // (offline, 404, malformed json) since a background update check should
    // never surface an error or block startup.
    public async Task<VersionInfo?> GetLatestVersionAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<VersionInfo>("downloads/version.json");
        }
        catch
        {
            return null;
        }
    }

    // Streams the response straight to disk instead of buffering the whole
    // ~80MB build in memory, and reports 0-100 progress off the response's
    // Content-Length (absent on a chunked/compressed response, in which
    // case progress just never fires - the caller's own status text still
    // shows something is happening).
    public async Task DownloadFileAsync(string url, string destinationPath, IProgress<double>? progress = null)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            totalRead += read;
            if (totalBytes is > 0)
                progress?.Report(100.0 * totalRead / totalBytes.Value);
        }
    }
}
