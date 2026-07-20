using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Voiceover.Client.Services;

public record SavedVoiceSettings(
    int? InputDeviceIndex,
    int? OutputDeviceIndex,
    bool NoiseSuppressionEnabled,
    VoiceInputMode InputMode,
    Key? PushToTalkKey,
    MouseButton? PushToTalkMouseButton = null,
    NoiseSuppressionBackend NoiseSuppressionBackend = NoiseSuppressionBackend.RNNoise,
    int RingTimeoutSeconds = 40,
    float SuppressionMix = 1f,
    bool Nsnet2UseGpu = false,
    int Nsnet2GpuDeviceId = 0,
    bool VadGateEnabled = false);

// Persists voice preferences (devices, noise suppression, input mode, PTT/
// push-to-mute hotkey) locally so they survive a log out/in - these are
// local-machine preferences (which mic, which key), not account data, so
// they live in a plain JSON file rather than the database. Unlike
// SessionStorage this isn't sensitive, so no DPAPI encryption.
public static class VoiceSettingsStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voiceover", "voicesettings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        // NoiseSuppressionBackendConverter must come first - System.Text.Json
        // picks the first registered converter whose CanConvert matches, so
        // this takes priority over the generic JsonStringEnumConverter below
        // for this one enum type specifically.
        Converters = { new NoiseSuppressionBackendConverter(), new JsonStringEnumConverter() }
    };

    // A saved file can name a backend from an older client version that no
    // longer exists (e.g. "WebRtcApm"/"DeepFilterNet"/"Dtln", removed after
    // being replaced by RNNoise/NSNet2) - the generic enum-by-name converter
    // would throw on that, which the try/catch below turns into "reset every
    // saved setting, not just the backend". Falling back to the default
    // backend here instead means the rest of the file (devices, hotkey,
    // ring timeout, etc.) still loads normally.
    private sealed class NoiseSuppressionBackendConverter : JsonConverter<NoiseSuppressionBackend>
    {
        public override NoiseSuppressionBackend Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Enum.TryParse<NoiseSuppressionBackend>(reader.GetString(), out var value) ? value : NoiseSuppressionBackend.RNNoise;

        public override void Write(Utf8JsonWriter writer, NoiseSuppressionBackend value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    public static void Save(SavedVoiceSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Best-effort - losing a saved preference isn't worth crashing the app over.
        }
    }

    public static SavedVoiceSettings? Load()
    {
        if (!File.Exists(FilePath)) return null;

        try
        {
            return JsonSerializer.Deserialize<SavedVoiceSettings>(File.ReadAllText(FilePath), Options);
        }
        catch
        {
            // Corrupted/outdated file - fall back to defaults rather than crash on startup.
            return null;
        }
    }
}
