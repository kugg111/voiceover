using System.Windows;
using System.Windows.Controls;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

// Attached property so message content can go through
// MessageContentRenderer's Inlines (bold/italic/code/links/mentions)
// straight from XAML - TextBlock.Text only accepts a plain string, so a
// data-bound rich-text row needs some attached-property indirection like
// this rather than a direct binding.
public static class FormattedTextBehavior
{
    public static readonly DependencyProperty ContentProperty = DependencyProperty.RegisterAttached(
        "Content", typeof(string), typeof(FormattedTextBehavior), new PropertyMetadata(null, OnContentChanged));

    public static void SetContent(TextBlock element, string? value) => element.SetValue(ContentProperty, value);
    public static string? GetContent(TextBlock element) => (string?)element.GetValue(ContentProperty);

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock) return;

        textBlock.Inlines.Clear();
        var content = e.NewValue as string ?? string.Empty;
        textBlock.Inlines.AddRange(MessageContentRenderer.BuildInlines(content));
    }
}
