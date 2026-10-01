using AutoFantic.Core.Calibration;

namespace AutoFantic.Core.Tests;

public class MeasurementStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 22, 0, 0, TimeSpan.Zero);

    // CPU fan and GPU fan; the CPU fan cools only the CPU, the GPU fan only the GPU
    private static readonly FanGroup CpuFan = new("CPU Fan (#0)", [new FanHeader(0, "c0", "CPU Fan", "board", "f0", [new(30, 510), new(100, 1480)], false)]);
    private static readonly FanGroup GpuFans = new("GPU fans (#8, #9)", [new FanHeader(8, "g1", "GPU Fan 1", "gpu", "gf1", [new(30, 0), new(100, 2870)], false)]);
    private static readonly FanGroup[] Groups = [CpuFan, GpuFans];

    private static readonly double[] CpuTruth = [0.2, 8, 0], GpuTruth = [0.05, 0, 6];

    /// <summary>A calibration: the plan's runs at the given load, temperatures from the truth plus sensor noise.</summary>
    private static List<StoredRun> Calibration(double cpuW, double gpuW, double noise, int seed)
    {
        var random = new Random(seed);
        double T(double[] k, double[] s, double w) =>
            22 + w * (k[0] + k[1] * ThermalModel.Basis(s[0]) + k[2] * ThermalModel.Basis(s[1])) + (random.NextDouble() * 2 - 1) * noise;

        return CalibrationPlan.Runs(Groups).Select(s => new StoredRun(T0, 22,
            new Dictionary<string, double> { ["c0"] = s[0], ["g1"] = s[1] }, cpuW, gpuW,
            new Dictionary<Component, double> { [Component.Cpu] = T(CpuTruth, s, cpuW), [Component.GpuCore] = T(GpuTruth, s, gpuW) })).ToList();
    }

    [Fact]
    public void A_gpu_game_and_a_cpu_render_together_teach_both_sides()
    {
        // the game: GPU hard at work, CPU lightly loaded (its temperatures say little about the CPU cooler)
        var game = Calibration(cpuW: 25, gpuW: 330, noise: 1.0, seed: 1);
        // the render: CPU hard at work, GPU nearly idle
        var render = Calibration(cpuW: 140, gpuW: 25, noise: 1.0, seed: 2);

        var store = MeasurementStore.Empty
            .Add(new StoredCalibration(T0, "game", 22, 25, 330, null, null, game.Count), game)
            .Add(new StoredCalibration(T0, "render", 22, 140, 25, null, null, render.Count), render);
        var model = ThermalModel.Fit(store.ObservationsFor(Groups), 22, 2);

        // the CPU fan's effect is learned from the render, the GPU fan's from the game
        Assert.InRange(model.Coefficients(Component.Cpu)[1], 7, 9);
        Assert.InRange(model.Coefficients(Component.GpuCore)[2], 5.5, 6.5);
        Assert.Equal(2, store.Calibrations.Count);
    }

    [Fact]
    public void Runs_for_other_fans_are_left_out()
    {
        var other = new StoredRun(T0, 22, new Dictionary<string, double> { ["x9"] = 50 }, 100, 100, new Dictionary<Component, double> { [Component.Cpu] = 60 });
        var store = MeasurementStore.Empty.Add(new StoredCalibration(T0, "old fans", 22, 100, 100, null, null, 1), [other]);

        Assert.Empty(store.ObservationsFor(Groups));
    }

    [Fact]
    public void Runs_from_before_two_fans_were_put_together_still_count()
    {
        var second = new FanHeader(1, "c1", "CPU Fan 2", "board", "f1", [new(30, 520), new(100, 1490)], false);
        var both = new FanGroup("CPU Fan 1 + 2 (#0, #1)", [CpuFan.Headers[0], second]);
        StoredRun Run(Dictionary<string, double> speeds) =>
            new(T0, 22, speeds, 100, 200, new Dictionary<Component, double> { [Component.Cpu] = 70, [Component.GpuCore] = 60 });
        var store = MeasurementStore.Empty.Add(new StoredCalibration(T0, "game", 22, 100, 200, null, null, 3),
        [
            Run(new() { ["c0"] = 100, ["c1"] = 100, ["g1"] = 65 }),
            Run(new() { ["c0"] = 35, ["c1"] = 35, ["g1"] = 65 }),
            Run(new() { ["c0"] = 100, ["c1"] = 35, ["g1"] = 65 }),
        ]);

        var observations = store.ObservationsFor([both, GpuFans]);

        Assert.Equal([100, 65], observations[0].Speeds);
        Assert.Equal([35, 65], observations[1].Speeds);
        Assert.All(observations.Take(2), o => Assert.Equal(1, o.Weight));
        // one at 100 %, one at 35 %: the speed that cools the same lies between them, nearer the slow one, and counts half
        Assert.Equal(54, observations[2].Speeds[0]);
        Assert.Equal(0.5, observations[2].Weight);

        // the other way round: measured together, taken apart later
        var together = MeasurementStore.Empty.Add(new StoredCalibration(T0, "game", 22, 100, 200, null, null, 1), [Run(new() { ["c0+c1"] = 65, ["g1"] = 40 })]);
        var apart = together.ObservationsFor([CpuFan, new FanGroup("CPU Fan 2 (#1)", [second]), GpuFans]);

        Assert.Equal([65, 65, 40], apart.Single().Speeds);
        Assert.Equal(1, apart.Single().Weight);
    }

    [Fact]
    public void An_old_result_is_imported_without_the_settings_that_were_too_hot()
    {
        var old = new CalibrationResult(T0, "Max 90", 25,
            [
                new CalibratedGroup("CPU Fan (#0)", [0], ["c0"], Component.Cpu, 9, 0, [], []),
                new CalibratedGroup("GPU fans (#8, #9)", [8], ["g1"], Component.GpuCore, 0, 30, [], []),
            ],
            [
                new LoadRow("idle", 30, 130, [30, 40], new Dictionary<Component, double>(), 0, true),
                new LoadRow("medium", 105, 330, [65, 45], new Dictionary<Component, double>(), 0, true),
                new LoadRow("high", 140, 350, [90, 80], new Dictionary<Component, double>(), 0, true),
            ],
            new Dictionary<Component, double[]> { [Component.Cpu] = [0.2, 8, 0], [Component.GpuCore] = [0.05, 0, 6] },
            60, 170);

        var store = MeasurementStore.Import(old, Groups, "Bodycam");

        Assert.Single(store.Calibrations);
        Assert.NotEmpty(store.Runs);
        Assert.All(store.Runs, r => Assert.Equal(0.5, r.Weight));
        // at 330 W with the GPU fans at their lowest the GPU would pass its 85 °C limit: not imported
        Assert.DoesNotContain(store.Runs, r => r.Final[Component.GpuCore] >= 85);
        Assert.Equal(140, store.Calibrations[0].TopCpu);
    }

    [Fact]
    public void Curves_keep_flat_stretches_and_the_bios_gets_four_points()
    {
        var table = new[]
        {
            new LoadRow("idle", 35, 130, [30], new Dictionary<Component, double> { [Component.Cpu] = 54 }, 0, true),
            new LoadRow("light", 70, 200, [30], new Dictionary<Component, double> { [Component.Cpu] = 67 }, 0, true),
            new LoadRow("medium", 105, 280, [65], new Dictionary<Component, double> { [Component.Cpu] = 80 }, 0, true),
            new LoadRow("high", 140, 350, [80], new Dictionary<Component, double> { [Component.Cpu] = 84 }, 0, true),
        };

        var curve = CalibrationResult.CurveFor(table, 0, Component.Cpu, fullSpeedAt: 87);

        // 67 °C still 30 %: the fan doesn't ramp up before the plan says so
        Assert.Contains(new CurvePoint(67, 30), curve);
        Assert.Equal(new CurvePoint(87, 100), curve[^1]);
        Assert.Equal(4, CalibrationResult.BiosPoints(curve).Count);
        Assert.Equal([54, 67, 80, 87], CalibrationResult.BiosPoints(curve).Select(p => p.Temperature));
    }
}
