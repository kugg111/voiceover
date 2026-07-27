using System.Collections.Concurrent;

namespace Voiceover.Server.Services;

// Shared sliding-window core behind InMemoryMessageRateLimiter and
// InMemoryCallRateLimiter - not registered in DI itself, just composed by
// both so the two distinct budgets (message-send vs. call-initiate) don't
// have to duplicate this logic.
internal class InMemorySlidingWindowLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<int, Queue<DateTime>> _sends = new();

    public InMemorySlidingWindowLimiter(int limit, TimeSpan window)
    {
        _limit = limit;
        _window = window;
    }

    public bool TryAcquire(int userId)
    {
        var queue = _sends.GetOrAdd(userId, _ => new Queue<DateTime>());
        var now = DateTime.UtcNow;

        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > _window)
                queue.Dequeue();

            if (queue.Count >= _limit) return false;

            queue.Enqueue(now);
            return true;
        }
    }

    // Removes per-user queues that are now empty after trimming - TryAcquire
    // only ever trims a user's own queue lazily on THEIR next call, so a
    // user who sends a burst and then goes permanently inactive would
    // otherwise leave an empty queue (and dictionary entry) sitting in
    // memory forever. The atomic KeyValuePair-based TryRemove only removes
    // if the entry still holds this exact queue reference, so a concurrent
    // TryAcquire that's mid-enqueue on the same user can't have its new
    // entry silently dropped by a racing eviction pass.
    public int EvictInactive()
    {
        var now = DateTime.UtcNow;
        var removed = 0;
        foreach (var (userId, queue) in _sends)
        {
            lock (queue)
            {
                while (queue.Count > 0 && now - queue.Peek() > _window)
                    queue.Dequeue();
                if (queue.Count == 0 &&
                    _sends.TryRemove(new KeyValuePair<int, Queue<DateTime>>(userId, queue)))
                    removed++;
            }
        }
        return removed;
    }
}
