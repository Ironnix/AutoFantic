using AutoFantic.Core.Analysis;
using AutoFantic.Core.Hardware;
using AutoFantic.Core.Logging;

namespace AutoFantic.Core.Tests;

public class LearningOpportunitiesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);

    private sealed record Phase(double Minutes, string? Foreground = "Game", double GpuWatts = 280, double GpuLoad = 97, double GpuTemp = 65);

    private static readonly Phase Desktop = new(0, "explorer", GpuWatts: 20, GpuLoad: 3, GpuTemp: 35);

    /// <summary>A 1 Hz log made of phases; CPU at a steady 110 W / 35 % / 65 °C throughout.</summary>
    private static List<LoggedSample> Log(params Phase[] phases)
    {
        var log = new List<LoggedSample>();
        var t = T0;
        foreach (var phase in phases)
        {
            for (int i = 0; i < phase.Minutes * 60; i++, t = t.AddSeconds(1))
            {
                bool idle = phase.GpuLoad < 10;
                log.Add(new LoggedSample(new Snapshot(t,
                [
                    new("c/t", "CPU", "Cpu", SensorKind.Temperature, "Core (Tctl/Tdie)", 65),
                    new("c/p", "CPU", "Cpu", SensorKind.Power, "Package", idle ? 25 : 110),
                    new("c/l", "CPU", "Cpu", SensorKind.Load, "CPU Total", idle ? 5 : 35),
                    new("g/t", "GPU", "GpuNvidia", SensorKind.Temperature, "GPU Core", (float)phase.GpuTemp),
                    new("g/p", "GPU", "GpuNvidia", SensorKind.Power, "GPU Package", (float)phase.GpuWatts),
                    new("g/l", "GPU", "GpuNvidia", SensorKind.Load, "GPU Core", (float)phase.GpuLoad),
                ]), phase.Foreground));
            }
        }
        return log;
    }

    [Fact]
    public void Steady_gaming_allows_experiments_with_cooldown_in_between()
    {
        // start after 2 min stable, 6 min each, 10 min cooldown: 2:00–8:00 and 18:00–24:00
        var report = LearningOpportunities.Analyze(Log(new Phase(30)));

        Assert.Equal(2, report.Completed);
        Assert.All(report.Attempts, a => Assert.True(a.Completed));
        Assert.Equal(T0.AddMinutes(2), report.Attempts[0].Start);
        Assert.Equal(T0.AddMinutes(18), report.Attempts[1].Start);
        Assert.Equal(LoadClass.GpuHeavy, report.Attempts[0].Class);
        Assert.Equal("Game", report.Attempts[0].Foreground);
    }

    [Fact]
    public void Leaving_the_game_discards_the_running_experiment()
    {
        var report = LearningOpportunities.Analyze(Log(new Phase(5), Desktop with { Minutes = 5 }));

        var attempt = Assert.Single(report.Attempts);
        Assert.False(attempt.Completed);
        Assert.Equal("program in the foreground changed", attempt.AbortReason);
    }

    [Fact]
    public void A_power_jump_discards_the_running_experiment()
    {
        var report = LearningOpportunities.Analyze(Log(new Phase(4), new Phase(3, GpuWatts: 340)));

        Assert.Equal("power jumped", report.Attempts[0].AbortReason);
    }

    [Fact]
    public void Unsteady_power_never_starts_an_experiment()
    {
        var phases = Enumerable.Range(0, 20)
            .Select(i => new Phase(0.5, GpuWatts: i % 2 == 0 ? 250 : 300))
            .ToArray();

        var report = LearningOpportunities.Analyze(Log(phases));

        Assert.Empty(report.Attempts);
        Assert.True(report.Blocked[Blocker.LoadNotStable] > TimeSpan.FromMinutes(9));
    }

    [Fact]
    public void Close_to_the_profile_target_only_faster_fans_are_tried()
    {
        var warm = Log(new Phase(15, GpuTemp: 75));

        var max80 = LearningOpportunities.Analyze(warm);
        var max90 = LearningOpportunities.Analyze(warm, new ExperimentRules { ProfileLimit = 90 });

        Assert.NotEmpty(max80.Attempts);
        Assert.All(max80.Attempts, a => Assert.Equal(ExperimentDirection.Faster, a.Direction));
        Assert.All(max80.Attempts, a => Assert.True(a.Completed));
        Assert.NotEmpty(max90.Attempts);
        Assert.All(max90.Attempts, a => Assert.Equal(ExperimentDirection.Slower, a.Direction));
        Assert.Equal(75, max80.UnderLoad.GpuCore);
    }

    [Fact]
    public void Near_a_safety_limit_nothing_is_tried()
    {
        var report = LearningOpportunities.Analyze(Log(new Phase(15, GpuTemp: 82))); // GPU core limit 85

        Assert.Empty(report.Attempts);
        Assert.True(report.Blocked[Blocker.AtSafetyLimit] > TimeSpan.FromMinutes(12));
    }

    [Fact]
    public void A_slower_experiment_is_discarded_when_it_gets_close_to_the_target()
    {
        var report = LearningOpportunities.Analyze(Log(new Phase(4, GpuTemp: 65), new Phase(4, GpuTemp: 76)));

        var attempt = report.Attempts[0];
        Assert.Equal(ExperimentDirection.Slower, attempt.Direction);
        Assert.Equal("temperature came close to a limit", attempt.AbortReason);
    }

    [Fact]
    public void Idle_time_is_never_used_for_experiments()
    {
        var report = LearningOpportunities.Analyze(Log(Desktop with { Minutes = 20 }));

        Assert.Empty(report.Attempts);
        Assert.Equal(TimeSpan.Zero, report.ActiveTime);
        Assert.True(report.TimeByClass[LoadClass.Idle] > TimeSpan.FromMinutes(19));
    }

    [Fact]
    public void Gaps_in_the_log_are_not_counted_and_break_stability()
    {
        var before = Log(new Phase(1.5));
        var after = Log(new Phase(1.5))
            .Select(s => s with { Snapshot = s.Snapshot with { Time = s.Snapshot.Time.AddHours(1) } });

        var report = LearningOpportunities.Analyze(before.Concat(after));

        Assert.InRange(report.Total.TotalMinutes, 2.9, 3.1);
        Assert.Empty(report.Attempts); // 1.5 min + 1.5 min is not 2 min of stable load
    }
}
