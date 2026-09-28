namespace AutoFantic.Core.Analysis;

/// <summary>
/// Decides when a temperature has settled under a stable load: over the whole window the
/// temperature trend is below <see cref="MaxSlopePerMinute"/> and the power stays within
/// <see cref="MaxPowerDeviation"/> of its mean. Waiting for this instead of a fixed timer
/// is what makes measurements comparable (slow fans settle slowly, fast fans quickly).
/// </summary>
public sealed class SteadyStateDetector(
    TimeSpan window,
    double maxSlopePerMinute = 0.2,
    double maxPowerDeviation = 0.07)
{
    private const int MinSamples = 10;

    // At low power (idle) a relative check is too strict: a few watts of jitter are always allowed.
    private const double MinAllowedSwingWatts = 3;

    // Oldest first. Trimmed so that exactly one sample sits at or beyond the window's start:
    // then the samples always span the whole window, whatever the sampling interval.
    private readonly List<(DateTimeOffset Time, double Temp, double Power)> _samples = [];

    public TimeSpan Window { get; } = window;

    public double MaxSlopePerMinute { get; } = maxSlopePerMinute;

    public double MaxPowerDeviation { get; } = maxPowerDeviation;

    public void Add(DateTimeOffset time, double temperature, double power)
    {
        _samples.Add((time, temperature, power));
        while (_samples.Count > 1 && time - _samples[1].Time >= Window)
            _samples.RemoveAt(0);
    }

    public void Reset() => _samples.Clear();

    /// <summary>True once the window is full and both conditions hold.</summary>
    public bool IsSteady =>
        IsWindowFull
        && SlopePerMinute is { } slope && Math.Abs(slope) <= MaxSlopePerMinute
        && PowerIsStable;

    public bool IsWindowFull =>
        _samples.Count >= MinSamples
        && _samples[^1].Time - _samples[0].Time >= Window;

    public double? MeanTemperature => _samples.Count == 0 ? null : _samples.Average(s => s.Temp);

    public double? MeanPower => _samples.Count == 0 ? null : _samples.Average(s => s.Power);

    /// <summary>Least-squares temperature trend in °C per minute.</summary>
    public double? SlopePerMinute
    {
        get
        {
            if (_samples.Count < 2)
                return null;

            var t0 = _samples[0].Time;
            double n = _samples.Count;
            double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
            foreach (var (time, temp, _) in _samples)
            {
                double x = (time - t0).TotalSeconds;
                sumX += x;
                sumY += temp;
                sumXY += x * temp;
                sumXX += x * x;
            }

            double denominator = n * sumXX - sumX * sumX;
            if (denominator == 0)
                return null;

            double perSecond = (n * sumXY - sumX * sumY) / denominator;
            return perSecond * 60;
        }
    }

    private bool PowerIsStable
    {
        get
        {
            double mean = MeanPower ?? 0;
            double maxSwing = _samples.Max(s => Math.Abs(s.Power - mean));
            return maxSwing <= Math.Max(mean * MaxPowerDeviation, MinAllowedSwingWatts);
        }
    }
}
