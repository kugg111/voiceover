using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

public record HotkeyOption(string Name, Key Key);

public partial class VoiceSettingsWindow : FluentWindow
{
    private readonly VoiceService _voice;
    private bool _loaded;

    // A fixed, curated list rather than a "press any key to bind" capture
    // control - keeps this simple, and all of these are safe defaults that
    // don't collide with normal typing or common app/OS shortcuts.
    private static readonly List<HotkeyOption> HotkeyOptions = new()
    {
        new("Right Ctrl", Key.RightCtrl),
        new("Right Shift", Key.RightShift),
        new("Right Alt", Key.RightAlt),
        new("Caps Lock", Key.CapsLock),
        new("Left Alt", Key.LeftAlt),
    };

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

        HotkeyCombo.ItemsSource = HotkeyOptions;
        HotkeyCombo.SelectedItem = HotkeyOptions.FirstOrDefault(h => h.Key == _voice.PushToTalkKey) ?? HotkeyOptions[0];

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

    private void HotkeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (HotkeyCombo.SelectedItem is HotkeyOption option)
            _voice.PushToTalkKey = option.Key;
    }
}
