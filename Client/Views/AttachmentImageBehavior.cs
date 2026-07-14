using System.Windows;
using System.Windows.Controls;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

// Attached property so an Image in the message DataTemplate can load an
// attachment preview through AttachmentImageCache (async, cached) straight
// from XAML - same pattern as FormattedTextBehavior, needed because plain
// data-binding Image.Source to a URL string has no way to route through the
// cache or tolerate the row being recycled (ListBox virtualization) before
// the load finishes.
public static class AttachmentImageBehavior
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(AttachmentImageBehavior), new PropertyMetadata(null, OnUrlChanged));

    public static void SetUrl(Image element, string? value) => element.SetValue(UrlProperty, value);
    public static string? GetUrl(Image element) => (string?)element.GetValue(UrlProperty);

    private static void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;

        var url = e.NewValue as string;
        image.Source = null;
        if (string.IsNullOrEmpty(url)) return;

        _ = LoadAsync(image, url);
    }

    // Fire-and-forget from the property callback - the ListBox recycles
    // Image instances as rows scroll, so by the time the cache lookup
    // completes this same Image may already have moved on to a different
    // row's URL; re-check before applying the result.
    private static async Task LoadAsync(Image image, string url)
    {
        var bitmap = await AttachmentImageCache.GetAsync(url);
        if (GetUrl(image) != url) return;

        image.Source = bitmap;
    }
}
