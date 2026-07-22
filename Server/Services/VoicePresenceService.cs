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
    // ServerId is stored alongside ChannelId so OnDisconnectedAsync - which only
    // has the connectionId, no way to ask the disconnecting client which server
    // it was viewing - can still know which server-presence group to notify.
    private record Entry(int ChannelId, int ServerId, int UserId, string Username, string? AvatarUrl);

    private readonly ConcurrentDictionary<string, Entry> _connections = new();

    // Returns the roster that existed before this connection joined, then adds it.
    // AvatarUrl is cached here at join-time (same as Username already was) rather
    // than looked up fresh on every roster read - a changed avatar won't show up
    // in an existing voice session until the next join, same staleness tradeoff
    // the cached username already has.
    public List<VoiceParticipant> Join(string connectionId, int channelId, int serverId, int userId, string username, string? avatarUrl)
    {
        var existing = GetRoster(channelId);
        _connections[connectionId] = new Entry(channelId, serverId, userId, username, avatarUrl);
        return existing;
    }

    public (int ChannelId, int ServerId, int UserId, string Username)? Leave(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var entry))
            return (entry.ChannelId, entry.ServerId, entry.UserId, entry.Username);
        return null;
    }

    // Looks up the server a still-connected connection is in, without removing
    // it - used by NotifySpeaking, which doesn't leave the voice channel.
    public int? GetServerId(string connectionId) =>
        _connections.TryGetValue(connectionId, out var entry) ? entry.ServerId : null;

    // Used by NotifySpeaking/NotifyMuted/NotifyDeafened to confirm the caller
    // actually joined this exact voice channel (via JoinVoiceChannel) before
    // trusting their self-reported state enough to broadcast it - otherwise
    // any authenticated user could call one of those with an arbitrary
    // channelId and inject a spoofed event into a voice channel they were
    // never part of.
    public bool IsInChannel(string connectionId, int channelId) =>
        _connections.TryGetValue(connectionId, out var entry) && entry.ChannelId == channelId;

    public List<VoiceParticipant> GetRoster(int channelId) =>
        _connections.Values
            .Where(e => e.ChannelId == channelId)
            .Select(e => new VoiceParticipant(e.UserId, e.Username, e.AvatarUrl))
            .ToList();
}
