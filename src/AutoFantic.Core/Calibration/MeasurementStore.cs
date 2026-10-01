using System.Text.Json;
using System.Text.Json.Serialization;
using AutoFantic.Core.Analysis;

namespace AutoFantic.Core.Calibration;

/// <summary>One measured run, stored with the fan groups it refers to (by their control ids).</summary>
/// <param name="Speeds">Effective speed per fan group, keyed by <see cref="MeasurementStore.Key"/>.</param>
/// <param name="Weight">1 for a real measurement, less for runs rebuilt from an older result.</param>
public sealed record StoredRun(
    DateTimeOffset Time,
    double Ambient,
    IReadOnlyDictionary<string, double> Speeds,
    double CpuPower,
    double GpuPower,
    IReadOnlyDictionary<Component, double> Final,
    double Weight = 1);

/// <summary>What one calibration covered: its load, room temperature and idle power.</summary>
public sealed record StoredCalibration(
    DateTimeOffset Time,
    string Load,
    double Ambient,
    double TopCpu,
    double TopGpu,
    double? IdleCpu,
    double? IdleGpu,
    int Runs);

/// <summary>
/// Every calibration's runs, kept together so each new calibration adds to what is known instead
/// of starting over: a GPU-heavy game teaches the GPU side, a CPU render the CPU side, and the
/// model is fitted to all of it.
/// </summary>
public sealed record MeasurementStore(IReadOnlyList<StoredCalibration> Calibrations, IReadOnlyList<StoredRun> Runs)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static MeasurementStore Empty { get; } = new([], []);

    /// <summary>Identifies a fan group across runs: its control ids.</summary>
    public static string Key(IEnumerable<string> controlIds) => string.Join("+", controlIds);

    public static string Key(FanGroup group) => Key(group.Headers.Select(h => h.ControlId));

    public MeasurementStore Add(StoredCalibration calibration, IEnumerable<StoredRun> runs) =>
        new([.. Calibrations, calibration], [.. Runs, .. runs]);

    // a run in which fans that run together now had different speeds counts this much of its weight
    private const double MixedWeight = 0.5;

    /// <summary>The runs that cover these groups, as observations in their order (<see cref="SpeedsIn"/>).</summary>
    public IReadOnlyList<Observation> ObservationsFor(IReadOnlyList<FanGroup> groups) =>
        Runs
            .Select(r => SpeedsIn(r, groups, out bool exact) is { } speeds
                ? new Observation(speeds, r.CpuPower, r.GpuPower, r.Final, r.Ambient, exact ? r.Weight : r.Weight * MixedWeight)
                : null)
            .OfType<Observation>()
            .ToList();

    /// <summary>
    /// The speed each of these groups ran at in a run; null if the run doesn't cover every one of
    /// their outputs. A run from before the user put outputs together, or took them apart, still
    /// counts: every output is looked up on its own. Outputs that run together now but had different
    /// speeds in the run (<paramref name="exact"/> is false) count as the one speed that cools the
    /// same (<see cref="ThermalModel.SameCooling"/>), which is right for like fans on one cooler.
    /// </summary>
    public static IReadOnlyList<double>? SpeedsIn(StoredRun run, IReadOnlyList<FanGroup> groups, out bool exact)
    {
        exact = true;
        var byOutput = new Dictionary<string, double>();
        foreach (var (key, speed) in run.Speeds)
            foreach (string id in key.Split('+'))
                byOutput[id] = speed;

        var speeds = new List<double>(groups.Count);
        foreach (var group in groups)
        {
            if (!group.Headers.All(h => byOutput.ContainsKey(h.ControlId)))
                return null;
            var each = group.Headers.Select(h => byOutput[h.ControlId]).ToList();
            bool same = each.All(s => s == each[0]);
            exact &= same;
            speeds.Add(same ? each[0] : Math.Round(ThermalModel.SameCooling(each)));
        }
        return speeds;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static MeasurementStore? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<MeasurementStore>(File.ReadAllText(path), Json) : null;

    /// <summary>
    /// Rebuilds runs from a result saved before measurements were kept: its fitted model, evaluated
    /// at the calibration plan's speeds and a typical load of that calibration (its "medium" level;
    /// the "high" level has CPU and GPU at their peaks at once, which never happened in a run).
    /// Settings the model says would have been too hot are left out (they were skipped back then, so
    /// there's nothing measured behind them). Counted at half weight, so real runs soon dominate.
    /// </summary>
    public static MeasurementStore Import(CalibrationResult old, IReadOnlyList<FanGroup> groups, string load)
    {
        var keys = old.Groups.Select(g => Key(g.ControlIds)).ToList();
        if (!groups.Select(Key).SequenceEqual(keys) || old.Model.Count == 0)
            return Empty;

        var high = old.Table.FirstOrDefault(r => r.Label == "high") ?? old.Table[^1];
        var typical = old.Table.FirstOrDefault(r => r.Label == "medium") ?? high;
        var idle = old.Table.FirstOrDefault(r => r.Label == "idle");
        var limits = new SafetyLimits();

        double Predict(Component c, IReadOnlyList<double> speeds)
        {
            var k = old.Model[c];
            double r = k[0] + speeds.Select((s, g) => k[g + 1] * ThermalModel.Basis(s)).Sum();
            return old.Ambient + (ThermalModel.IsCpu(c) ? typical.CpuPower : typical.GpuPower) * r;
        }

        var runs = new List<StoredRun>();
        foreach (var speeds in CalibrationPlan.Runs(groups))
        {
            var final = old.Model.Keys.ToDictionary(c => c, c => Predict(c, speeds));
            bool tooHot = final.GetValueOrDefault(Component.Cpu) >= limits.CpuMax
                || final.GetValueOrDefault(Component.GpuCore) >= limits.GpuCoreMax;
            if (tooHot)
                continue;
            runs.Add(new StoredRun(old.Created, old.Ambient, keys.Zip(speeds).ToDictionary(p => p.First, p => p.Second),
                typical.CpuPower, typical.GpuPower, final, Weight: 0.5));
        }

        var calibration = new StoredCalibration(old.Created, $"{load} (imported from the saved result)", old.Ambient,
            high.CpuPower, high.GpuPower, idle?.CpuPower, idle?.GpuPower, runs.Count);
        return Empty.Add(calibration, runs);
    }
}
