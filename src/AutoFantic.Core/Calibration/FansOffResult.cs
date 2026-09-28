using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoFantic.Core.Calibration;

/// <summary>
/// What the fans-off test measured at idle: the power at the time and where each temperature was
/// heading with every fan standing still. Saved, because the test can only run while the PC is
/// idle, and a calibration started in the middle of a game reuses the last one.
/// </summary>
public sealed record FansOffResult(
    DateTimeOffset Created,
    double CpuPower,
    double GpuPower,
    IReadOnlyDictionary<Component, double> Final,
    IReadOnlyList<string> Summary)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>°C per watt above room temperature with all fans off, per temperature.</summary>
    public Dictionary<Component, double> Resistance(double ambient) =>
        Final.ToDictionary(kv => kv.Key, kv => (kv.Value - ambient) / Math.Max(1, ThermalModel.IsCpu(kv.Key) ? CpuPower : GpuPower));

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static FansOffResult? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<FansOffResult>(File.ReadAllText(path), Json) : null;
}
