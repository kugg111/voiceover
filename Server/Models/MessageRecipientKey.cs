namespace Voiceover.Server.Models;

// One row per (message, recipient) - see Client/Services/E2eeService.cs's
// per-message envelope scheme for channel messages. Every channel message is
// encrypted once under a fresh random AES-256 key (Message.Content), and
// that one-time key is wrapped separately for every member who was in the
// channel's server at send time, including the sender. Replaces the old
// ServerMemberKey scheme (one shared key per server, wrapped per member),
// which had no way to onboard a member unless some other already-onboarded
// member happened to be online to hand them a copy - two brand new members
// joining together could deadlock forever with neither able to grant the
// other access. Fanning the wrap out per-message instead means a member just
// needs to already be a member when a message is SENT, with no separate
// grant step ever required.
public class MessageRecipientKey
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public int UserId { get; set; }
    public string WrappedKey { get; set; } = string.Empty;
}
