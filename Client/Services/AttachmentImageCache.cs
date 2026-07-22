using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace Voiceover.Client.Services;

// Same shape as AvatarImageCache - memory + disk cache keyed by URL, so
// scrolling a message list with inline images (which re-virtualizes rows via
// the ListBox in MainWindow) doesn't re-download/re-decode the same image
// every time it scrolls back into view. Unlike avatars, attachment URLs are
// also content-addressed (UploadController mints a fresh GUID filename per
// upload), so caching forever with no invalidation is safe here too.
public static class AttachmentImageCache
{
    // Unlike the avatar cache (naturally bounded by distinct-user count),
    // a long session scrolling image-heavy channel history can decode
    // arbitrarily many distinct attachment URLs - cap how many decoded
    // bitmaps stay resident. A plain bounded-FIFO eviction (oldest-inserted
    // first) rather than true LRU - simpler, and "good enough" here since
    // the failure mode of evicting a still-relevant image early is just a
    // cheap re-download/re-decode next time it scrolls into view, not a
    // correctness issue. The on-disk cache (below) stays unbounded - disk
    // is cheap and content-addressed uploads mean it never grows from
    // duplicate content anyway.
    private const int MaxCachedImages = 200;
    private static readonly ConcurrentQueue<string> InsertionOrder = new();

    private static readonly ConcurrentDictionary<string, BitmapImage> MemoryCache = new();
    private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> InFlight = new();
    private static readonly HttpClient Http = new();

    // Set once at login (see MainWindow.xaml.cs, right next to
    // SignalRService.ConnectAsync's own accessTokenProvider) - see
    // AvatarImageCache.AccessTokenProvider for the full reasoning, same
    // shape here.
    public static Func<Task<string?>>? AccessTokenProvider { get; set; }

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Voiceover", "attachmentcache");

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
                bytes = await FetchAsync(url);
                await File.WriteAllBytesAsync(cacheFile, bytes);
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // Inline previews render at up to 360px wide (see the Image's
            // MaxWidth in MainWindow.xaml) - decoding at that width instead
            // of the uploaded source resolution (up to the 8MB attachment
            // cap) keeps memory/decode cost bounded regardless of what the
            // sender uploaded.
            bitmap.DecodePixelWidth = 360;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            MemoryCache[url] = bitmap;
            InsertionOrder.Enqueue(url);
            while (MemoryCache.Count > MaxCachedImages && InsertionOrder.TryDequeue(out var oldest))
                MemoryCache.TryRemove(oldest, out _);

            return bitmap;
        }
        catch
        {
            // Bad URL, 404, network error, corrupt cached file, or a
            // non-image file this happened to be asked to load - callers
            // treat null as "don't show a preview."
            return null;
        }
    }

    private static string ToCacheFileName(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        return string.IsNullOrEmpty(ext) ? hash : hash + ext;
    }

    // /uploads now requires auth (see Program.cs) - see
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
