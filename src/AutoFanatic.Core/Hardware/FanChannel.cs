using LibreHardwareMonitor.Hardware;

namespace AutoFanatic.Core.Hardware;

/// <summary>
/// A controllable fan output: a mainboard header (Super I/O PWM channel) or a GPU fan.
/// Speeds are only changed through <see cref="HardwareSession"/>, which remembers every
/// channel it touched and hands all of them back to the BIOS / driver when it ends.
/// </summary>
public sealed class FanChannel
{
    private readonly ISensor _sensor;

    internal FanChannel(int index, ISensor sensor)
    {
        Index = index;
        _sensor = sensor;
    }

    /// <summary>Position in <see cref="HardwareSession.Channels"/>; handy on the command line.</summary>
    public int Index { get; }

    public string Id => _sensor.Identifier.ToString();

    public string Name => _sensor.Name;

    public string Hardware => _sensor.Hardware.Name;

    /// <summary>Current duty cycle in percent as reported by the hardware.</summary>
    public float? Percent => _sensor.Value;

    public float MinPercent => _sensor.Control.MinSoftwareValue;

    public float MaxPercent => _sensor.Control.MaxSoftwareValue;

    /// <summary>True while AutoFanatic (or another program) drives the channel instead of the BIOS / driver.</summary>
    public bool IsSoftwareControlled => _sensor.Control.ControlMode == ControlMode.Software;

    internal void Set(float percent) => _sensor.Control.SetSoftware(percent);

    internal void RestoreDefault() => _sensor.Control.SetDefault();

    public override string ToString() => $"#{Index} {Hardware} / {Name}";
}
