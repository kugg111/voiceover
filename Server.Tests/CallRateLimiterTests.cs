using Voiceover.Server.Services;

namespace Server.Tests;

// CallRateLimiter is a thin subclass of MessageRateLimiter with no
// overridden behavior (see its own class comment - it exists purely so
// ChatHub.InitiateCall gets its own DI-registered budget, separate from
// SendMessage's). These tests just confirm that separation actually holds -
// the full sliding-window logic itself is covered by MessageRateLimiterTests.
public class CallRateLimiterTests
{
    [Fact]
    public void TryAcquire_AllowsUpToLimit_ThenBlocks()
    {
        var limiter = new CallRateLimiter(limit: 2, window: TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire(1));
        Assert.True(limiter.TryAcquire(1));
        Assert.False(limiter.TryAcquire(1));
    }

    [Fact]
    public void TryAcquire_HasItsOwnBudget_SeparateFromAMessageRateLimiterInstance()
    {
        var messageLimiter = new MessageRateLimiter(limit: 1, window: TimeSpan.FromMinutes(1));
        var callLimiter = new CallRateLimiter(limit: 1, window: TimeSpan.FromMinutes(1));

        Assert.True(messageLimiter.TryAcquire(1));
        Assert.False(messageLimiter.TryAcquire(1));

        // Exhausting the message-send budget for this user doesn't touch the
        // separate call-ring budget - they're independent instances/DI
        // singletons with their own state.
        Assert.True(callLimiter.TryAcquire(1));
    }
}
