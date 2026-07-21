using PortAudioSharp;

namespace Voiceover.Client.Linux;

// PortAudio.Initialize()/Terminate() are global library calls, not
// per-stream - must happen exactly once regardless of how many
// MicCaptureSourceLinux/RemoteAudioPlaybackLinux instances get created
// and torn down across repeated voice-channel joins in one process.
internal static class PortAudioBootstrap
{
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        PortAudio.Initialize();
        _initialized = true;
    }
}
