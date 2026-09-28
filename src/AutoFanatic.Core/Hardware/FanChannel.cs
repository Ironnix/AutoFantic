namespace AutoFanatic.Core.Hardware;

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

    /// <summary>True while AutoFanatic (or another program) drives the channel instead of the BIOS / driver.</summary>
    public bool IsSoftwareControlled => isSoftwareControlled();

    internal void Set(float value) => set(value);

    internal void RestoreDefault() => restoreDefault();

    public override string ToString() => $"#{Index} {Hardware} / {Name}";
}
