namespace Voiceover.Server.Services;

// Maps an upload's file extension to its MIME type - shared between
// UploadController (setting StoredFile.ContentType on a fresh upload) and
// AdminService's one-time disk-to-DB migration (files on the old volume
// carry no content-type of their own, only a filename).
public static class UploadContentTypes
{
    public static string Resolve(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".zip" => "application/zip",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };
}
