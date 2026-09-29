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
    IReadOnlyList<string> OffAt,
    bool Bios = false);

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
/// <param name="Version">The rules the curves were made with (<see cref="CurrentVersion"/>); 0 = before versions existed.</param>
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
    IReadOnlyList<string>? Sources = null,
    int Version = 0)
{
    /// <summary>
    /// Raised whenever the way curves are made changes, so a result from before is worked out again
    /// from the stored measurements. 1: case fans follow the warmer of CPU and GPU, curves no steeper
    /// than <see cref="MaxSlope"/>.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// A curve rises at most this many % per °C (below the final ramp to 100 %). Steeper, a fan jumps
    /// between two speeds whenever the temperature wobbles by a degree.
    /// </summary>
    public const double MaxSlope = 5;

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
    /// <param name="maxSlope">No stretch below the final ramp to 100 % rises faster than this (% per °C);
    /// a steeper one starts rising earlier instead (<see cref="LimitSteepness"/>).</param>
    public static IReadOnlyList<CurvePoint> CurveFor(IReadOnlyList<LoadRow> table, int group, Component follows, double fullSpeedAt, double maxSlope = double.PositiveInfinity)
    {
        // rows whose target can't be met are left out: there the fans simply run flat out (fullSpeedAt)
        var points = table
            .Where(r => r.Speeds[group] > 0 && r.MeetsTarget)
            .Select(r => new CurvePoint(Math.Round(Followed(r.Temperatures, follows)), r.Speeds[group]))
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
        curve = [.. LimitSteepness(curve, maxSlope)];

        // the ramp to 100 % just above the target is a safety net for loads beyond the calibration: it
        // may be steep, or a quiet preset would have to run its fans fast long before they're needed
        if (curve.Count == 0 || (curve[^1].Temperature < fullSpeedAt && curve[^1].Percent < 100))
            curve.Add(new CurvePoint(fullSpeedAt, 100));
        else if (curve[^1].Percent < 100)
            curve[^1] = curve[^1] with { Percent = 100 };
        return curve;
    }

    /// <summary>
    /// The quietest curve that is nowhere below <paramref name="curve"/> and rises at most
    /// <paramref name="maxSlope"/> % per °C: a steep stretch starts rising earlier, never later, so
    /// the fan is never slower than planned at any temperature. Exact: the result bends only at the
    /// original points and where a slope-limited line meets the original curve.
    /// </summary>
    public static IReadOnlyList<CurvePoint> LimitSteepness(IReadOnlyList<CurvePoint> curve, double maxSlope)
    {
        if (curve.Count < 2 || double.IsPositiveInfinity(maxSlope))
            return curve;

        // the envelope: at t, the curve itself or a line of slope maxSlope down from any later point
        double Envelope(double t)
        {
            double value = Interpolate(curve, t);
            foreach (var p in curve)
                if (p.Temperature >= t)
                    value = Math.Max(value, p.Percent - maxSlope * (p.Temperature - t));
            return value;
        }

        var temperatures = curve.Select(p => p.Temperature).ToList();
        foreach (var anchor in curve)
        {
            for (int i = 0; i + 1 < curve.Count && curve[i + 1].Temperature <= anchor.Temperature; i++)
            {
                // where the line down from the anchor crosses the stretch between points i and i + 1
                var (a, b) = (curve[i], curve[i + 1]);
                double slope = (b.Percent - a.Percent) / Math.Max(1e-9, b.Temperature - a.Temperature);
                if (Math.Abs(slope - maxSlope) < 1e-9)
                    continue;
                double t = (anchor.Percent - maxSlope * anchor.Temperature - a.Percent + slope * a.Temperature) / (slope - maxSlope);
                if (t > a.Temperature && t < b.Temperature)
                    temperatures.Add(t);
            }
        }

        var points = temperatures.Distinct().Order().Select(t => new CurvePoint(Math.Round(t, 1), Math.Round(Envelope(t), 1))).ToList();

        // drop points that lie on the line between their neighbours
        var result = new List<CurvePoint> { points[0] };
        for (int i = 1; i < points.Count - 1; i++)
        {
            var (a, p, b) = (result[^1], points[i], points[i + 1]);
            double onLine = a.Percent + (b.Percent - a.Percent) * (p.Temperature - a.Temperature) / Math.Max(1e-9, b.Temperature - a.Temperature);
            if (Math.Abs(onLine - p.Percent) > 0.2)
                result.Add(p);
        }
        result.Add(points[^1]);
        return result;
    }

    /// <summary>Linear between the curve's points; flat before the first and after the last.</summary>
    public static double Interpolate(IReadOnlyList<CurvePoint> curve, double temperature)
    {
        if (curve.Count == 0)
            return 100;
        if (temperature <= curve[0].Temperature)
            return curve[0].Percent;
        for (int i = 1; i < curve.Count; i++)
        {
            var (a, b) = (curve[i - 1], curve[i]);
            if (temperature <= b.Temperature)
                return a.Percent + (b.Percent - a.Percent) * (temperature - a.Temperature) / Math.Max(1e-9, b.Temperature - a.Temperature);
        }
        return curve[^1].Percent;
    }

    /// <summary>The temperature a curve following <paramref name="follows"/> sees, from a table row's temperatures.</summary>
    private static double Followed(IReadOnlyDictionary<Component, double> temperatures, Component follows) =>
        follows == Component.Warmest
            ? Math.Max(temperatures.GetValueOrDefault(Component.Cpu), temperatures.GetValueOrDefault(Component.GpuCore))
            : temperatures.GetValueOrDefault(follows);
}
