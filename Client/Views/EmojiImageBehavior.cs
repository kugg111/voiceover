using System.Windows;
using System.Windows.Controls;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

// Attached property resolving an emoji-carrying string (a picker Button's
// Content, or a ReactionItem's Emoji) to an Image source. A plain unicode
// glyph resolves synchronously via EmojiImageCache's pre-rendered PNGs, same
// as the old EmojiToImageConverter this replaces; a "custom:{id}" token (see
// CustomEmojiRegistry) resolves asynchronously via AttachmentImageCache,
// same recycling-safe pattern as AttachmentImageBehavior - needed here too
// since these Images live inside a virtualized ListBox's DataTemplate and
// get recycled as rows scroll, same as attachment previews already do.
public static class EmojiImageBehavior
{
    public static readonly DependencyProperty TokenProperty = DependencyProperty.RegisterAttached(
        "Token", typeof(string), typeof(EmojiImageBehavior), new PropertyMetadata(null, OnTokenChanged));

    public static void SetToken(Image element, string? value) => element.SetValue(TokenProperty, value);
    public static string? GetToken(Image element) => (string?)element.GetValue(TokenProperty);

    private static void OnTokenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;

        var token = e.NewValue as string;
        image.Source = null;
        if (string.IsNullOrEmpty(token)) return;

        if (CustomEmojiRegistry.TryGetUrl(token, out var url))
        {
            _ = LoadCustomAsync(image, token, url);
            return;
        }

        image.Source = EmojiImageCache.Get(token);
    }

    // Fire-and-forget from the property callback, same reasoning as
    // AttachmentImageBehavior.LoadAsync - re-check the token still matches
    // before applying, in case this Image was recycled to a different row.
    private static async Task LoadCustomAsync(Image image, string token, string url)
    {
        var bitmap = await AttachmentImageCache.GetAsync(url);
        if (GetToken(image) != token) return;

        image.Source = bitmap;
    }
}
