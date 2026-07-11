using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

public partial class VoiceSettingsWindow : FluentWindow
{
    private readonly VoiceService _voice;
    private bool _loaded;
    private bool _recordingHotkey;

    public VoiceSettingsWindow(VoiceService voice)
    {
        InitializeComponent();
        _voice = voice;

        var inputs = AudioDeviceService.GetInputDevices();
        var outputs = AudioDeviceService.GetOutputDevices();

        InputDeviceCombo.ItemsSource = inputs;
        OutputDeviceCombo.ItemsSource = outputs;

        InputDeviceCombo.SelectedItem = inputs.FirstOrDefault(d => d.Index == _voice.InputDeviceIndex) ?? inputs.FirstOrDefault();
        OutputDeviceCombo.SelectedItem = outputs.FirstOrDefault(d => d.Index == _voice.OutputDeviceIndex) ?? outputs.FirstOrDefault();

        NoiseSuppressionCheck.IsChecked = _voice.NoiseSuppressionEnabled;

        HotkeyRecordButton.Content = FormatTriggerName();
        PreviewKeyDown += VoiceSettingsWindow_PreviewKeyDown;
        PreviewMouseDown += VoiceSettingsWindow_PreviewMouseDown;

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

    private void InputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (InputDeviceCombo.SelectedItem is AudioDevice device)
            _voice.InputDeviceIndex = device.Index;
    }

    private void OutputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (OutputDeviceCombo.SelectedItem is AudioDevice device)
            _voice.OutputDeviceIndex = device.Index;
    }

    private void NoiseSuppressionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _voice.NoiseSuppressionEnabled = NoiseSuppressionCheck.IsChecked == true;
    }

    private void InputModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;

        _voice.InputMode = sender switch
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
        // sure the window (where PreviewKeyDown/PreviewMouseDown are
        // handled) does too - otherwise a control that eats input first
        // could swallow it.
        Focus();
    }

    private void VoiceSettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
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

        _voice.PushToTalkKey = key;
        HotkeyRecordButton.Content = FormatTriggerName();
    }

    // Only Middle/XButton1/XButton2 are recordable - Left/Right are
    // deliberately excluded (see GlobalHotkeyService) since watching those
    // would fire on every normal click anywhere on the system, not just a
    // deliberate PTT press.
    private void VoiceSettingsWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_recordingHotkey) return;
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.XButton1 or MouseButton.XButton2)) return;

        _recordingHotkey = false;
        e.Handled = true;

        _voice.PushToTalkMouseButton = e.ChangedButton;
        HotkeyRecordButton.Content = FormatTriggerName();
    }

    // "RightCtrl" -> "Right Ctrl", "CapsLock" -> "Caps Lock", single
    // letters/numbers/function keys pass through unchanged ("A", "F5").
    private static string FormatKeyName(Key key) =>
        Regex.Replace(key.ToString(), "(?<!^)([A-Z])", " $1");

    // Mouse Button 4/5 matches the naming most Windows software (and
    // Discord itself) already uses for the side buttons - "XButton1"/"2"
    // wouldn't mean anything to most people.
    private string FormatTriggerName() => _voice.PushToTalkMouseButton switch
    {
        MouseButton.Middle => "Mouse Middle Button",
        MouseButton.XButton1 => "Mouse Button 4",
        MouseButton.XButton2 => "Mouse Button 5",
        _ => _voice.PushToTalkKey is { } key ? FormatKeyName(key) : "None"
    };
}
