using NAudio.Wave;

namespace Voiceover.Client.Services;

// Index and display name of a Windows audio device, matching the index scheme
// WindowsAudioEndPoint expects for its audioInDeviceIndex/audioOutDeviceIndex
// constructor args (which map straight onto NAudio's WaveIn/WaveOut device indices).
public record AudioDevice(int Index, string Name);

public static class AudioDeviceService
{
    public static List<AudioDevice> GetInputDevices() =>
        Enumerable.Range(0, WaveInEvent.DeviceCount)
            .Select(i => new AudioDevice(i, WaveInEvent.GetCapabilities(i).ProductName))
            .ToList();

    public static List<AudioDevice> GetOutputDevices() =>
        Enumerable.Range(0, WaveOut.DeviceCount)
            .Select(i => new AudioDevice(i, WaveOut.GetCapabilities(i).ProductName))
            .ToList();
}
