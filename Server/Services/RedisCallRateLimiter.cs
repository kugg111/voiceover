using StackExchange.Redis;

namespace Voiceover.Server.Services;

// Multi-instance equivalent of InMemoryCallRateLimiter, selected instead of
// it only when REDIS_URL is configured (see Program.cs).
public class RedisCallRateLimiter : ICallRateLimiter
{
    private readonly RedisFixedWindowRateLimiter _inner;

    public RedisCallRateLimiter(IConnectionMultiplexer redis, int limit, TimeSpan window)
    {
        _inner = new RedisFixedWindowRateLimiter(redis, limit, window, "ratelimit:call");
    }

    public Task<bool> TryAcquireAsync(int userId) => _inner.TryAcquireAsync(userId);
}
