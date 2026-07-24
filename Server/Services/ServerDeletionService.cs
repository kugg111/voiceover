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
// see AppDbContext), and MessageRecipientKey cascades along with its
// Message the same way; MessageReaction, BannedUser, and
// ModerationLogEntry have no FK/cascade configured at all (see each of
// their own class comments), so they'd otherwise be left as permanently
// orphaned rows.
public class ServerDeletionService
{
    private readonly AppDbContext _db;
    public ServerDeletionService(AppDbContext db) => _db = db;

    // Queues the removal - caller still owns the transaction and must call
    // SaveChangesAsync (matches every other multi-entity mutation in this
    // codebase, e.g. ServersController.Ban). Still Task-returning (not
    // void) so existing `await QueueDeleteAsync(...)` call sites don't need
    // to change - no longer literally `async` since nothing here awaits
    // anymore (RemoveRange(IQueryable) below runs synchronously, same as
    // it already did for BannedUsers/ModerationLogEntries).
    public Task QueueDeleteAsync(GuildServer server)
    {
        // A correlated EXISTS subquery, not "load every message id in the
        // server into memory, then WHERE MessageId IN (...) that whole
        // list" (the previous approach) - a server with years of message
        // history could have hundreds of thousands of ids, which both
        // pulls that much into app memory and risks the resulting IN
        // clause blowing past Npgsql/Postgres's practical parameter
        // limits. The subquery scales with Postgres's own query planner
        // instead, and each check is a primary-key lookup (m.Id ==
        // r.MessageId) so it stays cheap regardless of server size.
        _db.MessageReactions.RemoveRange(_db.MessageReactions.Where(r =>
            _db.Messages.Any(m => m.Id == r.MessageId && m.Channel!.GuildServerId == server.Id)));
        _db.BannedUsers.RemoveRange(_db.BannedUsers.Where(b => b.GuildServerId == server.Id));
        _db.ModerationLogEntries.RemoveRange(_db.ModerationLogEntries.Where(m => m.GuildServerId == server.Id));
        _db.GuildServers.Remove(server);
        return Task.CompletedTask;
    }
}
