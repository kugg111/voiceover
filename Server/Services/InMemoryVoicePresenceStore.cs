using System.Collections.Concurrent;
using Voiceover.Server.Dtos;

namespace Voiceover.Server.Services;

// Default implementation, used whenever REDIS_URL is unset (see Program.cs).
public class InMemoryVoicePresenceStore : IVoicePresenceStore
{
    // ServerId is stored alongside ChannelId so OnDisconnectedAsync - which only
    // has the connectionId, no way to ask the disconnecting client which server
    // it was viewing - can still know which server-presence group to notify.
    private record Entry(int ChannelId, int ServerId, int UserId, string Username, string? AvatarUrl);

    private readonly ConcurrentDictionary<string, Entry> _connections = new();

    // AvatarUrl is cached here at join-time (same as Username already was) rather
    // than looked up fresh on every roster read - a changed avatar won't show up
    // in an existing voice session until the next join, same staleness tradeoff
    // the cached username already has.
    public Task<List<VoiceParticipant>> JoinAsync(string connectionId, int channelId, int serverId, int userId, string username, string? avatarUrl)
    {
        var existing = GetRosterSync(channelId);
        _connections[connectionId] = new Entry(channelId, serverId, userId, username, avatarUrl);
        return Task.FromResult(existing);
    }

    public Task<(int ChannelId, int ServerId, int UserId, string Username)?> LeaveAsync(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var entry))
            return Task.FromResult<(int, int, int, string)?>((entry.ChannelId, entry.ServerId, entry.UserId, entry.Username));
        return Task.FromResult<(int, int, int, string)?>(null);
    }

    public Task<int?> GetServerIdAsync(string connectionId) =>
        Task.FromResult(_connections.TryGetValue(connectionId, out var entry) ? entry.ServerId : (int?)null);

    public Task<bool> IsInChannelAsync(string connectionId, int channelId) =>
        Task.FromResult(_connections.TryGetValue(connectionId, out var entry) && entry.ChannelId == channelId);

    public Task<List<VoiceParticipant>> GetRosterAsync(int channelId) => Task.FromResult(GetRosterSync(channelId));

    public Task<Dictionary<int, List<VoiceParticipant>>> GetRostersAsync(IEnumerable<int> channelIds) =>
        Task.FromResult(channelIds.ToDictionary(id => id, GetRosterSync));

    private List<VoiceParticipant> GetRosterSync(int channelId) =>
        _connections.Values
            .Where(e => e.ChannelId == channelId)
            .Select(e => new VoiceParticipant(e.UserId, e.Username, e.AvatarUrl))
            .ToList();
}
