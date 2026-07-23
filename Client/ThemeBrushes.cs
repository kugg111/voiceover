using System.Windows;
using System.Windows.Media;

namespace Voiceover.Client;

// Typed accessors for the theme colors that C# needs to set directly (as
// opposed to XAML, which references them with {StaticResource ...}). The colors
// themselves are declared exactly once, in App.xaml; these just resolve and
// cache the shared frozen brushes so the same red/gold is no longer hand-copied
// as Color.FromRgb(...) across MainWindow/CallWindow. Resolved lazily (not in a
// static initializer) because Application.Current's resources aren't populated
// until App's own InitializeComponent has run.
public static class ThemeBrushes
{
    private static Brush? _danger;
    private static Brush? _away;

    // Destructive/error red - App.xaml "DangerRed".
    public static Brush Danger => _danger ??= Resource("DangerRed");

    // Away/idle gold - App.xaml "AwayYellow".
    public static Brush Away => _away ??= Resource("AwayYellow");

    private static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);
}
