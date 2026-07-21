using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using PortAudioSharp;

namespace Voiceover.Client.Linux;

public class AudioDeviceItem
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public override string ToString() => Name;
}

public partial class SettingsWindow : Window
{
    // = null! - only the designer-only parameterless constructor below
    // ever leaves this unset.
    private readonly VoiceServiceLinux _voice = null!;

    public SettingsWindow() : this(null!) { }

    public SettingsWindow(VoiceServiceLinux voice)
    {
        _voice = voice;
        InitializeComponent();
        if (_voice is null) return;

        LoadDevices();

        NoiseSuppressionCheckBox.IsChecked = _voice.NoiseSuppressionEnabled;
        SuppressionMixSlider.Value = _voice.SuppressionMix * 100;
        MicGainSlider.Value = _voice.MicGain;
    }

    private void LoadDevices()
    {
        PortAudioBootstrap.EnsureInitialized();

        var inputDevices = new List<AudioDeviceItem> { new() { Index = -1, Name = "System default" } };
        var outputDevices = new List<AudioDeviceItem> { new() { Index = -1, Name = "System default" } };

        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxInputChannels > 0) inputDevices.Add(new AudioDeviceItem { Index = i, Name = info.name });
            if (info.maxOutputChannels > 0) outputDevices.Add(new AudioDeviceItem { Index = i, Name = info.name });
        }

        InputDeviceCombo.ItemsSource = inputDevices;
        OutputDeviceCombo.ItemsSource = outputDevices;

        InputDeviceCombo.SelectedItem = inputDevices.FirstOrDefault(d => d.Index == (_voice.InputDeviceIndex ?? -1)) ?? inputDevices[0];
        OutputDeviceCombo.SelectedItem = outputDevices.FirstOrDefault(d => d.Index == (_voice.OutputDeviceIndex ?? -1)) ?? outputDevices[0];
    }

    // Device changes are read back from VoiceServiceLinux the next time a
    // voice channel is joined (see JoinChannelAsyncCore) - same "takes
    // effect on next join, not mid-call" behavior as the WPF client's own
    // device pickers.
    private void InputDeviceCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (InputDeviceCombo.SelectedItem is AudioDeviceItem item)
            _voice.InputDeviceIndex = item.Index < 0 ? null : item.Index;
    }

    private void OutputDeviceCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OutputDeviceCombo.SelectedItem is AudioDeviceItem item)
            _voice.OutputDeviceIndex = item.Index < 0 ? null : item.Index;
    }

    private void NoiseSuppressionCheckBox_Click(object? sender, RoutedEventArgs e) =>
        _voice.NoiseSuppressionEnabled = NoiseSuppressionCheckBox.IsChecked == true;

    private void SuppressionMixSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e) =>
        _voice.SuppressionMix = (float)(SuppressionMixSlider.Value / 100.0);

    private void MicGainSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e) =>
        _voice.MicGain = (float)MicGainSlider.Value;

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
