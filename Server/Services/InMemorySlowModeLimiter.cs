using System.Collections.Concurrent;

namespace Voiceover.Server.Services;

// Default implementation, used whenever REDIS_URL is unset (see Program.cs).
public class InMemorySlowModeLimiter : ISlowModeLimiter
{
    private readonly ConcurrentDictionary<(int ChannelId, int UserId), DateTime> _lastSentAt = new();

    public Task<bool> TryAcquireAsync(int channelId, int userId, int slowModeSeconds)
    {
        if (slowModeSeconds <= 0) return Task.FromResult(true);

        var key = (channelId, userId);
        var now = DateTime.UtcNow;

        if (_lastSentAt.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(slowModeSeconds))
            return Task.FromResult(false);

        _lastSentAt[key] = now;
        return Task.FromResult(true);
    }

    // Removes entries whose last-sent timestamp is older than maxAge - keeps
    // this dictionary from growing forever as channels/users churn over a
    // long process uptime. Safe to call at any time: a stale entry only
    // ever gates a future send that TryAcquireAsync would already
    // unconditionally allow once slowModeSeconds has elapsed anyway, so
    // removing it changes no observable behavior. Consumed by
    // CleanupService, gated to only run against this in-memory
    // implementation - see that class for why the Redis-backed limiter
    // doesn't need (or implement) this.
    public int EvictOlderThan(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;
        foreach (var (key, lastSentAt) in _lastSentAt)
        {
            if (lastSentAt < cutoff && _lastSentAt.TryRemove(new KeyValuePair<(int, int), DateTime>(key, lastSentAt)))
                removed++;
        }
        return removed;
    }
}
