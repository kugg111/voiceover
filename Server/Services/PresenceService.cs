namespace Voiceover.Server.Services;

// Tracks who's online/away, in memory - no DB involvement, same tradeoff
// as VoicePresenceService (a server restart correctly resets everyone to
// offline until they reconnect, which is correct - it can't tell the
// difference from an actual disconnect at that point). A user can have
// several connections open at once (multiple windows/devices); they're
// only actually Offline once every one of them has dropped, which is why
// Connect/Disconnect track a set of connection ids per user rather than
// just a single state flag.
public class PresenceService
{
    private readonly Dictionary<string, int> _connectionToUser = new();
    private readonly Dictionary<int, HashSet<string>> _userConnections = new();
    private readonly Dictionary<int, string> _userState = new(); // "Online"/"Away" - absent means Offline
    private readonly object _lock = new();

    // Returns true if this was the user's first connection (they were fully
    // offline before) - callers only broadcast "Online" when this is true,
    // so a second window/device opening doesn't re-announce someone who's
    // already known to be online.
    public bool Connect(int userId, string connectionId)
    {
        lock (_lock)
        {
            _connectionToUser[connectionId] = userId;

            if (!_userConnections.TryGetValue(userId, out var connections))
            {
                connections = new HashSet<string>();
                _userConnections[userId] = connections;
            }

            var wasOffline = connections.Count == 0;
            connections.Add(connectionId);
            _userState[userId] = "Online";
            return wasOffline;
        }
    }

    // Returns the userId and whether this was their last open connection -
    // callers only broadcast "Offline" when it was, so closing one of
    // several open windows doesn't mark someone offline while they're
    // still connected elsewhere.
    public (int UserId, bool WasLastConnection)? Disconnect(string connectionId)
    {
        lock (_lock)
        {
            if (!_connectionToUser.Remove(connectionId, out var userId)) return null;

            if (!_userConnections.TryGetValue(userId, out var connections))
                return (userId, true);

            connections.Remove(connectionId);
            if (connections.Count > 0) return (userId, false);

            _userConnections.Remove(userId);
            _userState.Remove(userId);
            return (userId, true);
        }
    }

    // Only meaningful for an already-connected user - ChatHub.SetPresenceState
    // only ever calls this from a live connection.
    public void SetState(int userId, string state)
    {
        lock (_lock) { _userState[userId] = state; }
    }

    public string GetState(int userId)
    {
        lock (_lock) { return _userState.GetValueOrDefault(userId, "Offline"); }
    }
}
