namespace Voiceover.Server.Models;

public class DirectMessage
{
    public int Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public int SenderId { get; set; }
    public int RecipientId { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }

    // True for every message sent after E2EE shipped - Content is opaque
    // ciphertext the server never decrypts, encrypted client-side with a
    // key derived from the two participants' ECDH keypairs (see
    // Client/Services/E2eeService.cs). False only for rows written before
    // this existed, when Content is still ciphertext but under the old
    // server-held MessageEncryptionService key instead - those get
    // decrypted server-side one last time when read (see
    // DirectMessagesController) since the server is the only party that
    // can still read them at all.
    public bool IsE2ee { get; set; } = true;
}
