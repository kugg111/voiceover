using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Voiceover.Client.Views;

// Collapsed for null/empty, Visible otherwise - used by the channel
// category GroupStyle headers in MainWindow.xaml so the "uncategorized"
// group (CategoryName == "") renders no header at all.
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
