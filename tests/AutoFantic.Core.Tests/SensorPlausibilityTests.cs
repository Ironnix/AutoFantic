using AutoFantic.Core.Analysis;
using AutoFantic.Core.Hardware;

namespace AutoFantic.Core.Tests;

public class SensorPlausibilityTests
{
    private static readonly KeySensors Keys = new("cpu", null, "gpu", null, null, null);

    private static Snapshot With(float? cpu, float? gpu)
    {
        var readings = new List<SensorReading>();
        if (cpu.HasValue)
            readings.Add(new("cpu", "CPU", "Cpu", SensorKind.Temperature, "Core (Tctl/Tdie)", cpu));
        if (gpu.HasValue)
            readings.Add(new("gpu", "GPU", "GpuNvidia", SensorKind.Temperature, "GPU Core", gpu));
        return new Snapshot(DateTimeOffset.Now, readings);
    }

    [Fact]
    public void Normal_readings_pass()
    {
        var check = new SensorPlausibility(Keys);

        for (int i = 0; i < 10; i++)
            Assert.Null(check.Check(With(70, 65)));
    }

    [Fact]
    public void A_sensor_that_stops_reporting_is_caught_after_a_few_samples()
    {
        var check = new SensorPlausibility(Keys);
        Assert.Null(check.Check(With(70, 65)));

        Assert.Null(check.Check(With(70, null)));
        Assert.Null(check.Check(With(70, null)));
        string? error = check.Check(With(70, null));

        Assert.NotNull(error);
        Assert.Contains("GPU core", error);
        Assert.Contains("stopped reporting", error);
    }

    [Fact]
    public void A_single_failed_read_is_tolerated()
    {
        var check = new SensorPlausibility(Keys);
        Assert.Null(check.Check(With(70, 65)));

        for (int i = 0; i < 10; i++)
        {
            Assert.Null(check.Check(With(0, 65)));
            Assert.Null(check.Check(With(70, 65))); // good read in between resets the count
        }
    }

    [Fact]
    public void A_sensor_stuck_at_zero_is_caught()
    {
        var check = new SensorPlausibility(Keys);
        Assert.Null(check.Check(With(70, 65)));

        string? error = null;
        for (int i = 0; i < SensorPlausibility.ToleratedBadSamples; i++)
            error = check.Check(With(0, 65));

        Assert.NotNull(error);
        Assert.Contains("CPU", error);
    }

    [Fact]
    public void A_sensor_missing_from_the_start_is_not_watched()
    {
        var check = new SensorPlausibility(Keys);

        for (int i = 0; i < 10; i++)
            Assert.Null(check.Check(With(70, null)));
    }
}
