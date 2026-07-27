using StackExchange.Redis;

namespace Voiceover.Server.Services;

// Shared fixed-window core behind RedisMessageRateLimiter and
// RedisCallRateLimiter - not registered in DI itself, just composed by both.
// Deliberately a fixed window (INCR+EXPIRE), not the in-memory limiters'
// sliding-window queue: Redis has no built-in sliding-window primitive
// without a sorted-set-per-request cost, and this app's own HTTP-layer
// AddRateLimiter policies (Program.cs) already use ASP.NET Core's
// FixedWindowRateLimiter - matching that algorithm keeps "how rate limiting
// behaves in this app" consistent rather than introducing a second
// semantics. The window boundary can allow up to 2x the limit in the worst
// case (a burst just before + just after a window flips); acceptable for
// anti-spam, same as the existing HTTP policies already accept.
internal class RedisFixedWindowRateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly string _keyPrefix;

    public RedisFixedWindowRateLimiter(IConnectionMultiplexer redis, int limit, TimeSpan window, string keyPrefix)
    {
        _redis = redis;
        _limit = limit;
        _window = window;
        _keyPrefix = keyPrefix;
    }

    public async Task<bool> TryAcquireAsync(int userId)
    {
        var db = _redis.GetDatabase();
        var key = $"{_keyPrefix}:{userId}";
        var count = await db.StringIncrementAsync(key);
        if (count == 1) await db.KeyExpireAsync(key, _window);
        return count <= _limit;
    }
}
