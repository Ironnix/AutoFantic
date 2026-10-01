using static AutoFantic.Core.Texts;

namespace AutoFantic.Core.Calibration;

/// <summary>One measured run, with the part that came closest to its limit (the bottleneck).</summary>
/// <param name="Source">The calibration it belongs to, e.g. "VALORANT-Win64-Shipping".</param>
/// <param name="Speeds">Fan speed per group name, in %.</param>
/// <param name="Headroom">How far the bottleneck stayed below its target, °C (negative = above it).</param>
public sealed record RunInsight(
    DateTimeOffset Time,
    string Source,
    bool Imported,
    IReadOnlyList<(string Group, double Percent)> Speeds,
    double CpuPower,
    double GpuPower,
    IReadOnlyDictionary<Component, double> Final,
    Component Bottleneck,
    double Headroom);

/// <summary>One load level of the result, and in plain words why the fans run as they do there.</summary>
public sealed record LevelInsight(
    string Label,
    double CpuPower,
    double GpuPower,
    Component Bottleneck,
    double Temperature,
    double Allowed,
    string Why);

/// <summary>
/// Explains a calibration: which part limits the cooling in each run and at each load level, and
/// why the result picked these fan speeds (the fan that cools the limiting part most per dB of
/// noise is the one that runs faster).
/// </summary>
public static class CalibrationInsights
{
    public static string Name(Component c) => c switch
    {
        Component.Cpu => "CPU",
        Component.GpuCore => "GPU",
        Component.GpuHotspot => T("GPU hotspot"),
        Component.Warmest => T("CPU or GPU (the warmer)"),
        _ => T("GPU memory"),
    };

    /// <summary>What a curve's temperature axis is, in words: "CPU temperature", "GPU temperature" or "the warmer of CPU and GPU".</summary>
    public static string FollowsName(Component c) => c switch
    {
        Component.Cpu => T("CPU temperature"),
        Component.Warmest => T("warmer of CPU and GPU"),
        _ => T("GPU temperature"),
    };

    /// <summary>The temperature a curve following <paramref name="c"/> is read at.</summary>
    public static double? Followed(Component c, double? cpu, double? gpu) => c switch
    {
        Component.Cpu => cpu,
        Component.Warmest => cpu is null ? gpu : gpu is null ? cpu : Math.Max(cpu.Value, gpu.Value),
        _ => gpu,
    };

    /// <summary>Every stored run for these fans, newest calibration first, with its bottleneck under this profile.</summary>
    public static IReadOnlyList<RunInsight> Runs(MeasurementStore store, IReadOnlyList<FanGroup> groups, Profile profile)
    {
        var keys = groups.Select(MeasurementStore.Key).ToList();
        return store.Runs
            .Where(r => keys.All(r.Speeds.ContainsKey))
            .OrderByDescending(r => r.Time)
            .Select(r =>
            {
                var (bottleneck, headroom) = Tightest(r.Final, profile);
                string source = store.Calibrations.LastOrDefault(c => c.Time == r.Time)?.Load ?? "calibration";
                var speeds = groups.Select((g, i) => (g.Name, r.Speeds[keys[i]])).ToList();
                return new RunInsight(r.Time, source, r.Weight < 1, speeds, r.CpuPower, r.GpuPower, r.Final, bottleneck, headroom);
            })
            .ToList();
    }

    /// <summary>Per load level of the result: the limiting part and why the fans run as they do.</summary>
    public static IReadOnlyList<LevelInsight> Levels(CalibrationResult result, FanInventory inventory, Profile profile)
    {
        var groups = inventory.Groups().ToList();
        if (groups.Count != result.Groups.Count || result.Model.Count == 0)
            return [];

        var model = result.ModelInUse();
        var insights = new List<LevelInsight>();
        foreach (var row in result.Table)
        {
            var (bottleneck, headroom) = Tightest(row.Temperatures, profile);
            double temperature = row.Temperatures.GetValueOrDefault(bottleneck);
            double allowed = profile.Target(bottleneck);
            insights.Add(new LevelInsight(row.Label, row.CpuPower, row.GpuPower, bottleneck, temperature, allowed,
                Why(row, bottleneck, temperature, allowed, groups, model, profile)));
        }
        return insights;
    }

    private static (Component Part, double Headroom) Tightest(IReadOnlyDictionary<Component, double> temps, Profile profile) =>
        temps.Count == 0
            ? (Component.Cpu, double.PositiveInfinity)
            : temps.Select(kv => (kv.Key, profile.Target(kv.Key) - kv.Value)).MinBy(x => x.Item2);

    private static string Why(LoadRow row, Component bottleneck, double temperature, double allowed,
        List<FanGroup> groups, ThermalModel model, Profile profile)
    {
        var speeds = row.Speeds;
        var off = groups.Where((g, i) => speeds[i] == 0).Select(g => g.Name).ToList();
        var raised = groups.Select((g, i) => (Group: g, Index: i)).Where(x => speeds[x.Index] > x.Group.MinSpinning + 0.5).ToList();
        string part = Name(bottleneck);

        if (!row.MeetsTarget)
            return T($"Even with the fans this fast the {part} reaches {temperature:0} °C, above the {allowed:0} °C of {T(profile.Name)}. Here the safety limits take over (all fans to 100 % if it gets critical).");

        if (raised.Count == 0)
        {
            string slow = off.Count == groups.Count
                ? T("every fan can be off")
                : off.Count > 0 ? T($"{string.Join(T(" and "), off)} can be off and the rest run at their slowest") : T("every fan runs at its slowest speed");
            return T($"Cool enough: {slow}. The warmest part is the {part} at {temperature:0} °C, {allowed - temperature:0} °C below its limit.");
        }

        // which of the raised fans does the most for the limiting part per dB of noise
        var reasons = raised
            .Select(x =>
            {
                var more = speeds.ToArray();
                more[x.Index] = Math.Min(100, speeds[x.Index] + 10);
                double cooling = Predict(model, bottleneck, speeds, row) - Predict(model, bottleneck, more, row);
                double noise = NoiseModel.Group(x.Group, more[x.Index]) - NoiseModel.Group(x.Group, speeds[x.Index]);
                return (x.Group, Speed: speeds[x.Index], PerDb: noise > 0.01 ? cooling / noise : cooling * 100);
            })
            .OrderByDescending(x => x.Speed)
            .ToList();

        var main = reasons[0];
        string text = main.PerDb > 0.05
            ? T($"The {part} is the limit ({temperature:0} of {allowed:0} °C). {main.Group.Name} runs at {main.Speed:0} %: it cools the {part} by about {main.PerDb:0.0} °C per dB of noise, the cheapest way to stay below {allowed:0} °C.")
            : T($"The {part} is the limit ({temperature:0} of {allowed:0} °C). {main.Group.Name} runs at {main.Speed:0} % to keep the {part} below {allowed:0} °C.");
        if (reasons.Count > 1)
            text += T($" Also faster: {string.Join(", ", reasons.Skip(1).Select(r => $"{r.Group.Name} {r.Speed:0} %"))}.");
        if (off.Count > 0)
            text += T($" Off: {string.Join(", ", off)}.");
        return text;
    }

    private static double Predict(ThermalModel model, Component component, IReadOnlyList<double> speeds, LoadRow row) =>
        model.Components.Contains(component) ? model.Predict(component, speeds, row.CpuPower, row.GpuPower) : 0;
}
