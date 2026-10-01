using AutoFantic.Core.Calibration;
using AutoFantic.Core.Monitoring;

namespace AutoFantic.Core.Tests;

/// <summary>Improving the curves from everyday use: what the history says against the calibration, and what that does to the curves.</summary>
public class UseLearningTests
{
    // in the morning (UTC), so the five hours of a day's use stay on one calendar day in nearly every time zone
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);

    // two fan outputs; the CPU fan cools only the CPU, the case fans only the GPU
    private static readonly FanInventory Fans = new(T0,
    [
        new FanHeader(0, "c0", "CPU Fan", "board", "f0", [new(30, 510), new(60, 970), new(100, 1480)], false),
        new FanHeader(1, "g1", "Case Fans", "board", "f1", [new(30, 520), new(60, 990), new(100, 1500)], false),
    ]);

    private static readonly double[] CpuTruth = [0.2, 8, 0], GpuTruth = [0.05, 0, 6];
    private static readonly (double Cpu, double Gpu) CalibratedTop = (120, 300);

    private static double Truth(double[] k, IReadOnlyList<double> speeds, double watts) =>
        22 + watts * (k[0] + k[1] * ThermalModel.Basis(speeds[0]) + k[2] * ThermalModel.Basis(speeds[1]));

    /// <summary>One calibration at a gaming load, in a 22 °C room.</summary>
    private static MeasurementStore Calibrated()
    {
        var runs = CalibrationPlan.Runs(Fans.Groups()).Select(s => new StoredRun(T0, 22,
            new Dictionary<string, double> { ["c0"] = s[0], ["g1"] = s[1] }, CalibratedTop.Cpu, CalibratedTop.Gpu,
            new Dictionary<Component, double> { [Component.Cpu] = Truth(CpuTruth, s, CalibratedTop.Cpu), [Component.GpuCore] = Truth(GpuTruth, s, CalibratedTop.Gpu) })).ToList();
        return MeasurementStore.Empty.Add(new StoredCalibration(T0, "game", 22, CalibratedTop.Cpu, CalibratedTop.Gpu, 30, 40, runs.Count), runs);
    }

    private static CalibrationResult Curves(UseCorrection? use = null) =>
        CalibrationCalculator.Calculate(Calibrated(), Fans, Preset.For("balanced").Profile, 22, null, use)!;

    /// <summary>
    /// Days of use after the calibration: three hours of a game every evening, then two hours at the
    /// desktop, a minute each. The PC is as the calibration found it, except for what is passed in:
    /// how much warmer the CPU and the GPU run at a given power.
    /// </summary>
    private static List<UseMinute> Use(Func<double, double>? cpuExtra = null, Func<double, double>? gpuExtra = null, int days = 3, double gameGpu = 290)
    {
        var random = new Random(3);
        var minutes = new List<UseMinute>();
        for (int day = 0; day < days; day++)
        {
            for (int i = 0; i < 300; i++)
            {
                bool gaming = i < 180;
                double cpuW = (gaming ? 110 : 30) + random.NextDouble() * 6, gpuW = (gaming ? gameGpu : 40) + random.NextDouble() * 10;
                double[] speeds = [(gaming ? 70 : 30) + random.NextDouble() * 4, (gaming ? 80 : 30) + random.NextDouble() * 4];
                minutes.Add(new UseMinute(T0.AddDays(day + 1).AddMinutes(i),
                    Truth(CpuTruth, speeds, cpuW) + (cpuExtra?.Invoke(cpuW) ?? 0) + (random.NextDouble() - 0.5),
                    Truth(GpuTruth, speeds, gpuW) + (gpuExtra?.Invoke(gpuW) ?? 0) + (random.NextDouble() - 0.5),
                    cpuW, gpuW, speeds));
            }
        }
        return minutes;
    }

    private static UseAnalysis Analyze(List<UseMinute> minutes) =>
        UseLearning.Analyze(Curves(), minutes, T0, T0, T0.AddDays(5), CalibratedTop);

    [Fact]
    public void Only_minutes_after_the_power_settled_count()
    {
        var day = Use(days: 1);

        var settled = UseLearning.Settled(day);

        // the first minutes of the game and the first at the desktop are left out: still warming up or cooling down
        Assert.Equal(300 - 2 * UseLearning.SettledAfter, settled.Count);
        Assert.Equal(T0.AddDays(1).AddMinutes(UseLearning.SettledAfter), settled[0].Time);
        Assert.DoesNotContain(settled, m => m.Time == T0.AddDays(1).AddMinutes(181));
    }

    [Fact]
    public void A_pc_that_runs_as_calibrated_gets_its_curves_back()
    {
        var use = Analyze(Use()).Correction!;

        // what little the minutes differ from the calibration is within what they can tell: it counts as nothing
        Assert.Null(use.Cpu);
        Assert.Null(use.Gpu);
        Assert.Equal(CalibratedTop, (use.TopCpu, use.TopGpu));
        Assert.Equal(3, use.Days);
        Assert.True(UseLearning.Same(UseLearning.Compare(Curves(), Curves(use))));
    }

    [Fact]
    public void A_cpu_that_runs_warmer_at_light_load_than_the_model_expects_is_found()
    {
        // like a real one: 8 °C more than the straight line at the desktop, about right at the calibration's load
        var use = Analyze(Use(cpuExtra: watts => 10 - 0.08 * watts)).Correction!;

        Assert.NotNull(use.Cpu);
        Assert.Null(use.Gpu);
        Assert.InRange(use.Cpu.Extra(30), 6.5, 8.5);
        Assert.InRange(use.Cpu.Extra(113), 0, 2);

        var (before, after) = (Curves(), Curves(use));
        double Idle(CalibrationResult result) => result.Table.Single(r => r.Label == "idle").Temperatures[Component.Cpu];
        Assert.InRange(Idle(after) - Idle(before), 6, 9);
    }

    [Fact]
    public void A_dusty_graphics_card_is_found_and_makes_its_fans_work_harder()
    {
        // the card gets its heat out worse: 0.02 °C more per watt = 6 °C more at 300 W, hardly anything at the desktop
        var use = Analyze(Use(gpuExtra: watts => 0.02 * watts)).Correction!;

        Assert.Null(use.Cpu);
        Assert.InRange(use.Gpu!.Extra(295), 5, 7);
        Assert.InRange(use.Gpu.Extra(45), 0, 2);

        var (before, after) = (Curves(), Curves(use));
        var high = (Before: before.Table.Single(r => r.Label == "high"), After: after.Table.Single(r => r.Label == "high"));
        Assert.True(high.After.Speeds[1] > high.Before.Speeds[1], "the fans that cool the GPU run faster at full load");

        // what the calibrations measured stays as it was; the model in use counts the correction in
        Assert.Equal(before.Model[Component.GpuCore], after.Model[Component.GpuCore]);
        double[] speeds = [50, 50];
        Assert.Equal(before.ModelInUse().Predict(Component.GpuCore, speeds, 100, 290) + use.Gpu.Extra(290),
            after.ModelInUse().Predict(Component.GpuCore, speeds, 100, 290), 6);
    }

    [Fact]
    public void Beyond_what_was_seen_the_correction_fades_out_towards_the_calibrations_load()
    {
        // seen up to half the calibration's load
        var part = new PartUse(Offset: 12, PerWatt: -0.1, From: 30, To: 70, Calibrated: 140);

        Assert.Equal(9, part.Extra(30), 6);
        Assert.Equal(9, part.Extra(10), 6);    // below the range: as at its start
        Assert.Equal(5, part.Extra(70), 6);
        Assert.Equal(2.5, part.Extra(105), 6); // halfway to the calibration's load: half of it
        Assert.Equal(0, part.Extra(140), 6);   // there the calibration measured
        Assert.Equal(0, part.Extra(200), 6);
        Assert.Equal(PartUse.Most, new PartUse(40, 0, 30, 70, 140).Extra(50)); // never more than the cap

        // seen nearly up to the calibration's load, or beyond it: what it showed there stays for anything heavier
        Assert.Equal(-2.6, new PartUse(0, -0.02, 50, 130, 140).Extra(300), 6);
        Assert.Equal(-3, new PartUse(0, -0.02, 50, 150, 140).Extra(300), 6);
    }

    [Fact]
    public void A_game_heavier_than_the_calibration_raises_the_load_the_curves_cover()
    {
        var use = Analyze(Use(gameGpu: 360)).Correction!;

        Assert.InRange(use.TopGpu, 360, 372);
        Assert.InRange(Curves(use).Table.Single(r => r.Label == "high").GpuPower, 360, 372);
        Assert.Equal(300, Curves().Table.Single(r => r.Label == "high").GpuPower);
    }

    [Fact]
    public void Too_little_use_gives_no_answer_yet()
    {
        var analysis = Analyze(Use(days: 1).Take(40).ToList());

        Assert.Null(analysis.Correction);
        Assert.Equal(40 - UseLearning.SettledAfter, analysis.Minutes);
    }

    [Fact]
    public void The_correction_counts_only_until_the_next_calibration()
    {
        var use = Analyze(Use()).Correction!;
        var store = Calibrated();

        Assert.True(use.Fits(store));
        Assert.False(use.Fits(store.Add(new StoredCalibration(T0.AddDays(9), "render", 22, 140, 30, null, null, 0), [])));
        Assert.Equal(T0, UseLearning.From(T0.AddDays(5), T0));                      // since the calibration …
        Assert.Equal(T0.AddDays(10), UseLearning.From(T0.AddDays(40), T0));         // … but at most 30 days back
    }

    [Fact]
    public void The_correction_survives_a_round_trip_through_json()
    {
        string path = Path.Combine(Path.GetTempPath(), $"use-{Guid.NewGuid():N}.json");
        try
        {
            var use = Analyze(Use(cpuExtra: watts => 10 - 0.08 * watts)).Correction!;
            use.Save(path);

            Assert.Equal(use, UseCorrection.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Minutes_in_which_a_fan_stood_still_dont_count()
    {
        using var store = new HistoryStore(null);
        var calibration = Curves();
        var cpuFan = new Series("fan.c0.percent", "CPU Fan", SeriesKind.FanPercent);
        var cpuRpm = new Series("fan.c0.rpm", "CPU Fan", SeriesKind.FanRpm);
        var caseFans = new Series("fan.g1.percent", "Case Fans", SeriesKind.FanPercent);
        var caseRpm = new Series("fan.g1.rpm", "Case Fans", SeriesKind.FanRpm);

        // ten minutes with every fan turning, then ten with the case fans switched off
        for (int s = 0; s < 1200; s++)
        {
            bool off = s >= 600;
            store.Add(T0.AddSeconds(s),
            [
                (HistoryRecorder.CpuTemp, 60), (HistoryRecorder.GpuTemp, 55), (HistoryRecorder.CpuPower, 60), (HistoryRecorder.GpuPower, 120),
                (cpuFan, 40), (cpuRpm, 700), (caseFans, off ? 0 : 35), (caseRpm, off ? 0 : 600),
            ]);
        }

        var minutes = UseLearning.Minutes(store, calibration, T0, T0.AddSeconds(1200));

        Assert.Equal(10, minutes.Count);
        Assert.All(minutes, m => Assert.Equal([40, 35], m.Speeds));
        Assert.All(minutes, m => Assert.True(m.Time < T0.AddSeconds(600)));
    }
}
