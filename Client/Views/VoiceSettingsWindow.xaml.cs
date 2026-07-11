using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

public partial class VoiceSettingsWindow : FluentWindow
{
    public VoiceSettingsWindow(VoiceService voice)
    {
        InitializeComponent();
        Panel.Initialize(voice);
    }
}
