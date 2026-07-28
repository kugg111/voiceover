using System.Text.Json;
using StackExchange.Redis;

namespace Voiceover.Server.Services;

// Multi-instance equivalent of InMemoryCallSignalingStore, selected instead
// of it only when REDIS_URL is configured (see Program.cs). CreateAsync's
// "both participants must be free" check has a higher correctness bar than
// presence/voice (a lost race here means two overlapping calls for the same
// user, not just a stale roster entry), so it's a Lua script - Redis runs it
// atomically server-side rather than this process doing a read-then-write
// that another replica could interleave with.
//
// Key scheme:
//   call:{callId}     -> JSON-serialized CallSession, TTL'd as a leak guard
//   call:user:{userId} -> callId, same TTL
// The TTL (CallEntryTtl) is pure hygiene, not part of the app's actual call
// lifecycle - every normal path (decline/hangup/disconnect cleanup) already
// calls RemoveAsync, same as the in-memory store never expiring anything on
// its own. It just means a call entry can't outlive a crashed cleanup path
// forever the way an in-memory one would (restart clears it instead).
public class RedisCallSignalingStore : ICallSignalingStore
{
    private static readonly TimeSpan CallEntryTtl = TimeSpan.FromHours(24);

    private const string CreateCallScript = @"
        if redis.call('EXISTS', KEYS[1]) == 1 or redis.call('EXISTS', KEYS[2]) == 1 then
            return 0
        end
        redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[3])
        redis.call('SET', KEYS[2], ARGV[1], 'EX', ARGV[3])
        redis.call('SET', KEYS[3], ARGV[2], 'EX', ARGV[3])
        return 1";

    private readonly IConnectionMultiplexer _redis;

    public RedisCallSignalingStore(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    private static string CallKey(string callId) => $"call:{callId}";
    private static string UserCallKey(int userId) => $"call:user:{userId}";

    public async Task<CallSession?> CreateAsync(int callerId, int calleeId)
    {
        var db = _redis.GetDatabase();
        var callId = $"call-{Guid.NewGuid():N}";
        var session = new CallSession(callId, callerId, calleeId, CallState.Ringing, DateTime.UtcNow);

        var keys = new RedisKey[] { UserCallKey(callerId), UserCallKey(calleeId), CallKey(callId) };
        var values = new RedisValue[] { callId, JsonSerializer.Serialize(session), (int)CallEntryTtl.TotalSeconds };
        var result = (int)await db.ScriptEvaluateAsync(CreateCallScript, keys, values);

        return result == 1 ? session : null;
    }

    public async Task<CallSession?> GetAsync(string callId)
    {
        var json = await _redis.GetDatabase().StringGetAsync(CallKey(callId));
        // Explicit (string) cast, not just json! - RedisValue has implicit
        // conversions to both string and byte[]/ReadOnlySpan<byte>, and a
        // newer JsonSerializer.Deserialize overload set (added after the
        // .NET 8 -> 10 upgrade) makes the call ambiguous (CS0121) without
        // picking one explicitly.
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<CallSession>((string)json!);
    }

    public async Task<CallSession?> AcceptAsync(string callId)
    {
        var db = _redis.GetDatabase();
        var session = await GetAsync(callId);
        if (session is null) return null;

        var updated = session with { State = CallState.Active, ConnectedAt = DateTime.UtcNow };
        await db.StringSetAsync(CallKey(callId), JsonSerializer.Serialize(updated), CallEntryTtl);
        return updated;
    }

    public async Task<CallSession?> RemoveAsync(string callId)
    {
        var db = _redis.GetDatabase();
        var session = await GetAsync(callId);
        if (session is null) return null;

        await db.KeyDeleteAsync(CallKey(callId));
        await db.KeyDeleteAsync(UserCallKey(session.CallerId));
        await db.KeyDeleteAsync(UserCallKey(session.CalleeId));
        return session;
    }

    public async Task<CallSession?> GetActiveCallForUserAsync(int userId)
    {
        var callId = await _redis.GetDatabase().StringGetAsync(UserCallKey(userId));
        return callId.IsNullOrEmpty ? null : await GetAsync(callId!);
    }
}
