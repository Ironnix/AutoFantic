using AutoFantic.Core.Analysis;

namespace AutoFantic.Core.Tests;

public class KneeFinderTests
{
    // A typical cooler: R(f) = 0.1 + 2 / f. Steep at first, then flat.
    private static readonly (float, double)[] TypicalCooler =
        new float[] { 30, 45, 60, 80, 100 }.Select(f => (f, 0.1 + 2.0 / f)).ToArray();

    [Fact]
    public void Balanced_threshold_puts_the_knee_at_80_percent()
    {
        var result = KneeFinder.Find(TypicalCooler, KneeFinder.BalancedMinGain);

        Assert.Equal(80, result.KneePercent);
        Assert.False(result.StillImprovingAtMax);
        Assert.Equal(4, result.Gains.Count);
    }

    [Fact]
    public void Quiet_threshold_puts_the_knee_lower()
    {
        var result = KneeFinder.Find(TypicalCooler, KneeFinder.QuietMinGain);

        Assert.Equal(60, result.KneePercent);
    }

    [Fact]
    public void Order_of_the_points_does_not_matter()
    {
        var shuffled = TypicalCooler.Reverse().ToArray();

        Assert.Equal(80, KneeFinder.Find(shuffled).KneePercent);
    }

    [Fact]
    public void A_curve_that_keeps_improving_has_no_knee_inside_the_range()
    {
        // straight line: every +10 % still gives ~10 %
        var linear = new (float, double)[] { (30, 1.0), (50, 0.8), (70, 0.64), (90, 0.51) };

        var result = KneeFinder.Find(linear);

        Assert.True(result.StillImprovingAtMax);
        Assert.Equal(90, result.KneePercent);
    }

    [Fact]
    public void Flat_from_the_start_means_the_lowest_speed_is_enough()
    {
        var flat = new (float, double)[] { (30, 0.20), (60, 0.199), (100, 0.198) };

        Assert.Equal(30, KneeFinder.Find(flat).KneePercent);
    }

    [Fact]
    public void One_noisy_dip_does_not_create_a_false_knee()
    {
        // 45 → 60 happens to measure almost no gain, but 60 → 80 helps a lot again
        var noisy = new (float, double)[] { (30, 0.30), (45, 0.25), (60, 0.249), (80, 0.20), (100, 0.198) };

        Assert.Equal(80, KneeFinder.Find(noisy).KneePercent);
    }

    [Fact]
    public void Fewer_than_two_points_give_no_result()
    {
        var result = KneeFinder.Find([(50f, 0.2)]);

        Assert.Null(result.KneePercent);
        Assert.Empty(result.Gains);
    }
}
