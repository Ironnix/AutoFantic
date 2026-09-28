using System.Text.Json;

namespace AutoFanatic.Core.Calibration;

public sealed record CurvePoint(double Temperature, double Percent);

/// <summary>A fan group's result: what it cools, and its curve (fan % by the temperature it follows).</summary>
/// <param name="CpuEffect">°C the CPU gets warmer when this group goes from 100 % to its lowest speed, at calibration load.</param>
/// <param name="GpuEffect">The same for the GPU core.</param>
public sealed record CalibratedGroup(
    string Name,
    IReadOnlyList<int> Channels,
    IReadOnlyList<string> ControlIds,
    Component Follows,
    double CpuEffect,
    double GpuEffect,
    IReadOnlyList<CurvePoint> Curve);

/// <summary>One load level of the result table: the quietest mix found for it.</summary>
public sealed record LoadRow(
    string Label,
    double CpuPower,
    double GpuPower,
    IReadOnlyList<double> Speeds,
    IReadOnlyDictionary<Component, double> Temperatures,
    double Noise,
    bool MeetsTarget);

/// <summary>Everything a quick calibration produced, saved as JSON for the controller to use later.</summary>
public sealed record CalibrationResult(
    DateTimeOffset Created,
    string Profile,
    double Ambient,
    IReadOnlyList<CalibratedGroup> Groups,
    IReadOnlyList<LoadRow> Table,
    IReadOnlyDictionary<Component, double[]> Model)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static CalibrationResult? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<CalibrationResult>(File.ReadAllText(path), Json) : null;

    /// <summary>
    /// A group's curve from the table: at each load level, the temperature it follows and the speed
    /// chosen there. Sorted by temperature and kept rising, so it can go straight into a BIOS or
    /// MSI Afterburner curve.
    /// </summary>
    public static IReadOnlyList<CurvePoint> CurveFor(IReadOnlyList<LoadRow> table, int group, Component follows)
    {
        var points = table
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
        return curve;
    }
}
