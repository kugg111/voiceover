using System.Net.Http;
using System.Net.Http.Headers;

namespace Voiceover.Client.Services;

// Wired into ApiService's main HttpClient (see its constructor) so every
// authenticated request transparently gets a live access token instead of
// every single ApiService method needing to remember to check/refresh one
// itself. Purely proactive (refreshes before sending if the token is at or
// near expiry - see ApiService.EnsureFreshAccessTokenAsync) rather than
// reactive retry-on-401: some requests here (UploadFileAsync) carry
// stream-backed content that can only be sent once, so silently retrying a
// failed request isn't safe to do generically for every call in this class.
internal class AuthRefreshHandler : DelegatingHandler
{
    private readonly Func<Task<string?>> _ensureFreshTokenAsync;

    public AuthRefreshHandler(Func<Task<string?>> ensureFreshTokenAsync)
    {
        _ensureFreshTokenAsync = ensureFreshTokenAsync;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _ensureFreshTokenAsync();
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }
}
