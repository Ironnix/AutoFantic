using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoFantic.Core.Calibration;

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
/// <param name="Rms">How far the model is off from the measurements, °C per temperature.</param>
/// <param name="Sources">The calibrations the result is built from, e.g. "28.09. 22:26 Bodycam".</param>
public sealed record CalibrationResult(
    DateTimeOffset Created,
    string Profile,
    double Ambient,
    IReadOnlyList<CalibratedGroup> Groups,
    IReadOnlyList<LoadRow> Table,
    IReadOnlyDictionary<Component, double[]> Model,
    double StopCpuWatts,
    double StopGpuWatts,
    IReadOnlyDictionary<Component, double>? Rms = null,
    IReadOnlyList<string>? Sources = null)
{
    // "silent" is −∞ dB
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static CalibrationResult? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<CalibrationResult>(File.ReadAllText(path), Json) : null;

    /// <summary>
    /// The four points a BIOS fan curve (e.g. MSI Smart Fan) takes: the first three points of the
    /// curve and the one where it reaches 100 %. A shorter curve gets extra points in its longest
    /// stretch, on the line, because a BIOS wants four different temperatures.
    /// </summary>
    public static IReadOnlyList<CurvePoint> BiosPoints(IReadOnlyList<CurvePoint> curve)
    {
        if (curve.Count == 0)
            return [];
        if (curve.Count == 1)
            return [.. new[] { 15, 10, 5 }.Select(d => curve[0] with { Temperature = curve[0].Temperature - d }), curve[0]];

        var points = curve.Take(Math.Min(3, curve.Count - 1)).Append(curve[^1]).ToList();
        while (points.Count < 4)
        {
            int widest = Enumerable.Range(1, points.Count - 1).MaxBy(i => points[i].Temperature - points[i - 1].Temperature);
            var (a, b) = (points[widest - 1], points[widest]);
            double t = Math.Round((a.Temperature + b.Temperature) / 2);
            points.Insert(widest, new CurvePoint(t, Math.Round(a.Percent + (b.Percent - a.Percent) * (t - a.Temperature) / (b.Temperature - a.Temperature))));
        }
        return points;
    }

    /// <summary>
    /// A group's curve from the table: at each load level where it spins, the temperature it follows
    /// and the speed chosen there. Sorted by temperature and kept rising, so it can go straight into
    /// a BIOS or MSI Afterburner curve. Every level stays a point, including flat stretches (30 % at
    /// 54 °C and still 30 % at 67 °C), so the curve doesn't ramp up earlier than planned. Levels
    /// where it is off are left out: a fan that stands still leaves its part warmer than a light
    /// load with the fan on, which a temperature curve can't express; <see cref="CalibratedGroup.OffAt"/>
    /// carries that instead.
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
            else
                curve.Add(new CurvePoint(point.Temperature, percent));
        }

        if (curve.Count == 0 || (curve[^1].Temperature < fullSpeedAt && curve[^1].Percent < 100))
            curve.Add(new CurvePoint(fullSpeedAt, 100));
        else if (curve[^1].Percent < 100)
            curve[^1] = curve[^1] with { Percent = 100 };
        return curve;
    }
}
