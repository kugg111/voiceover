using System.Net;
using System.Net.Http;

namespace Voiceover.Client.Services;

// Transient-failure retry with exponential backoff, inserted into
// ApiService's HttpClient handler chains. Only retries conditions a retry
// could actually fix - a dropped connection, a timeout, or the server
// briefly unavailable/overloaded (5xx, 429) - never a 4xx, since those are
// deterministic outcomes (wrong password, bad request, forbidden) that
// retrying won't change. ApiService had no retry logic at all before this -
// a transient blip just surfaced as a failed call the caller had to notice
// and prompt the user to retry manually.
public class RetryHandler : DelegatingHandler
{
    private static readonly TimeSpan[] Delays = { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
                if (!IsTransientFailure(response) || attempt >= Delays.Length) return response;
            }
            catch (HttpRequestException) when (attempt < Delays.Length)
            {
                // Connection-level failure (DNS, refused, dropped mid-request) -
                // retry below.
            }
            catch (TaskCanceledException) when (attempt < Delays.Length && !cancellationToken.IsCancellationRequested)
            {
                // A timeout, not an actual cancellation by the caller (that
                // case's token IS the one that got cancelled - checked above
                // so a real Ctrl+C-style cancel isn't swallowed into a retry).
            }

            response?.Dispose();
            await Task.Delay(Delays[attempt], cancellationToken);
        }
    }

    private static bool IsTransientFailure(HttpResponseMessage response) =>
        (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests;
}
