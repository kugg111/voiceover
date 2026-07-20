using System.Management;

namespace Voiceover.Client.Services;

// Index and display name of an installed GPU, matching the adapter index
// DirectML's SessionOptions.AppendExecutionProvider_DML(int) expects - see
// Nsnet2Processor.cs. Mirrors AudioDeviceService's shape for the same
// "index the picker returns maps onto a device-selection API" pattern.
public record GpuDevice(int Index, string Name);

public static class GpuDeviceService
{
    // Win32_VideoController enumeration order is a best-effort stand-in for
    // DXGI adapter order (what AppendExecutionProvider_DML actually
    // indexes) - both come from the same underlying driver/PnP
    // enumeration, but WMI doesn't guarantee it matches DXGI exactly.
    // Nsnet2Processor's GPU init already falls back to CPU on any failure,
    // so a rare mismatch here means "picked a different but still valid
    // GPU" at worst, not a crash.
    public static List<GpuDevice> GetGpus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            return searcher.Get()
                .Cast<ManagementBaseObject>()
                .Select((mo, i) => new GpuDevice(i, mo["Name"] as string ?? $"GPU {i}"))
                .ToList();
        }
        catch
        {
            // WMI can be unavailable/locked down on some machines - an
            // empty list just means the picker shows nothing to choose
            // from, not a crash. GPU inference itself still works via the
            // default device 0 if the user never gets to pick.
            return new List<GpuDevice>();
        }
    }
}
