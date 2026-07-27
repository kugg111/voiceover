using System.Windows.Media.Imaging;

namespace Voiceover.Client;

// Cached icon images for buttons whose icon changes with state (mute/deafen
// toggle) - loaded once since it's the same bytes every time, not per
// Window/Button. Mirrors ThemeBrushes' role for colors: a single place these
// live instead of a new BitmapImage() at every click.
public static class IconImages
{
    public static readonly BitmapImage Mic = Load("mic.png");
    public static readonly BitmapImage MicMute = Load("mic-mute.png");
    public static readonly BitmapImage DeafenOff = Load("deafen-off.png");
    public static readonly BitmapImage DeafenOn = Load("deafen-on.png");

    private static BitmapImage Load(string fileName) =>
        new(new Uri($"pack://application:,,,/Assets/Icons/{fileName}"));
}
