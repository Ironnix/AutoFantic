using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoFanatic.Core.Calibration;

/// <summary>A curve the user set by hand for one fan group, and whether it may stop at idle.</summary>
public sealed record CurveOverride(IReadOnlyList<CurvePoint> Curve, bool AllowOff);

/// <summary>
/// The user's own curves (runs\curves.json), laid over the calibrated ones. A new calibration
/// doesn't touch them; "Reset to recommended" removes one. A fan may only be allowed to stop where
/// the calibration found stopping safe: the setting can switch it off, never on.
/// </summary>
public sealed record CurveOverrides(IReadOnlyDictionary<string, CurveOverride> ByGroup)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static CurveOverrides None { get; } = new(new Dictionary<string, CurveOverride>());

    public static string Key(CalibratedGroup group) => MeasurementStore.Key(group.ControlIds);

    public CurveOverride? For(CalibratedGroup group) => ByGroup.GetValueOrDefault(Key(group));

    public CurveOverrides With(CalibratedGroup group, CurveOverride curve) =>
        new(new Dictionary<string, CurveOverride>(ByGroup) { [Key(group)] = Normalized(curve) });

    public CurveOverrides Without(CalibratedGroup group)
    {
        var copy = new Dictionary<string, CurveOverride>(ByGroup);
        copy.Remove(Key(group));
        return new CurveOverrides(copy);
    }

    /// <summary>The calibration with the user's curves in place of the calibrated ones.</summary>
    public CalibrationResult ApplyTo(CalibrationResult calibration) =>
        calibration with
        {
            Groups = calibration.Groups
                .Select(g => For(g) is { } o ? g with { Curve = o.Curve, OffAt = o.AllowOff ? g.OffAt : [] } : g)
                .ToList(),
        };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static CurveOverrides Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<CurveOverrides>(File.ReadAllText(path), Json) ?? None : None;

    /// <summary>Sorted by temperature, speeds within 0–100 % and never falling as it gets warmer.</summary>
    public static CurveOverride Normalized(CurveOverride curve)
    {
        var points = new List<CurvePoint>();
        foreach (var p in curve.Curve.OrderBy(p => p.Temperature))
        {
            double percent = Math.Clamp(Math.Round(p.Percent), 0, 100);
            if (points.Count > 0)
                percent = Math.Max(percent, points[^1].Percent);
            double temperature = Math.Round(Math.Clamp(p.Temperature, 20, 105));
            if (points.Count > 0 && temperature <= points[^1].Temperature)
                temperature = points[^1].Temperature + 1;
            points.Add(new CurvePoint(temperature, percent));
        }
        return curve with { Curve = points };
    }
}
