using AutoFanatic.Core.Calibration;
using AutoFanatic.Core.Control;

namespace AutoFanatic.Core.Tests;

public class CurveControllerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);

    // one CPU fan that may be off at idle, one GPU fan that may not
    private static CalibrationResult Calibration() => new(
        T0, "Max 80", 22,
        [
            new CalibratedGroup("CPU Fan (#0)", [0], ["c0"], Component.Cpu, 10, 0,
                [new(45, 30), new(70, 50), new(82, 100)], ["idle"]),
            new CalibratedGroup("GPU fans (#8, #9)", [8, 9], ["g1", "g2"], Component.GpuCore, 0, 12,
                [new(50, 40), new(75, 60), new(82, 100)], []),
        ],
        [], new Dictionary<Component, double[]>(), StopCpuWatts: 50, StopGpuWatts: 150);

    private static CurveController Controller() => new(Calibration(), [30, 40]);

    private static Dictionary<Component, double> Temps(double cpu, double gpu) =>
        new() { [Component.Cpu] = cpu, [Component.GpuCore] = gpu };

    /// <summary>Runs the controller at 1 Hz with constant conditions, returns the last output.</summary>
    private static double[] Hold(CurveController c, ref DateTimeOffset t, int seconds, double cpu, double gpu, double cpuW, double gpuW)
    {
        double[] output = [];
        for (int i = 0; i < seconds; i++)
        {
            t = t.AddSeconds(1);
            output = c.Step(t, Temps(cpu, gpu), cpuW, gpuW);
        }
        return output;
    }

    [Fact]
    public void Follows_the_curve_once_settled()
    {
        var c = Controller();
        var t = T0;

        var output = Hold(c, ref t, 200, cpu: 57.5, gpu: 62.5, cpuW: 80, gpuW: 250);

        Assert.Equal(40, output[0], 1);   // halfway between 45 °C → 30 % and 70 °C → 50 %
        Assert.Equal(50, output[1], 1);   // halfway between 50 °C → 40 % and 75 °C → 60 %
    }

    [Fact]
    public void Speeds_up_fast_and_slows_down_gently()
    {
        var c = Controller();
        var t = T0;
        Hold(c, ref t, 200, cpu: 45, gpu: 50, cpuW: 80, gpuW: 250); // settled at the bottom: 30 % / 40 %

        var blip = Hold(c, ref t, 3, cpu: 81, gpu: 50, cpuW: 120, gpuW: 250);
        Assert.InRange(blip[0], 30, 40); // a short spike barely moves the fan (smoothing) …
        var up = Hold(c, ref t, 12, cpu: 81, gpu: 50, cpuW: 120, gpuW: 250);
        Assert.InRange(up[0], 65, 85);   // … a lasting one does, within seconds

        Hold(c, ref t, 120, cpu: 81, gpu: 50, cpuW: 120, gpuW: 250);
        var down = Hold(c, ref t, 10, cpu: 45, gpu: 50, cpuW: 80, gpuW: 250);
        Assert.InRange(down[0], 80, 95); // −1 %/s at most
    }

    [Fact]
    public void Switches_off_at_idle_and_back_on_with_a_kick_when_load_comes()
    {
        var c = Controller();
        var t = T0;

        var idle = Hold(c, ref t, 120, cpu: 45, gpu: 40, cpuW: 30, gpuW: 100);
        Assert.Equal(0, idle[0]);            // CPU fan off at idle …
        Assert.NotEqual(0, idle[1]);         // … the GPU fans are never allowed to stop here

        var game = Hold(c, ref t, 1, cpu: 50, gpu: 45, cpuW: 90, gpuW: 250);
        Assert.Equal(CurveController.KickPercent, game[0]); // load came: on, with a kick to get it turning

        var later = Hold(c, ref t, 60, cpu: 50, gpu: 45, cpuW: 90, gpuW: 250);
        Assert.InRange(later[0], 30, 40);
    }

    [Fact]
    public void Does_not_switch_off_while_it_is_warm_even_at_low_load()
    {
        var c = Controller();
        var t = T0;

        var output = Hold(c, ref t, 300, cpu: 58, gpu: 50, cpuW: 30, gpuW: 100);

        Assert.NotEqual(0, output[0]);
    }

    [Fact]
    public void Starts_right_at_the_curve_instead_of_roaring_first()
    {
        var output = Controller().Step(T0, Temps(57.5, 62.5), 80, 250);

        Assert.Equal(40, output[0], 1);
        Assert.Equal(50, output[1], 1);
    }

    [Fact]
    public void Above_the_last_point_the_fans_run_flat_out()
    {
        Assert.Equal(100, CurveController.Interpolate([new(45, 30), new(82, 100)], 90));
        Assert.Equal(30, CurveController.Interpolate([new(45, 30), new(82, 100)], 20));
    }
}
