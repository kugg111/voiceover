using StackExchange.Redis;

namespace Voiceover.Server.Services;

// Multi-instance equivalent of InMemorySlowModeLimiter, selected instead of
// it only when REDIS_URL is configured (see Program.cs). A single atomic
// SET-if-not-exists-with-TTL both checks and starts the cooldown in one
// round trip - simpler than the in-memory version's explicit
// TryGetValue+compare, and self-expiring so there's no eviction pass to run
// here (see CleanupService).
public class RedisSlowModeLimiter : ISlowModeLimiter
{
    private readonly IConnectionMultiplexer _redis;

    public RedisSlowModeLimiter(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public Task<bool> TryAcquireAsync(int channelId, int userId, int slowModeSeconds)
    {
        if (slowModeSeconds <= 0) return Task.FromResult(true);

        var db = _redis.GetDatabase();
        var key = $"slowmode:{channelId}:{userId}";
        return db.StringSetAsync(key, "1", TimeSpan.FromSeconds(slowModeSeconds), When.NotExists);
    }
}
