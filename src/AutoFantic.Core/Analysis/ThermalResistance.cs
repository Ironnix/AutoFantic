namespace AutoFantic.Core.Analysis;

/// <summary>
/// R = (T_component − T_ambient) / P_component in °C per watt. For a given fan mix R stays
/// roughly constant whatever the load, which makes readings from different games comparable.
/// </summary>
public static class ThermalResistance
{
    /// <summary>Below this power the ratio is dominated by noise, so no value is returned.</summary>
    public const double MinPowerWatts = 15;

    public static double? Compute(double componentTemp, double ambientTemp, double powerWatts) =>
        powerWatts < MinPowerWatts ? null : (componentTemp - ambientTemp) / powerWatts;
}
