using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Core.Analysis;

/// <summary>
/// Hard limits in °C. Crossing one ends any experiment and sends every fan to 100 % until
/// <see cref="SafetyRecovery"/> says the temperatures are safely back down.
/// </summary>
public sealed record SafetyLimits(
    float CpuMax = 90,
    float GpuCoreMax = 85,
    float GpuHotspotMax = 100,
    float GpuMemoryMax = 100)
{
    /// <summary>Returns a description of the first limit crossed, or null if everything is fine.</summary>
    /// <param name="margin">Treat a value this many °C below a limit as crossing it already.</param>
    public string? Check(Snapshot snapshot, KeySensors keys, float margin = 0)
    {
        return Over("CPU", snapshot.Value(keys.CpuTemp), CpuMax, margin)
            ?? Over("GPU core", snapshot.Value(keys.GpuTemp), GpuCoreMax, margin)
            ?? Over("GPU hotspot", snapshot.Value(keys.GpuHotspot), GpuHotspotMax, margin)
            ?? Over("GPU memory", snapshot.Value(keys.GpuMemory), GpuMemoryMax, margin);
    }

    private static string? Over(string label, float? value, float max, float margin) =>
        value is { } v && v >= max - margin ? $"{label} {v:0.0} °C ≥ limit {max:0} °C" : null;
}

/// <summary>
/// After a hard limit, fans stay at 100 % until every temperature has been at least
/// <see cref="Margin"/> below its limit for <see cref="CoolFor"/>. Then normal control, and
/// experiments, carry on: there is no lock-out period.
/// </summary>
public sealed class SafetyRecovery(SafetyLimits limits, KeySensors keys)
{
    public const float Margin = 5;

    public static readonly TimeSpan CoolFor = TimeSpan.FromSeconds(10);

    private DateTimeOffset? _coolSince;

    /// <summary>Feed one sample; true once the temperatures have stayed safely low for long enough.</summary>
    public bool IsRecovered(Snapshot snapshot)
    {
        if (limits.Check(snapshot, keys, Margin) is not null)
        {
            _coolSince = null;
            return false;
        }

        _coolSince ??= snapshot.Time;
        return snapshot.Time - _coolSince >= CoolFor;
    }
}
