using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Voiceover.Client.Services;

// Near-twin of AttachmentImageCache, but caches to a local file path
// instead of a BitmapImage - NAudio's AudioFileReader (see
// VoiceMessagePlayer) needs a real seekable file, not an in-memory decoded
// object. Same authenticated-fetch + SHA-256-keyed disk cache reasoning:
// voice-message URLs are content-addressed (fresh GUID per upload), so
// caching forever with no invalidation is safe.
public static class AttachmentAudioCache
{
    private static readonly HttpClient Http = new();

    // Set once at login, same shape as AttachmentImageCache.AccessTokenProvider.
    public static Func<Task<string?>>? AccessTokenProvider { get; set; }

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Voiceover", "attachmentaudiocache");

    // Returns a local file path with the audio's bytes already on disk, or
    // null on any failure (bad URL, 404, network error) - callers treat
    // null as "can't play this."
    public static async Task<string?> GetFileAsync(string url)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var cacheFile = Path.Combine(CacheDir, ToCacheFileName(url));
            if (File.Exists(cacheFile)) return cacheFile;

            var bytes = await FetchAsync(url);
            await File.WriteAllBytesAsync(cacheFile, bytes);
            return cacheFile;
        }
        catch
        {
            return null;
        }
    }

    private static string ToCacheFileName(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        return string.IsNullOrEmpty(ext) ? hash : hash + ext;
    }

    // /uploads requires auth (see Program.cs) - see
    // AvatarImageCache.FetchAsync for the full reasoning, same shape here.
    private static async Task<byte[]> FetchAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var token = AccessTokenProvider is null ? null : await AccessTokenProvider();
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }
}
