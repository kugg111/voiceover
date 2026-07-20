using System.IO;
using System.Windows.Threading;
using NAudio.Wave;

namespace Voiceover.Client.Services;

// Records a short voice-message clip to a temp WAV file for the message
// composer - a separate, on-demand capture instance rather than reusing
// MicCaptureSource, which assumes a continuously-open device for a whole
// voice-channel session. 16kHz/mono (not the 48kHz voice-channel rate) -
// voice-message intelligibility doesn't need 48kHz, and it keeps a 2-minute
// clip comfortably under UploadController's 8MB cap (~3.84MB at this rate).
public class VoiceMessageRecorder : IDisposable
{
    private const int SampleRate = 16000;
    private const int Channels = 1;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(2);

    public event Action<TimeSpan>? OnElapsedTick;
    public event Action? OnMaxDurationReached;

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _tempFilePath;
    private DispatcherTimer? _elapsedTimer;
    private DateTime _startedAtUtc;
    private bool _maxDurationFired;

    public bool IsRecording => _waveIn is not null;

    public void Start()
    {
        if (IsRecording) return;
        if (WaveInEvent.DeviceCount == 0) throw new InvalidOperationException("No microphone available.");

        var deviceIndex = VoiceSettingsStorage.Load()?.InputDeviceIndex ?? -1;
        if (deviceIndex >= WaveInEvent.DeviceCount) deviceIndex = -1;

        _tempFilePath = Path.Combine(Path.GetTempPath(), $"voiceover-voicemsg-{Guid.NewGuid():N}.wav");
        _writer = new WaveFileWriter(_tempFilePath, new WaveFormat(SampleRate, 16, Channels));

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = 50
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();

        _startedAtUtc = DateTime.UtcNow;
        _maxDurationFired = false;
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _elapsedTimer.Tick += (_, _) =>
        {
            var elapsed = DateTime.UtcNow - _startedAtUtc;
            OnElapsedTick?.Invoke(elapsed);
            if (elapsed >= MaxDuration && !_maxDurationFired)
            {
                _maxDurationFired = true;
                OnMaxDurationReached?.Invoke();
            }
        };
        _elapsedTimer.Start();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args) =>
        _writer?.Write(args.Buffer, 0, args.BytesRecorded);

    // Stops capture and finalizes the WAV file, returning its temp path -
    // the caller uploads it and deletes the file afterward, same try/finally
    // pattern MainWindow.MessageInput_Pasting already uses for its own
    // temp file.
    public string Stop()
    {
        _elapsedTimer?.Stop();
        _elapsedTimer = null;

        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _waveIn = null;
        }

        _writer?.Dispose();
        _writer = null;

        var path = _tempFilePath ?? throw new InvalidOperationException("Not recording.");
        _tempFilePath = null;
        return path;
    }

    public void Dispose()
    {
        _elapsedTimer?.Stop();
        _waveIn?.Dispose();
        _writer?.Dispose();
    }
}
