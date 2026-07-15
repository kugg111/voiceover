using System.Globalization;
using System.Windows.Data;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

// Used via a Button's ContentTemplate (see MainWindow.xaml's emoji picker
// and reaction-add button) - the emoji glyph itself stays as plain string
// Content everywhere (existing code that reads Content back, e.g.
// MainWindow.ReactionMenuItem_Click, keeps working unchanged), only its
// visual presentation swaps from a rendered text glyph to a pre-rendered
// color image (see EmojiImageCache for why).
public class EmojiToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string emoji ? EmojiImageCache.Get(emoji) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
