namespace Voiceover.Server.Services;

// Default implementation, used whenever REDIS_URL is unset (see Program.cs) -
// no DB table, same tradeoff InMemoryPresenceStore/InMemoryVoicePresenceStore
// already make: a call is inherently ephemeral, so a server restart
// correctly drops all of them rather than needing to reconcile stale rows.
//
// Create touches two dictionaries as one logical operation (register the
// call, mark both participants busy) - lock-guarded like the presence
// store's own compound operations.
public class InMemoryCallSignalingStore : ICallSignalingStore
{
    private readonly Dictionary<string, CallSession> _calls = new();
    private readonly Dictionary<int, string> _userToCallId = new();
    private readonly object _lock = new();

    public Task<CallSession?> CreateAsync(int callerId, int calleeId)
    {
        lock (_lock)
        {
            if (_userToCallId.ContainsKey(callerId) || _userToCallId.ContainsKey(calleeId))
                return Task.FromResult<CallSession?>(null);

            var session = new CallSession($"call-{Guid.NewGuid():N}", callerId, calleeId, CallState.Ringing, DateTime.UtcNow);
            _calls[session.CallId] = session;
            _userToCallId[callerId] = session.CallId;
            _userToCallId[calleeId] = session.CallId;
            return Task.FromResult<CallSession?>(session);
        }
    }

    public Task<CallSession?> GetAsync(string callId)
    {
        lock (_lock) { return Task.FromResult(_calls.GetValueOrDefault(callId)); }
    }

    public Task<CallSession?> AcceptAsync(string callId)
    {
        lock (_lock)
        {
            if (!_calls.TryGetValue(callId, out var session)) return Task.FromResult<CallSession?>(null);
            var updated = session with { State = CallState.Active, ConnectedAt = DateTime.UtcNow };
            _calls[callId] = updated;
            return Task.FromResult<CallSession?>(updated);
        }
    }

    public Task<CallSession?> RemoveAsync(string callId)
    {
        lock (_lock)
        {
            if (!_calls.Remove(callId, out var session)) return Task.FromResult<CallSession?>(null);
            _userToCallId.Remove(session.CallerId);
            _userToCallId.Remove(session.CalleeId);
            return Task.FromResult<CallSession?>(session);
        }
    }

    public Task<CallSession?> GetActiveCallForUserAsync(int userId)
    {
        lock (_lock)
        {
            return Task.FromResult(_userToCallId.TryGetValue(userId, out var callId) ? _calls.GetValueOrDefault(callId) : null);
        }
    }
}
