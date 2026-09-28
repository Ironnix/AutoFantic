using AutoFantic.Core.Calibration;
using AutoFantic.Core.Simulation;

namespace AutoFantic.Core.Tests;

public sealed class CalibrationRunnerTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("autofantic-test-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_whole_calibration_finds_the_fans_measures_and_leaves_them_on_bios_control()
    {
        // 400 simulated seconds per real second: the whole procedure in a few seconds
        using var pc = new SimulatedPc(timeScale: 400, load: _ => SimLoad.Idle);
        var runner = new CalibrationRunner(pc, _folder, new CalibrationOptions(22, Preset.For("balanced"), BuiltInLoad: true));
        var stages = new List<CalibrationStage>();
        runner.Progress += p => stages.Add(p.Stage);

        var outcome = runner.Run(CancellationToken.None);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Contains(CalibrationStage.FindingFans, stages);
        Assert.Contains(CalibrationStage.FansOff, stages);
        Assert.Contains(CalibrationStage.Measuring, stages);
        Assert.Equal(CalibrationStage.Finished, stages[^1]);

        var store = MeasurementStore.Load(Path.Combine(_folder, "measurements.json"))!;
        Assert.Equal(9, store.Runs.Count);
        Assert.Equal(3, outcome.Result!.Groups.Count); // CPU fan, case fans, GPU fan; never the pump
        Assert.True(File.Exists(Path.Combine(_folder, "calibration.html")));
        Assert.All(pc.Channels, c => Assert.False(c.IsSoftwareControlled));
    }

    [Fact]
    public void Stopping_hands_the_fans_back_and_keeps_nothing()
    {
        using var pc = new SimulatedPc(timeScale: 400, load: _ => SimLoad.Idle);
        using var cancel = new CancellationTokenSource();
        var runner = new CalibrationRunner(pc, _folder, new CalibrationOptions(22, Preset.For("balanced"), BuiltInLoad: true));
        runner.Progress += p =>
        {
            if (p.Stage == CalibrationStage.Measuring && p.Run == 3)
                cancel.Cancel();
        };

        var outcome = runner.Run(cancel.Token);

        Assert.False(outcome.Success);
        Assert.False(File.Exists(Path.Combine(_folder, "measurements.json")));
        Assert.All(pc.Channels, c => Assert.False(c.IsSoftwareControlled));
    }
}

public class PresetAndInsightTests
{
    [Theory]
    [InlineData("silent", "Silent")]
    [InlineData("Max 90", "Silent")]
    [InlineData("80", "Balanced")]
    [InlineData("Balanced", "Balanced")]
    [InlineData("cool", "Cool")]
    [InlineData("max", "Max cooling")]
    [InlineData(null, "Balanced")]
    public void Presets_are_found_by_id_name_or_old_profile(string? key, string name) =>
        Assert.Equal(name, Preset.For(key).Name);

    [Fact]
    public void Presets_never_put_a_target_on_a_safety_limit()
    {
        Assert.All(Preset.All, p =>
        {
            Assert.True(p.Profile.Cpu <= 87);
            Assert.True(p.Profile.GpuCore <= 82);
        });
    }

    [Fact]
    public void The_bottleneck_of_a_run_is_the_part_closest_to_its_target()
    {
        var group = new FanGroup("CPU Fan (#0)", [new FanHeader(0, "c0", "CPU Fan", "board", "f0", [new(30, 500), new(100, 1500)], false)]);
        var run = new StoredRun(DateTimeOffset.Now, 22, new Dictionary<string, double> { ["c0"] = 50 }, 100, 300,
            new Dictionary<Component, double> { [Component.Cpu] = 70, [Component.GpuCore] = 78, [Component.GpuHotspot] = 90 });
        var store = MeasurementStore.Empty.Add(new StoredCalibration(run.Time, "game", 22, 100, 300, null, null, 1), [run]);

        var insight = Assert.Single(CalibrationInsights.Runs(store, [group], Preset.For("balanced").Profile));

        // CPU 10 below 80, GPU 2 below 80, hotspot 5 below 95: the GPU core is the tightest
        Assert.Equal(Component.GpuCore, insight.Bottleneck);
        Assert.Equal(2, insight.Headroom, 6);
        Assert.Equal("game", insight.Source);
    }
}
