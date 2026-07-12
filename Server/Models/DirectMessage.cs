namespace Voiceover.Server.Models;

public class DirectMessage
{
    public int Id { get; set; }

    // Opaque E2EE ciphertext - encrypted client-side with a key derived
    // from the two participants' ECDH keypairs (see
    // Client/Services/E2eeService.cs). The server never has a usable key
    // and never attempts to decrypt this.
    public string Content { get; set; } = string.Empty;
    public int SenderId { get; set; }
    public int RecipientId { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }
}
