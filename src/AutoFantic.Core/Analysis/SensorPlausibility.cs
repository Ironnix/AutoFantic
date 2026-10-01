using AutoFantic.Core.Hardware;
using static AutoFantic.Core.Texts;

namespace AutoFantic.Core.Analysis;

/// <summary>
/// Catches a key temperature sensor that stops reporting or reports nonsense (0, or far beyond any
/// real value). <see cref="SafetyLimits"/> can only stop on a value it gets: a lost sensor would
/// otherwise look like "everything fine" while a fan sits at a low speed.
///
/// Only sensors that read plausibly on the first check are watched, so a sensor that never
/// existed on this PC doesn't block anything. The CPU temperature is the exception: every PC has
/// one, and a CPU that reads 0 °C from the start (the PawnIO driver missing or not loading) must
/// not pass for a cool one. A few bad samples in a row are tolerated, because a
/// single failed read happens now and then. A frozen value is not treated as an error: the GPU
/// core reports whole degrees and can honestly sit on one value for minutes under steady load.
/// </summary>
public sealed class SensorPlausibility(KeySensors keys)
{
    public const float MinPlausible = 1;
    public const float MaxPlausible = 150;

    /// <summary>Bad samples in a row before it counts as a sensor error (about 3 s at 1 Hz).</summary>
    public const int ToleratedBadSamples = 3;

    private List<(string Label, string Id)>? _watched;
    private readonly Dictionary<string, int> _badInARow = [];

    /// <summary>Returns a description of the sensor error, or null if every watched sensor looks fine.</summary>
    public string? Check(Snapshot snapshot)
    {
        _watched ??= Candidates()
            .Where(c => c.Id == keys.CpuTemp || IsPlausible(snapshot.Value(c.Id)))
            .ToList();

        foreach (var (label, id) in _watched)
        {
            float? value = snapshot.Value(id);
            if (IsPlausible(value))
            {
                _badInARow[id] = 0;
                continue;
            }

            int bad = _badInARow[id] = _badInARow.GetValueOrDefault(id) + 1;
            if (bad >= ToleratedBadSamples)
                return value is { } v
                    ? T($"{T(label)} sensor reads {v:0.0} °C, which can't be right")
                    : T($"{T(label)} sensor stopped reporting");
        }
        return null;
    }

    private IEnumerable<(string Label, string Id)> Candidates()
    {
        if (keys.CpuTemp is { } cpu)
            yield return ("CPU", cpu);
        if (keys.GpuTemp is { } gpu)
            yield return ("GPU core", gpu);
        if (keys.GpuHotspot is { } hot)
            yield return ("GPU hotspot", hot);
        if (keys.GpuMemory is { } mem)
            yield return ("GPU memory", mem);
    }

    /// <summary>A temperature a working sensor can report: not missing, not 0, not far beyond any real value.</summary>
    public static bool IsPlausible(float? value) =>
        value is { } v && float.IsFinite(v) && v >= MinPlausible && v <= MaxPlausible;
}
