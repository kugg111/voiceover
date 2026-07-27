using StackExchange.Redis;

namespace Voiceover.Server.Services;

// Multi-instance equivalent of InMemoryPresenceStore, selected instead of it
// only when REDIS_URL is configured (see Program.cs) - every replica reads/
// writes the same Redis keys, so "who's online" agrees across instances
// instead of each replica only knowing about its own connections.
//
// Key scheme:
//   presence:conn:{connectionId}          -> userId
//   presence:user:{userId}:connections    -> Set<connectionId>
//   presence:user:{userId}:state          -> "Online"/"Away" (absent = Offline)
public class RedisPresenceStore : IPresenceStore
{
    private readonly IConnectionMultiplexer _redis;

    public RedisPresenceStore(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    private static string ConnKey(string connectionId) => $"presence:conn:{connectionId}";
    private static string UserConnectionsKey(int userId) => $"presence:user:{userId}:connections";
    private static string UserStateKey(int userId) => $"presence:user:{userId}:state";

    public async Task<bool> ConnectAsync(int userId, string connectionId)
    {
        var db = _redis.GetDatabase();
        await db.StringSetAsync(ConnKey(connectionId), userId);
        await db.SetAddAsync(UserConnectionsKey(userId), connectionId);
        var count = await db.SetLengthAsync(UserConnectionsKey(userId));
        await db.StringSetAsync(UserStateKey(userId), "Online");
        // This connection was just added, so a post-add count of 1 means it
        // was the only (and therefore first) one - equivalent to the
        // in-memory version's pre-add "connections.Count == 0" check.
        return count == 1;
    }

    public async Task<(int UserId, bool WasLastConnection)?> DisconnectAsync(string connectionId)
    {
        var db = _redis.GetDatabase();
        var userIdValue = await db.StringGetAsync(ConnKey(connectionId));
        if (userIdValue.IsNullOrEmpty) return null;

        var userId = (int)userIdValue;
        await db.KeyDeleteAsync(ConnKey(connectionId));
        await db.SetRemoveAsync(UserConnectionsKey(userId), connectionId);
        var remaining = await db.SetLengthAsync(UserConnectionsKey(userId));
        if (remaining > 0) return (userId, false);

        await db.KeyDeleteAsync(UserConnectionsKey(userId));
        await db.KeyDeleteAsync(UserStateKey(userId));
        return (userId, true);
    }

    public Task SetStateAsync(int userId, string state) =>
        _redis.GetDatabase().StringSetAsync(UserStateKey(userId), state);

    public async Task<string> GetStateAsync(int userId)
    {
        var value = await _redis.GetDatabase().StringGetAsync(UserStateKey(userId));
        return value.IsNullOrEmpty ? "Offline" : value!;
    }

    public async Task<Dictionary<int, string>> GetStatesAsync(IEnumerable<int> userIds)
    {
        var ids = userIds.ToList();
        if (ids.Count == 0) return new Dictionary<int, string>();

        var db = _redis.GetDatabase();
        var keys = ids.Select(id => (RedisKey)UserStateKey(id)).ToArray();
        var values = await db.StringGetAsync(keys);

        var result = new Dictionary<int, string>();
        for (var i = 0; i < ids.Count; i++)
            result[ids[i]] = values[i].IsNullOrEmpty ? "Offline" : values[i]!;
        return result;
    }
}
