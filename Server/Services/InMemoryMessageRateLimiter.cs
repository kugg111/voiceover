namespace Voiceover.Server.Services;

// Default implementation, used whenever REDIS_URL is unset (see Program.cs).
// Same in-memory/per-process shape as before this was pulled behind
// IMessageRateLimiter - no need for anything heavier - a missed limit on
// restart just means everyone's budget resets, which is fine for anti-spam
// rather than a hard security boundary.
public class InMemoryMessageRateLimiter : IMessageRateLimiter
{
    private readonly InMemorySlidingWindowLimiter _inner;

    public InMemoryMessageRateLimiter(int limit, TimeSpan window)
    {
        _inner = new InMemorySlidingWindowLimiter(limit, window);
    }

    public Task<bool> TryAcquireAsync(int userId) => Task.FromResult(_inner.TryAcquire(userId));

    // Consumed by CleanupService, gated to only run against this in-memory
    // implementation - see CleanupService for why the Redis-backed limiter
    // doesn't need (or implement) this.
    public int EvictInactive() => _inner.EvictInactive();
}
