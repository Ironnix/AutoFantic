using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoFanatic.Core.Calibration;

public sealed record CurvePoint(double Temperature, double Percent);

/// <summary>A fan group's result: what it cools, and its curve (fan % by the temperature it follows).</summary>
/// <param name="CpuEffect">°C the CPU gets warmer when this group goes from 100 % to its lowest speed, at calibration load.</param>
/// <param name="GpuEffect">The same for the GPU core.</param>
/// <param name="OffAt">Load levels at which this group is switched off (e.g. "idle").</param>
public sealed record CalibratedGroup(
    string Name,
    IReadOnlyList<int> Channels,
    IReadOnlyList<string> ControlIds,
    Component Follows,
    double CpuEffect,
    double GpuEffect,
    IReadOnlyList<CurvePoint> Curve,
    IReadOnlyList<string> OffAt);

/// <summary>One load level of the result table: the quietest mix found for it.</summary>
public sealed record LoadRow(
    string Label,
    double CpuPower,
    double GpuPower,
    IReadOnlyList<double> Speeds,
    IReadOnlyDictionary<Component, double> Temperatures,
    double Noise,
    bool MeetsTarget);

/// <summary>Everything a calibration produced, saved as JSON for "Use my curves".</summary>
/// <param name="StopCpuWatts">Fans may only be off while the CPU draws at most this …</param>
/// <param name="StopGpuWatts">… and the GPU at most this.</param>
public sealed record CalibrationResult(
    DateTimeOffset Created,
    string Profile,
    double Ambient,
    IReadOnlyList<CalibratedGroup> Groups,
    IReadOnlyList<LoadRow> Table,
    IReadOnlyDictionary<Component, double[]> Model,
    double StopCpuWatts,
    double StopGpuWatts)
{
    // "silent" is −∞ dB
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static CalibrationResult? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<CalibrationResult>(File.ReadAllText(path), Json) : null;

    /// <summary>
    /// A group's curve from the table: at each load level where it spins, the temperature it follows
    /// and the speed chosen there. Sorted by temperature and kept rising, so it can go straight into
    /// a BIOS or MSI Afterburner curve. Levels where it is off are left out: a fan that stands still
    /// leaves its part warmer than a light load with the fan on, which a temperature curve can't
    /// express; <see cref="CalibratedGroup.OffAt"/> carries that instead.
    /// </summary>
    /// <param name="fullSpeedAt">From this temperature on the fan runs at 100 %: whatever task gets hotter
    /// than anything the calibration saw is still covered.</param>
    public static IReadOnlyList<CurvePoint> CurveFor(IReadOnlyList<LoadRow> table, int group, Component follows, double fullSpeedAt)
    {
        // rows whose target can't be met are left out: there the fans simply run flat out (fullSpeedAt)
        var points = table
            .Where(r => r.Speeds[group] > 0 && r.MeetsTarget)
            .Select(r => new CurvePoint(Math.Round(r.Temperatures.GetValueOrDefault(follows)), r.Speeds[group]))
            .OrderBy(p => p.Temperature)
            .ToList();

        var curve = new List<CurvePoint>();
        foreach (var point in points)
        {
            double percent = curve.Count == 0 ? point.Percent : Math.Max(point.Percent, curve[^1].Percent);
            if (curve.Count > 0 && Math.Abs(curve[^1].Temperature - point.Temperature) < 1)
                curve[^1] = curve[^1] with { Percent = percent };
            else if (curve.Count == 0 || percent != curve[^1].Percent || point == points[^1])
                curve.Add(new CurvePoint(point.Temperature, percent));
        }

        if (curve.Count == 0 || (curve[^1].Temperature < fullSpeedAt && curve[^1].Percent < 100))
            curve.Add(new CurvePoint(fullSpeedAt, 100));
        else if (curve[^1].Percent < 100)
            curve[^1] = curve[^1] with { Percent = 100 };
        return curve;
    }
}
