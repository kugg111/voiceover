using System.Text.Json;
using StackExchange.Redis;
using Voiceover.Server.Dtos;

namespace Voiceover.Server.Services;

// Multi-instance equivalent of InMemoryVoicePresenceStore, selected instead
// of it only when REDIS_URL is configured (see Program.cs).
//
// Key scheme:
//   voice:conn:{connectionId}          -> JSON-serialized Entry
//   voice:channel:{channelId}:members  -> Set<connectionId>
public class RedisVoicePresenceStore : IVoicePresenceStore
{
    private record Entry(int ChannelId, int ServerId, int UserId, string Username, string? AvatarUrl);

    private readonly IConnectionMultiplexer _redis;

    public RedisVoicePresenceStore(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    private static string ConnKey(string connectionId) => $"voice:conn:{connectionId}";
    private static string ChannelMembersKey(int channelId) => $"voice:channel:{channelId}:members";

    public async Task<List<VoiceParticipant>> JoinAsync(string connectionId, int channelId, int serverId, int userId, string username, string? avatarUrl)
    {
        var existing = await GetRosterAsync(channelId);

        var db = _redis.GetDatabase();
        var entry = new Entry(channelId, serverId, userId, username, avatarUrl);
        await db.StringSetAsync(ConnKey(connectionId), JsonSerializer.Serialize(entry));
        await db.SetAddAsync(ChannelMembersKey(channelId), connectionId);

        return existing;
    }

    public async Task<(int ChannelId, int ServerId, int UserId, string Username)?> LeaveAsync(string connectionId)
    {
        var db = _redis.GetDatabase();
        var json = await db.StringGetAsync(ConnKey(connectionId));
        if (json.IsNullOrEmpty) return null;

        var entry = JsonSerializer.Deserialize<Entry>(json!)!;
        await db.KeyDeleteAsync(ConnKey(connectionId));
        await db.SetRemoveAsync(ChannelMembersKey(entry.ChannelId), connectionId);
        return (entry.ChannelId, entry.ServerId, entry.UserId, entry.Username);
    }

    public async Task<int?> GetServerIdAsync(string connectionId)
    {
        var entry = await GetEntryAsync(connectionId);
        return entry?.ServerId;
    }

    public async Task<bool> IsInChannelAsync(string connectionId, int channelId)
    {
        var entry = await GetEntryAsync(connectionId);
        return entry is not null && entry.ChannelId == channelId;
    }

    public async Task<List<VoiceParticipant>> GetRosterAsync(int channelId)
    {
        var db = _redis.GetDatabase();
        var memberIds = await db.SetMembersAsync(ChannelMembersKey(channelId));
        if (memberIds.Length == 0) return new List<VoiceParticipant>();

        var jsonValues = await db.StringGetAsync(memberIds.Select(m => (RedisKey)ConnKey(m!)).ToArray());
        return jsonValues
            .Where(v => !v.IsNullOrEmpty)
            .Select(v => JsonSerializer.Deserialize<Entry>(v!)!)
            .Select(e => new VoiceParticipant(e.UserId, e.Username, e.AvatarUrl))
            .ToList();
    }

    public async Task<Dictionary<int, List<VoiceParticipant>>> GetRostersAsync(IEnumerable<int> channelIds)
    {
        var ids = channelIds.ToList();
        var rosters = await Task.WhenAll(ids.Select(GetRosterAsync));
        var result = new Dictionary<int, List<VoiceParticipant>>();
        for (var i = 0; i < ids.Count; i++) result[ids[i]] = rosters[i];
        return result;
    }

    private async Task<Entry?> GetEntryAsync(string connectionId)
    {
        var json = await _redis.GetDatabase().StringGetAsync(ConnKey(connectionId));
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<Entry>(json!);
    }
}
