using System.Collections.Concurrent;

namespace Voiceover.Server.Services;

// Caches the "who should hear about this user's presence changes" answer
// (accepted friend ids + joined server ids) so ChatHub.BroadcastPresenceChangeAsync
// doesn't run two DB queries on every single Online/Away transition - at
// real usage volumes (idle detection flips on lock/unlock, focus changes)
// this was the hottest per-transition cost in the hub.
//
// Time-based rather than write-invalidated like UserAvatarCache: friend/
// membership changes happen at several different call sites (accept/remove
// friend, join/leave/kick from a server), and wiring explicit invalidation
// into all of them is a lot of surface for a cache whose only job is
// deciding a notification audience - a short TTL means a just-added friend
// or server might miss one presence ping for a few seconds, which is
// harmless (their own next transition, or a page refresh, catches up), so
// this stays a simple self-healing cache instead of a fully wired one.
public class PresenceAudienceCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private record Entry(List<int> FriendIds, List<int> ServerIds, DateTime CachedAtUtc);

    private readonly ConcurrentDictionary<int, Entry> _cache = new();

    public bool TryGet(int userId, out List<int> friendIds, out List<int> serverIds)
    {
        if (_cache.TryGetValue(userId, out var entry) && DateTime.UtcNow - entry.CachedAtUtc < Ttl)
        {
            friendIds = entry.FriendIds;
            serverIds = entry.ServerIds;
            return true;
        }

        friendIds = new List<int>();
        serverIds = new List<int>();
        return false;
    }

    public void Set(int userId, List<int> friendIds, List<int> serverIds) =>
        _cache[userId] = new Entry(friendIds, serverIds, DateTime.UtcNow);
}
