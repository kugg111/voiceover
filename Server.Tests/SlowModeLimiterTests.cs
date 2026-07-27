using Voiceover.Server.Services;

namespace Server.Tests;

public class SlowModeLimiterTests
{
    [Fact]
    public async Task TryAcquire_ZeroSeconds_AlwaysAllowed()
    {
        var limiter = new InMemorySlowModeLimiter();

        Assert.True(await limiter.TryAcquireAsync(channelId: 1, userId: 1, slowModeSeconds: 0));
        Assert.True(await limiter.TryAcquireAsync(channelId: 1, userId: 1, slowModeSeconds: 0));
        Assert.True(await limiter.TryAcquireAsync(channelId: 1, userId: 1, slowModeSeconds: 0));
    }

    [Fact]
    public async Task TryAcquire_BlocksWithinCooldown_ThenAllowsOnceItElapses()
    {
        var limiter = new InMemorySlowModeLimiter();

        Assert.True(await limiter.TryAcquireAsync(channelId: 1, userId: 1, slowModeSeconds: 1));
        Assert.False(await limiter.TryAcquireAsync(channelId: 1, userId: 1, slowModeSeconds: 1));

        Thread.Sleep(1100);

        Assert.True(await limiter.TryAcquireAsync(channelId: 1, userId: 1, slowModeSeconds: 1));
    }

    [Fact]
    public async Task TryAcquire_KeyedByChannelAndUser_Independently()
    {
        var limiter = new InMemorySlowModeLimiter();

        Assert.True(await limiter.TryAcquireAsync(channelId: 1, userId: 1, slowModeSeconds: 30));
        Assert.False(await limiter.TryAcquireAsync(channelId: 1, userId: 1, slowModeSeconds: 30));

        // Same user, different channel - a cooldown in one channel doesn't
        // apply to another (slow-mode is a per-channel setting).
        Assert.True(await limiter.TryAcquireAsync(channelId: 2, userId: 1, slowModeSeconds: 30));

        // Same channel, different user - one user's cooldown doesn't apply to another.
        Assert.True(await limiter.TryAcquireAsync(channelId: 1, userId: 2, slowModeSeconds: 30));
    }
}
