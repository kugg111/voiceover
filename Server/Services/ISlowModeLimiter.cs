namespace Voiceover.Server.Services;

// Per-channel slow-mode - same "SignalR hub methods bypass the HTTP rate
// limiter" reasoning as IMessageRateLimiter. Keyed by (channelId, userId)
// rather than userId alone, since slow-mode is a per-channel setting - the
// same user might be free to post immediately in one channel while still on
// cooldown in another.
public interface ISlowModeLimiter
{
    // True if this send is allowed right now given slowModeSeconds (0 = no
    // limit, always true); false if the caller is still on cooldown.
    // Records the send as a side effect only when it's allowed.
    Task<bool> TryAcquireAsync(int channelId, int userId, int slowModeSeconds);
}
