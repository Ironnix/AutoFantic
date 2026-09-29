using AutoFantic.Core.Calibration;
using AutoFantic.Core.Control;
using AutoFantic.Core.Logging;
using AutoFantic.Core.Simulation;

namespace AutoFantic.Core.Tests;

public class FanControlLoopTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 28, 23, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>The simulated PC's CPU fan, case fans and GPU fan, each with a flat curve at the given speed.</summary>
    private static CalibrationResult Calibration(double cpuFan, double caseFans, double gpuFan) => new(
        DateTimeOffset.Now, "Max 80", 22,
        [
            new CalibratedGroup("CPU Fan (#0)", [0], ["/sim/superio/control/0"], Component.Cpu, 10, 0, [new(40, cpuFan), new(90, cpuFan)], []),
            new CalibratedGroup("Case Fans (#1)", [1], ["/sim/superio/control/1"], Component.Cpu, 2, 4, [new(40, caseFans), new(90, caseFans)], []),
            new CalibratedGroup("GPU Fan (#3)", [3], ["/sim/gpu/control/0"], Component.GpuCore, 0, 12, [new(40, gpuFan), new(90, gpuFan)], []),
        ],
        [], new Dictionary<Component, double[]>(), StopCpuWatts: 0, StopGpuWatts: 0);

    private static (SimulatedPc Pc, FakeClock Clock) Pc(SimLoad load)
    {
        var clock = new FakeClock();
        return (new SimulatedPc(load: _ => load, clock: () => clock.Now), clock);
    }

    private static LoopStatus Run(FanControlLoop loop, FakeClock clock, int seconds)
    {
        LoopStatus status = null!;
        for (int i = 0; i < seconds; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            status = loop.Tick();
        }
        return status;
    }

    [Fact]
    public void Drives_the_fans_by_the_curves()
    {
        var (pc, clock) = Pc(SimLoad.Game);
        using var loop = new FanControlLoop(pc, Calibration(55, 45, 70), [30, 30, 40]);

        var status = Run(loop, clock, 5);

        Assert.Equal(LoopState.Running, status.State);
        Assert.Equal(55, pc.Channels[0].Percent);
        Assert.Equal(70, pc.Channels[3].Percent);
        Assert.True(pc.Channels[0].IsSoftwareControlled);
        Assert.Equal([55.0, 45.0, 70.0], status.Fans.Select(f => f.Percent!.Value));
    }

    [Fact]
    public void Pausing_hands_every_fan_to_the_bios_and_resuming_takes_them_back()
    {
        var (pc, clock) = Pc(SimLoad.Game);
        using var loop = new FanControlLoop(pc, Calibration(55, 45, 70), [30, 30, 40]);
        Run(loop, clock, 3);

        loop.Paused = true;
        var paused = Run(loop, clock, 3);

        Assert.Equal(LoopState.Paused, paused.State);
        Assert.All(pc.Channels, c => Assert.False(c.IsSoftwareControlled));
        Assert.All(paused.Fans, f => Assert.Null(f.Percent));

        loop.Paused = false;
        var resumed = Run(loop, clock, 2);
        Assert.Equal(LoopState.Running, resumed.State);
        Assert.Equal(55, pc.Channels[0].Percent);
    }

    [Fact]
    public void Too_hot_runs_every_fan_at_full_speed_until_cool_then_carries_on()
    {
        // a heavy game with the GPU fan at 30 %, where it stands still: the GPU overheats
        var (pc, clock) = Pc(new SimLoad(120, 320, 40, 99, "Game"));
        using var loop = new FanControlLoop(pc, Calibration(60, 60, 30), [30, 30, 30]);
        string? alert = null;
        loop.Alert += a => alert = a;

        LoopStatus status;
        int seconds = 0;
        do
        {
            status = Run(loop, clock, 1);
        }
        while (status.State != LoopState.CoolingDown && ++seconds < 900);

        Assert.Equal(LoopState.CoolingDown, status.State);
        Assert.Contains("GPU core", alert);
        Assert.All(pc.Channels.Where(c => c.Index != 2), c => Assert.Equal(100, c.Percent));

        // at 100 % it cools down, then control carries on (and will heat up again: that's the flat 30 % curve)
        do
        {
            status = Run(loop, clock, 1);
        }
        while (status.State == LoopState.CoolingDown && ++seconds < 2000);
        Assert.Equal(LoopState.Running, status.State);
    }

    [Fact]
    public void A_fan_given_to_the_bios_is_handed_back_and_left_alone_while_the_others_follow_their_curves()
    {
        var (pc, clock) = Pc(SimLoad.Game);
        var calibration = Calibration(55, 45, 70);
        using var loop = new FanControlLoop(pc, calibration, [30, 30, 40]);
        Run(loop, clock, 3);
        Assert.True(pc.Channels[1].IsSoftwareControlled);

        // the case fans go to the BIOS: handed back right away, the others carry on
        var overrides = CurveOverrides.None.WithBios(calibration.Groups[1], true);
        loop.UseCalibration(overrides.ApplyTo(calibration), [30, 30, 40]);
        var status = Run(loop, clock, 3);

        Assert.False(pc.Channels[1].IsSoftwareControlled);
        Assert.True(pc.Channels[0].IsSoftwareControlled);
        Assert.Equal(55, pc.Channels[0].Percent);
        var caseFans = status.Fans[1];
        Assert.True(caseFans.Bios);
        Assert.Null(caseFans.Percent);
        Assert.Equal(pc.Channels[1].Percent, caseFans.BiosPercent); // what the BIOS runs it at, read back

        // and back to AutoFantic
        loop.UseCalibration(overrides.WithBios(calibration.Groups[1], false).ApplyTo(calibration), [30, 30, 40]);
        Run(loop, clock, 3);
        Assert.Equal(45, pc.Channels[1].Percent);
    }

    [Fact]
    public void A_fan_given_to_the_bios_stays_with_it_even_at_a_safety_limit()
    {
        var (pc, clock) = Pc(new SimLoad(120, 320, 40, 99, "Game"));
        var calibration = Calibration(60, 60, 30);
        using var loop = new FanControlLoop(pc, CurveOverrides.None.WithBios(calibration.Groups[0], true).ApplyTo(calibration), [30, 30, 30]);

        LoopStatus status;
        int seconds = 0;
        do
        {
            status = Run(loop, clock, 1);
        }
        while (status.State != LoopState.CoolingDown && ++seconds < 900);

        Assert.Equal(LoopState.CoolingDown, status.State);
        Assert.False(pc.Channels[0].IsSoftwareControlled); // the BIOS's own curve applies
        Assert.Equal(100, pc.Channels[1].Percent);
        Assert.Equal(100, pc.Channels[3].Percent);
    }

    [Fact]
    public void Without_a_calibration_the_bios_keeps_the_fans_until_the_first_one_is_done()
    {
        var (pc, clock) = Pc(SimLoad.Game);
        using var loop = new FanControlLoop(pc, calibration: null, []);

        var status = Run(loop, clock, 3);
        Assert.Equal(LoopState.NotSetUp, status.State);
        Assert.NotNull(status.CpuTemp);                // the window still shows the temperatures
        Assert.Empty(status.Fans);
        Assert.All(pc.Channels, c => Assert.False(c.IsSoftwareControlled));

        loop.Paused = true;                            // a calibration pauses it …
        loop.Paused = false;                           // … and resumes it, still not set up
        Assert.Equal(LoopState.NotSetUp, Run(loop, clock, 1).State);

        loop.UseCalibration(Calibration(55, 45, 70), [30, 30, 40]);
        status = Run(loop, clock, 2);
        Assert.Equal(LoopState.Running, status.State);
        Assert.Equal(55, pc.Channels[0].Percent);
    }

    [Fact]
    public void Safety_stops_and_fans_switching_off_are_written_to_the_log()
    {
        var log = ActivityLog.InMemoryOnly();
        var (pc, clock) = Pc(new SimLoad(120, 320, 40, 99, "Game"));
        using var loop = new FanControlLoop(pc, Calibration(60, 60, 30), [30, 30, 30], log);

        int seconds = 0;
        while (loop.Tick().State != LoopState.CoolingDown && ++seconds < 900)
            clock.Advance(TimeSpan.FromSeconds(1));
        while (loop.Tick().State == LoopState.CoolingDown && ++seconds < 2000)
            clock.Advance(TimeSpan.FromSeconds(1));

        var safety = log.Entries.Where(e => e.Kind == LogKind.Safety).ToList();
        Assert.Contains("all fans at 100 %", safety[0].Text);
        Assert.Contains("Cooled down again", safety[1].Text);

        // at idle, a fan that may stop switches off, and that is logged too
        var idle = ActivityLog.InMemoryOnly();
        var (quiet, quietClock) = Pc(SimLoad.Idle);
        var calibration = Calibration(40, 40, 40);
        calibration = calibration with
        {
            Groups = [calibration.Groups[0], calibration.Groups[1], calibration.Groups[2] with { OffAt = ["idle"] }],
            StopCpuWatts = 60,
            StopGpuWatts = 60,
        };
        using var idleLoop = new FanControlLoop(quiet, calibration, [30, 30, 30], idle);
        Run(idleLoop, quietClock, 120);

        var off = Assert.Single(idle.Entries, e => e.Kind == LogKind.Fans);
        Assert.StartsWith("GPU Fan (#3) off: idle and cool", off.Text);
    }

    [Fact]
    public void Stopping_hands_every_fan_back_to_the_bios()
    {
        var (pc, clock) = Pc(SimLoad.Game);
        var loop = new FanControlLoop(pc, Calibration(55, 45, 70), [30, 30, 40]);
        Run(loop, clock, 3);

        loop.Dispose();
        loop.Dispose(); // exit, crash handler and logoff may all call it

        Assert.All(pc.Channels, c => Assert.False(c.IsSoftwareControlled));
    }

    [Fact]
    public void A_fan_output_that_no_longer_exists_is_refused_at_the_start()
    {
        var (pc, _) = Pc(SimLoad.Game);
        var calibration = Calibration(50, 50, 50);
        var broken = calibration with { Groups = [calibration.Groups[0] with { ControlIds = ["/lpc/gone/control/7"] }] };

        Assert.Throws<InvalidOperationException>(() => new FanControlLoop(pc, broken, [30]));
    }
}
