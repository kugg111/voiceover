using Voiceover.Server.Services;

namespace Server.Tests;

public class SlowModeLimiterTests
{
    [Fact]
    public void TryAcquire_ZeroSeconds_AlwaysAllowed()
    {
        var limiter = new SlowModeLimiter();

        Assert.True(limiter.TryAcquire(channelId: 1, userId: 1, slowModeSeconds: 0));
        Assert.True(limiter.TryAcquire(channelId: 1, userId: 1, slowModeSeconds: 0));
        Assert.True(limiter.TryAcquire(channelId: 1, userId: 1, slowModeSeconds: 0));
    }

    [Fact]
    public void TryAcquire_BlocksWithinCooldown_ThenAllowsOnceItElapses()
    {
        var limiter = new SlowModeLimiter();

        Assert.True(limiter.TryAcquire(channelId: 1, userId: 1, slowModeSeconds: 1));
        Assert.False(limiter.TryAcquire(channelId: 1, userId: 1, slowModeSeconds: 1));

        Thread.Sleep(1100);

        Assert.True(limiter.TryAcquire(channelId: 1, userId: 1, slowModeSeconds: 1));
    }

    [Fact]
    public void TryAcquire_KeyedByChannelAndUser_Independently()
    {
        var limiter = new SlowModeLimiter();

        Assert.True(limiter.TryAcquire(channelId: 1, userId: 1, slowModeSeconds: 30));
        Assert.False(limiter.TryAcquire(channelId: 1, userId: 1, slowModeSeconds: 30));

        // Same user, different channel - a cooldown in one channel doesn't
        // apply to another (slow-mode is a per-channel setting).
        Assert.True(limiter.TryAcquire(channelId: 2, userId: 1, slowModeSeconds: 30));

        // Same channel, different user - one user's cooldown doesn't apply to another.
        Assert.True(limiter.TryAcquire(channelId: 1, userId: 2, slowModeSeconds: 30));
    }
}
