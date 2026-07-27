namespace Voiceover.Server.Services;

// InitiateCall anti-spam - a separate budget from IMessageRateLimiter (own
// DI registration/interface) so ringing someone repeatedly - a much more
// disruptive form of spam than a burst of chat messages - gets its own much
// tighter limit rather than sharing or fighting over the message budget.
public interface ICallRateLimiter
{
    Task<bool> TryAcquireAsync(int userId);
}
