using System.Collections.Concurrent;

namespace Voiceover.Server.Services;

// ASP.NET Core's built-in rate limiting middleware only covers HTTP request
// pipelines - it never sees SignalR hub method invocations, which arrive
// over a single long-lived WebSocket connection instead of discrete
// requests. SendMessage/SendDirectMessage need their own throttling, so this
// is a small sliding-window limiter, same in-memory/per-process shape as
// PresenceService/VoicePresenceService (no need for anything heavier - a
// missed limit on restart just means everyone's budget resets, which is
// fine for anti-spam rather than a hard security boundary).
public class MessageRateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<int, Queue<DateTime>> _sends = new();

    public MessageRateLimiter(int limit, TimeSpan window)
    {
        _limit = limit;
        _window = window;
    }

    // True if this send is allowed (and counts against the budget); false if
    // the caller is over the limit and should be dropped/rejected.
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
}
