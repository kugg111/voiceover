using System.Collections.Concurrent;

namespace Voiceover.Server.Services;

// Caches userId -> AvatarUrl in memory so ChatHub.SendMessage doesn't hit
// the DB for a full User row on every single message send just to read a
// field that almost never changes. Avatars change rarely (UsersController.
// SetAvatar is the only writer) compared to how often messages get sent, so
// a cache with a single explicit invalidation point is a clear win over a
// per-message lookup - same "in-memory, no DB involvement" tradeoff
// PresenceService already makes for online/away state.
public class UserAvatarCache
{
    private readonly ConcurrentDictionary<int, string?> _avatarUrls = new();

    // Called by UsersController.SetAvatar right after persisting - the one
    // and only place an avatar can change, so this is the one and only
    // place the cache needs to be told about it.
    public void Set(int userId, string? avatarUrl) => _avatarUrls[userId] = avatarUrl;

    public bool TryGet(int userId, out string? avatarUrl) => _avatarUrls.TryGetValue(userId, out avatarUrl);
}
