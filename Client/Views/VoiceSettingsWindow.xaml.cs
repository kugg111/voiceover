using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DiscordClone.Client.Services;

namespace DiscordClone.Client.Views;

public partial class VoiceSettingsWindow : Window
{
    private readonly VoiceService _voice;
    private bool _loaded;

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
}
