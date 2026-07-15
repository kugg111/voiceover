using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voiceover.Client.Services;

// Neither WPF's own text rendering pipeline nor classic GDI (System.
// Windows.Forms.TextRenderer/DrawTextEx) can draw color emoji glyphs on
// this platform - both were tried and both came out monochrome. Only a
// genuinely different, DirectWrite-based rendering path (the kind browsers
// use) actually draws them in color, so these are pre-rendered PNGs -
// generated once via a real browser canvas render, not at runtime - bundled
// as pack resources under Assets/Emoji and looked up here by emoji glyph.
public static class EmojiImageCache
{
    // Index order matches Client/Assets/Emoji/emoji_NN.png exactly - this
    // is also the order the emoji picker grid renders them in (see
    // MainWindow.xaml).
    private static readonly string[] Emojis =
    {
        "👍", "👎", "❤️", "😂", "😮", "😢", "🎉", "🔥",
        "😀", "😁", "😆", "😊", "😍", "🥰", "😘", "😜",
        "🤔", "😴", "😭", "😡", "🥳", "😎", "🤯", "😱",
        "👏", "🙌", "🙏", "💪", "👀", "🤝", "✅", "❌",
        "💯", "⭐", "✨", "💔", "💕", "🎂", "🍕", "☕",
        "🤣", "😅", "🙄", "😬", "🤗", "😇", "🥺", "🫡"
    };

    private static readonly Dictionary<string, ImageSource> Cache = BuildCache();

    private static Dictionary<string, ImageSource> BuildCache()
    {
        var map = new Dictionary<string, ImageSource>();
        for (var i = 0; i < Emojis.Length; i++)
        {
            var image = new BitmapImage(new Uri($"pack://application:,,,/Assets/Emoji/emoji_{i:D2}.png"));
            image.Freeze();
            map[Emojis[i]] = image;
        }
        return map;
    }

    public static ImageSource? Get(string emoji) => Cache.GetValueOrDefault(emoji);
}
