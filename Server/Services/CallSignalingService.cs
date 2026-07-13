namespace Voiceover.Server.Services;

public enum CallState { Ringing, Active }

public record CallSession(string CallId, int CallerId, int CalleeId, CallState State, DateTime StartedAt, DateTime? ConnectedAt = null);

// Tracks active/ringing private 1:1 voice calls between friends, in memory
// only - no DB table, same tradeoff PresenceService/VoicePresenceService
// already make: a call is inherently ephemeral, so a server restart
// correctly drops all of them rather than needing to reconcile stale rows.
//
// Create touches two dictionaries as one logical operation (register the
// call, mark both participants busy) - lock-guarded like PresenceService's
// own compound operations, rather than VoicePresenceService's simpler
// single-dictionary approach (which doesn't need one).
public class CallSignalingService
{
    private readonly Dictionary<string, CallSession> _calls = new();
    private readonly Dictionary<int, string> _userToCallId = new();
    private readonly object _lock = new();

    // Returns null if either participant is already in a call - callers
    // should treat that as "can't start this call right now" rather than
    // silently overwriting an in-progress one.
    public CallSession? Create(int callerId, int calleeId)
    {
        lock (_lock)
        {
            if (_userToCallId.ContainsKey(callerId) || _userToCallId.ContainsKey(calleeId))
                return null;

            var session = new CallSession($"call-{Guid.NewGuid():N}", callerId, calleeId, CallState.Ringing, DateTime.UtcNow);
            _calls[session.CallId] = session;
            _userToCallId[callerId] = session.CallId;
            _userToCallId[calleeId] = session.CallId;
            return session;
        }
    }

    public CallSession? Get(string callId)
    {
        lock (_lock) { return _calls.GetValueOrDefault(callId); }
    }

    public CallSession? Accept(string callId)
    {
        lock (_lock)
        {
            if (!_calls.TryGetValue(callId, out var session)) return null;
            var updated = session with { State = CallState.Active, ConnectedAt = DateTime.UtcNow };
            _calls[callId] = updated;
            return updated;
        }
    }

    // Covers decline/hangup/disconnect-cleanup alike - same "one shared
    // remove-and-notify shape" FriendsController's DELETE endpoint already
    // uses for decline/cancel/unfriend.
    public CallSession? Remove(string callId)
    {
        lock (_lock)
        {
            if (!_calls.Remove(callId, out var session)) return null;
            _userToCallId.Remove(session.CallerId);
            _userToCallId.Remove(session.CalleeId);
            return session;
        }
    }

    // Used by ChatHub.OnDisconnectedAsync to find and clean up any call the
    // disconnecting user was part of (ringing or active), without the
    // caller needing to already know the call id.
    public CallSession? GetActiveCallForUser(int userId)
    {
        lock (_lock)
        {
            return _userToCallId.TryGetValue(userId, out var callId) ? _calls.GetValueOrDefault(callId) : null;
        }
    }
}
