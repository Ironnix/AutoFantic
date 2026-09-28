using AutoFantic.Core.Calibration;
using AutoFantic.Core.Control;
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
