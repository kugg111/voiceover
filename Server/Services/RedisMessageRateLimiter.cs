using StackExchange.Redis;

namespace Voiceover.Server.Services;

// Multi-instance equivalent of InMemoryMessageRateLimiter, selected instead
// of it only when REDIS_URL is configured (see Program.cs) - every replica
// shares the same budget per user instead of each replica handing out its
// own copy of the limit independently.
public class RedisMessageRateLimiter : IMessageRateLimiter
{
    private readonly RedisFixedWindowRateLimiter _inner;

    public RedisMessageRateLimiter(IConnectionMultiplexer redis, int limit, TimeSpan window)
    {
        _inner = new RedisFixedWindowRateLimiter(redis, limit, window, "ratelimit:msg");
    }

    public Task<bool> TryAcquireAsync(int userId) => _inner.TryAcquireAsync(userId);
}
