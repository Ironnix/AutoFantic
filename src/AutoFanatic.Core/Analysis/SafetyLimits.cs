using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Core.Analysis;

/// <summary>Hard limits in °C. Crossing one ends any experiment and hands the fans back to the BIOS.</summary>
public sealed record SafetyLimits(
    float CpuMax = 90,
    float GpuCoreMax = 85,
    float GpuHotspotMax = 100,
    float GpuMemoryMax = 100)
{
    /// <summary>Returns a description of the first limit crossed, or null if everything is fine.</summary>
    public string? Check(Snapshot snapshot, KeySensors keys)
    {
        return Over("CPU", snapshot.Value(keys.CpuTemp), CpuMax)
            ?? Over("GPU core", snapshot.Value(keys.GpuTemp), GpuCoreMax)
            ?? Over("GPU hotspot", snapshot.Value(keys.GpuHotspot), GpuHotspotMax)
            ?? Over("GPU memory", snapshot.Value(keys.GpuMemory), GpuMemoryMax);
    }

    private static string? Over(string label, float? value, float max) =>
        value is { } v && v >= max ? $"{label} {v:0.0} °C ≥ limit {max:0} °C" : null;
}
