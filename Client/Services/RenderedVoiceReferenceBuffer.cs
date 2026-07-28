using System.Diagnostics;

namespace Voiceover.Client.Services;

// Shared record of every remote-voice sample this app has recently written
// to a real output device via RemoteAudioPlayback - fixed at LiveKit's own
// AudioStream format (48kHz, mono, 16-bit), since every RemoteAudioPlayback
// instance forces that regardless of the track's native format (see its own
// AudioStream(track, 48000, 1, ...) call).
//
// ScreenAudioCaptureSource subtracts this from what it captures via WASAPI
// loopback before publishing "system audio", so a viewer never hears their
// own voice (or anyone else's already-playing voice) echoed back through a
// screen-sharer's re-broadcast of "everything currently playing on their
// speakers" - see that class for the full mechanism. This works because the
// loopback tap and this buffer are both, ultimately, the exact same PCM
// bytes: not acoustic echo (mic picking up a speaker), just the same digital
// signal reaching two different consumers, so subtracting a known reference
// is exact rather than the estimation problem real acoustic echo
// cancellation has to solve.
//
// Multiple RemoteAudioPlayback instances (one per remote participant, or
// per other sharer's system-audio track) write concurrently from independent
// read loops with no shared cadence - this mixes (sums, clamped) their
// contributions into one timeline indexed by wall-clock-derived sample
// position, exactly the way Windows' own shared-mode audio engine mixes
// multiple WaveOutEvent streams onto the same physical device. Wall clock
// (not a per-writer counter) is the only synchronization point independent
// threads can agree on without coordinating with each other directly.
public static class RenderedVoiceReferenceBuffer
{
    private const int SampleRate = 48000;

    // 1 second - comfortably longer than ScreenAudioCaptureSource's delay
    // compensation plus scheduling jitter between the render and capture
    // threads, with no real memory cost (96KB).
    private const int RetentionSamples = SampleRate; // 1000ms worth

    private static readonly object Lock = new();
    private static readonly short[] Ring = new short[RetentionSamples];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static long NowSamplePosition => (long)(Clock.Elapsed.TotalSeconds * SampleRate);

    // Called by RemoteAudioPlayback's read loop with the exact PCM it's
    // about to hand to its own WaveOutEvent (post per-user gain, since
    // that's the real signal that ends up on the speaker and therefore in
    // any loopback capture of it). Mono only - RemoteAudioPlayback never
    // plays anything else.
    public static void AddRendered(short[] mono48kPcm)
    {
        lock (Lock)
        {
            long startPos = NowSamplePosition;
            for (int i = 0; i < mono48kPcm.Length; i++)
            {
                var idx = (int)((startPos + i) % RetentionSamples);
                Ring[idx] = (short)Math.Clamp(Ring[idx] + mono48kPcm[i], short.MinValue, short.MaxValue);
            }
        }
    }

    // Returns sampleCount mono samples of mixed reference audio ending
    // delayMs before now - the app's best guess at exactly what it was
    // rendering to speakers around the time a loopback-captured block of
    // that same size was actually produced. Consumed samples are zeroed so
    // a later read after the ring wraps around (every 1s) can't pick up
    // stale, already-subtracted data from a previous lap.
    public static short[] TakeReference(int sampleCount, int delayMs)
    {
        lock (Lock)
        {
            long startPos = NowSamplePosition - (long)(delayMs * SampleRate / 1000.0) - sampleCount;
            var result = new short[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                var pos = startPos + i;
                if (pos < 0) continue;
                var idx = (int)(pos % RetentionSamples);
                result[i] = Ring[idx];
                Ring[idx] = 0;
            }
            return result;
        }
    }
}
