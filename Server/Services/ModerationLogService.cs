using Voiceover.Server.Data;
using Voiceover.Server.Hubs;
using Voiceover.Server.Models;
using Microsoft.AspNetCore.SignalR;

namespace Voiceover.Server.Services;

// Single write path for every moderation action's audit trail - scoped
// (wraps AppDbContext per-request, same lifetime as PermissionService)
// rather than a singleton, since it just inserts a row through the
// request's own DbContext instead of owning any state of its own.
public class ModerationLogService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<ChatHub> _hub;

    public ModerationLogService(AppDbContext db, IHubContext<ChatHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    public async Task LogAsync(int serverId, int actorId, string actorUsername, string action,
        int? targetId = null, string? targetUsername = null, string? details = null)
    {
        _db.ModerationLogEntries.Add(new ModerationLogEntry
        {
            GuildServerId = serverId,
            ActorUserId = actorId,
            ActorUsername = actorUsername,
            Action = action,
            TargetUserId = targetId,
            TargetUsername = targetUsername,
            Details = details
        });
        await _db.SaveChangesAsync();

        // Every moderation action funnels through here, so this one
        // broadcast covers live-refreshing ModerationLogWindow for anyone
        // who has it open, regardless of which specific action fired.
        await _hub.Clients.Group(HubGroups.ServerPresence(serverId)).SendAsync("ModerationLogChanged", serverId);
    }
}
