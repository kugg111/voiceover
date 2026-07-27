namespace Voiceover.Server.Services;

// Default implementation, used whenever REDIS_URL is unset (see Program.cs) -
// in-process only, same tradeoff as before this was pulled behind
// IPresenceStore: a server restart correctly resets everyone to offline
// until they reconnect, which is correct - it can't tell the difference from
// an actual disconnect at that point. Not safe to share across instances
// (see RedisPresenceStore for the multi-instance equivalent).
public class InMemoryPresenceStore : IPresenceStore
{
    private readonly Dictionary<string, int> _connectionToUser = new();
    private readonly Dictionary<int, HashSet<string>> _userConnections = new();
    private readonly Dictionary<int, string> _userState = new(); // "Online"/"Away" - absent means Offline
    private readonly object _lock = new();

    public Task<bool> ConnectAsync(int userId, string connectionId)
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
            return Task.FromResult(wasOffline);
        }
    }

    public Task<(int UserId, bool WasLastConnection)?> DisconnectAsync(string connectionId)
    {
        lock (_lock)
        {
            if (!_connectionToUser.Remove(connectionId, out var userId))
                return Task.FromResult<(int, bool)?>(null);

            if (!_userConnections.TryGetValue(userId, out var connections))
                return Task.FromResult<(int, bool)?>((userId, true));

            connections.Remove(connectionId);
            if (connections.Count > 0)
                return Task.FromResult<(int, bool)?>((userId, false));

            _userConnections.Remove(userId);
            _userState.Remove(userId);
            return Task.FromResult<(int, bool)?>((userId, true));
        }
    }

    public Task SetStateAsync(int userId, string state)
    {
        lock (_lock) { _userState[userId] = state; }
        return Task.CompletedTask;
    }

    public Task<string> GetStateAsync(int userId)
    {
        lock (_lock) { return Task.FromResult(_userState.GetValueOrDefault(userId, "Offline")); }
    }

    public Task<Dictionary<int, string>> GetStatesAsync(IEnumerable<int> userIds)
    {
        lock (_lock)
        {
            var result = userIds.ToDictionary(id => id, id => _userState.GetValueOrDefault(id, "Offline"));
            return Task.FromResult(result);
        }
    }
}
