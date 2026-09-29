using AutoFantic.Core.Calibration;
using AutoFantic.Core.Control;
using AutoFantic.Core.Monitoring;
using AutoFantic.Core.Simulation;

namespace AutoFantic.Core.Tests;

public class HealthAndQuietTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 20, 0, 0, TimeSpan.Zero);

    // ── quiet mode ─────────────────────────────────────────────────────────────────────

    // a CPU fan that may never stop and GPU fans that can (0-RPM), at a GPU idle power too high for "off at idle"
    private static CalibrationResult Calibration() => new(
        T0, "Balanced", 22,
        [
            new CalibratedGroup("CPU Fan (#0)", [0], ["c0"], Component.Cpu, 10, 0, [new(40, 40), new(80, 100)], []),
            new CalibratedGroup("GPU fans", [8, 9], ["g1", "g2"], Component.GpuCore, 0, 12, [new(40, 45), new(80, 100)], []),
        ],
        [], new Dictionary<Component, double[]>(), StopCpuWatts: 50, StopGpuWatts: 60);

    private static double[] Hold(CurveController c, ref DateTimeOffset t, int seconds, double cpu, double gpu, double cpuW = 30, double gpuW = 130)
    {
        double[] output = [];
        for (int i = 0; i < seconds; i++)
        {
            t = t.AddSeconds(1);
            output = c.Step(t, new Dictionary<Component, double> { [Component.Cpu] = cpu, [Component.GpuCore] = gpu }, cpuW, gpuW);
        }
        return output;
    }

    [Fact]
    public void Quiet_runs_every_fan_at_its_slowest_and_stops_the_ones_that_can_even_above_idle_power()
    {
        var c = new CurveController(Calibration(), [25, 35], canStop: [false, true]);
        var t = T0;
        var normal = Hold(c, ref t, 120, cpu: 50, gpu: 52);
        Assert.InRange(normal[0], 54, 56);        // CPU fan on its curve (55 % at 50 °C)
        Assert.InRange(normal[1], 60, 63);        // GPU fans on theirs (61.5 %): 130 W is too much for "off at idle"

        c.Quiet = "away";
        var quiet = Hold(c, ref t, 120, cpu: 50, gpu: 52);
        Assert.Equal(25, quiet[0], precision: 1); // the CPU fan at its slowest
        Assert.Equal(0, quiet[1]);                // the GPU fans off
        Assert.Equal(FanNote.Quiet, c.Status[0].Note);

        var warm = Hold(c, ref t, 30, cpu: 50, gpu: 70);
        Assert.True(warm[1] >= 35, "warm again: the GPU fans run, quiet or not");
    }

    [Fact]
    public void Quiet_still_cools_a_render_while_you_are_away()
    {
        var c = new CurveController(Calibration(), [25, 35], canStop: [false, true]) { Quiet = "away" };
        var t = T0;
        var hot = Hold(c, ref t, 200, cpu: 75, gpu: 45);
        Assert.InRange(hot[0], 92, 93); // 75 °C is past the quiet range: the CPU fan's curve (92.5 %) again
        var between = Hold(c, ref t, 200, cpu: 65, gpu: 45);
        Assert.InRange(between[0], 50, 52.5); // halfway into the blend: between the slowest (25 %) and the curve (77.5 %)
    }

    [Theory]
    [InlineData("23:30", 0, "night")]
    [InlineData("06:59", 0, "night")]
    [InlineData("07:00", 0, null)]
    [InlineData("14:00", 11, "away")]
    [InlineData("14:00", 9, null)]
    public void Quiet_comes_at_night_and_when_nobody_is_at_the_pc(string time, int idleMinutes, string? expected)
    {
        var settings = new QuietSettings(WhenAway: true, AwayMinutes: 10, AtNight: true, NightFrom: "23:00", NightTo: "07:00");
        var now = DateTime.Today + TimeOnly.Parse(time).ToTimeSpan();

        Assert.Equal(expected, settings.Reason(now, TimeSpan.FromMinutes(idleMinutes)));
        Assert.Null((settings with { WhenAway = false, AtNight = false }).Reason(now, TimeSpan.FromHours(3)));
    }

    [Fact]
    public void At_idle_the_loop_reads_every_2_seconds_and_under_load_every_second()
    {
        var clock = T0;
        var load = SimLoad.Idle;
        using var pc = new SimulatedPc(load: _ => load, clock: () => clock);
        using var loop = new FanControlLoop(pc, null, []);

        loop.Tick();
        Assert.True(loop.Relaxed);

        load = SimLoad.Game;
        clock = clock.AddSeconds(2);
        loop.Tick();
        Assert.False(loop.Relaxed);
    }

    // ── sessions ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_game_played_for_a_while_is_a_session_and_short_breaks_dont_split_it()
    {
        var tracker = new SessionTracker();
        var t = T0;
        (string Program, DateTimeOffset Start, DateTimeOffset End)? Run(int seconds, string? program, double gpu)
        {
            (string, DateTimeOffset, DateTimeOffset)? ended = null;
            for (int i = 0; i < seconds; i++)
            {
                t = t.AddSeconds(1);
                ended ??= tracker.Feed(t, program, 20, gpu);
            }
            return ended;
        }

        Assert.Null(Run(600, "VALORANT-Win64-Shipping", 95));
        Assert.Null(Run(45, "VALORANT-Win64-Shipping", 10));   // the menu
        Assert.Null(Run(30, "Discord", 50));                   // a quick look at another window
        Assert.Null(Run(600, "VALORANT-Win64-Shipping", 95));
        var session = Run(120, "explorer", 5);                  // closed
        Assert.NotNull(session);
        Assert.Equal("VALORANT-Win64-Shipping", session.Value.Program);
        Assert.InRange((session.Value.End - session.Value.Start).TotalMinutes, 21, 22);

        Assert.Null(Run(60, "cs2", 90));                        // one minute is no session
        Assert.Null(Run(200, "explorer", 5));
        Assert.Equal("VALORANT", Sessions.Pretty("VALORANT-Win64-Shipping"));
        Assert.Equal("cs2", Sessions.Pretty("cs2"));
    }

    [Fact]
    public void A_stored_session_has_the_average_and_highest_of_every_value()
    {
        using var store = new HistoryStore(null);
        for (int s = 0; s < 600; s++)
            store.Add(T0.AddSeconds(s), [(HistoryRecorder.GpuTemp, s < 300 ? 70.0 : 74.0), (HistoryRecorder.GpuHotspot, s == 400 ? 91.0 : 85.0)]);

        store.AddSession("cs2", T0, T0.AddSeconds(599), "Silent");

        var session = Assert.Single(store.Sessions());
        Assert.Equal("Silent", session.Preset);
        Assert.Equal(72, session.Stats[HistoryRecorder.GpuTemp.Key].Avg, precision: 1);
        Assert.Equal(91, session.Stats[HistoryRecorder.GpuHotspot.Key].Max);
    }

    // ── cooling health ─────────────────────────────────────────────────────────────────

    // a model like ralf-pc's: CPU about 0.4 °C/W, GPU about 0.15 °C/W, each a little better with its fan faster
    private static CalibrationResult HealthCalibration() => new(
        T0, "Balanced", 22,
        [
            new CalibratedGroup("CPU Fan (#0)", [0], ["c0"], Component.Cpu, 6, 0, [new(40, 30), new(85, 100)], []),
            new CalibratedGroup("GPU fans", [8], ["g1"], Component.GpuCore, 0, 30, [new(40, 30), new(80, 100)], []),
        ],
        [new LoadRow("high", 140, 350, [80, 80], new Dictionary<Component, double>(), 0, true)],
        new Dictionary<Component, double[]> { [Component.Cpu] = [0.30, 0.10, 0.0], [Component.GpuCore] = [0.10, 0.0, 0.05] },
        StopCpuWatts: 50, StopGpuWatts: 150);

    private static List<CoolingHealth.Minute> Minutes(CalibrationResult calibration, double room, double cpuExtraPerW, double gpuExtraPerW, int count = 400)
    {
        var model = ThermalModel.FromCoefficients(calibration.Ambient, 2, calibration.Model);
        var random = new Random(7);
        var minutes = new List<CoolingHealth.Minute>();
        for (int i = 0; i < count; i++)
        {
            // half idle, half gaming, like a normal day
            bool gaming = i % 2 == 0;
            double cpuW = gaming ? 90 + random.NextDouble() * 50 : 25 + random.NextDouble() * 10;
            double gpuW = gaming ? 250 + random.NextDouble() * 100 : 30 + random.NextDouble() * 20;
            double[] speeds = [30 + random.NextDouble() * 50, 30 + random.NextDouble() * 50];
            double cpu = model.Predict(Component.Cpu, speeds, cpuW, gpuW) + room + cpuExtraPerW * cpuW + (random.NextDouble() - 0.5);
            double gpu = model.Predict(Component.GpuCore, speeds, cpuW, gpuW) + room + gpuExtraPerW * gpuW + (random.NextDouble() - 0.5);
            minutes.Add(new CoolingHealth.Minute(cpu, gpu, cpuW, gpuW, speeds));
        }
        return minutes;
    }

    [Fact]
    public void Cooling_as_good_as_at_the_calibration_shows_no_extra()
    {
        var calibration = HealthCalibration();
        var result = CoolingHealth.Analyze(calibration, Minutes(calibration, room: 0, 0, 0))!;

        Assert.InRange(result.RoomShift, -0.5, 0.5);
        Assert.InRange(result.CpuExtra!.Value, -1, 1);
        Assert.InRange(result.GpuExtra!.Value, -1, 1);
    }

    [Fact]
    public void A_warmer_room_is_told_apart_from_dust()
    {
        var calibration = HealthCalibration();

        // summer: 4 °C warmer room, cooling as good as ever
        var summer = CoolingHealth.Analyze(calibration, Minutes(calibration, room: 4, 0, 0))!;
        Assert.InRange(summer.RoomShift, 3.5, 4.5);
        Assert.InRange(summer.GpuExtra!.Value, -1, 1);

        // dusty graphics card: 0.02 °C more per watt = 7 °C more at its full 350 W, same room
        var dusty = CoolingHealth.Analyze(calibration, Minutes(calibration, room: 0, 0, 0.02))!;
        Assert.InRange(dusty.GpuExtra!.Value, 5.5, 8);
        Assert.InRange(dusty.CpuExtra!.Value, -1, 1);
        Assert.InRange(dusty.RoomShift, -1, 1);
    }

    [Fact]
    public void Without_enough_minutes_or_load_there_is_no_verdict_yet()
    {
        var calibration = HealthCalibration();
        Assert.Null(CoolingHealth.Analyze(calibration, Minutes(calibration, 0, 0, 0, count: 30)));

        // idle all day: the room shows, but whether the cooling is worse can't be told
        var idle = Minutes(calibration, 2, 0, 0).Where(m => m.GpuPower < 100).ToList();
        var result = CoolingHealth.Analyze(calibration, idle)!;
        Assert.InRange(result.RoomShift, 1, 3);
        Assert.Null(result.GpuExtra);
    }

    [Fact]
    public void The_trend_compares_the_last_week_with_the_first_week_after_the_calibration()
    {
        var calibrated = T0;
        var start = DateOnly.FromDateTime(T0.Date);
        var days = Enumerable.Range(0, 30)
            .Select(i => new HealthDay(start.AddDays(i), calibrated, new HealthResult(300, 0, 1.0, i < 7 ? 1.5 : 1.5 + (i - 6) * 0.2)))
            .ToList();

        var trend = CoolingHealth.Trend(days, calibrated, start.AddDays(29))!.Value;

        Assert.Equal(1.5, trend.Baseline.GpuExtra!.Value, precision: 3);
        Assert.InRange(trend.GpuChange!.Value, 4.0, 4.6);  // the graphics card got clearly warmer
        Assert.Equal(0, trend.CpuChange!.Value, precision: 3);
        Assert.Null(CoolingHealth.Trend(days.Take(5).ToList(), calibrated, start.AddDays(4))); // still the baseline week
    }
}
