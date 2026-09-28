namespace AutoFanatic.Core.Analysis;

/// <summary>One step of a fan sweep: the fan speed and the values averaged once temperatures had settled.</summary>
public sealed record SweepStep(
    float FanPercent,
    double? CpuTemp,
    double? CpuPower,
    double? GpuTemp,
    double? GpuHotspot,
    double? GpuMemory,
    double? GpuPower,
    bool Settled,
    TimeSpan Duration)
{
    public double? CpuResistance(double ambient) => Resistance(CpuTemp, CpuPower, ambient);

    public double? GpuResistance(double ambient) => Resistance(GpuTemp, GpuPower, ambient);

    public double? HotspotResistance(double ambient) => Resistance(GpuHotspot, GpuPower, ambient);

    private static double? Resistance(double? temp, double? power, double ambient) =>
        temp is { } t && power is { } p ? ThermalResistance.Compute(t, ambient, p) : null;
}

/// <summary>Improvement between two neighbouring sweep steps, scaled to "per +10 % fan speed".</summary>
public sealed record KneeGain(float FromPercent, float ToPercent, double GainPer10Percent);

/// <param name="KneePercent">Fan speed beyond which more speed stops paying off; null if there wasn't enough data.</param>
/// <param name="StillImprovingAtMax">True if even the last step up still helped noticeably, i.e. no knee inside the tested range.</param>
public sealed record KneeResult(float? KneePercent, bool StillImprovingAtMax, IReadOnlyList<KneeGain> Gains);

public static class KneeFinder
{
    /// <summary>Balanced: less than 3 % better per +10 % fan is "not worth the noise".</summary>
    public const double BalancedMinGain = 0.03;

    public const double QuietMinGain = 0.05;

    public const double PerformanceMinGain = 0.015;

    /// <summary>
    /// Finds the knee in a curve of fan speed → "how hot" (thermal resistance, or temperature
    /// rise above ambient; lower is better). The knee is the first speed from which every
    /// further step up improves by less than <paramref name="minGainPer10Percent"/>. Requiring
    /// all later steps to stay below keeps one noisy step from producing a false knee.
    /// </summary>
    public static KneeResult Find(IEnumerable<(float FanPercent, double Value)> points, double minGainPer10Percent = BalancedMinGain)
    {
        var sorted = points
            .Where(p => p.Value > 0)
            .OrderBy(p => p.FanPercent)
            .ToList();

        var gains = new List<KneeGain>();
        for (int i = 0; i + 1 < sorted.Count; i++)
        {
            var (fromFan, fromValue) = sorted[i];
            var (toFan, toValue) = sorted[i + 1];
            float span = toFan - fromFan;
            if (span <= 0)
                continue;

            double relative = (fromValue - toValue) / fromValue;
            gains.Add(new KneeGain(fromFan, toFan, relative * 10 / span));
        }

        if (gains.Count == 0)
            return new KneeResult(null, false, gains);

        for (int i = 0; i < gains.Count; i++)
        {
            if (gains.Skip(i).All(g => g.GainPer10Percent < minGainPer10Percent))
                return new KneeResult(gains[i].FromPercent, false, gains);
        }

        return new KneeResult(sorted[^1].FanPercent, true, gains);
    }
}
