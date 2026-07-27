using Voiceover.Server.Dtos;

namespace Voiceover.Server.Services;

// Tracks which SignalR connection is currently in which voice channel. A
// connection is in at most one voice channel at a time (the client always
// leaves before joining another). Used to seed newly-joining clients with
// the current roster, and to clean up presence on abrupt disconnects
// (crash, network drop) where LeaveVoiceChannel is never explicitly called.
// See InMemoryVoicePresenceStore (single instance, default) and
// RedisVoicePresenceStore (multi-instance, opt-in via REDIS_URL).
public interface IVoicePresenceStore
{
    // Returns the roster that existed before this connection joined, then adds it.
    Task<List<VoiceParticipant>> JoinAsync(string connectionId, int channelId, int serverId, int userId, string username, string? avatarUrl);

    Task<(int ChannelId, int ServerId, int UserId, string Username)?> LeaveAsync(string connectionId);

    // Looks up the server a still-connected connection is in, without removing
    // it - used by NotifySpeaking, which doesn't leave the voice channel.
    Task<int?> GetServerIdAsync(string connectionId);

    // Used by NotifySpeaking/NotifyMuted/NotifyDeafened to confirm the caller
    // actually joined this exact voice channel (via JoinVoiceChannel) before
    // trusting their self-reported state enough to broadcast it - otherwise
    // any authenticated user could call one of those with an arbitrary
    // channelId and inject a spoofed event into a voice channel they were
    // never part of.
    Task<bool> IsInChannelAsync(string connectionId, int channelId);

    Task<List<VoiceParticipant>> GetRosterAsync(int channelId);

    // Batched form of GetRosterAsync - GetVoiceRostersForServer builds one
    // roster per channel in an already-materialized list, which can't await
    // one at a time inside a LINQ-to-Objects .Select(). Also lets the Redis
    // implementation fetch every channel in parallel instead of serially.
    Task<Dictionary<int, List<VoiceParticipant>>> GetRostersAsync(IEnumerable<int> channelIds);
}
