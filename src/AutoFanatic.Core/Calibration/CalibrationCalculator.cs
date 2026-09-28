using AutoFanatic.Core.Analysis;

namespace AutoFanatic.Core.Calibration;

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
    public static CalibrationResult? Calculate(MeasurementStore store, FanInventory inventory, Profile profile, double ambient, FansOffResult? fansOff)
    {
        var groups = inventory.Groups().ToList();
        var observations = store.ObservationsFor(groups);
        if (observations.Count < 3 || store.Calibrations.Count == 0)
            return null;

        var model = ThermalModel.Fit(observations, ambient, groups.Count);

        // load levels: idle up to the highest CPU and the highest GPU load any calibration saw (worst
        // case: both at once), then 30 % beyond for anything heavier
        double topCpu = store.Calibrations.Max(c => c.TopCpu), topGpu = store.Calibrations.Max(c => c.TopGpu);
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
            model.Components.ToDictionary(c => c, c => model.Coefficients(c).ToArray()),
            stopCpu, stopGpu, model.Rms, sources);
    }

    /// <summary>What a group cools (at the highest load), the temperature it follows, its curve and when it's off.</summary>
    private static CalibratedGroup Describe(FanGroup group, int index, ThermalModel model, List<LoadRow> table, double cpuPower, double gpuPower, Profile profile)
    {
        double low = CalibrationPlan.Levels(group)[2];
        double Effect(Component c, double power) =>
            model.Components.Contains(c) ? power * model.Coefficients(c)[index + 1] * (ThermalModel.Basis(low) - ThermalModel.Basis(100)) : 0;

        double cpu = Effect(Component.Cpu, cpuPower), gpu = Effect(Component.GpuCore, gpuPower);

        // GPU fans follow the GPU; mainboard fans follow the CPU, the one temperature every BIOS can
        // use, and one that rises steadily with the load levels
        var follows = group.IsGpu ? Component.GpuCore : Component.Cpu;

        // just above the target everything runs flat out: covers any task hotter than the calibration
        var limits = new SafetyLimits();
        double safety = follows == Component.Cpu ? limits.CpuMax : limits.GpuCoreMax;
        double fullSpeedAt = Math.Min(profile.Target(follows) + 2, safety - 2);

        return new CalibratedGroup(
            group.Name,
            group.Headers.Select(h => h.Channel).ToList(),
            group.Headers.Select(h => h.ControlId).ToList(),
            follows, cpu, gpu,
            CalibrationResult.CurveFor(table, index, follows, fullSpeedAt),
            table.Where(r => r.Speeds[index] == 0).Select(r => r.Label).ToList());
    }
}
