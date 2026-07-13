using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace Voiceover.Client.Services;

// Shared across every AvatarView instance - before this existed, each row in
// every member/message/friend/DM list built its own BitmapImage straight
// from the URL, so the same popular avatar got independently re-downloaded
// by every single row that showed it (and again on every list refresh).
// Caches both in memory (this session) and on disk (across restarts).
//
// Safe to cache forever, no staleness/invalidation logic needed: avatar
// URLs are content-addressed - UploadController mints a fresh GUID filename
// on every upload rather than overwriting one, so a given URL's bytes never
// change over its lifetime. Changing your avatar produces a new URL, not
// new content at the old one.
public static class AvatarImageCache
{
    private static readonly ConcurrentDictionary<string, BitmapImage> MemoryCache = new();

    // Coalesces concurrent requests for the same not-yet-cached URL (e.g.
    // the same avatar rendering in a dozen message rows at once on first
    // load of a channel) into a single download instead of a dozen.
    private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> InFlight = new();

    private static readonly HttpClient Http = new();

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Voiceover", "avatarcache");

    public static async Task<BitmapImage?> GetAsync(string url)
    {
        if (MemoryCache.TryGetValue(url, out var cached)) return cached;

        try
        {
            return await InFlight.GetOrAdd(url, LoadAsync);
        }
        finally
        {
            InFlight.TryRemove(url, out _);
        }
    }

    private static async Task<BitmapImage?> LoadAsync(string url)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var cacheFile = Path.Combine(CacheDir, ToCacheFileName(url));

            byte[] bytes;
            if (File.Exists(cacheFile))
            {
                bytes = await File.ReadAllBytesAsync(cacheFile);
            }
            else
            {
                bytes = await Http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(cacheFile, bytes);
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // Avatars are uploaded at whatever resolution the user's source
            // photo happened to be (easily 1000px+) but the largest AvatarView
            // anywhere in the UI renders at 80px - decoding at full native
            // resolution just to downscale it in the visual tree wastes
            // decode time and, since this bitmap is cached forever (see the
            // type-level comment), that waste sits in memory for the rest of
            // the session for every distinct avatar ever shown. 160px covers
            // the largest on-screen size with headroom for high-DPI displays
            // without ballooning back up to arbitrary source resolution.
            bitmap.DecodePixelWidth = 160;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            // Required to hand a BitmapImage across threads/store it in a
            // static cache read by many AvatarView instances - also makes
            // it immutable, which is exactly right for cached content.
            bitmap.Freeze();

            MemoryCache[url] = bitmap;
            return bitmap;
        }
        catch
        {
            // Bad URL, 404, network error, corrupt cached file - callers
            // treat a null result as "fall back to the initial-letter
            // circle", not a crash.
            return null;
        }
    }

    private static string ToCacheFileName(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        return string.IsNullOrEmpty(ext) ? hash : hash + ext;
    }
}
