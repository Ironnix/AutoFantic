using AutoFantic.Core.Calibration;

namespace AutoFantic.Core.Tests;

public class CurveShapeTests
{
    private static double MaxSlope(IReadOnlyList<CurvePoint> curve) =>
        curve.Zip(curve.Skip(1), (a, b) => (b.Percent - a.Percent) / (b.Temperature - a.Temperature)).DefaultIfEmpty(0).Max();

    [Fact]
    public void A_steep_stretch_starts_rising_earlier_instead()
    {
        // ralf-pc's GPU curve: 40 → 75 % between 75 and 78 °C (almost 12 % per °C)
        IReadOnlyList<CurvePoint> steep = [new(45, 30), new(75, 40), new(78, 75)];

        var smooth = CalibrationResult.LimitSteepness(steep, 5);

        Assert.InRange(MaxSlope(smooth), 0, 5.05);
        Assert.Equal(new CurvePoint(45, 30), smooth[0]);   // idle stays as quiet as it was
        Assert.Equal(new CurvePoint(78, 75), smooth[^1]);  // the top is the same
        // never slower than planned, at any temperature
        for (double t = 40; t <= 80; t += 0.5)
            Assert.True(CalibrationResult.Interpolate(smooth, t) >= CalibrationResult.Interpolate(steep, t) - 0.1, $"slower at {t} °C");
        // and no louder than needed: it bends away from the old curve where the 5 %/°C line meets it (about 70.7 °C)
        Assert.Contains(smooth, p => Math.Abs(p.Temperature - 70.7) < 0.1);
        Assert.Equal(3, smooth.Count);
    }

    [Fact]
    public void A_curve_that_is_already_gentle_stays_as_it_is()
    {
        IReadOnlyList<CurvePoint> gentle = [new(40, 30), new(60, 45), new(75, 70)];

        Assert.Equal(gentle, CalibrationResult.LimitSteepness(gentle, 5));
    }

    [Fact]
    public void The_final_ramp_to_full_speed_may_stay_steep()
    {
        // Silent keeps the CPU fans slow up to 85 °C: smoothing the safety ramp above would make them loud long before
        var table = new List<LoadRow>
        {
            Row("idle", 30, 50),
            Row("medium", 35, 72),
            Row("high", 40, 85),
        };

        var curve = CalibrationResult.CurveFor(table, 0, Component.Cpu, fullSpeedAt: 89, CalibrationResult.MaxSlope);

        Assert.Equal(new CurvePoint(85, 40), curve[^2]);
        Assert.Equal(new CurvePoint(89, 100), curve[^1]);
    }

    [Fact]
    public void A_curve_following_the_warmer_part_takes_the_warmer_temperature_of_each_load_level()
    {
        var table = new List<LoadRow>
        {
            Row("idle", 30, cpu: 45, gpu: 40),
            Row("game", 50, cpu: 55, gpu: 76), // a GPU-heavy game: the GPU is the warmer one
        };

        var curve = CalibrationResult.CurveFor(table, 0, Component.Warmest, fullSpeedAt: 89);

        Assert.Equal(new CurvePoint(45, 30), curve[0]);
        Assert.Equal(new CurvePoint(76, 50), curve[1]);
    }

    [Fact]
    public void Case_fans_follow_the_warmer_part_and_the_cpu_cooler_the_cpu()
    {
        var mainboard = new FanGroup("#5", [new FanHeader(5, "/lpc/x/control/5", "System Fan #4", "Nuvoton", "/lpc/x/fan/5", [new(100, 1500)], IsPump: false)]);
        var gpu = new FanGroup("GPU", [new FanHeader(8, "/gpu-nvidia/0/control/1", "GPU Fan 1", "RTX 3080", "/gpu-nvidia/0/fan/1", [new(100, 2875)], IsPump: false)]);

        // ralf-pc: case fans about 11 °C on the CPU and 5 °C on the GPU; CPU fan 6 °C on the CPU only
        Assert.Equal(Component.Warmest, CalibrationCalculator.Follows(mainboard, cpuEffect: 11, gpuEffect: 5));
        Assert.Equal(Component.Cpu, CalibrationCalculator.Follows(mainboard, cpuEffect: 6, gpuEffect: 0.3));
        Assert.Equal(Component.Cpu, CalibrationCalculator.Follows(mainboard, cpuEffect: 20, gpuEffect: 3)); // helps the GPU only a little
        Assert.Equal(Component.GpuCore, CalibrationCalculator.Follows(gpu, cpuEffect: 0, gpuEffect: 39));
    }

    private static LoadRow Row(string label, double speed, double cpu, double gpu = 40) =>
        new(label, 50, 100, [speed], new Dictionary<Component, double> { [Component.Cpu] = cpu, [Component.GpuCore] = gpu }, 20, true);
}
