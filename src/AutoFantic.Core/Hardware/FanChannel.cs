namespace AutoFantic.Core.Hardware;

/// <summary>
/// A controllable fan output: a mainboard header (Super I/O PWM channel), a GPU fan, or a
/// simulated one. Speeds are only changed through a <see cref="FanSession"/>, which remembers
/// every channel it touched and hands all of them back to the BIOS / driver when it ends.
/// </summary>
public sealed class FanChannel(
    int index,
    string id,
    string name,
    string hardware,
    float minPercent,
    float maxPercent,
    Func<float?> percent,
    Func<bool> isSoftwareControlled,
    Action<float> set,
    Action restoreDefault)
{
    /// <summary>Position in <see cref="FanSession.Channels"/>; handy on the command line.</summary>
    public int Index { get; } = index;

    public string Id { get; } = id;

    public string Name { get; } = name;

    public string Hardware { get; } = hardware;

    public float MinPercent { get; } = minPercent;

    public float MaxPercent { get; } = maxPercent;

    /// <summary>Current duty cycle in percent as reported by the hardware.</summary>
    public float? Percent => percent();

    /// <summary>True while AuFantic (or another program) drives the channel instead of the BIOS / driver.</summary>
    public bool IsSoftwareControlled => isSoftwareControlled();

    /// <summary>A graphics card's fan.</summary>
    public bool IsGpu => IsGpuId(Id);

    /// <summary>A header on the mainboard's fan chip. Those are only there with the PawnIO driver.</summary>
    public bool IsMainboard => IsMainboardId(Id);

    /// <summary>The pump of a water cooler with its own controller (<see cref="IsCoolerPumpOutput"/>).</summary>
    public bool IsCoolerPump => IsCoolerPumpOutput(Id, Name);

    public static bool IsGpuId(string id) => id.Contains("gpu", StringComparison.OrdinalIgnoreCase);

    // LibreHardwareMonitor's ids of a Super I/O chip start with "/lpc/"; the simulated PC's with "/sim/superio/"
    public static bool IsMainboardId(string id) =>
        id.StartsWith("/lpc/", StringComparison.OrdinalIgnoreCase) || id.StartsWith("/sim/superio/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The pump of a water cooler with its own controller (NZXT Kraken "Pump Control", Aquacomputer):
    /// a real pump, never experimented with. A mainboard header named "Pump Fan" is not one: whatever
    /// is plugged in there is found by how it turns.
    /// </summary>
    public static bool IsCoolerPumpOutput(string id, string name) =>
        !IsGpuId(id) && !IsMainboardId(id) && name.Contains("Pump", StringComparison.OrdinalIgnoreCase);

    internal void Set(float value) => set(value);

    internal void RestoreDefault() => restoreDefault();

    public override string ToString() => $"#{Index} {Hardware} / {Name}";
}
