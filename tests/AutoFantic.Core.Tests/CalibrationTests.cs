using AutoFantic.Core.Calibration;
using AutoFantic.Core.Hardware;
using AutoFantic.Core.Simulation;

namespace AutoFantic.Core.Tests;

public class CalibrationTests
{
    // ── step response ──────────────────────────────────────────────────────────────────

    private static List<(double, double)> Exponential(double final, double start, double tau, double seconds, double noise = 0.1, int seed = 3)
    {
        var random = new Random(seed);
        return Enumerable.Range(3, (int)seconds - 2)
            .Select(t => ((double)t, final + (start - final) * Math.Exp(-t / tau) + (random.NextDouble() * 2 - 1) * noise))
            .ToList();
    }

    [Fact]
    public void Predicts_the_final_temperature_after_about_one_time_constant()
    {
        var fit = StepResponseFit.Fit(Exponential(final: 72, start: 60, tau: 60, seconds: 75))!;

        Assert.InRange(fit.Final, 71.3, 72.7);
        Assert.InRange(fit.Tau, 45, 80);
        Assert.True(fit.Reliable);
    }

    [Fact]
    public void Too_early_to_tell_is_flagged_as_unreliable()
    {
        var fit = StepResponseFit.Fit(Exponential(final: 72, start: 60, tau: 200, seconds: 40))!;

        Assert.False(fit.Reliable);
    }

    [Fact]
    public void A_flat_temperature_is_simply_its_mean()
    {
        var fit = StepResponseFit.Fit(Exponential(final: 65, start: 65, tau: 60, seconds: 60, noise: 0.3))!;

        Assert.InRange(fit.Final, 64.7, 65.3);
        Assert.True(fit.Reliable);
    }

    // ── thermal model ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Recovers_what_each_fan_cools_from_the_nine_runs()
    {
        // truth: group 0 cools the CPU, group 1 both, group 2 the GPU
        double[] cpu = [0.2, 8, 1.5, 0], gpu = [0.05, 0, 2, 5];
        var groups = Groups(3);
        var observations = CalibrationPlan.Runs(groups).Select(speeds => new Observation(
            speeds, 90, 250,
            new Dictionary<Component, double>
            {
                [Component.Cpu] = 22 + 90 * (cpu[0] + speeds.Select((s, g) => cpu[g + 1] * ThermalModel.Basis(s)).Sum()),
                [Component.GpuCore] = 22 + 250 * (gpu[0] + speeds.Select((s, g) => gpu[g + 1] * ThermalModel.Basis(s)).Sum()),
            })).ToList();

        var model = ThermalModel.Fit(observations, 22, 3);

        Assert.Equal(cpu, model.Coefficients(Component.Cpu).ToArray(), (a, b) => Math.Abs(a - b) < 1e-6);
        Assert.Equal(gpu, model.Coefficients(Component.GpuCore).ToArray(), (a, b) => Math.Abs(a - b) < 1e-6);
    }

    [Fact]
    public void Coefficients_never_go_negative()
    {
        // noise that would pull an unrelated fan's coefficient below zero in a plain fit
        var x = new List<double[]> { new[] { 1, 0.01 }, new[] { 1, 0.02 }, new[] { 1, 0.03 } };
        var y = new List<double> { 0.30, 0.29, 0.28 };

        var beta = ThermalModel.NonNegativeLeastSquares(x, y);

        Assert.True(beta[1] >= 0);
        Assert.InRange(beta[0], 0.28, 0.30);
    }

    // ── plan ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_plan_starts_cool_and_tries_every_speed_of_every_group()
    {
        var groups = Groups(4);

        var runs = CalibrationPlan.Runs(groups);

        Assert.Equal(9, runs.Count);
        Assert.All(runs[0], s => Assert.Equal(100, s));
        for (int g = 0; g < 4; g++)
            Assert.Equal(3, runs.Select(r => r[g]).Distinct().Count());
    }

    [Fact]
    public void One_group_needs_only_three_runs()
    {
        Assert.Equal(3, CalibrationPlan.Runs(Groups(1)).Count);
    }

    // ── inventory ──────────────────────────────────────────────────────────────────────

    private static FanHeader Header(int channel, string name, string hardware, string? rpmSensor, bool gpu = false, bool pump = false) =>
        new(channel, gpu ? $"/gpu-nvidia/0/control/{channel}" : $"/lpc/nct6686d/0/control/{channel}", name, hardware, rpmSensor,
            rpmSensor is null ? [] : gpu
                ? [new(30, 0), new(60, 1335), new(100, 2875)]
                : [new(30, 510), new(60, 971), new(100, 1483)],
            pump);

    private static FanInventory RalfsPc() => new(DateTimeOffset.Now,
    [
        Header(0, "CPU Fan", "Nuvoton NCT6686D", "fan0"),
        Header(1, "Pump Fan", "Nuvoton NCT6686D", "fan1"),
        Header(2, "System Fan #1", "Nuvoton NCT6686D", null),
        Header(5, "System Fan #4", "Nuvoton NCT6686D", "fan5"),
        Header(8, "GPU Fan 1", "NVIDIA GeForce RTX 3080", "gfan0", gpu: true),
        Header(9, "GPU Fan 2", "NVIDIA GeForce RTX 3080", "gfan1", gpu: true),
    ]);

    [Fact]
    public void Empty_headers_are_left_out_and_the_gpu_fans_form_one_group()
    {
        var groups = RalfsPc().Groups();

        Assert.Equal(["CPU Fan (#0)", "Pump Fan header (#1)", "System Fan #4 (#5)", "GPU fans (#8, #9)"], groups.Select(g => g.Name));
        Assert.True(groups[3].IsGpu);
        Assert.True(groups[3].CanStop);
        Assert.False(groups[0].CanStop);
    }

    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(2, 2.0)]
    [InlineData(3, 3.0)] // three fans on the card's two outputs: 2 + 1, not 2 + 2
    [InlineData(4, 4.0)]
    [InlineData(5, 5.0)]
    public void The_fans_the_user_counted_on_a_graphics_card_stay_that_many(int fans, double timesAsLoud)
    {
        var one = RalfsPc().Groups()[3] with { Headers = [RalfsPc().Groups()[3].Headers[0]] };
        var inventory = RalfsPc().WithLoudness(RalfsPc().Groups()[3], fans, loudnessDb: 0);
        var gpu = inventory.Groups()[3];

        Assert.Equal(fans, gpu.Headers.Sum(h => h.FanCount));
        // as loud as that many single fans: 10·log10(n) above one of them
        Assert.Equal(10 * Math.Log10(timesAsLoud), NoiseModel.Group(gpu, 100) - NoiseModel.Group(one, 100), 1);
        Assert.All(inventory.Headers.Where(h => !h.IsGpu), h => Assert.Equal(1, h.FanCount));
    }

    [Fact]
    public void What_the_user_says_a_fan_cools_goes_before_what_was_measured()
    {
        var measured = RalfsPc();
        var cpuFan = measured.Groups()[0];
        Assert.Equal(Component.Cpu, CalibrationCalculator.Follows(cpuFan, cpuEffect: 6, gpuEffect: 0.2));

        // "these are case fans" and "this one sits on the graphics card", although the calibration saw them cool the CPU
        var said = measured.WithCools(cpuFan, Component.Warmest).WithCools(measured.Groups()[1], Component.GpuCore);
        Assert.Equal(Component.Warmest, CalibrationCalculator.Follows(said.Groups()[0], 6, 0.2));
        Assert.Equal(Component.GpuCore, CalibrationCalculator.Follows(said.Groups()[1], 6, 0.2));
        Assert.Equal(Component.GpuCore, CalibrationCalculator.Follows(said.Groups()[3], 0, 8)); // the others: as before

        // finding the fans again keeps it; "as measured" takes it back
        Assert.Equal(Component.Warmest, measured.KeepingKnown(said).Groups()[0].Cools);
        Assert.Equal(Component.Cpu, CalibrationCalculator.Follows(said.WithCools(said.Groups()[0], null).Groups()[0], 6, 0.2));
    }

    [Fact]
    public void A_confirmed_pump_is_never_used()
    {
        var inventory = RalfsPc().WithPumps(new HashSet<int> { 1 });

        Assert.DoesNotContain(inventory.Groups(), g => g.Headers.Any(h => h.Channel == 1));
        Assert.Equal("Pump Fan", inventory.Headers.Single(h => h.Channel == 1).DisplayName);
    }

    [Fact]
    public void Inventory_survives_a_round_trip_through_json()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fans-{Guid.NewGuid():N}.json");
        try
        {
            RalfsPc().Save(path);
            var loaded = FanInventory.Load(path)!;

            Assert.Equal(RalfsPc().Groups().Select(g => g.Name), loaded.Groups().Select(g => g.Name));
            Assert.Equal(1335, loaded.Headers.Single(h => h.Channel == 8).RpmAt(60));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_fan_that_stops_at_zero_may_be_switched_off_and_still_spins_from_its_lowest_speed()
    {
        var inventory = RalfsPc().WithRpmAtZero(new Dictionary<int, float> { [0] = 0, [5] = 420 });
        var groups = inventory.Groups();

        var cpuFan = groups.Single(g => g.Name.StartsWith("CPU Fan"));
        var caseFans = groups.Single(g => g.Name.StartsWith("System Fan #4"));
        var gpu = groups.Single(g => g.IsGpu);

        Assert.True(cpuFan.CanStop);
        Assert.Equal(30, cpuFan.MinSpinning);
        Assert.False(caseFans.CanStop); // still turns at 0 %
        Assert.Equal(40, gpu.MinSpinning);  // stood still at 30 %: starts above that
    }

    [Fact]
    public void Mainboard_fans_that_can_stop_are_off_at_idle_when_it_stays_cool()
    {
        var groups = RalfsPc().WithRpmAtZero(new Dictionary<int, float> { [0] = 0, [1] = 0, [5] = 0 }).Groups().ToList();
        var observations = CalibrationPlan.Runs(groups).Select(speeds => new Observation(speeds, 90, 250, new Dictionary<Component, double>
        {
            [Component.Cpu] = 22 + 90 * (0.2 + 2 * ThermalModel.Basis(speeds[0]) + 1 * ThermalModel.Basis(speeds[1]) + 0.5 * ThermalModel.Basis(speeds[2])),
            [Component.GpuCore] = 22 + 250 * (0.05 + 0.5 * ThermalModel.Basis(speeds[2]) + 2 * ThermalModel.Basis(speeds[3])),
        })).ToList();
        var optimizer = new MixOptimizer(ThermalModel.Fit(observations, 22, 4), groups, Profile.Max(80)) { StopOnlyUpTo = (45, 125) };

        var idle = optimizer.Best(20, 20);
        var load = optimizer.Best(90, 250);

        Assert.All(idle.Speeds, s => Assert.Equal(0, s));
        Assert.All(load.Speeds, s => Assert.NotEqual(0, s));
    }

    [Fact]
    public void Everything_off_uses_the_measured_fans_off_temperatures()
    {
        var groups = RalfsPc().WithRpmAtZero(new Dictionary<int, float> { [0] = 0, [1] = 0, [5] = 0 }).Groups().ToList();
        var observations = CalibrationPlan.Runs(groups).Select(speeds => new Observation(speeds, 90, 250, new Dictionary<Component, double>
        {
            [Component.Cpu] = 22 + 90 * (0.2 + 2 * ThermalModel.Basis(speeds[0])),
            [Component.GpuCore] = 22 + 250 * (0.05 + 2 * ThermalModel.Basis(speeds[3])),
        })).ToList();
        var model = ThermalModel.Fit(observations, 22, 4);

        // the fans-off test found it much warmer without fans than the model would guess
        var measured = new Dictionary<Component, double> { [Component.Cpu] = 1.5, [Component.GpuCore] = 1.0 };
        var optimizer = new MixOptimizer(model, groups, Profile.Max(80)) { AllOffResistance = measured };

        var allOff = optimizer.Predict([0, 0, 0, 0], 20, 30);

        Assert.Equal(22 + 20 * 1.5, allOff[Component.Cpu], 6);
        Assert.Equal(22 + 30 * 1.0, allOff[Component.GpuCore], 6);
        Assert.Equal(52, allOff[Component.GpuCore], 6);
    }

    [Fact]
    public void A_gpu_fan_above_its_stop_point_is_not_silent()
    {
        var gpu = RalfsPc().Headers.Single(h => h.Channel == 8);

        Assert.Equal(0, gpu.RpmAt(0));
        Assert.InRange(gpu.RpmAt(40), 800, 1000);
    }

    // ── optimizer ──────────────────────────────────────────────────────────────────────

    private static List<FanGroup> Groups(int count) =>
        RalfsPc().Groups().Take(count).ToList();

    private static MixOptimizer SimOptimizer(double limit = 80)
    {
        // CPU fan (#0) cools the CPU, the "pump header" fan too, case fans (#5) both, GPU fans the GPU
        var groups = Groups(4);
        var observations = CalibrationPlan.Runs(groups).Select(speeds => new Observation(speeds, 90, 250, new Dictionary<Component, double>
        {
            [Component.Cpu] = 22 + 90 * (0.2 + 6 * ThermalModel.Basis(speeds[0]) + 3 * ThermalModel.Basis(speeds[1]) + 1.5 * ThermalModel.Basis(speeds[2])),
            [Component.GpuCore] = 22 + 250 * (0.05 + 1.5 * ThermalModel.Basis(speeds[2]) + 5 * ThermalModel.Basis(speeds[3])),
        })).ToList();
        return new MixOptimizer(ThermalModel.Fit(observations, 22, 4), groups, Profile.Max(limit));
    }

    [Fact]
    public void Light_load_gets_the_slowest_speeds()
    {
        var mix = SimOptimizer().Best(30, 60);

        Assert.True(mix.MeetsTarget);
        Assert.Equal(30, mix.Speeds[0]);
        Assert.All(mix.Temperatures.Values, t => Assert.True(t <= 78));
    }

    [Fact]
    public void Heavy_load_speeds_up_only_as_much_as_needed()
    {
        var optimizer = SimOptimizer(limit: 70);
        var mix = optimizer.Best(120, 320);

        Assert.True(mix.MeetsTarget);
        Assert.True(mix.Temperatures[Component.Cpu] <= 68.01);
        Assert.True(mix.Temperatures[Component.GpuCore] <= 68.01);
        Assert.True(mix.Speeds.Any(s => s < 100), "some fan should stay below full speed");
    }

    [Fact]
    public void Gpu_fans_only_stop_while_everything_is_cool()
    {
        var optimizer = SimOptimizer();

        var idle = optimizer.Best(25, 20);
        var gaming = optimizer.Best(60, 150);

        Assert.Equal(0, idle.Speeds[3]);
        Assert.NotEqual(0, gaming.Speeds[3]);
    }

    [Fact]
    public void An_unreachable_target_gives_the_coolest_mix()
    {
        var mix = SimOptimizer(limit: 40).Best(120, 320);

        Assert.False(mix.MeetsTarget);
        Assert.All(mix.Speeds, s => Assert.Equal(100, s));
    }

    [Fact]
    public void The_table_never_slows_a_fan_down_as_the_load_rises()
    {
        var table = SimOptimizer().Table([(25, 20), (50, 100), (75, 200), (100, 300)]);

        for (int g = 0; g < 4; g++)
            for (int i = 1; i < table.Count; i++)
                Assert.True(table[i].Speeds[g] >= table[i - 1].Speeds[g]);
    }

    // ── the whole procedure on the simulated PC ────────────────────────────────────────

    [Fact]
    public void Calibrating_the_simulated_pc_finds_what_each_fan_cools()
    {
        var clock = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        using var pc = new SimulatedPc(load: _ => SimLoad.Idle, clock: () => clock);
        var keys = KeySensors.Detect(pc.Read());
        using var load = pc.StartTestLoad(out _);

        // the simulated PC's fans: CPU fan, case fans, GPU fan (the pump is left out)
        var channels = new[] { pc.Channels[0], pc.Channels[1], pc.Channels[3] };
        var groups = channels.Select(c => new FanGroup(c.Name, [new FanHeader(c.Index, c.Id, c.Name, c.Hardware, "rpm",
            c.Index == 3 ? [new(30, 0), new(60, 1800), new(100, 3000)] : [new(30, 540), new(60, 1080), new(100, 1800)], false)])).ToList();

        var observations = new List<Observation>();
        foreach (var speeds in CalibrationPlan.Runs(groups))
        {
            for (int g = 0; g < channels.Length; g++)
                pc.SetPercent(channels[g], (float)speeds[g]);

            var cpu = new List<(double, double)>();
            var gpu = new List<(double, double)>();
            double cpuW = 0, gpuW = 0;
            for (int t = 1; t <= 90; t++)
            {
                clock = clock.AddSeconds(1);
                var s = pc.Read();
                cpu.Add((t, s.Value(keys.CpuTemp)!.Value));
                gpu.Add((t, s.Value(keys.GpuTemp)!.Value));
                cpuW += s.Value(keys.CpuPower)!.Value / 90;
                gpuW += s.Value(keys.GpuPower)!.Value / 90;
            }

            observations.Add(new Observation(speeds, cpuW, gpuW, new Dictionary<Component, double>
            {
                [Component.Cpu] = StepResponseFit.Fit(cpu)!.Final,
                [Component.GpuCore] = StepResponseFit.Fit(gpu)!.Final,
            }));
        }

        var model = ThermalModel.Fit(observations, 22, 3);
        double Effect(Component c, int g, double power) =>
            power * model.Coefficients(c)[g + 1] * (ThermalModel.Basis(35) - ThermalModel.Basis(100));

        Assert.InRange(Effect(Component.Cpu, 0, 90), 8, 13);      // the CPU fan cools the CPU …
        Assert.InRange(Effect(Component.GpuCore, 0, 250), 0, 1);  // … and not the GPU
        Assert.InRange(Effect(Component.GpuCore, 2, 250), 9, 15); // the GPU fan cools the GPU
        Assert.InRange(Effect(Component.GpuCore, 1, 250), 2, 7);  // the case fans help the GPU too
        Assert.All(model.Rms.Values, rms => Assert.True(rms < 1, $"model off by {rms:0.00} °C"));
    }
}
