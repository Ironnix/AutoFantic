using AutoFantic.Core.Calibration;

namespace AutoFantic.Core.Tests;

public class CurveOverridesTests
{
    private static readonly CalibrationResult Calibration = new(
        DateTimeOffset.Now, "Max 80", 22,
        [
            new CalibratedGroup("CPU Fan (#0)", [0], ["c0"], Component.Cpu, 9, 0, [new(50, 30), new(82, 100)], ["idle"]),
            new CalibratedGroup("GPU fans (#8, #9)", [8, 9], ["g1", "g2"], Component.GpuCore, 0, 30, [new(55, 40), new(83, 100)], []),
        ],
        [], new Dictionary<Component, double[]>(), 60, 170);

    [Fact]
    public void A_curve_set_by_hand_replaces_the_calibrated_one()
    {
        var overrides = CurveOverrides.None.With(Calibration.Groups[0], new CurveOverride([new(40, 20), new(70, 60), new(85, 100)], AllowOff: true));

        var applied = overrides.ApplyTo(Calibration);

        Assert.Equal([new CurvePoint(40, 20), new CurvePoint(70, 60), new CurvePoint(85, 100)], applied.Groups[0].Curve);
        Assert.Equal(["idle"], applied.Groups[0].OffAt);
        Assert.Equal(Calibration.Groups[1].Curve, applied.Groups[1].Curve); // the other fan keeps its curve
    }

    [Fact]
    public void Switching_stop_off_keeps_the_fan_turning_at_idle()
    {
        var overrides = CurveOverrides.None.With(Calibration.Groups[0], new CurveOverride(Calibration.Groups[0].Curve, AllowOff: false));

        Assert.Empty(overrides.ApplyTo(Calibration).Groups[0].OffAt);
    }

    [Fact]
    public void Stopping_can_never_be_switched_on_where_the_calibration_found_it_unsafe()
    {
        var overrides = CurveOverrides.None.With(Calibration.Groups[1], new CurveOverride(Calibration.Groups[1].Curve, AllowOff: true));

        Assert.Empty(overrides.ApplyTo(Calibration).Groups[1].OffAt);
    }

    [Fact]
    public void Curves_are_tidied_up_sorted_rising_and_within_range()
    {
        var messy = new CurveOverride([new(80, 50), new(40, 70), new(60, 130), new(60, -5)], AllowOff: false);

        var tidy = CurveOverrides.Normalized(messy).Curve;

        Assert.Equal([40.0, 60, 61, 80], tidy.Select(p => p.Temperature));
        Assert.Equal([70.0, 100, 100, 100], tidy.Select(p => p.Percent)); // 130 % capped; after that it never falls
    }

    [Fact]
    public void Reset_removes_the_hand_made_curve_and_survives_a_round_trip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"curves-{Guid.NewGuid():N}.json");
        try
        {
            var overrides = CurveOverrides.None
                .With(Calibration.Groups[0], new CurveOverride([new(45, 25), new(85, 100)], true))
                .With(Calibration.Groups[1], new CurveOverride([new(50, 35), new(83, 100)], false));
            overrides.Save(path);
            var loaded = CurveOverrides.Load(path).Without(Calibration.Groups[0]);

            Assert.Null(loaded.For(Calibration.Groups[0]));
            Assert.Equal([new CurvePoint(50, 35), new CurvePoint(83, 100)], loaded.For(Calibration.Groups[1])!.Curve);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
