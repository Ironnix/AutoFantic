namespace AutoFantic.Core.Hardware;

/// <summary>The handful of sensors AuFantic actually reasons about. Each is a sensor id, or null if not found.</summary>
public sealed record KeySensors(
    string? CpuTemp,
    string? CpuPower,
    string? GpuTemp,
    string? GpuHotspot,
    string? GpuMemory,
    string? GpuPower,
    string? CpuLoad = null,
    string? GpuLoad = null)
{
    // Names as LibreHardwareMonitor reports them, best match first.
    private static readonly string[] CpuTempNames = ["Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Tctl", "Core Average", "Core Max"];
    private static readonly string[] CpuPowerNames = ["Package", "CPU Package"];
    private static readonly string[] GpuTempNames = ["GPU Core"];
    private static readonly string[] GpuHotspotNames = ["GPU Hot Spot", "GPU Hotspot"];
    private static readonly string[] GpuMemoryNames = ["GPU Memory Junction", "GPU Memory"];
    private static readonly string[] GpuPowerNames = ["GPU Package", "GPU Power", "GPU Board Power", "GPU Core"];
    private static readonly string[] CpuLoadNames = ["CPU Total"];
    private static readonly string[] GpuLoadNames = ["GPU Core", "D3D 3D"];

    public static KeySensors Detect(Snapshot snapshot)
    {
        var cpu = snapshot.Readings.Where(r => r.HardwareType == "Cpu").ToList();
        var gpu = PickGpu(snapshot);

        return new KeySensors(
            CpuTemp: Pick(cpu, SensorKind.Temperature, CpuTempNames),
            CpuPower: Pick(cpu, SensorKind.Power, CpuPowerNames),
            GpuTemp: Pick(gpu, SensorKind.Temperature, GpuTempNames),
            GpuHotspot: Pick(gpu, SensorKind.Temperature, GpuHotspotNames),
            GpuMemory: Pick(gpu, SensorKind.Temperature, GpuMemoryNames),
            GpuPower: Pick(gpu, SensorKind.Power, GpuPowerNames),
            CpuLoad: Pick(cpu, SensorKind.Load, CpuLoadNames),
            GpuLoad: Pick(gpu, SensorKind.Load, GpuLoadNames));
    }

    /// <summary>
    /// The graphics card whose fans matter. With an iGPU next to a discrete card, prefer NVIDIA,
    /// then an AMD GPU that reports a hotspot (integrated Radeons don't).
    /// </summary>
    private static List<SensorReading> PickGpu(Snapshot snapshot)
    {
        var byGpu = snapshot.Readings
            .Where(r => r.HardwareType.StartsWith("Gpu", StringComparison.Ordinal))
            .GroupBy(r => r.Hardware)
            .ToList();

        var best = byGpu.FirstOrDefault(g => g.First().HardwareType == "GpuNvidia")
            ?? byGpu.FirstOrDefault(g => g.Any(r => GpuHotspotNames.Contains(r.Name)))
            ?? byGpu.FirstOrDefault();

        return best?.ToList() ?? [];
    }

    private static string? Pick(IEnumerable<SensorReading> readings, SensorKind kind, string[] names)
    {
        var candidates = readings.Where(r => r.Kind == kind).ToList();
        foreach (var name in names)
        {
            var hit = candidates.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                return hit.Id;
        }
        return null;
    }
}
