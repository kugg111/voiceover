using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NAudio.Wave;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

// Voice device/input-mode/hotkey settings - embedded as the Sound Options
// tab of the unified Settings page (opened from the avatar). Kept as its
// own UserControl (rather than inlined into SettingsPage directly) so it
// isn't tied to one specific host.
public partial class VoiceSettingsPanel : UserControl
{
    private VoiceService? _voice;
    private bool _loaded;
    private bool _recordingHotkey;
    private bool _testingMic;
    private Window? _hostWindow;

    public VoiceSettingsPanel()
    {
        InitializeComponent();
        Loaded += VoiceSettingsPanel_Loaded;
        Unloaded += VoiceSettingsPanel_Unloaded;
    }

    public void Initialize(VoiceService voice)
    {
        _voice = voice;

        var inputs = AudioDeviceService.GetInputDevices();
        var outputs = AudioDeviceService.GetOutputDevices();

        InputDeviceCombo.ItemsSource = inputs;
        OutputDeviceCombo.ItemsSource = outputs;

        InputDeviceCombo.SelectedItem = inputs.FirstOrDefault(d => d.Index == _voice.InputDeviceIndex) ?? inputs.FirstOrDefault();
        OutputDeviceCombo.SelectedItem = outputs.FirstOrDefault(d => d.Index == _voice.OutputDeviceIndex) ?? outputs.FirstOrDefault();

        NoiseSuppressionCheck.IsChecked = _voice.NoiseSuppressionEnabled;
        NoiseSuppressionBackendPanel.Visibility = _voice.NoiseSuppressionEnabled ? Visibility.Visible : Visibility.Collapsed;
        switch (_voice.NoiseSuppressionBackend)
        {
            case NoiseSuppressionBackend.Nsnet2:
                Nsnet2Radio.IsChecked = true;
                break;
            case NoiseSuppressionBackend.FacebookDenoiser:
                FacebookDenoiserRadio.IsChecked = true;
                break;
            default:
                RNNoiseRadio.IsChecked = true;
                break;
        }
        SuppressionMixSlider.Value = _voice.SuppressionMix * 100;
        UpdateSuppressionMixDisplay();
        UpdateSuppressionMixAvailability();

        VadGateCheck.IsChecked = _voice.VadGateEnabled;

        HotkeyRecordButton.Content = FormatTriggerName();

        switch (_voice.InputMode)
        {
            case VoiceInputMode.PushToTalk:
                PushToTalkRadio.IsChecked = true;
                break;
            case VoiceInputMode.PushToMute:
                PushToMuteRadio.IsChecked = true;
                break;
            default:
                VoiceActivityRadio.IsChecked = true;
                break;
        }
        HotkeyPanel.Visibility = _voice.InputMode == VoiceInputMode.VoiceActivity ? Visibility.Collapsed : Visibility.Visible;

        RingTimeoutSlider.Value = _voice.RingTimeoutSeconds;
        UpdateRingTimeoutDisplay();

        _loaded = true;
    }

    // Hotkey recording needs PreviewKeyDown/PreviewMouseDown on whichever
    // window actually hosts this panel (a different one depending on where
    // it's embedded), so it's wired dynamically once the panel is in a live
    // visual tree instead of assuming a fixed window in the constructor.
    private void VoiceSettingsPanel_Loaded(object sender, RoutedEventArgs e)
    {
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is null) return;

        _hostWindow.PreviewKeyDown += HostWindow_PreviewKeyDown;
        _hostWindow.PreviewMouseDown += HostWindow_PreviewMouseDown;
    }

    private void VoiceSettingsPanel_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is null) return;

        _hostWindow.PreviewKeyDown -= HostWindow_PreviewKeyDown;
        _hostWindow.PreviewMouseDown -= HostWindow_PreviewMouseDown;
        _hostWindow = null;
    }

    private void InputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (InputDeviceCombo.SelectedItem is AudioDevice device)
            _voice!.InputDeviceIndex = device.Index;
    }

    private void OutputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (OutputDeviceCombo.SelectedItem is AudioDevice device)
            _voice!.OutputDeviceIndex = device.Index;
    }

    private void NoiseSuppressionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _voice!.NoiseSuppressionEnabled = NoiseSuppressionCheck.IsChecked == true;
        NoiseSuppressionBackendPanel.Visibility = _voice.NoiseSuppressionEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NoiseSuppressionBackendRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _voice!.NoiseSuppressionBackend = sender switch
        {
            _ when sender == Nsnet2Radio => NoiseSuppressionBackend.Nsnet2,
            _ when sender == FacebookDenoiserRadio => NoiseSuppressionBackend.FacebookDenoiser,
            _ => NoiseSuppressionBackend.RNNoise
        };
        UpdateSuppressionMixAvailability();
    }

    private void VadGateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _voice!.VadGateEnabled = VadGateCheck.IsChecked == true;
    }

    // Stored/displayed as 0-100% in the UI - VoiceService.SuppressionMix
    // itself is 0-1 (a plain multiplier used directly in the blend math).
    private void SuppressionMixSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSuppressionMixDisplay();
        if (!_loaded) return;
        _voice!.SuppressionMix = (float)(e.NewValue / 100.0);
    }

    private void UpdateSuppressionMixDisplay() =>
        SuppressionMixDisplay.Text = $"{SuppressionMixSlider.Value:0}%";

    // NSNet2's own overlap-add reconstruction gives it real, fixed
    // algorithmic delay. NoiseSuppressionProcessor.ProcessFrame runs the
    // dry signal through a matching delay line before blending for this
    // backend specifically, so the slider is safe to use for either engine
    // without the comb-filtering ("echo"/"doubling") that blending against
    // an undelayed raw signal would cause.
    private void UpdateSuppressionMixAvailability()
    {
        SuppressionMixSlider.IsEnabled = true;
        SuppressionMixHelpText.Text = "Blends the suppressed signal with your raw mic - applies no matter which engine is selected above. 100% is fully processed; lower this if the engine is eating quiet parts of your voice.";
    }

    // Records ~3s from the selected input device, runs it through a
    // throwaway NoiseSuppressionProcessor configured with the panel's
    // current settings (not the live capture pipeline - Settings can be
    // open without an active voice channel), and plays the result back
    // through the selected output device. Lets someone hear the effect of a
    // slider change immediately instead of needing a live call with someone
    // else listening.
    private async void TestMicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_testingMic || _voice is null) return;

        var inputIndex = (InputDeviceCombo.SelectedItem as AudioDevice)?.Index ?? -1;
        var outputIndex = (OutputDeviceCombo.SelectedItem as AudioDevice)?.Index ?? -1;
        if (inputIndex < 0 || outputIndex < 0)
        {
            TestMicResultText.Text = "Select an input and output device first.";
            return;
        }

        _testingMic = true;
        TestMicButton.IsEnabled = false;
        TestMicResultText.Text = "";

        try
        {
            TestMicButton.Content = "Recording... (3s)";
            var pcm = await RecordAsync(inputIndex, TimeSpan.FromSeconds(3));

            TestMicButton.Content = "Processing...";
            // Off the UI thread - constructing NoiseSuppressionProcessor
            // (ONNX session init for NSNet2) and running ~150 frames of DSP
            // through it is real blocking work; running it directly on the
            // awaited UI-thread continuation (as this used to) froze the
            // dispatcher for however long that took.
            var resultText = await Task.Run(() =>
            {
                using var processor = new NoiseSuppressionProcessor
                {
                    Enabled = _voice.NoiseSuppressionEnabled,
                    Backend = _voice.NoiseSuppressionBackend,
                    SuppressionMix = _voice.SuppressionMix,
                    VadGateEnabled = _voice.VadGateEnabled
                };

                // Same 20ms/960-sample frame size MicCaptureSource's live
                // capture loop uses - keeps Test Mic's processing identical
                // to what actually gets published in a real call. A
                // trailing partial frame (recording length isn't guaranteed
                // to be an exact multiple) is just left unprocessed/raw - a
                // few milliseconds of untouched tail is inaudible.
                const int frameSize = 960;
                for (int offset = 0; offset + frameSize <= pcm.Length; offset += frameSize)
                {
                    var frame = new short[frameSize];
                    Array.Copy(pcm, offset, frame, 0, frameSize);
                    processor.ProcessFrame(frame);
                    Array.Copy(frame, 0, pcm, offset, frameSize);
                }

                return processor.Backend switch
                {
                    NoiseSuppressionBackend.Nsnet2 => $"NSNet2: ~{processor.LastNsnet2Ms:0.0}ms/frame (20ms budget)",
                    NoiseSuppressionBackend.FacebookDenoiser => $"Facebook Denoiser: ~{processor.LastFacebookDenoiserMs:0.0}ms/frame (20ms budget)",
                    _ => $"RNNoise: ~{processor.LastRNNoiseMs:0.0}ms/frame (20ms budget)"
                };
            });
            TestMicResultText.Text = resultText;

            TestMicButton.Content = "Playing back...";
            // PlaybackAsync's WaveOutEvent.Init/Play also block on device
            // open - same reasoning, keep it off the UI thread. WaveOutEvent
            // doesn't need the calling thread's message pump (it drives its
            // own callback thread internally), so this is safe.
            await Task.Run(() => PlaybackAsync(outputIndex, pcm));
        }
        catch (Exception ex)
        {
            TestMicResultText.Text = $"Test failed: {ex.Message}";
        }
        finally
        {
            _testingMic = false;
            TestMicButton.IsEnabled = true;
            TestMicButton.Content = "Test Mic";
        }
    }

    private static Task<short[]> RecordAsync(int deviceIndex, TimeSpan duration)
    {
        var tcs = new TaskCompletionSource<short[]>();
        var samples = new List<short>();
        var waveIn = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = new WaveFormat(48000, 16, 1),
            BufferMilliseconds = 50
        };

        waveIn.DataAvailable += (_, args) =>
        {
            var chunk = new short[args.BytesRecorded / 2];
            Buffer.BlockCopy(args.Buffer, 0, chunk, 0, args.BytesRecorded);
            lock (samples) samples.AddRange(chunk);
        };
        waveIn.RecordingStopped += (_, _) =>
        {
            waveIn.Dispose();
            tcs.TrySetResult(samples.ToArray());
        };

        waveIn.StartRecording();
        _ = Task.Delay(duration).ContinueWith(_ => waveIn.StopRecording());

        return tcs.Task;
    }

    private static Task PlaybackAsync(int deviceIndex, short[] pcm)
    {
        var tcs = new TaskCompletionSource();
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

        var stream = new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(48000, 16, 1));
        var waveOut = new WaveOutEvent { DeviceNumber = deviceIndex };
        waveOut.Init(stream);
        waveOut.PlaybackStopped += (_, _) =>
        {
            waveOut.Dispose();
            stream.Dispose();
            tcs.TrySetResult();
        };
        waveOut.Play();

        return tcs.Task;
    }

    private void RingTimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateRingTimeoutDisplay();
        if (!_loaded) return;
        _voice!.RingTimeoutSeconds = (int)e.NewValue;
    }

    // RingTimeoutDisplay can still be null the first time this fires -
    // unlike AttenuationLimitSlider (Minimum="0", matching the Slider's own
    // default Value so no coercion happens at parse time), this slider's
    // Minimum="10" is above the default Value=0, so setting Minimum during
    // XAML parsing immediately coerces Value up to 10 and raises
    // ValueChanged before the sibling TextBlock below it in the markup has
    // been constructed yet. Initialize() calls this again once the whole
    // panel is loaded, so skipping the early call here is harmless.
    private void UpdateRingTimeoutDisplay()
    {
        if (RingTimeoutDisplay is null) return;
        RingTimeoutDisplay.Text = $"{RingTimeoutSlider.Value:0}s";
    }

    private void InputModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;

        _voice!.InputMode = sender switch
        {
            _ when sender == PushToTalkRadio => VoiceInputMode.PushToTalk,
            _ when sender == PushToMuteRadio => VoiceInputMode.PushToMute,
            _ => VoiceInputMode.VoiceActivity
        };

        HotkeyPanel.Visibility = _voice.InputMode == VoiceInputMode.VoiceActivity ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HotkeyRecordButton_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        HotkeyRecordButton.Content = "Press any key or mouse button... (Esc to cancel)";
        // The button itself already has focus from being clicked, but make
        // sure the host window (where PreviewKeyDown/PreviewMouseDown are
        // handled) does too - otherwise a control that eats input first
        // could swallow it.
        _hostWindow?.Focus();
    }

    private void HostWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey) return;

        // Alt-combinations arrive as Key.System with the real key in
        // SystemKey instead - unwrap it so recording Right Alt et al. works.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        _recordingHotkey = false;
        e.Handled = true;

        if (key == Key.Escape)
        {
            HotkeyRecordButton.Content = FormatTriggerName();
            return;
        }

        _voice!.PushToTalkKey = key;
        HotkeyRecordButton.Content = FormatTriggerName();
    }

    // Only Middle/XButton1/XButton2 are recordable - Left/Right are
    // deliberately excluded (see GlobalHotkeyService) since watching those
    // would fire on every normal click anywhere on the system, not just a
    // deliberate PTT press.
    private void HostWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_recordingHotkey) return;
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.XButton1 or MouseButton.XButton2)) return;

        _recordingHotkey = false;
        e.Handled = true;

        _voice!.PushToTalkMouseButton = e.ChangedButton;
        HotkeyRecordButton.Content = FormatTriggerName();
    }

    // "RightCtrl" -> "Right Ctrl", "CapsLock" -> "Caps Lock", single
    // letters/numbers/function keys pass through unchanged ("A", "F5").
    private static string FormatKeyName(Key key) =>
        Regex.Replace(key.ToString(), "(?<!^)([A-Z])", " $1");

    // Mouse Button 4/5 matches the naming most Windows software (and
    // Discord itself) already uses for the side buttons - "XButton1"/"2"
    // wouldn't mean anything to most people.
    private string FormatTriggerName() => _voice?.PushToTalkMouseButton switch
    {
        MouseButton.Middle => "Mouse Middle Button",
        MouseButton.XButton1 => "Mouse Button 4",
        MouseButton.XButton2 => "Mouse Button 5",
        _ => _voice?.PushToTalkKey is { } key ? FormatKeyName(key) : "None"
    };
}
