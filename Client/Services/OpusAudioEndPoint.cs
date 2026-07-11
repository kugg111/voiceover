using Concentus;
using Concentus.Enums;
using NAudio.Wave;
using SIPSorceryMedia.Abstractions;
using SoundFlow.Extensions.WebRtc.Apm;

namespace Voiceover.Client.Services;

// Replaces SIPSorceryMedia.Windows' WindowsAudioEndPoint for the mic capture
// and speaker playback paths. That class is a thin, general-purpose NAudio
// wrapper with no gain control on capture and no jitter buffering on
// playback - fine for a quick demo, not for "sounds like Discord". This
// talks to NAudio and Concentus (Opus) directly so it can:
//   - boost quiet mics instead of only picking up close-range speech
//   - smooth out network jitter and conceal lost packets on playback,
//     instead of decoding and playing whatever arrives the instant it does
//   - fix a real bug in the upstream Encode/Decode duration handling (see
//     OnMicDataAvailable) that was corrupting RTP timestamps
public class OpusAudioEndPoint : IAudioSource, IAudioSink, IDisposable
{
    private const int SampleRate = 48000;

    // The PCM convention throughout SIPSorcery/Concentus here is mono - the
    // OPUS SDP format is always "opus/48000/2" per RFC 7587, but that's a
    // fixed wire-format declaration, not the actual stream channel count.
    private const int Channels = 1;
    private const int FrameDurationMs = 20;
    private const int SamplesPerFrame = SampleRate / 1000 * FrameDurationMs; // 960

    // Boosts quiet mics ("only picks up from really close") - applied to the
    // raw captured PCM before encoding. A soft-knee limiter (see ApplyGain)
    // keeps loud passages from clipping/crackling once boosted.
    public static float MicGain { get; set; } = 4.0f;

    // volatile because these are written from the UI thread (mute button /
    // settings checkbox) and read from the NAudio capture callback thread
    // on every frame - a stale read would just be a one-frame (20ms) delay
    // in practice, but there's no reason to rely on that.
    private static volatile bool _micMuted;
    private static volatile bool _noiseSuppressionEnabled = true;
    private static volatile bool _deafened;

    // Static rather than per-instance because a mesh voice call creates one
    // OpusAudioEndPoint (and one independent mic capture stream) per remote
    // peer - muting has to silence all of them at once, not just whichever
    // one happened to be created first.
    public static bool MicMuted { get => _micMuted; set => _micMuted = value; }

    // Also static/global for the same reason - deafening stops you hearing
    // *every* remote peer at once, not just whichever endpoint this is.
    // Checked in PlaybackTick rather than skipping decode entirely, so the
    // jitter buffer keeps draining at the normal cadence instead of piling
    // up while deafened and needing to catch up the moment it's turned off.
    public static bool Deafened { get => _deafened; set => _deafened = value; }

    // Boosting a quiet mic 4x also boosts everything else picked up by it -
    // breathing, keyboard clatter, room hum. Runs Google's real WebRTC noise
    // suppression (see ApplyNoiseSuppression) rather than a blunt RMS
    // threshold gate - toggleable for the same reason a gate was: no single
    // setting is right for every mic/voice, some people would rather have
    // the raw signal.
    public static bool NoiseSuppressionEnabled { get => _noiseSuppressionEnabled; set => _noiseSuppressionEnabled = value; }

    // RFC 7587: dynamic payload type, 48kHz clock, "2" channels declared in
    // the SDP regardless of the actual (mono) stream content, FEC hinted.
    public static readonly AudioFormat OpusFormat =
        new(AudioCodecsEnum.OPUS, 111, SampleRate, 2, "minptime=10;useinbandfec=1");

    private readonly List<AudioFormat> _formats = new() { OpusFormat };

    private readonly int _inputDeviceIndex;
    private readonly int _outputDeviceIndex;

    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _waveProvider;

    private IOpusEncoder? _encoder;
    private IOpusDecoder? _decoder;

    private readonly List<short> _captureAccumulator = new();
    private readonly byte[] _encodeBuffer = new byte[1275]; // OPUS_MAXIMUM_ENCODED_FRAME_SIZE; reused across calls
                                                              // rather than re-allocated per frame (was stackalloc'd
                                                              // inside a loop, which CA2014 correctly flagged - stack
                                                              // memory from stackalloc isn't reclaimed until the whole
                                                              // method returns, not at the end of each iteration).

    private bool _sourceStarted;
    private bool _sinkStarted;

    // Per-instance (not static like MicGain) because this is a mesh call -
    // each remote peer gets its own OpusAudioEndPoint, and a volume slider
    // is inherently "how loud is *this* person", not a global setting.
    // 1.0 = unchanged; VoiceService.SetRemoteVolume is the public entry
    // point (this class doesn't know user IDs, VoiceService maps those to
    // endpoint instances).
    public float PlaybackVolume { get; set; } = 1.0f;

    // --- noise suppression (capture side) ---
    // Real WebRTC APM, not a hand-rolled gate - see ApplyNoiseSuppression.
    // Per-instance (like _encoder/_decoder) rather than static/shared, since
    // each instance owns its own independent mic capture stream and the
    // APM's noise-floor learning is meaningful per-stream, not global.
    private AudioProcessingModule? _apm;
    private ApmConfig? _apmConfig;

    // WebRTC APM processes fixed 10ms frames regardless of the caller's own
    // framing (AudioProcessingModule.GetFrameSize(48000) == 480) - half of
    // this class's own 20ms/960-sample frame, so each frame needs two calls.
    // Buffers are reused across calls rather than allocated per frame, same
    // reasoning as _encodeBuffer below.
    private const int ApmFrameSamples = 480;
    private readonly float[] _apmInput = new float[ApmFrameSamples];
    private readonly float[] _apmOutput = new float[ApmFrameSamples];
    private readonly float[][] _apmInputChannels;
    private readonly float[][] _apmOutputChannels;
    private static readonly StreamConfig ApmStreamConfig = new(SampleRate, Channels);

    // --- jitter buffer (playback side) ---
    // Frames can arrive out of order or with uneven timing over UDP; decoding
    // and playing each one the instant it lands (the old behaviour) is what
    // produced the reported clicks/glitches. Buffering a few frames before
    // starting playback, draining them at a steady 20ms cadence, and
    // concealing genuine gaps with Opus's own loss-concealment mode instead
    // of just skipping them fixes that at the cost of ~60ms extra latency -
    // an easy trade for voice chat.
    private readonly object _jitterLock = new();
    private readonly Dictionary<ushort, byte[]> _jitterBuffer = new();
    private const int JitterTargetFrames = 3;
    private const int JitterMaxFrames = 50;
    private bool _playbackStarted;
    private ushort _nextPlaySeq;
    private Timer? _playbackTimer;
    private ushort _fallbackSeq;

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
#pragma warning disable CS0067 // required by IAudioSource; nothing in this codebase subscribes to it
    public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
#pragma warning restore CS0067
    public event RawAudioSampleDelegate? OnAudioSourceRawSample;
    public event SourceErrorDelegate? OnAudioSourceError;
    public event SourceErrorDelegate? OnAudioSinkError;

    public OpusAudioEndPoint(int outputDeviceIndex, int inputDeviceIndex)
    {
        _outputDeviceIndex = outputDeviceIndex;
        _inputDeviceIndex = inputDeviceIndex;
        _apmInputChannels = new[] { _apmInput };
        _apmOutputChannels = new[] { _apmOutput };

        InitCapture();
        InitPlayback();
    }

    // Opus is the only format this end point supports, so there's nothing to
    // restrict/select - these exist purely for IAudioSource/IAudioSink conformance.
    public void RestrictFormats(Func<AudioFormat, bool> filter) { }
    public List<AudioFormat> GetAudioSourceFormats() => _formats;
    public List<AudioFormat> GetAudioSinkFormats() => _formats;
    public void SetAudioSourceFormat(AudioFormat audioFormat) { }
    public void SetAudioSinkFormat(AudioFormat audioFormat) { }
    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;
    public bool IsAudioSourcePaused() => !_sourceStarted;
    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample) =>
        throw new NotImplementedException();

    [Obsolete("Use SubmitEncodedFrame instead - this overload has no sequence number to jitter-buffer with.")]
    public void GotAudioRtp(System.Net.IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload) =>
        SubmitEncodedFrame((ushort)seqnum, payload);

    // IAudioSink's interface method has no sequence number, so a caller using
    // it can't be jitter-buffered correctly - VoiceService calls
    // SubmitEncodedFrame directly with the real RTP sequence number instead.
    // This exists for interface conformance and callers that don't have one.
    public void GotEncodedMediaFrame(EncodedAudioFrame encodedMediaFrame) =>
        SubmitEncodedFrame(_fallbackSeq++, encodedMediaFrame.EncodedAudio);

    private void InitCapture()
    {
        if (WaveInEvent.DeviceCount == 0)
        {
            OnAudioSourceError?.Invoke("No audio capture devices are available.");
            return;
        }

        if (_inputDeviceIndex >= 0 && _inputDeviceIndex >= WaveInEvent.DeviceCount)
        {
            OnAudioSourceError?.Invoke($"The requested audio input device index {_inputDeviceIndex} exceeds the maximum index of {WaveInEvent.DeviceCount - 1}.");
            return;
        }

        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 64000;          // well above typical low-bitrate VoIP presets - Discord-grade clarity
        _encoder.Complexity = 10;          // max quality/CPU tradeoff; trivial cost for one voice stream
        _encoder.UseVBR = true;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        _encoder.UseInbandFEC = true;      // lets the decoder recover some lost packets from the next one
        _encoder.PacketLossPercent = 10;

        _apm = new AudioProcessingModule();
        _apm.Initialize();
        _apmConfig = new ApmConfig();
        _apmConfig.SetNoiseSuppression(true, NoiseSuppressionLevel.High);
        _apmConfig.SetHighPassFilter(true); // cuts low-frequency rumble (desk thumps, mic handling) below speech range
        _apm.ApplyConfig(_apmConfig);

        _waveIn = new WaveInEvent
        {
            DeviceNumber = _inputDeviceIndex,
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = FrameDurationMs,
            NumberOfBuffers = 2
        };
        _waveIn.DataAvailable += OnMicDataAvailable;
    }

    private void InitPlayback()
    {
        try
        {
            _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);

            var format = new WaveFormat(SampleRate, 16, Channels);
            _waveProvider = new BufferedWaveProvider(format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2)
            };
            _waveOut = new WaveOutEvent { DeviceNumber = _outputDeviceIndex };
            _waveOut.Init(_waveProvider);
        }
        catch (Exception ex)
        {
            OnAudioSinkError?.Invoke($"OpusAudioEndPoint failed to initialise playback device. {ex.Message}");
        }
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (_encoder is null) return;

        if (MicMuted)
        {
            // Drop whatever was mid-accumulation rather than let it sit and
            // get sent as a stale burst the moment the mic is unmuted.
            _captureAccumulator.Clear();
            return;
        }

        int sampleCount = args.BytesRecorded / 2;
        var incoming = new short[sampleCount];
        Buffer.BlockCopy(args.Buffer, 0, incoming, 0, args.BytesRecorded);
        _captureAccumulator.AddRange(incoming);

        // NAudio's actual callback buffer size isn't guaranteed to line up
        // exactly with Opus's fixed set of valid frame sizes (120/240/480/
        // 960/1920/2880 samples at 48kHz) - accumulate and slice off exact
        // 20ms/960-sample frames rather than assuming each callback already
        // is one.
        while (_captureAccumulator.Count >= SamplesPerFrame)
        {
            var frame = _captureAccumulator.GetRange(0, SamplesPerFrame).ToArray();
            _captureAccumulator.RemoveRange(0, SamplesPerFrame);

            // Runs on the raw captured signal, before gain - the APM's noise
            // modelling is calibrated for normal mic input levels, and
            // boosting first would distort the noise floor it's trying to
            // characterize.
            ApplyNoiseSuppression(frame);
            ApplyGain(frame, MicGain);

            int encodedLength;
            try
            {
                encodedLength = _encoder.Encode(frame, SamplesPerFrame, _encodeBuffer, _encodeBuffer.Length);
            }
            catch
            {
                continue;
            }

            if (encodedLength > 0)
            {
                // durationRtpUnits must be the sample count this frame represents
                // (960 for 20ms @ 48kHz) - NOT the encoded byte length. Opus's
                // VBR output size varies packet to packet, so using the byte
                // length here (as SIPSorceryMedia.Windows' WindowsAudioEndPoint
                // does) corrupts the RTP timestamp, throwing off playout timing
                // on the receiving end and manifesting as audio artifacts.
                OnAudioSourceEncodedSample?.Invoke(SamplesPerFrame, _encodeBuffer.AsSpan(0, encodedLength).ToArray());
            }

            OnAudioSourceRawSample?.Invoke(AudioSamplingRatesEnum.Rate48kHz, FrameDurationMs, frame);
        }
    }

    // Boosts quiet input, then compresses through a tanh soft-knee limiter
    // instead of hard-clipping - a boosted loud passage compresses smoothly
    // rather than crackling the way naive multiply-and-truncate would.
    private static void ApplyGain(short[] pcm, float gain)
    {
        for (int i = 0; i < pcm.Length; i++)
        {
            float normalized = (pcm[i] * gain) / short.MaxValue;
            float limited = (float)Math.Tanh(normalized);
            pcm[i] = (short)(limited * short.MaxValue);
        }
    }

    // Runs the real WebRTC noise suppressor over the frame in place. The APM
    // works in fixed 480-sample (10ms) chunks regardless of the caller's own
    // framing, so this class's 960-sample (20ms) frame is processed as two
    // consecutive halves. Unlike the old RMS-threshold gate this doesn't
    // just judge "loud or not" and attenuate everything below a cutoff - it
    // has an actual spectral noise model, so it can distinguish steady-state
    // noise (fans, hum) and transients (a keyboard click) from speech
    // instead of gating on volume alone.
    private void ApplyNoiseSuppression(short[] pcm)
    {
        if (!NoiseSuppressionEnabled || _apm is null) return;

        for (int offset = 0; offset < pcm.Length; offset += ApmFrameSamples)
        {
            for (int i = 0; i < ApmFrameSamples; i++)
                _apmInput[i] = pcm[offset + i] / (float)short.MaxValue;

            _apm.ProcessStream(_apmInputChannels, ApmStreamConfig, ApmStreamConfig, _apmOutputChannels);

            for (int i = 0; i < ApmFrameSamples; i++)
                pcm[offset + i] = (short)Math.Clamp(_apmOutput[i] * short.MaxValue, short.MinValue, short.MaxValue);
        }
    }

    public void SubmitEncodedFrame(ushort sequenceNumber, byte[] encodedAudio)
    {
        lock (_jitterLock)
        {
            if (_playbackStarted)
            {
                // A big forward gap means the sender paused for a while
                // (e.g. they muted, rather than a handful of packets being
                // reordered or lost) - _nextPlaySeq is still sitting back
                // where playback left off. Left alone, it would crawl
                // forward one PLC-concealed frame per 20ms tick to "catch
                // up", and every genuinely new frame arriving in the
                // meantime would pile up in the buffer; once that hit
                // JitterMaxFrames the guard below would drop every frame
                // after it, so playback could never actually catch up and
                // stayed silent/stuck for the rest of the call. Resync
                // straight to just before this frame instead of crawling.
                int gap = (short)(sequenceNumber - _nextPlaySeq);
                if (gap > JitterMaxFrames)
                {
                    _jitterBuffer.Clear();
                    _nextPlaySeq = (ushort)(sequenceNumber - JitterTargetFrames);
                }
            }

            if (_jitterBuffer.Count >= JitterMaxFrames)
            {
                // The peer is either wildly out of order or we've stalled -
                // drop rather than let this grow unbounded. Playback will
                // catch up via PLC-concealed frames until it does.
                return;
            }

            _jitterBuffer[sequenceNumber] = encodedAudio;

            if (!_playbackStarted && _jitterBuffer.Count >= JitterTargetFrames)
            {
                _nextPlaySeq = _jitterBuffer.Keys.Min();
                _playbackStarted = true;
                _playbackTimer = new Timer(_ => PlaybackTick(), null, 0, FrameDurationMs);
            }
        }
    }

    private void PlaybackTick()
    {
        if (_decoder is null || _waveProvider is null) return;

        byte[]? payload = null;
        lock (_jitterLock)
        {
            if (_jitterBuffer.TryGetValue(_nextPlaySeq, out var found))
            {
                payload = found;
                _jitterBuffer.Remove(_nextPlaySeq);
            }

            // Purge anything so far behind _nextPlaySeq it'll never be played
            // (a very late arrival) so the buffer can't grow unbounded.
            ushort cutoff = _nextPlaySeq;
            var stale = _jitterBuffer.Keys.Where(k => (short)(k - cutoff) < -5).ToList();
            foreach (var s in stale) _jitterBuffer.Remove(s);

            _nextPlaySeq++;
        }

        var pcm = new short[SamplesPerFrame * Channels];
        int decoded;
        try
        {
            // Missing frame -> Opus packet-loss concealment (empty span) instead
            // of silence or skipping, so a single lost/late packet doesn't click.
            decoded = payload is not null
                ? _decoder.Decode(payload, pcm, SamplesPerFrame, false)
                : _decoder.Decode(ReadOnlySpan<byte>.Empty, pcm, SamplesPerFrame, false);
        }
        catch
        {
            return;
        }

        if (decoded <= 0) return;

        // Still fully decoded above even while deafened, so the jitter
        // buffer keeps draining at the normal cadence - only the final
        // "send it to the speaker" step is skipped, rather than letting
        // frames pile up and needing to catch up the moment deafen is
        // turned back off (the same class of bug the jitter buffer's
        // resync logic in SubmitEncodedFrame exists to avoid).
        if (Deafened) return;

        if (PlaybackVolume != 1.0f)
            ApplyGain(pcm, PlaybackVolume);

        var bytes = new byte[decoded * Channels * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        _waveProvider.AddSamples(bytes, 0, bytes.Length);
    }

    public Task StartAudio()
    {
        if (!_sourceStarted)
        {
            _sourceStarted = true;
            _waveIn?.StartRecording();
        }
        return Task.CompletedTask;
    }

    public Task PauseAudio()
    {
        _waveIn?.StopRecording();
        return Task.CompletedTask;
    }

    public Task ResumeAudio()
    {
        _waveIn?.StartRecording();
        return Task.CompletedTask;
    }

    public Task CloseAudio()
    {
        if (_sourceStarted)
        {
            _sourceStarted = false;
            if (_waveIn is not null)
            {
                _waveIn.DataAvailable -= OnMicDataAvailable;
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;
            }
            (_encoder as IDisposable)?.Dispose();
            _encoder = null;
            _apmConfig?.Dispose();
            _apmConfig = null;
            _apm?.Dispose();
            _apm = null;
        }
        return Task.CompletedTask;
    }

    public Task StartAudioSink()
    {
        if (!_sinkStarted)
        {
            _sinkStarted = true;
            _waveOut?.Play();
        }
        return Task.CompletedTask;
    }

    public Task PauseAudioSink()
    {
        _waveOut?.Pause();
        return Task.CompletedTask;
    }

    public Task ResumeAudioSink()
    {
        _waveOut?.Play();
        return Task.CompletedTask;
    }

    public Task CloseAudioSink()
    {
        if (_sinkStarted)
        {
            _sinkStarted = false;
            _playbackTimer?.Dispose();
            _playbackTimer = null;
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            (_decoder as IDisposable)?.Dispose();
            _decoder = null;
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        CloseAudio();
        CloseAudioSink();
    }
}
