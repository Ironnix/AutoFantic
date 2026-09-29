using AutoFantic.Core.Monitoring;

namespace AutoFantic.Core.Tests;

public class FanWearTests
{
    private static readonly DateOnly Start = new(2026, 9, 1);

    // one steady hour a day at 40 % and one at 60 %; after the first week the fan loses 0.5 % of its speed a day
    private static List<FanDay> Aging(int days, double lossPerDay = 0.5, int from = 0) =>
        [.. Enumerable.Range(from, days).Select(i =>
        {
            double factor = 1 - Math.Max(0, i - 7) * lossPerDay / 100;
            return new FanDay(Start.AddDays(i), "fan", new Dictionary<int, FanStep> { [40] = new(800 * factor, 60), [60] = new(1200 * factor, 60) });
        })];

    [Fact]
    public void A_fan_that_gets_slower_is_told_against_its_first_week()
    {
        var days = Aging(40);

        var now = FanWear.Now(days, DateOnly.MinValue, Start.AddDays(39));

        Assert.False(now.Learning);
        Assert.InRange(now.Change!.Value, -15.5, -14.5); // the last week: days 33..39, 26..32 days of 0.5 % → about −14.75 %
        Assert.Equal(7 * 120, now.Minutes);
        var daily = FanWear.Daily(days, DateOnly.MinValue);
        Assert.Equal(0, daily[0].Change, precision: 3);
        Assert.True(daily[^1].Change < daily[20].Change);
    }

    [Fact]
    public void The_first_week_is_learned_before_anything_is_compared()
    {
        var now = FanWear.Now(Aging(3), DateOnly.MinValue, Start.AddDays(3));

        Assert.True(now.Learning);
        Assert.Equal(3, now.FirstWeekDays);
        Assert.Null(now.Change);
        Assert.Equal(new FanWearResult(null, 0, 0), FanWear.Now([], DateOnly.MinValue, Start)); // never ran
    }

    [Fact]
    public void Start_again_after_cleaning_gives_the_fan_a_new_first_week()
    {
        // worn for 40 days, then cleaned: from day 40 on it turns steadily at its (lower) new speed
        var worn = Aging(40);
        var cleaned = Enumerable.Range(40, 20).Select(i => new FanDay(Start.AddDays(i), "fan",
            new Dictionary<int, FanStep> { [40] = new(700, 60), [60] = new(1050, 60) })).ToList();
        var settings = new FanWearSettings().WithWarning("fan", "worn").StartAgain("fan", Start.AddDays(40));

        var now = FanWear.Now([.. worn, .. cleaned], settings.SinceFor("fan"), Start.AddDays(59));

        Assert.Equal(0, now.Change!.Value, precision: 3);
        Assert.Null(settings.Warned!.GetValueOrDefault("fan")); // it may warn again
        Assert.Equal(DateOnly.MinValue, settings.SinceFor("other fan"));
    }

    [Fact]
    public void Only_speed_steps_the_first_week_knows_are_compared()
    {
        var firstWeek = Enumerable.Range(0, 7).Select(i => new FanDay(Start.AddDays(i), "fan", new Dictionary<int, FanStep> { [40] = new(800, 60) }));
        var later = Enumerable.Range(7, 14).Select(i => new FanDay(Start.AddDays(i), "fan", new Dictionary<int, FanStep> { [70] = new(1300, 60), [40] = new(760, 5) }));

        var now = FanWear.Now([.. firstWeek, .. later], DateOnly.MinValue, Start.AddDays(20));

        // 70 % wasn't there in the first week, and the 5 minutes a day at 40 % add up to 35 in the last week: enough
        Assert.Equal(-5, now.Change!.Value, precision: 3);
        Assert.Null(FanWear.Change(new Dictionary<int, FanStep> { [70] = new(1300, 600) }, FanWear.Combine(firstWeek)));
    }

    [Fact]
    public void Only_steady_minutes_of_a_turning_fan_count()
    {
        static HistoryPoint P(double avg, double spread = 0) => new(DateTimeOffset.UnixEpoch, avg, avg - spread / 2, avg + spread / 2);
        var steps = FanWear.Steps(
        [
            (P(41), P(810)), (P(39), P(790)),  // 40 % step
            (P(62), P(1210)),                  // 60 % step
            (P(0), P(0)),                      // off
            (P(50, spread: 10), P(1000)),      // the setting changed within the minute
            (P(50), P(1000, spread: 400)),     // speeding up
            (P(30), P(0)),                     // set but standing (0-RPM)
        ]);

        Assert.Equal([40, 60], steps.Keys.Order());
        Assert.Equal(new FanStep(800, 2), steps[40]);
        Assert.Equal(1, steps[60].Minutes);
    }

    [Fact]
    public void Days_are_stored_and_read_back_and_the_history_gives_the_steps()
    {
        using var store = new HistoryStore(null);
        var percent = new Series("fan.x.percent", "Fan", SeriesKind.FanPercent);
        var rpm = new Series("fan.x.rpm", "Fan", SeriesKind.FanRpm);
        var t0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        for (int m = 0; m < 120; m++)
            store.Add(t0.AddMinutes(m), [(percent, m < 60 ? 40 : 60), (rpm, m < 60 ? 800 : 1200)]);

        var steps = FanWear.Steps(store, percent, rpm, t0, t0.AddHours(2));
        store.SaveFanDay(new FanDay(Start, "x", steps));
        var back = store.FanDays().Single();

        Assert.Equal(800, back.Steps[40].Rpm, precision: 3);
        Assert.Equal(1200, back.Steps[60].Rpm, precision: 3);
        Assert.Equal(120, back.Minutes, tolerance: 1);
        Assert.Equal("x", back.Fan);
    }
}
