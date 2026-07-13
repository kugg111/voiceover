namespace Voiceover.Server.Services;

// Same sliding-window logic as MessageRateLimiter, registered as its own DI
// singleton so InitiateCall gets its own budget instead of sharing (or
// fighting over) message-send's - repeatedly ringing someone is a much more
// disruptive form of spam than a burst of chat messages, so it needs a much
// tighter limit than SendMessage's.
public class CallRateLimiter : MessageRateLimiter
{
    public CallRateLimiter(int limit, TimeSpan window) : base(limit, window)
    {
    }
}
