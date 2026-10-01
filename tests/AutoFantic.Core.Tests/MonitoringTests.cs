using AutoFantic.Core.Calibration;
using AutoFantic.Core.Control;
using AutoFantic.Core.Hardware;
using AutoFantic.Core.Monitoring;
using AutoFantic.Core.Simulation;

namespace AutoFantic.Core.Tests;

public sealed class MonitoringTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Series Cpu = HistoryRecorder.CpuTemp;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "autofantic-history-" + Guid.NewGuid().ToString("N"));

    public MonitoringTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Keeps_the_average_and_the_extremes_of_every_stretch()
    {
        using var store = new HistoryStore(null);
        // 10 minutes at 50 °C with one 1-second spike to 90 °C
        for (int s = 0; s < 600; s++)
            store.Add(T0.AddSeconds(s), [(Cpu, s == 300 ? 90.0 : 50.0)]);

        var fine = store.Query(Cpu.Key, T0, T0.AddMinutes(10));
        Assert.InRange(fine.Count, 118, 121);                          // 5-second points
        Assert.Equal(90, fine.Max(p => p.Max));                        // the spike is kept …
        Assert.Equal(50 + 40 / 5.0, fine.Single(p => p.Max == 90).Avg, precision: 6); // … and averaged into its stretch

        var coarse = store.Query(Cpu.Key, T0, T0.AddMinutes(10), maxPoints: 10);
        Assert.InRange(coarse.Count, 9, 11);                           // merged to about 10 points
        Assert.Equal(90, coarse.Max(p => p.Max));
    }

    [Fact]
    public void Long_ranges_come_from_the_coarser_resolutions_and_old_points_are_deleted()
    {
        using var store = new HistoryStore(null);
        // one sample a minute for 5 days: the 5-second points keep only the last 2 days
        for (int m = 0; m < 5 * 24 * 60; m++)
            store.Add(T0.AddMinutes(m), [(Cpu, 40 + m % 20)]);
        var end = T0.AddDays(5);

        var week = store.Query(Cpu.Key, end.AddDays(-7), end, maxPoints: 1000);
        Assert.True(week[0].Time <= T0.AddHours(1), "the whole 5 days are there");

        var fine = store.Query(Cpu.Key, end.AddHours(-1), end);
        Assert.InRange(fine.Count, 55, 62);                             // the last hour at full detail
        Assert.Equal(40 + 19, fine.Max(p => p.Max));
    }

    [Fact]
    public void The_history_survives_a_restart_and_a_renamed_fan_keeps_its_history()
    {
        string path = Path.Combine(_folder, HistoryStore.FileName);
        var fan = new Series("fan.x.rpm", "Case fans", SeriesKind.FanRpm);
        using (var store = new HistoryStore(path))
        {
            for (int s = 0; s < 30; s++)
                store.Add(T0.AddSeconds(s), [(Cpu, 55), (fan, 900)]);
        }

        using var again = new HistoryStore(path);
        Assert.Equal([Cpu.Key, fan.Key], again.AllSeries().Select(s => s.Key));
        Assert.Equal(55, again.Query(Cpu.Key, T0, T0.AddMinutes(1)).Average(p => p.Avg), precision: 6);

        again.Add(T0.AddSeconds(40), [(fan with { Name = "Front fans" }, 950)]);
        Assert.Equal("Front fans", again.AllSeries().Single(s => s.Key == fan.Key).Name);
        Assert.Equal(7, again.Query(fan.Key, T0, T0.AddMinutes(1)).Count);
    }

    [Fact]
    public void Fans_put_together_carry_on_from_the_history_of_each()
    {
        using var store = new HistoryStore(null);
        var first = new Series("fan.c0.rpm", "CPU Fan 1 (#0)", SeriesKind.FanRpm);
        var second = new Series("fan.c1.rpm", "CPU Fan 2 (#1)", SeriesKind.FanRpm);
        for (int s = 0; s < 600; s++)
            store.Add(T0.AddSeconds(s), [(first, 500), (second, 700)]);

        var both = new Series("fan.c0+c1.rpm", "CPU Fan 1 + 2 (#0, #1)", SeriesKind.FanRpm);
        store.Carry([first.Key, second.Key, "fan.never-recorded.rpm"], both);

        var carried = store.Query(both.Key, T0, T0.AddMinutes(10));
        Assert.InRange(carried.Count, 118, 121);
        Assert.All(carried, p => Assert.Equal((600, 500, 700), (p.Avg, p.Min, p.Max)));

        // what the fans did together since is theirs: taken apart, each carries on from it, and keeps its own past
        for (int s = 600; s < 660; s++)
            store.Add(T0.AddSeconds(s), [(both, 900)]);
        store.Carry([both.Key], second);

        var apart = store.Query(second.Key, T0, T0.AddMinutes(11));
        Assert.Equal(700, apart[0].Avg);
        Assert.Equal(900, apart[^1].Avg);
    }

    [Fact]
    public void A_warning_comes_once_after_its_seconds_and_again_only_after_it_was_clearly_back()
    {
        var watch = new WarningWatch { Settings = new WarningSettings(false, [new WarningRule(Cpu.Key, Above: true, Limit: 85, Seconds: 10)]) };
        var t = T0;
        IReadOnlyList<string> Step(double cpu)
        {
            t = t.AddSeconds(1);
            return watch.Check(t, new Dictionary<string, double> { [Cpu.Key] = cpu }, [], _ => Cpu);
        }

        Assert.Empty(Step(90));                                      // a spike: not yet
        for (int i = 0; i < 9; i++)
            Assert.Empty(Step(88));
        Assert.Contains("CPU above 85 °C for 10 s (now 88 °C)", Assert.Single(Step(88)));
        Assert.Empty(Step(88));                                      // once
        Assert.Empty(Step(84));                                      // wobbling just below …
        for (int i = 0; i < 12; i++)
            Assert.Empty(Step(87));                                  // … and above again: no second warning
        Assert.Empty(Step(80));                                      // clearly back: armed again
        for (int i = 0; i < 10; i++)
            Assert.Empty(Step(88));
        Assert.Single(Step(88));                                     // 10 s past the limit again
    }

    [Fact]
    public void The_last_7_days_in_a_few_numbers()
    {
        using var store = new HistoryStore(null);
        var now = T0.AddDays(15);
        // 15 days, a value every 10 minutes: the GPU at 50 °C, once 78 °C in the last 7 days, once 85 °C before them
        for (int m = 0; m < 15 * 24 * 60; m += 10)
        {
            double gpu = m == 12 * 24 * 60 ? 78 : m == 5 * 24 * 60 ? 85 : 50;
            store.Add(T0.AddMinutes(m), [(HistoryRecorder.GpuTemp, gpu), (HistoryRecorder.GpuHotspot, gpu + 10), (Cpu, 60)]);
        }
        store.AddSession("VALORANT-Win64-Shipping", now.AddDays(-2), now.AddDays(-2).AddHours(2), "Balanced");
        store.AddSession("cs2", now.AddDays(-1), now.AddDays(-1).AddMinutes(30), "Balanced");
        store.AddSession("cs2", now.AddDays(-10), now.AddDays(-10).AddHours(1), "Balanced");

        var week = WeekSummary.Of(store, now);

        Assert.Equal(TimeSpan.FromMinutes(150), week.Played);
        Assert.Equal(2, week.Sessions);
        Assert.Equal("VALORANT", week.Mostly);
        Assert.Equal(TimeSpan.FromHours(1), week.PlayedBefore);
        Assert.Equal((78, 88, 60), (week.GpuMax, week.HotspotMax, week.CpuMax));

        // AuFantic installed three days ago: there is no week before to compare with
        using var fresh = new HistoryStore(null);
        fresh.Add(now.AddDays(-3), [(Cpu, 55)]);
        fresh.Add(now.AddDays(-3).AddMinutes(1), [(Cpu, 55)]);
        var first = WeekSummary.Of(fresh, now);

        Assert.Equal((0, null, null), (first.Sessions, first.Mostly, first.PlayedBefore));
        Assert.Equal((null, 55), (first.GpuMax, first.CpuMax));
    }

    [Fact]
    public void A_fan_that_stands_still_although_it_should_turn_is_reported()
    {
        var watch = new WarningWatch();
        var t = T0;
        IReadOnlyList<string> Step(double? percent, double rpm)
        {
            t = t.AddSeconds(1);
            return watch.Check(t, new Dictionary<string, double>(), [new FanSample("Case fans (#5)", percent, rpm)], _ => null);
        }

        for (int i = 0; i < 30; i++)
            Assert.Empty(Step(0, 0));                                // off on purpose: fine
        for (int i = 0; i < 30; i++)
            Assert.Empty(Step(null, 0));                             // the BIOS has it: not AuFantic's business
        for (int i = 0; i < 15; i++)
            Assert.Empty(Step(40, 0));
        Assert.Contains("Case fans (#5) stands still", Assert.Single(Step(40, 0)));
    }

    [Fact]
    public void A_focused_read_returns_only_the_sensors_asked_for()
    {
        using var pc = new SimulatedPc();
        var keys = KeySensors.Detect(pc.Read());
        var only = new HashSet<string> { keys.CpuTemp!, "/sim/superio/fan/1" };

        var snapshot = pc.Read(only);

        Assert.Equal(only.Order(), snapshot.Readings.Select(r => r.Id).Order());
        Assert.True(pc.Read().Readings.Count > 10);
    }

    [Fact]
    public void The_recorder_keeps_temperatures_power_and_every_fans_speed_and_rpm()
    {
        var clock = T0;
        using var pc = new SimulatedPc(load: _ => SimLoad.Game, clock: () => clock);
        var inventory = new FanInventory(T0,
        [
            new FanHeader(1, "/sim/superio/control/1", "Case Fans", "Simulated Super I/O", "/sim/superio/fan/1", [new(40, 560), new(100, 1400)], IsPump: false),
        ]);
        var group = inventory.Groups()[0];
        var calibration = new CalibrationResult(T0, "Balanced", 22,
            [new CalibratedGroup(group.Name, [1], ["/sim/superio/control/1"], Component.Warmest, 2, 4, [new(30, 60), new(90, 60)], [])],
            [], new Dictionary<Component, double[]>(), 0, 0);
        using var loop = new FanControlLoop(pc, calibration, [30]);
        using var store = new HistoryStore(null);
        var recorder = new HistoryRecorder(store, loop.Keys);
        recorder.UseFans(inventory.Groups());
        loop.Watch(recorder.SensorIds);
        loop.Sampled += recorder.Record;

        for (int s = 0; s < 20; s++)
        {
            clock = clock.AddSeconds(1);
            loop.Tick();
        }

        var percent = HistoryRecorder.FanSeries(group, SeriesKind.FanPercent);
        var rpm = HistoryRecorder.FanSeries(group, SeriesKind.FanRpm);
        Assert.Equal(60, store.Query(percent.Key, T0, clock).Last().Avg, precision: 3);
        Assert.Equal(14 * 60, store.Query(rpm.Key, T0, clock).Last().Avg, precision: 0);
        Assert.InRange(store.Query(HistoryRecorder.CpuPower.Key, T0, clock).Last().Avg, 100, 140);
        Assert.Contains(store.AllSeries(), s => s.Key == HistoryRecorder.GpuHotspot.Key);
    }
}
