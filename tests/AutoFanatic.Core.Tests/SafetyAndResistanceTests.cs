using AutoFanatic.Core.Analysis;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Core.Tests;

public class SafetyAndResistanceTests
{
    private static readonly KeySensors Keys = new("cpu", null, "gpu", "hot", null, null);

    private static Snapshot With(float cpu, float gpu, float hot) => new(DateTimeOffset.Now,
    [
        new("cpu", "CPU", "Cpu", SensorKind.Temperature, "Core (Tctl/Tdie)", cpu),
        new("gpu", "GPU", "GpuNvidia", SensorKind.Temperature, "GPU Core", gpu),
        new("hot", "GPU", "GpuNvidia", SensorKind.Temperature, "GPU Hot Spot", hot),
    ]);

    [Fact]
    public void Within_limits_reports_nothing() =>
        Assert.Null(new SafetyLimits().Check(With(75, 70, 85), Keys));

    [Fact]
    public void Hotspot_over_limit_is_reported()
    {
        string? violation = new SafetyLimits().Check(With(75, 70, 101), Keys);

        Assert.NotNull(violation);
        Assert.Contains("hotspot", violation);
    }

    [Fact]
    public void Missing_sensors_are_not_a_violation() =>
        Assert.Null(new SafetyLimits().Check(new Snapshot(DateTimeOffset.Now, []), Keys));

    [Fact]
    public void Thermal_resistance_is_rise_over_ambient_per_watt() =>
        Assert.Equal(0.2, ThermalResistance.Compute(75, 25, 250)!.Value, precision: 6);

    [Fact]
    public void Thermal_resistance_is_undefined_at_idle_power() =>
        Assert.Null(ThermalResistance.Compute(40, 25, 8));
}
