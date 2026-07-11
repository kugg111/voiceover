using System.Media;
using Voiceover.Client.Views;

namespace Voiceover.Client.Services;

// Sound + toast for things that happen while you're not looking at the app
// (see MainWindow's use of this - gated on Application.Current.MainWindow's
// IsActive so it doesn't also fire when you're already looking right at
// the event in the UI).
public static class NotificationService
{
    // Only one toast on screen at a time - a burst of join/leave events
    // replaces the previous toast rather than stacking a pile of popups.
    private static ToastNotificationWindow? _currentToast;

    public static void ShowToast(string title, string message)
    {
        try { _currentToast?.Close(); }
        catch { /* already closing/closed - fine, we're about to replace it anyway */ }

        _currentToast = new ToastNotificationWindow(title, message);
        _currentToast.Show();
    }

    public static void PlayVoiceJoinSound() => SystemSounds.Asterisk.Play();
    public static void PlayVoiceLeaveSound() => SystemSounds.Hand.Play();
    public static void PlayMessageSound() => SystemSounds.Exclamation.Play();
}
