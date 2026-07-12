using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

// Voice device/input-mode/hotkey settings - embedded as the Sound Options
// tab of the unified Settings window (opened from the avatar). Kept as its
// own UserControl (rather than inlined into SettingsWindow directly) so it
// isn't tied to one specific host window.
public partial class VoiceSettingsPanel : UserControl
{
    private VoiceService? _voice;
    private bool _loaded;
    private bool _recordingHotkey;
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
            case NoiseSuppressionBackend.RNNoise:
                RNNoiseRadio.IsChecked = true;
                break;
            case NoiseSuppressionBackend.DeepFilterNet:
                DeepFilterNetRadio.IsChecked = true;
                break;
            default:
                WebRtcApmRadio.IsChecked = true;
                break;
        }
        DeepFilterOptionsPanel.Visibility = _voice.NoiseSuppressionBackend == NoiseSuppressionBackend.DeepFilterNet
            ? Visibility.Visible : Visibility.Collapsed;
        AttenuationLimitSlider.Value = _voice.DeepFilterAttenuationLimit;
        UpdateAttenuationLimitDisplay();

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
            _ when sender == RNNoiseRadio => NoiseSuppressionBackend.RNNoise,
            _ when sender == DeepFilterNetRadio => NoiseSuppressionBackend.DeepFilterNet,
            _ => NoiseSuppressionBackend.WebRtcApm
        };
        DeepFilterOptionsPanel.Visibility = _voice.NoiseSuppressionBackend == NoiseSuppressionBackend.DeepFilterNet
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AttenuationLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateAttenuationLimitDisplay();
        if (!_loaded) return;
        _voice!.DeepFilterAttenuationLimit = (float)e.NewValue;
    }

    private void UpdateAttenuationLimitDisplay() =>
        AttenuationLimitDisplay.Text = $"{AttenuationLimitSlider.Value:0} dB";

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
