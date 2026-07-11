using System.Media;
using System.Windows;
using Voiceover.Client.Views;

namespace Voiceover.Client.Services;

// Sound + toast for things that happen while you're not looking at the app
// (see MainWindow's use of this - gated on Application.Current.MainWindow's
// IsActive so it doesn't also fire when you're already looking right at
// the event in the UI) - plus the voice-join sound, which also plays for
// your own join regardless of focus, as direct feedback that it worked.
public static class NotificationService
{
    // Only one toast on screen at a time - a burst of join/leave events
    // replaces the previous toast rather than stacking a pile of popups.
    private static ToastNotificationWindow? _currentToast;

    // Short synthesized tones (see Assets/Sounds) rather than the OS's own
    // notification sound - loaded once from the embedded resource and
    // reused, rather than re-opening the resource stream on every play.
    private static readonly Lazy<SoundPlayer> MessageSound = new(() => LoadSound("message.wav"));
    private static readonly Lazy<SoundPlayer> VoiceJoinSound = new(() => LoadSound("voice-join.wav"));
    private static readonly Lazy<SoundPlayer> VoiceLeaveSound = new(() => LoadSound("voice-leave.wav"));
    private static readonly Lazy<SoundPlayer> MuteSound = new(() => LoadSound("mute.wav"));
    private static readonly Lazy<SoundPlayer> UnmuteSound = new(() => LoadSound("unmute.wav"));

    private static SoundPlayer LoadSound(string fileName)
    {
        var uri = new Uri($"pack://application:,,,/Assets/Sounds/{fileName}");
        var stream = Application.GetResourceStream(uri)!.Stream;
        var player = new SoundPlayer(stream);
        player.Load();
        return player;
    }

    public static void ShowToast(string title, string message)
    {
        try { _currentToast?.Close(); }
        catch { /* already closing/closed - fine, we're about to replace it anyway */ }

        _currentToast = new ToastNotificationWindow(title, message);
        _currentToast.Show();
    }

    public static void PlayVoiceJoinSound() => VoiceJoinSound.Value.Play();
    public static void PlayVoiceLeaveSound() => VoiceLeaveSound.Value.Play();
    public static void PlayMessageSound() => MessageSound.Value.Play();
    public static void PlayMuteSound() => MuteSound.Value.Play();
    public static void PlayUnmuteSound() => UnmuteSound.Value.Play();
}
