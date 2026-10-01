using AutoFantic.Core.Analysis;

namespace AutoFantic.Core.Calibration;

/// <summary>
/// From every stored measurement to the result: fit the thermal model, pick the quietest fan mix
/// per load level for the profile, and derive each fan's curve. Pure computation, no hardware, so
/// the console tool, the window (switching Max 80 / Max 90) and tests all use the same code.
/// </summary>
public static class CalibrationCalculator
{
    public static readonly string[] LoadLabels = ["idle", "light", "medium", "high", "beyond"];

    // idle, ⅓, ⅔ of the way to the highest load measured, that load, and 30 % beyond it
    private static readonly double[] LoadShares = [0, 0.33, 0.66, 1, 1.3];

    /// <summary>The result, or null if fewer than three stored runs match these fans.</summary>
    /// <param name="use">What everyday use showed against the calibrations, to count in (a warmer room, heat
    /// that gets out worse or better, heavier loads than any calibration saw); null = the calibrations alone.</param>
    public static CalibrationResult? Calculate(MeasurementStore store, FanInventory inventory, Profile profile, double ambient, FansOffResult? fansOff, UseCorrection? use = null)
    {
        var groups = inventory.Groups().ToList();
        var observations = store.ObservationsFor(groups);
        if (observations.Count < 3 || store.Calibrations.Count == 0)
            return null;

        var measured = ThermalModel.Fit(observations, ambient, groups.Count);
        var model = use is null ? measured : measured.With(use);

        // load levels: idle up to the highest CPU and the highest GPU load any calibration saw (worst
        // case: both at once), or everyday use if that was heavier, then 30 % beyond for anything heavier still
        double topCpu = Math.Max(store.Calibrations.Max(c => c.TopCpu), use?.TopCpu ?? 0);
        double topGpu = Math.Max(store.Calibrations.Max(c => c.TopGpu), use?.TopGpu ?? 0);
        double lowCpu = store.Calibrations.Select(c => c.IdleCpu).OfType<double>().DefaultIfEmpty(fansOff?.CpuPower ?? topCpu * 0.2).Min();
        double lowGpu = store.Calibrations.Select(c => c.IdleGpu).OfType<double>().DefaultIfEmpty(fansOff?.GpuPower ?? topGpu * 0.2).Min();
        var loads = LoadShares.Select(f => (lowCpu + (topCpu - lowCpu) * f, lowGpu + (topGpu - lowGpu) * f)).ToList();

        // fans may be off only at idle-like power (and while it's cool, see MixOptimizer)
        double stopCpu = Math.Max(lowCpu * 1.25, topCpu / 2), stopGpu = Math.Max(lowGpu * 1.25, topGpu / 2);
        var optimizer = new MixOptimizer(model, groups, profile)
        {
            StopOnlyUpTo = (stopCpu, stopGpu),
            AllOffResistance = fansOff?.Resistance(ambient),
        };
        var table = optimizer.Table(loads)
            .Select((m, i) => new LoadRow(LoadLabels[i], loads[i].Item1, loads[i].Item2, m.Speeds, m.Temperatures, m.Noise, m.MeetsTarget))
            .ToList();

        var calibrated = groups.Select((g, i) => Describe(g, i, model, table, topCpu, topGpu, profile)).ToList();
        var sources = store.Calibrations.Select(c => $"{c.Time:dd.MM. HH:mm} · {c.Load} · {c.Runs} runs").ToList();
        return new CalibrationResult(
            DateTimeOffset.Now, profile.Name, ambient, calibrated, table,
            measured.Components.ToDictionary(c => c, c => measured.Coefficients(c).ToArray()),
            stopCpu, stopGpu, measured.Rms, sources, CalibrationResult.CurrentVersion, use);
    }

    /// <summary>What a group cools (at the highest load), the temperature it follows, its curve and when it's off.</summary>
    private static CalibratedGroup Describe(FanGroup group, int index, ThermalModel model, List<LoadRow> table, double cpuPower, double gpuPower, Profile profile)
    {
        double low = CalibrationPlan.Levels(group)[2];
        double Effect(Component c, double power) =>
            model.Components.Contains(c) ? power * model.Coefficients(c)[index + 1] * (ThermalModel.Basis(low) - ThermalModel.Basis(100)) : 0;

        double cpu = Effect(Component.Cpu, cpuPower), gpu = Effect(Component.GpuCore, gpuPower);
        var follows = Follows(group, cpu, gpu);

        // just above the target everything runs flat out: covers any task hotter than the calibration.
        // A fan following the warmer of the two goes by the higher target, or in Silent (CPU 87, GPU 82)
        // a CPU that is exactly as warm as planned would already run the case fans flat out
        var limits = new SafetyLimits();
        double fullSpeedAt = follows switch
        {
            Component.Cpu => Math.Min(profile.Target(Component.Cpu) + 2, limits.CpuMax - 2),
            Component.Warmest => Math.Min(Math.Max(profile.Target(Component.Cpu), profile.Target(Component.GpuCore)) + 2, limits.CpuMax - 2),
            _ => Math.Min(profile.Target(Component.GpuCore) + 2, limits.GpuCoreMax - 2),
        };

        return new CalibratedGroup(
            group.Name,
            group.Headers.Select(h => h.Channel).ToList(),
            group.Headers.Select(h => h.ControlId).ToList(),
            follows, cpu, gpu,
            CalibrationResult.CurveFor(table, index, follows, fullSpeedAt, CalibrationResult.MaxSlope),
            table.Where(r => r.Speeds[index] == 0).Select(r => r.Label).ToList());
    }

    /// <summary>
    /// GPU fans follow the GPU. A mainboard fan that clearly cools the GPU too (case fans: at least
    /// 2 °C, and at least a quarter of what it does for the CPU) follows whichever is warmer, so a
    /// GPU-heavy game with a cool CPU still gets the airflow. The others (the CPU cooler) follow the CPU.
    /// What the user says a fan cools (<see cref="FanGroup.Cools"/>) goes before all of that.
    /// </summary>
    public static Component Follows(FanGroup group, double cpuEffect, double gpuEffect) =>
        group.Cools is Component.Cpu or Component.GpuCore or Component.Warmest ? group.Cools.Value
        : group.IsGpu ? Component.GpuCore
        : gpuEffect >= 2 && gpuEffect >= 0.25 * cpuEffect ? Component.Warmest
        : Component.Cpu;
}
