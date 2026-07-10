using System.Collections.Concurrent;
using Voiceover.Server.Dtos;

namespace Voiceover.Server.Services;

// Tracks which SignalR connection is currently in which voice channel, in memory.
// A connection is in at most one voice channel at a time (the client always leaves
// before joining another). Used to seed newly-joining clients with the current
// roster, and to clean up presence on abrupt disconnects (crash, network drop)
// where LeaveVoiceChannel is never explicitly called.
public class VoicePresenceService
{
    private record Entry(int ChannelId, int UserId, string Username);

    private readonly ConcurrentDictionary<string, Entry> _connections = new();

    // Returns the roster that existed before this connection joined, then adds it.
    public List<VoiceParticipant> Join(string connectionId, int channelId, int userId, string username)
    {
        var existing = GetRoster(channelId);
        _connections[connectionId] = new Entry(channelId, userId, username);
        return existing;
    }

    public (int ChannelId, int UserId, string Username)? Leave(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var entry))
            return (entry.ChannelId, entry.UserId, entry.Username);
        return null;
    }

    public List<VoiceParticipant> GetRoster(int channelId) =>
        _connections.Values
            .Where(e => e.ChannelId == channelId)
            .Select(e => new VoiceParticipant(e.UserId, e.Username))
            .ToList();
}
