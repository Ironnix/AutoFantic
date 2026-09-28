using AutoFanatic.Core.Analysis;

namespace AutoFanatic.Core.Tests;

public class SteadyStateDetectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static SteadyStateDetector Feed(Func<int, (double Temp, double Power)> sample, int seconds = 70)
    {
        var detector = new SteadyStateDetector(TimeSpan.FromSeconds(60));
        for (int s = 0; s <= seconds; s++)
        {
            var (temp, power) = sample(s);
            detector.Add(Start.AddSeconds(s), temp, power);
        }
        return detector;
    }

    [Fact]
    public void Flat_temperature_under_constant_power_is_steady()
    {
        var detector = Feed(s => (70 + (s % 2 == 0 ? 0.2 : -0.2), 250));

        Assert.True(detector.IsSteady);
        Assert.InRange(detector.SlopePerMinute!.Value, -0.1, 0.1);
    }

    [Fact]
    public void Rising_temperature_is_not_steady()
    {
        // +1 °C per minute, well above the 0.2 °C/min threshold
        var detector = Feed(s => (60 + s / 60.0, 250));

        Assert.False(detector.IsSteady);
        Assert.InRange(detector.SlopePerMinute!.Value, 0.95, 1.05);
    }

    [Fact]
    public void Jumping_power_is_not_steady_even_if_temperature_is_flat()
    {
        // a loading screen: 250 W → 120 W for a few seconds
        var detector = Feed(s => (70, s is > 30 and < 36 ? 120 : 250));

        Assert.False(detector.IsSteady);
    }

    [Fact]
    public void Small_absolute_jitter_at_idle_is_allowed()
    {
        var detector = Feed(s => (40, 12 + (s % 3)));

        Assert.True(detector.IsSteady);
    }

    [Fact]
    public void Not_steady_until_the_window_is_full()
    {
        var detector = Feed(s => (70, 250), seconds: 30);

        Assert.False(detector.IsWindowFull);
        Assert.False(detector.IsSteady);
    }

    [Fact]
    public void Old_samples_leave_the_window()
    {
        // hot and rising first, then 70 s of flat: only the flat part should count
        var detector = new SteadyStateDetector(TimeSpan.FromSeconds(60));
        for (int s = 0; s < 120; s++)
            detector.Add(Start.AddSeconds(s), s < 50 ? 50 + s : 70, 250);

        Assert.True(detector.IsSteady);
        Assert.Equal(70, detector.MeanTemperature!.Value, precision: 3);
    }
}
