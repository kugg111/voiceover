namespace Voiceover.Server.Services;

// Default implementation, used whenever REDIS_URL is unset (see Program.cs).
// Same sliding-window core as InMemoryMessageRateLimiter, just with its own
// budget/queues so InitiateCall doesn't share (or fight over) SendMessage's.
public class InMemoryCallRateLimiter : ICallRateLimiter
{
    private readonly InMemorySlidingWindowLimiter _inner;

    public InMemoryCallRateLimiter(int limit, TimeSpan window)
    {
        _inner = new InMemorySlidingWindowLimiter(limit, window);
    }

    public Task<bool> TryAcquireAsync(int userId) => Task.FromResult(_inner.TryAcquire(userId));
}
