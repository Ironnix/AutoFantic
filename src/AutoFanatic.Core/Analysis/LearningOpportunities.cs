using AutoFanatic.Core.Hardware;
using AutoFanatic.Core.Logging;

namespace AutoFanatic.Core.Analysis;

/// <summary>The experiment rules from "How it learns → Experiments". Defaults are the design's starting values.</summary>
public sealed record ExperimentRules
{
    /// <summary>Load must have been stable this long before an experiment may start.</summary>
    public TimeSpan StableFor { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>"Stable": CPU and GPU power stay within this share of their mean over <see cref="StableFor"/>.</summary>
    public double MaxPowerDeviation { get; init; } = 0.07;

    /// <summary>A running experiment is discarded when power moves more than this away from where it started.</summary>
    public double AbortPowerJump { get; init; } = 0.15;

    /// <summary>Reference (60 s) plus waiting for steady state after the fan change. Real sweeps will tell the true figure.</summary>
    public TimeSpan ExperimentLength { get; init; } = TimeSpan.FromMinutes(6);

    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The profile target, e.g. 80 for "Max 80"; applies to the CPU and the GPU core.</summary>
    public double ProfileLimit { get; init; } = 80;

    public SafetyLimits Limits { get; init; } = new();

    /// <summary>
    /// An experiment that slows a fan down starts only this far below the profile target and the
    /// safety limits. Speeding a fan up only cools, so it needs no such margin.
    /// </summary>
    public double StartHeadroom { get; init; } = 8;

    /// <summary>
    /// Experiments are discarded when a temperature comes this close to a limit: the profile
    /// target and the safety limits for a slower fan, only the safety limits for a faster one.
    /// </summary>
    public double AbortHeadroom { get; init; } = 5;

    /// <summary>Longer gaps in the log (sleep, a stopped recording) break any stable stretch.</summary>
    public TimeSpan MaxGap { get; init; } = TimeSpan.FromSeconds(10);

    // at low power a relative check is too strict (same idea as the steady-state detector)
    internal const double MinAllowedSwingWatts = 3;
}

/// <summary>Why no experiment was running at a given moment.</summary>
public enum Blocker
{
    IdleOrUnknownLoad,
    LoadNotStable,
    AtSafetyLimit,
    Cooldown,
}

/// <summary>Slower = a fan one step down (the quiet side, needs temperature headroom); faster = one step up (always safe).</summary>
public enum ExperimentDirection
{
    Slower,
    Faster,
}

public sealed record ExperimentAttempt(
    DateTimeOffset Start,
    TimeSpan Length,
    LoadClass Class,
    string? Foreground,
    ExperimentDirection Direction,
    string? AbortReason)
{
    public bool Completed => AbortReason is null;
}

/// <summary>Median temperatures while under load (idle excluded); null where the sensor is missing.</summary>
public sealed record TypicalTemperatures(double? Cpu, double? GpuCore, double? GpuHotspot, double? GpuMemory);

public sealed record OpportunityReport(
    TimeSpan Total,
    IReadOnlyDictionary<LoadClass, TimeSpan> TimeByClass,
    TimeSpan UnknownClassTime,
    IReadOnlyList<(string Foreground, TimeSpan Time)> TimeByForeground,
    IReadOnlyDictionary<Blocker, TimeSpan> Blocked,
    TimeSpan Experimenting,
    IReadOnlyList<ExperimentAttempt> Attempts,
    TypicalTemperatures UnderLoad)
{
    public int Completed => Attempts.Count(a => a.Completed);

    /// <summary>Time under a real load (not idle, class known): what the learning actually works with.</summary>
    public TimeSpan ActiveTime => TimeByClass.Where(kv => kv.Key != LoadClass.Idle).Aggregate(TimeSpan.Zero, (sum, kv) => sum + kv.Value);
}

/// <summary>
/// Replays a recorded session (e.g. an evening of gaming) through the experiment rules: how often
/// would AutoFanatic have been allowed to start an experiment, how many would have completed,
/// and what blocked the rest. Answers the central question of the design, "is normal use a
/// good enough test?", with real data before any learning code exists.
///
/// The replay can't know how an experiment would have changed the temperatures; with neighbouring
/// fan steps that effect is small, so the recorded temperatures stand in for it.
/// </summary>
public static class LearningOpportunities
{
    private sealed record Sample(
        DateTimeOffset Time,
        TimeSpan Duration,
        LoadClass? Class,
        string? Foreground,
        double CpuPower,
        double GpuPower,
        double? CpuTemp,
        double? GpuTemp,
        double? Hotspot,
        double? Memory);

    public static OpportunityReport Analyze(IEnumerable<LoggedSample> log, ExperimentRules? rules = null)
    {
        rules ??= new ExperimentRules();
        var samples = Prepare(log, rules);

        var byClass = Enum.GetValues<LoadClass>().ToDictionary(c => c, _ => TimeSpan.Zero);
        var byForeground = new Dictionary<string, TimeSpan>();
        var blocked = Enum.GetValues<Blocker>().ToDictionary(b => b, _ => TimeSpan.Zero);
        var attempts = new List<ExperimentAttempt>();
        TimeSpan total = TimeSpan.Zero, unknown = TimeSpan.Zero, experimenting = TimeSpan.Zero;

        var window = new List<Sample>(); // same context, last StableFor
        DateTimeOffset? contextStart = null;
        DateTimeOffset lastExperimentEnd = DateTimeOffset.MinValue;
        Sample? running = null;           // the sample an experiment started at
        var direction = ExperimentDirection.Slower;
        double refCpu = 0, refGpu = 0;
        Sample? previous = null;

        foreach (var s in samples)
        {
            total += s.Duration;
            if (s.Class is { } cls)
                byClass[cls] += s.Duration;
            else
                unknown += s.Duration;
            string fg = s.Foreground ?? "(unknown)";
            byForeground[fg] = byForeground.GetValueOrDefault(fg) + s.Duration;

            string? contextBreak = previous is null ? null : ContextBreak(previous, s, rules);
            if (previous is null || contextBreak is not null)
            {
                window.Clear();
                contextStart = s.Time;
            }
            window.Add(s);
            window.RemoveAll(w => s.Time - w.Time > rules.StableFor);
            previous = s;

            if (running is not null)
            {
                bool tooHot = direction == ExperimentDirection.Slower
                    ? NearTarget(s, rules, rules.AbortHeadroom)
                    : NearSafetyLimit(s, rules, rules.AbortHeadroom);
                string? abort = contextBreak
                    ?? PowerJump(s, refCpu, refGpu, rules)
                    ?? (tooHot ? "temperature came close to a limit" : null);

                if (abort is not null || s.Time - running.Time >= rules.ExperimentLength)
                {
                    attempts.Add(new ExperimentAttempt(running.Time, s.Time - running.Time, running.Class!.Value, running.Foreground, direction, abort));
                    lastExperimentEnd = s.Time;
                    running = null;
                }
                else
                {
                    experimenting += s.Duration;
                    continue;
                }
            }

            var blocker = s.Class is null or LoadClass.Idle ? Blocker.IdleOrUnknownLoad
                : s.Time - contextStart < rules.StableFor || !PowerStable(window, rules) ? Blocker.LoadNotStable
                : NearSafetyLimit(s, rules, rules.AbortHeadroom) ? Blocker.AtSafetyLimit
                : s.Time - lastExperimentEnd < rules.Cooldown ? Blocker.Cooldown
                : (Blocker?)null;

            if (blocker is { } b)
            {
                blocked[b] += s.Duration;
                continue;
            }

            running = s;
            direction = NearTarget(s, rules, rules.StartHeadroom) ? ExperimentDirection.Faster : ExperimentDirection.Slower;
            refCpu = window.Average(w => w.CpuPower);
            refGpu = window.Average(w => w.GpuPower);
            experimenting += s.Duration;
        }

        if (running is not null && previous is not null)
            attempts.Add(new ExperimentAttempt(running.Time, previous.Time - running.Time, running.Class!.Value, running.Foreground, direction, "log ended"));

        var foregrounds = byForeground
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        var underLoad = samples.Where(s => s.Class is { } c && c != LoadClass.Idle).ToList();
        var typical = new TypicalTemperatures(
            Median(underLoad.Select(s => s.CpuTemp)),
            Median(underLoad.Select(s => s.GpuTemp)),
            Median(underLoad.Select(s => s.Hotspot)),
            Median(underLoad.Select(s => s.Memory)));

        return new OpportunityReport(total, byClass, unknown, foregrounds, blocked, experimenting, attempts, typical);
    }

    private static double? Median(IEnumerable<double?> values)
    {
        var sorted = values.OfType<double>().Order().ToList();
        return sorted.Count == 0 ? null : sorted[sorted.Count / 2];
    }

    private static List<Sample> Prepare(IEnumerable<LoggedSample> log, ExperimentRules rules)
    {
        var list = log.OrderBy(l => l.Snapshot.Time).ToList();
        if (list.Count == 0)
            return [];

        // the key sensors can't change within one log, so detect them once
        var keys = KeySensors.Detect(list[0].Snapshot);
        var samples = new List<Sample>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var snapshot = list[i].Snapshot;
            var gap = i == 0 ? TimeSpan.Zero : snapshot.Time - list[i - 1].Snapshot.Time;
            var duration = gap > rules.MaxGap ? TimeSpan.Zero : gap;

            samples.Add(new Sample(
                snapshot.Time,
                duration,
                LoadClassifier.Classify(snapshot.Value(keys.CpuLoad), snapshot.Value(keys.GpuLoad)),
                list[i].Foreground,
                snapshot.Value(keys.CpuPower) ?? 0,
                snapshot.Value(keys.GpuPower) ?? 0,
                snapshot.Value(keys.CpuTemp),
                snapshot.Value(keys.GpuTemp),
                snapshot.Value(keys.GpuHotspot),
                snapshot.Value(keys.GpuMemory)));
        }
        return samples;
    }

    /// <summary>Anything that ends a stable stretch: another load class, another program, a gap in the log.</summary>
    private static string? ContextBreak(Sample previous, Sample current, ExperimentRules rules) =>
        current.Time - previous.Time > rules.MaxGap ? "gap in the log"
        : current.Foreground != previous.Foreground ? "program in the foreground changed"
        : current.Class != previous.Class ? "load class changed"
        : null;

    private static bool PowerStable(List<Sample> window, ExperimentRules rules) =>
        Stable(window.Select(w => w.CpuPower).ToList(), rules) && Stable(window.Select(w => w.GpuPower).ToList(), rules);

    private static bool Stable(List<double> watts, ExperimentRules rules)
    {
        double mean = watts.Average();
        double allowed = Math.Max(mean * rules.MaxPowerDeviation, ExperimentRules.MinAllowedSwingWatts);
        return watts.All(w => Math.Abs(w - mean) <= allowed);
    }

    private static string? PowerJump(Sample s, double refCpu, double refGpu, ExperimentRules rules) =>
        Jumped(s.CpuPower, refCpu, rules) || Jumped(s.GpuPower, refGpu, rules) ? "power jumped" : null;

    private static bool Jumped(double watts, double reference, ExperimentRules rules) =>
        Math.Abs(watts - reference) > Math.Max(reference * rules.AbortPowerJump, 2 * ExperimentRules.MinAllowedSwingWatts);

    /// <summary>Within <paramref name="headroom"/> of the profile target or a safety limit: no room to slow a fan down.</summary>
    private static bool NearTarget(Sample s, ExperimentRules rules, double headroom)
    {
        var limits = rules.Limits;
        return Over(s.CpuTemp, Math.Min(rules.ProfileLimit, limits.CpuMax) - headroom)
            || Over(s.GpuTemp, Math.Min(rules.ProfileLimit, limits.GpuCoreMax) - headroom)
            || Over(s.Hotspot, limits.GpuHotspotMax - headroom)
            || Over(s.Memory, limits.GpuMemoryMax - headroom);
    }

    /// <summary>Within <paramref name="headroom"/> of a safety limit: the safety system is in charge, no experiments at all.</summary>
    private static bool NearSafetyLimit(Sample s, ExperimentRules rules, double headroom)
    {
        var limits = rules.Limits;
        return Over(s.CpuTemp, limits.CpuMax - headroom)
            || Over(s.GpuTemp, limits.GpuCoreMax - headroom)
            || Over(s.Hotspot, limits.GpuHotspotMax - headroom)
            || Over(s.Memory, limits.GpuMemoryMax - headroom);
    }

    private static bool Over(double? value, double limit) => value is { } v && v > limit;
}
