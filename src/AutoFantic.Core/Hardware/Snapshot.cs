namespace AutoFantic.Core.Hardware;

public enum SensorKind
{
    Temperature,
    Fan,
    Control,
    Power,
    Load,
    Clock,
    Other,
}

/// <summary>One sensor value at one point in time. <see cref="Id"/> is the stable LibreHardwareMonitor identifier, e.g. "/lpc/nct6798d/0/control/1".</summary>
public sealed record SensorReading(
    string Id,
    string Hardware,
    string HardwareType,
    SensorKind Kind,
    string Name,
    float? Value);

/// <summary>All sensor values read in one pass.</summary>
public sealed record Snapshot(DateTimeOffset Time, IReadOnlyList<SensorReading> Readings)
{
    public SensorReading? Find(string? id) =>
        id is null ? null : Readings.FirstOrDefault(r => r.Id == id);

    public float? Value(string? id) => Find(id)?.Value;

    public IEnumerable<SensorReading> OfKind(SensorKind kind) => Readings.Where(r => r.Kind == kind);
}
