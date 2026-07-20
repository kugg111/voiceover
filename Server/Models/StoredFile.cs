namespace Voiceover.Server.Models;

// Backs UploadController - avatars/icons/attachments live as rows here
// (Postgres bytea) instead of on the Railway volume's filesystem, so a
// redeploy/restart or a volume change can never lose them and the app no
// longer depends on any persistent local disk. FileName is the same
// GUID+extension UploadController already generated for the old on-disk
// path, kept as the primary key so every existing AvatarUrl/IconUrl/
// AttachmentUrl ("/uploads/{FileName}") column across User/GuildServer/
// Message needed zero changes - only where those bytes are read from
// changed, not the URLs that point at them. Every upload is content-
// addressed by a freshly generated GUID (UploadController never reuses or
// overwrites a name), so a given row's Data never changes after insert -
// safe to cache aggressively on the way out.
public class StoredFile
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public byte[] Data { get; set; } = [];
    public int Size { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
