using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Voiceover.Server.Services;

namespace Server.Tests;

public class ImageResizeServiceTests
{
    private static byte[] MakePng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public void ResizeIfOversized_ShrinksImageLargerThanCap_PreservingAspectRatio()
    {
        var original = MakePng(2000, 1000);

        var resized = ImageResizeService.ResizeIfOversized(original, ".png", maxDimension: 1920);

        using var image = Image.Load(resized);
        Assert.Equal(1920, image.Width);
        Assert.Equal(960, image.Height);
    }

    [Fact]
    public void ResizeIfOversized_LeavesSmallImageUnchanged()
    {
        var original = MakePng(100, 50);

        var result = ImageResizeService.ResizeIfOversized(original, ".png", maxDimension: 1920);

        Assert.Equal(original, result);
    }

    [Fact]
    public void ResizeIfOversized_LeavesNonImageExtensionUnchanged()
    {
        var original = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }; // "%PDF-"

        var result = ImageResizeService.ResizeIfOversized(original, ".pdf", maxDimension: 1920);

        Assert.Same(original, result);
    }
}
