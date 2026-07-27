using Voiceover.Server.Services;

namespace Server.Tests;

public class MessageRateLimiterTests
{
    [Fact]
    public async Task TryAcquire_AllowsUpToLimit_ThenBlocks()
    {
        var limiter = new InMemoryMessageRateLimiter(limit: 3, window: TimeSpan.FromMinutes(1));

        Assert.True(await limiter.TryAcquireAsync(1));
        Assert.True(await limiter.TryAcquireAsync(1));
        Assert.True(await limiter.TryAcquireAsync(1));
        Assert.False(await limiter.TryAcquireAsync(1));
    }

    [Fact]
    public async Task TryAcquire_TracksEachUserIndependently()
    {
        var limiter = new InMemoryMessageRateLimiter(limit: 1, window: TimeSpan.FromMinutes(1));

        Assert.True(await limiter.TryAcquireAsync(1));
        Assert.False(await limiter.TryAcquireAsync(1));

        // A different user's budget is untouched by user 1 having exhausted theirs.
        Assert.True(await limiter.TryAcquireAsync(2));
        Assert.False(await limiter.TryAcquireAsync(2));
    }

    [Fact]
    public async Task TryAcquire_AllowsAgainOnceWindowSlidesPast()
    {
        var limiter = new InMemoryMessageRateLimiter(limit: 1, window: TimeSpan.FromMilliseconds(50));

        Assert.True(await limiter.TryAcquireAsync(1));
        Assert.False(await limiter.TryAcquireAsync(1));

        Thread.Sleep(80);

        Assert.True(await limiter.TryAcquireAsync(1));
    }
}
