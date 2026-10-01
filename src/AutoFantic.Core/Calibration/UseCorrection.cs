using System.Text.Json;

namespace AutoFantic.Core.Calibration;

/// <summary>
/// How much warmer (or cooler) one part ran in everyday use than the calibrated model expects, by
/// its power: a straight line through what was seen between <paramref name="From"/> and
/// <paramref name="To"/> watts. Below that range it stays as at <paramref name="From"/>. Above it,
/// it fades towards <paramref name="Calibrated"/>, the highest power a calibration measured at:
/// there the model is the measurement, unless everyday use came close to that load itself (then
/// what it showed there stays, also for anything heavier).
/// </summary>
/// <param name="Offset">°C at 0 W on the line …</param>
/// <param name="PerWatt">… and how it changes per watt.</param>
public sealed record PartUse(double Offset, double PerWatt, double From, double To, double Calibrated)
{
    /// <summary>More than this isn't a matter for the curves (a wrong room temperature, a sensor, a changed PC): it's capped.</summary>
    public const double Most = 10;

    /// <summary>°C to add to the model's temperature at this power.</summary>
    public double Extra(double power)
    {
        double atEnd = Offset + PerWatt * To;
        // how much of it stays at the calibrations' load: nothing if use only reached half of that load, all of it from 90 %
        double kept = To >= Calibrated ? 1 : Math.Clamp((To / Calibrated - 0.5) / 0.4, 0, 1);
        double extra = power <= To ? Offset + PerWatt * Math.Max(power, From)
            : power < Calibrated ? atEnd * (1 - (1 - kept) * (power - To) / (Calibrated - To))
            : atEnd * kept;
        return Math.Clamp(extra, -Most, Most);
    }
}

/// <summary>
/// What everyday use showed against the calibrations (see Monitoring.UseLearning): kept in use.json
/// and counted in whenever the curves are worked out, on top of the calibrated model. It belongs to
/// one calibration: after calibrating again it no longer counts, the new measurements do.
/// </summary>
/// <param name="Found">When the history was analysed.</param>
/// <param name="Calibration">The latest calibration it was told against.</param>
/// <param name="Minutes">The settled minutes of history it rests on, and on how many <paramref name="Days"/>.</param>
/// <param name="Cpu">How the CPU really ran against the model; null = as calibrated (within what the minutes can tell).</param>
/// <param name="Gpu">The same for the graphics card (its core; hotspot and memory go with it).</param>
/// <param name="TopCpu">The highest CPU power the curves cover, in W: the calibrations', or everyday use if that was clearly heavier.</param>
public sealed record UseCorrection(
    DateTimeOffset Found,
    DateTimeOffset Calibration,
    int Minutes,
    int Days,
    PartUse? Cpu,
    PartUse? Gpu,
    double TopCpu,
    double TopGpu)
{
    public const string FileName = "use.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>°C to add to the calibrated model's temperature of this part at these powers.</summary>
    public double Extra(Component component, double cpuPower, double gpuPower) =>
        ThermalModel.IsCpu(component) ? Cpu?.Extra(cpuPower) ?? 0 : Gpu?.Extra(gpuPower) ?? 0;

    /// <summary>True while these are still the calibrations it was told against (none added since).</summary>
    public bool Fits(MeasurementStore store) =>
        store.Calibrations.Count > 0 && store.Calibrations.Max(c => c.Time).ToUnixTimeSeconds() == Calibration.ToUnixTimeSeconds();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static UseCorrection? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UseCorrection>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
