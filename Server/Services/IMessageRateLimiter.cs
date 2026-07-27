namespace Voiceover.Server.Services;

// SendMessage/SendDirectMessage/reaction-toggle anti-spam - ASP.NET Core's
// built-in rate limiting middleware only covers HTTP request pipelines, it
// never sees SignalR hub method invocations (a single long-lived WebSocket
// connection, not discrete requests), so hub methods need their own
// throttling. See InMemoryMessageRateLimiter (single instance, default) and
// RedisMessageRateLimiter (multi-instance, opt-in via REDIS_URL).
public interface IMessageRateLimiter
{
    // True if this send is allowed (and counts against the budget); false if
    // the caller is over the limit and should be dropped/rejected.
    Task<bool> TryAcquireAsync(int userId);
}
