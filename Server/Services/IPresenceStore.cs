namespace Voiceover.Server.Services;

// Tracks who's online/away, behind a store abstraction so single-instance
// deployments keep the original in-memory implementation (InMemoryPresenceStore)
// while a multi-instance deployment can opt into RedisPresenceStore instead -
// see Program.cs for the REDIS_URL-gated selection. A user can have several
// connections open at once (multiple windows/devices); they're only actually
// Offline once every one of them has dropped, which is why Connect/Disconnect
// track a set of connection ids per user rather than just a single state flag.
public interface IPresenceStore
{
    // Returns true if this was the user's first connection (they were fully
    // offline before) - callers only broadcast "Online" when this is true,
    // so a second window/device opening doesn't re-announce someone who's
    // already known to be online.
    Task<bool> ConnectAsync(int userId, string connectionId);

    // Returns the userId and whether this was their last open connection -
    // callers only broadcast "Offline" when it was, so closing one of
    // several open windows doesn't mark someone offline while they're
    // still connected elsewhere.
    Task<(int UserId, bool WasLastConnection)?> DisconnectAsync(string connectionId);

    // Only meaningful for an already-connected user - ChatHub.SetPresenceState
    // only ever calls this from a live connection.
    Task SetStateAsync(int userId, string state);

    Task<string> GetStateAsync(int userId);

    // Batched form of GetStateAsync - FriendsController/ServersController
    // both need a state per row of an already-materialized list, which can't
    // await one at a time inside a LINQ-to-Objects .Select(). Also lets the
    // Redis implementation fetch every id in one round trip instead of N.
    Task<Dictionary<int, string>> GetStatesAsync(IEnumerable<int> userIds);
}
