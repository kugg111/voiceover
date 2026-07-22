using System.Collections.Concurrent;

namespace Voiceover.Server.Services;

// Same in-memory/per-process shape as MessageRateLimiter (SendMessage is a
// SignalR hub method, never seen by the HTTP rate-limiting middleware - see
// that class's own comment for why this can't just be an AddRateLimiter
// policy instead). Keyed by (channelId, userId) rather than userId alone,
// since slow-mode is a per-channel setting - the same user might be free to
// post immediately in one channel while still on cooldown in another.
public class SlowModeLimiter
{
    private readonly ConcurrentDictionary<(int ChannelId, int UserId), DateTime> _lastSentAt = new();

    // True if this send is allowed right now given slowModeSeconds (0 = no
    // limit, always true); false if the caller is still on cooldown.
    // Records the send as a side effect only when it's allowed.
    public bool TryAcquire(int channelId, int userId, int slowModeSeconds)
    {
        if (slowModeSeconds <= 0) return true;

        var key = (channelId, userId);
        var now = DateTime.UtcNow;

        if (_lastSentAt.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(slowModeSeconds))
            return false;

        _lastSentAt[key] = now;
        return true;
    }

    // Removes entries whose last-sent timestamp is older than maxAge - keeps
    // this dictionary from growing forever as channels/users churn over a
    // long process uptime. Safe to call at any time: a stale entry only
    // ever gates a future send that TryAcquire would already unconditionally
    // allow once slowModeSeconds has elapsed anyway, so removing it changes
    // no observable behavior. See CleanupService, which calls this
    // periodically alongside its own DB-side cleanup.
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
