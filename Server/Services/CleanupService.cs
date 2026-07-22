using Microsoft.EntityFrameworkCore;
using Voiceover.Server.Data;

namespace Voiceover.Server.Services;

// First BackgroundService in this codebase - RefreshTokens are only ever
// soft-marked (RevokedAt set on rotation/logout, see AuthController) and
// expired Invites are never removed at all, so both tables grow forever
// without this. Runs as a long-lived singleton but needs a scoped
// AppDbContext per tick, hence IServiceScopeFactory rather than injecting
// AppDbContext directly.
public class CleanupService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    // Revoked tokens are kept around for a while past revocation (rather
    // than deleted immediately) purely so a "why did my session die"
    // investigation has something to look at - no functional reason beyond
    // that.
    private static readonly TimeSpan RevokedTokenRetention = TimeSpan.FromDays(30);

    // In-memory (not DB-backed) per-process caches with no eviction of their
    // own - see each class's EvictOlderThan/EvictInactive for why they'd
    // otherwise grow without bound over a long process uptime. Safe to
    // inject as singletons directly (unlike AppDbContext) since neither
    // holds any scoped/disposable state.
    private static readonly TimeSpan SlowModeEntryMaxAge = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SlowModeLimiter _slowModeLimiter;
    private readonly MessageRateLimiter _messageRateLimiter;
    private readonly ILogger<CleanupService> _logger;

    public CleanupService(IServiceScopeFactory scopeFactory, SlowModeLimiter slowModeLimiter,
        MessageRateLimiter messageRateLimiter, ILogger<CleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _slowModeLimiter = slowModeLimiter;
        _messageRateLimiter = messageRateLimiter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // Run once immediately on startup, then on the timer - a container
        // that gets redeployed more often than the interval would otherwise
        // never actually reach a tick.
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var now = DateTime.UtcNow;
            var deletedTokens = await db.RefreshTokens
                .Where(t => t.ExpiresAt < now || (t.RevokedAt != null && t.RevokedAt < now - RevokedTokenRetention))
                .ExecuteDeleteAsync(ct);
            var deletedInvites = await db.Invites
                .Where(i => i.ExpiresAt != null && i.ExpiresAt < now)
                .ExecuteDeleteAsync(ct);

            var evictedSlowMode = _slowModeLimiter.EvictOlderThan(SlowModeEntryMaxAge);
            var evictedRateLimit = _messageRateLimiter.EvictInactive();

            if (deletedTokens > 0 || deletedInvites > 0)
                _logger.LogInformation(
                    "Cleanup pass removed {TokenCount} expired refresh tokens and {InviteCount} expired invites",
                    deletedTokens, deletedInvites);
            if (evictedSlowMode > 0 || evictedRateLimit > 0)
                _logger.LogInformation(
                    "Cleanup pass evicted {SlowModeCount} stale slow-mode entries and {RateLimitCount} inactive rate-limit entries",
                    evictedSlowMode, evictedRateLimit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort - a failed cleanup pass shouldn't crash the host;
            // it just tries again next interval.
            _logger.LogError(ex, "Cleanup pass failed");
        }
    }
}
