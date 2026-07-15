using Voiceover.Server.Data;
using Voiceover.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Services;

// Cascade-safe cleanup for a GuildServer, shared between ServersController's
// owner-initiated delete and UsersController.DeleteMyAccount's "no
// successor, just delete the server" branch - previously duplicated inline
// in the latter, and missing BannedUser/ModerationLogEntry cleanup even
// there. Channels, their Messages, Memberships, and Invites all cascade
// automatically once GuildServers.Remove is called (real, required FKs -
// see AppDbContext); MessageReaction, ServerMemberKey, BannedUser, and
// ModerationLogEntry have no FK/cascade configured at all (see each of
// their own class comments), so they'd otherwise be left as permanently
// orphaned rows.
public class ServerDeletionService
{
    private readonly AppDbContext _db;
    public ServerDeletionService(AppDbContext db) => _db = db;

    // Queues the removal - caller still owns the transaction and must call
    // SaveChangesAsync (matches every other multi-entity mutation in this
    // codebase, e.g. ServersController.Ban).
    public async Task QueueDeleteAsync(GuildServer server)
    {
        var messageIds = await _db.Messages.Where(m => m.Channel!.GuildServerId == server.Id).Select(m => m.Id).ToListAsync();
        _db.MessageReactions.RemoveRange(_db.MessageReactions.Where(r => messageIds.Contains(r.MessageId)));
        _db.ServerMemberKeys.RemoveRange(_db.ServerMemberKeys.Where(k => k.GuildServerId == server.Id));
        _db.BannedUsers.RemoveRange(_db.BannedUsers.Where(b => b.GuildServerId == server.Id));
        _db.ModerationLogEntries.RemoveRange(_db.ModerationLogEntries.Where(m => m.GuildServerId == server.Id));
        _db.GuildServers.Remove(server);
    }
}
