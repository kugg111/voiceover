namespace Voiceover.Server.Models;

// One row per (server, member) - the server's shared channel-message key,
// wrapped so only that specific member can unwrap it (see
// Client/Services/E2eeService.cs). The server only ever stores/relays these
// opaque blobs; it never has a usable key itself, same trust model as DMs.
//
// WrappedByUserId records whose ECDH keypair the wrap was derived against -
// the unwrapping member needs this to know which public key to redo the
// derivation with (see E2eeService's per-pair wrap-key derivation). For a
// server's very first key (bootstrap, see ServersController.SetServerKey)
// this equals UserId itself - a member "wrapping for themselves" using a
// key only their own private key can reproduce.
public class ServerMemberKey
{
    public int Id { get; set; }
    public int GuildServerId { get; set; }
    public int UserId { get; set; }
    public int WrappedByUserId { get; set; }
    public string WrappedKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
