using Voiceover.Server.Services;

namespace Server.Tests;

public class MessageRateLimiterTests
{
    [Fact]
    public void TryAcquire_AllowsUpToLimit_ThenBlocks()
    {
        var limiter = new MessageRateLimiter(limit: 3, window: TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire(1));
        Assert.True(limiter.TryAcquire(1));
        Assert.True(limiter.TryAcquire(1));
        Assert.False(limiter.TryAcquire(1));
    }

    [Fact]
    public void TryAcquire_TracksEachUserIndependently()
    {
        var limiter = new MessageRateLimiter(limit: 1, window: TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire(1));
        Assert.False(limiter.TryAcquire(1));

        // A different user's budget is untouched by user 1 having exhausted theirs.
        Assert.True(limiter.TryAcquire(2));
        Assert.False(limiter.TryAcquire(2));
    }

    [Fact]
    public void TryAcquire_AllowsAgainOnceWindowSlidesPast()
    {
        var limiter = new MessageRateLimiter(limit: 1, window: TimeSpan.FromMilliseconds(50));

        Assert.True(limiter.TryAcquire(1));
        Assert.False(limiter.TryAcquire(1));

        Thread.Sleep(80);

        Assert.True(limiter.TryAcquire(1));
    }
}
