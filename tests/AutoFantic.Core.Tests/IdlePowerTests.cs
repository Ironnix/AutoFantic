using AutoFantic.Core.Monitoring;

namespace AutoFantic.Core.Tests;

/// <summary>The graphics card's power while the PC is idle, read from the history, and what the monitors' refresh rates say about it.</summary>
public sealed class IdlePowerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    // one sample a minute, as (CPU load %, GPU load %, GPU power W); returns the time after the last one
    private static DateTimeOffset Feed(HistoryStore store, DateTimeOffset from, int minutes, double cpuLoad, double gpuLoad, double gpuPower)
    {
        for (int m = 0; m < minutes; m++)
            store.Add(from.AddMinutes(m), [(HistoryRecorder.CpuLoad, cpuLoad), (HistoryRecorder.GpuLoad, gpuLoad), (HistoryRecorder.GpuPower, gpuPower)]);
        return from.AddMinutes(minutes);
    }

    [Fact]
    public void A_card_that_rests_at_idle_gets_no_hint()
    {
        using var store = new HistoryStore(null);
        var end = Feed(store, T0, 90, cpuLoad: 3, gpuLoad: 2, gpuPower: 24);

        var found = IdlePower.Gpu(store, end);

        Assert.NotNull(found);
        Assert.Equal(24, found.Watts, precision: 3);
        Assert.False(found.High);
    }

    [Fact]
    public void A_card_that_draws_130_W_while_doing_nothing_is_noticed_and_a_game_does_not_count()
    {
        using var store = new HistoryStore(null);
        var t = Feed(store, T0, 40, cpuLoad: 2, gpuLoad: 4, gpuPower: 130); // the desktop
        t = Feed(store, t, 120, cpuLoad: 30, gpuLoad: 97, gpuPower: 320);   // a game: not idle
        t = Feed(store, t, 5, cpuLoad: 2, gpuLoad: 4, gpuPower: 128);

        var found = IdlePower.Gpu(store, t);

        Assert.NotNull(found);
        Assert.Equal(45, found.Minutes);
        Assert.Equal(130, found.Watts, precision: 3);
        Assert.True(found.High);
    }

    [Fact]
    public void Too_few_idle_minutes_give_no_answer_yet()
    {
        using var store = new HistoryStore(null);
        var t = Feed(store, T0, IdlePower.MinMinutes - 5, cpuLoad: 2, gpuLoad: 4, gpuPower: 130);
        t = Feed(store, t, 60, cpuLoad: 30, gpuLoad: 97, gpuPower: 320);

        Assert.Null(IdlePower.Gpu(store, t));
        Assert.Null(IdlePower.Gpu(new HistoryStore(null), t)); // nothing recorded at all
    }

    [Fact]
    public void After_the_monitors_were_set_right_the_hint_is_gone_within_the_hour()
    {
        using var store = new HistoryStore(null);
        var t = Feed(store, T0, 600, cpuLoad: 2, gpuLoad: 4, gpuPower: 130); // days of 130 W at idle …
        t = Feed(store, t, 40, cpuLoad: 2, gpuLoad: 2, gpuPower: 25);        // … then the same refresh rate everywhere

        var found = IdlePower.Gpu(store, t);

        Assert.NotNull(found);
        Assert.Equal(IdlePower.LatestMinutes, found.Minutes); // only the latest idle minutes count
        Assert.Equal(25, found.Watts, precision: 3);
        Assert.False(found.High);
    }

    [Fact]
    public void Idle_minutes_older_than_a_week_do_not_count()
    {
        using var store = new HistoryStore(null);
        Feed(store, T0, 60, cpuLoad: 2, gpuLoad: 4, gpuPower: 130);

        Assert.Null(IdlePower.Gpu(store, T0 + IdlePower.LookBack + TimeSpan.FromHours(2)));
    }

    [Theory]
    [InlineData(new[] { 280, 60, 60 }, IdleCause.DifferentRates)]
    [InlineData(new[] { 144, 60 }, IdleCause.DifferentRates)]
    [InlineData(new[] { 60, 59 }, IdleCause.Unknown)]      // 59.94 Hz and 60 Hz: the same rate
    [InlineData(new[] { 144, 144 }, IdleCause.Unknown)]
    [InlineData(new[] { 240 }, IdleCause.HighRate)]
    [InlineData(new[] { 165, 165 }, IdleCause.HighRate)]
    [InlineData(new[] { 60 }, IdleCause.Unknown)]
    [InlineData(new int[0], IdleCause.Unknown)]            // Windows doesn't say
    public void The_monitors_refresh_rates_say_what_the_likely_cause_is(int[] rates, IdleCause expected) =>
        Assert.Equal(expected, IdlePower.Cause(rates));
}
