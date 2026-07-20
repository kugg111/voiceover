using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Voiceover.Server.Services;

// Downscales oversized image uploads (avatars, server icons, and message
// attachment images alike - one uniform policy, no per-context awareness)
// before UploadController stores them in the StoredFiles table. Most
// uploads land here as small UI elements or chat screenshots that never
// need to be viewed above ~1920px - resizing keeps the DB from
// accumulating full-resolution phone photos for no visual benefit.
public static class ImageResizeService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp"
    };

    public static byte[] ResizeIfOversized(byte[] data, string extension, int maxDimension = 1920)
    {
        if (!ImageExtensions.Contains(extension))
            return data;

        using var image = Image.Load(data);
        if (image.Width <= maxDimension && image.Height <= maxDimension)
            return data;

        // ResizeMode.Max scales down to fit within the box while preserving
        // aspect ratio, and never scales up - Mutate operates frame-by-frame
        // so an animated GIF keeps every frame instead of collapsing to one.
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(maxDimension, maxDimension),
            Mode = ResizeMode.Max
        }));

        using var output = new MemoryStream();
        switch (extension.ToLowerInvariant())
        {
            case ".jpg" or ".jpeg":
                image.Save(output, new JpegEncoder { Quality = 85 });
                break;
            case ".webp":
                image.Save(output, new WebpEncoder { Quality = 85 });
                break;
            default:
                // PNG/GIF: lossless formats - re-encode at default settings,
                // the dimension reduction alone already shrinks these a lot.
                image.Save(output, image.Metadata.DecodedImageFormat!);
                break;
        }

        return output.ToArray();
    }
}
