using Voiceover.Server.Services;

namespace Server.Tests;

// InMemoryCallRateLimiter shares its sliding-window core (InMemorySlidingWindowLimiter)
// with InMemoryMessageRateLimiter but is its own class/DI registration (via
// ICallRateLimiter) so ChatHub.InitiateCall gets its own budget, separate
// from SendMessage's. These tests just confirm that separation actually
// holds - the full sliding-window logic itself is covered by
// MessageRateLimiterTests.
public class CallRateLimiterTests
{
    [Fact]
    public async Task TryAcquire_AllowsUpToLimit_ThenBlocks()
    {
        var limiter = new InMemoryCallRateLimiter(limit: 2, window: TimeSpan.FromMinutes(1));

        Assert.True(await limiter.TryAcquireAsync(1));
        Assert.True(await limiter.TryAcquireAsync(1));
        Assert.False(await limiter.TryAcquireAsync(1));
    }

    [Fact]
    public async Task TryAcquire_HasItsOwnBudget_SeparateFromAMessageRateLimiterInstance()
    {
        var messageLimiter = new InMemoryMessageRateLimiter(limit: 1, window: TimeSpan.FromMinutes(1));
        var callLimiter = new InMemoryCallRateLimiter(limit: 1, window: TimeSpan.FromMinutes(1));

        Assert.True(await messageLimiter.TryAcquireAsync(1));
        Assert.False(await messageLimiter.TryAcquireAsync(1));

        // Exhausting the message-send budget for this user doesn't touch the
        // separate call-ring budget - they're independent instances/DI
        // singletons with their own state.
        Assert.True(await callLimiter.TryAcquireAsync(1));
    }
}
