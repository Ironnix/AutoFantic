using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoFantic.Core.Calibration;

/// <summary>A curve the user set by hand for one fan group, and whether it may stop at low load while cool.</summary>
public sealed record CurveOverride(IReadOnlyList<CurvePoint> Curve, bool AllowOff);

/// <summary>
/// The user's own curves (curves.json), laid over the calibrated ones. A new calibration
/// doesn't touch them; "Reset to recommended" removes one. The user may let a fan stop that the
/// calibration keeps turning (the window offers it only for fans that measurably stood still at
/// 0 %); the fan control still switches it on again as soon as it gets warm. And the user may give
/// single fan groups back to the BIOS (<paramref name="Bios"/>): AuFantic leaves them alone.
/// </summary>
/// <param name="Bios">The groups (by key) the BIOS controls; their curves are kept for when they come back.</param>
public sealed record CurveOverrides(IReadOnlyDictionary<string, CurveOverride> ByGroup, IReadOnlyList<string>? Bios = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static CurveOverrides None { get; } = new(new Dictionary<string, CurveOverride>());

    public static string Key(CalibratedGroup group) => MeasurementStore.Key(group.ControlIds);

    public CurveOverride? For(CalibratedGroup group) => ByGroup.GetValueOrDefault(Key(group));

    public CurveOverrides With(CalibratedGroup group, CurveOverride curve) =>
        this with { ByGroup = new Dictionary<string, CurveOverride>(ByGroup) { [Key(group)] = Normalized(curve) } };

    /// <summary>Back to the recommended curve (whether the BIOS has the fans stays as it is).</summary>
    public CurveOverrides Without(CalibratedGroup group)
    {
        var copy = new Dictionary<string, CurveOverride>(ByGroup);
        copy.Remove(Key(group));
        return this with { ByGroup = copy };
    }

    /// <summary>True if the user gave this group to the BIOS.</summary>
    public bool IsBios(CalibratedGroup group) => Bios?.Contains(Key(group)) == true;

    public CurveOverrides WithBios(CalibratedGroup group, bool bios) =>
        this with { Bios = [.. (Bios ?? []).Where(k => k != Key(group)), .. bios ? [Key(group)] : Array.Empty<string>()] };

    /// <summary>The calibration with the user's curves in place of the calibrated ones.</summary>
    public CalibrationResult ApplyTo(CalibrationResult calibration) =>
        calibration with
        {
            Groups = calibration.Groups
                .Select(g => (For(g) is { } o ? g with { Curve = o.Curve, OffAt = o.AllowOff ? (g.OffAt.Count > 0 ? g.OffAt : ["idle"]) : [] } : g) with { Bios = IsBios(g) })
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
