namespace Voiceover.Server.Services;

public enum CallState { Ringing, Active }

public record CallSession(string CallId, int CallerId, int CalleeId, CallState State, DateTime StartedAt, DateTime? ConnectedAt = null);

// Tracks active/ringing private 1:1 voice calls between friends. See
// InMemoryCallSignalingStore (single instance, default) and
// RedisCallSignalingStore (multi-instance, opt-in via REDIS_URL) - the
// latter needs the "both participants must be free" check in CreateAsync to
// stay atomic across replicas, so it uses a Lua script rather than a plain
// read-then-write.
public interface ICallSignalingStore
{
    // Returns null if either participant is already in a call - callers
    // should treat that as "can't start this call right now" rather than
    // silently overwriting an in-progress one.
    Task<CallSession?> CreateAsync(int callerId, int calleeId);

    Task<CallSession?> GetAsync(string callId);

    Task<CallSession?> AcceptAsync(string callId);

    // Covers decline/hangup/disconnect-cleanup alike - same "one shared
    // remove-and-notify shape" FriendsController's DELETE endpoint already
    // uses for decline/cancel/unfriend.
    Task<CallSession?> RemoveAsync(string callId);

    // Used by ChatHub.OnDisconnectedAsync to find and clean up any call the
    // disconnecting user was part of (ringing or active), without the
    // caller needing to already know the call id.
    Task<CallSession?> GetActiveCallForUserAsync(int userId);
}
