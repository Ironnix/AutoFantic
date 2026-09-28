using AutoFanatic.Core.Calibration;

namespace AutoFanatic.Core.Tests;

public class PowerResponseFitTests
{
    /// <summary>A part with a fast and a slow thermal path, driven by the given power, sampled at 1 Hz.</summary>
    private static List<(double, double, double)> Simulate(Func<int, double> power, double rFast, double rSlow, double tau,
        double startHeat, int seconds, double noise = 0.2, int seed = 7)
    {
        var random = new Random(seed);
        var samples = new List<(double, double, double)>();
        double heat = startHeat; // heatsink's rise above room, left over from the previous fan setting
        for (int t = 0; t <= seconds; t++)
        {
            double p = power(t);
            double temp = 22 + heat + rFast * p + (random.NextDouble() * 2 - 1) * noise;
            samples.Add((t, temp, p));
            heat += (rSlow * p - heat) * (1 - Math.Exp(-1 / tau));
        }
        return samples;
    }

    [Fact]
    public void A_steady_load_gives_the_settled_resistance()
    {
        var samples = Simulate(_ => 250, rFast: 0.02, rSlow: 0.16, tau: 50, startHeat: 30, seconds: 90);

        var fit = PowerResponseFit.Fit(samples, 22)!;

        Assert.InRange(fit.Resistance, 0.17, 0.19);
        Assert.True(fit.Reliable);
    }

    [Fact]
    public void A_jumpy_game_load_still_gives_the_right_resistance()
    {
        // CS2-like: rounds, buy phases and fights make the GPU power jump every few seconds
        var random = new Random(11);
        var watts = Enumerable.Range(0, 200).Select(_ => 120 + random.NextDouble() * 180).ToArray();
        var samples = Simulate(t => watts[t], rFast: 0.03, rSlow: 0.15, tau: 60, startHeat: 20, seconds: 150);

        var fit = PowerResponseFit.Fit(samples, 22)!;

        Assert.InRange(fit.Resistance, 0.17, 0.19);
        Assert.InRange(fit.Tau, 40, 85);
    }

    [Fact]
    public void Temperature_still_falling_from_the_previous_setting_is_not_mistaken_for_better_cooling()
    {
        // fans just went faster: the heatsink is still cooling down from a much warmer state
        var samples = Simulate(_ => 200, rFast: 0.02, rSlow: 0.12, tau: 45, startHeat: 50, seconds: 100);

        var fit = PowerResponseFit.Fit(samples, 22)!;

        Assert.InRange(fit.Resistance, 0.13, 0.15);
    }

    [Fact]
    public void No_load_means_no_answer()
    {
        var samples = Simulate(_ => 5, rFast: 0.02, rSlow: 0.2, tau: 40, startHeat: 2, seconds: 60);

        Assert.Null(PowerResponseFit.Fit(samples, 22));
    }
}
