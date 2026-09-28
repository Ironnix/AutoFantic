using AutoFanatic.Core.Analysis;
using AutoFanatic.Core.Hardware;
using AutoFanatic.Core.Simulation;

namespace AutoFanatic.Core.Tests;

public class SimulatedPcTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => Now += by;
    }

    private static (SimulatedPc Pc, FakeClock Clock) Create()
    {
        var clock = new FakeClock();
        return (new SimulatedPc(clock: () => clock.Now), clock);
    }

    private static Snapshot SettleAndRead(SimulatedPc pc, FakeClock clock)
    {
        clock.Advance(TimeSpan.FromMinutes(10)); // many time constants: fully settled
        return pc.Read();
    }

    [Fact]
    public void Session_clock_is_the_simulated_clock()
    {
        var (pc, clock) = Create();
        clock.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal(clock.Now, pc.Now);
        Assert.Equal(clock.Now, pc.Read().Time);
    }

    [Fact]
    public void Sped_up_clock_runs_ahead_of_real_time()
    {
        using var pc = new SimulatedPc(timeScale: 50);
        var start = pc.Now;
        Thread.Sleep(200);

        Assert.Equal(50, pc.TimeScale);
        Assert.True(pc.Now - start >= TimeSpan.FromSeconds(5)); // 0.2 s × 50 = 10 s
    }

    [Fact]
    public void A_session_moves_from_desktop_into_the_game_and_heats_up()
    {
        var clock = new FakeClock();
        using var pc = new SimulatedPc(load: SimLoad.Session, clock: () => clock.Now);
        var keys = KeySensors.Detect(pc.Read());

        Assert.Equal("explorer", pc.Foreground());
        double desktopGpu = pc.Read().Value(keys.GpuTemp)!.Value;

        clock.Advance(TimeSpan.FromMinutes(10));
        var inGame = pc.Read();

        Assert.Equal("SimGame", pc.Foreground());
        Assert.True(inGame.Value(keys.GpuLoad) > 90);
        Assert.True(inGame.Value(keys.GpuTemp) > desktopGpu + 20);
    }

    [Fact]
    public void Key_sensors_are_detected_like_on_real_hardware()
    {
        var (pc, _) = Create();
        var keys = KeySensors.Detect(pc.Read());

        Assert.NotNull(keys.CpuTemp);
        Assert.NotNull(keys.CpuPower);
        Assert.NotNull(keys.GpuTemp);
        Assert.NotNull(keys.GpuHotspot);
        Assert.NotNull(keys.GpuMemory);
        Assert.NotNull(keys.GpuPower);
        Assert.NotNull(keys.CpuLoad);
        Assert.NotNull(keys.GpuLoad);
        Assert.Equal(4, pc.Channels.Count);
    }

    [Fact]
    public void Slower_cpu_fan_means_hotter_cpu()
    {
        var (pc, clock) = Create();
        var keys = KeySensors.Detect(pc.Read());
        var cpuFan = pc.Channels[0];

        pc.SetPercent(cpuFan, 100);
        double fast = SettleAndRead(pc, clock).Value(keys.CpuTemp)!.Value;
        pc.SetPercent(cpuFan, 30);
        double slow = SettleAndRead(pc, clock).Value(keys.CpuTemp)!.Value;

        Assert.True(slow > fast + 5, $"expected clearly hotter at 30 % ({slow:0.0}) than at 100 % ({fast:0.0})");
    }

    [Fact]
    public void Temperature_moves_gradually_not_instantly()
    {
        var (pc, clock) = Create();
        var keys = KeySensors.Detect(pc.Read());
        double before = pc.Read().Value(keys.CpuTemp)!.Value;

        pc.SetPercent(pc.Channels[0], 25);
        clock.Advance(TimeSpan.FromSeconds(5));
        double shortlyAfter = pc.Read().Value(keys.CpuTemp)!.Value;
        double settled = SettleAndRead(pc, clock).Value(keys.CpuTemp)!.Value;

        Assert.InRange(shortlyAfter, before + 0.5, settled - 1);
    }

    [Fact]
    public void The_model_has_a_knee_the_finder_can_see()
    {
        var points = new float[] { 30, 45, 60, 80, 100 }
            .Select(f => (f, SimulatedPc.CpuResistance(f, 40)))
            .ToList();

        var knee = KneeFinder.Find(points);

        Assert.NotNull(knee.KneePercent);
        Assert.False(knee.StillImprovingAtMax);
        Assert.InRange(knee.KneePercent!.Value, 45, 80);
    }

    [Fact]
    public void Dispose_hands_every_changed_fan_back_to_default()
    {
        var (pc, _) = Create();
        var cpuFan = pc.Channels[0];
        var gpuFan = pc.Channels[3];
        float cpuDefault = cpuFan.Percent!.Value, gpuDefault = gpuFan.Percent!.Value;

        pc.SetPercent(cpuFan, 90);
        pc.SetPercent(gpuFan, 35);
        Assert.True(cpuFan.IsSoftwareControlled);

        pc.Dispose();

        Assert.False(cpuFan.IsSoftwareControlled);
        Assert.False(gpuFan.IsSoftwareControlled);
        Assert.Equal(cpuDefault, cpuFan.Percent);
        Assert.Equal(gpuDefault, gpuFan.Percent);
    }

    [Fact]
    public void Set_percent_is_clamped_to_the_channel_range()
    {
        var (pc, _) = Create();

        Assert.Equal(100, pc.SetPercent(pc.Channels[0], 150));
        Assert.Equal(0, pc.SetPercent(pc.Channels[0], -5));
    }

    [Fact]
    public void Reading_after_dispose_fails_loudly()
    {
        var (pc, _) = Create();
        pc.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pc.Read());
    }
}
