using LibreHardwareMonitor.Hardware;

namespace AutoFanatic.Core.Hardware;

/// <summary>
/// Direct access to sensors and fan headers through LibreHardwareMonitorLib.
/// Needs admin rights (the library loads the PawnIO kernel driver).
/// Disposing the session hands every fan it changed back to BIOS / driver control.
/// </summary>
public sealed class HardwareSession : IDisposable
{
    private readonly Computer _computer;
    private readonly HashSet<FanChannel> _touched = [];
    private readonly Lock _lock = new();
    private bool _disposed;

    public HardwareSession()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
        };
        _computer.Open();
        Update();

        Channels = AllSensors()
            .Where(s => s.SensorType == SensorType.Control && s.Control is not null)
            .Select((s, i) => new FanChannel(i, s))
            .ToList();
    }

    public IReadOnlyList<FanChannel> Channels { get; }

    /// <summary>Hardware tree as found by the library (for the "list" dump).</summary>
    public IReadOnlyList<IHardware> Hardware => _computer.Hardware.ToList();

    public Snapshot Read()
    {
        lock (_lock)
        {
            Update();
            var readings = AllSensors()
                .Select(s => new SensorReading(
                    s.Identifier.ToString(),
                    s.Hardware.Name,
                    s.Hardware.HardwareType.ToString(),
                    ToKind(s.SensorType),
                    s.Name,
                    s.Value))
                .ToList();
            return new Snapshot(DateTimeOffset.Now, readings);
        }
    }

    /// <summary>Sets a fan to a fixed duty cycle, clamped to what the channel allows.</summary>
    public float SetPercent(FanChannel channel, float percent)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            float value = Math.Clamp(percent, channel.MinPercent, channel.MaxPercent);
            _touched.Add(channel);
            channel.Set(value);
            return value;
        }
    }

    public void RestoreDefault(FanChannel channel)
    {
        lock (_lock)
        {
            channel.RestoreDefault();
            _touched.Remove(channel);
        }
    }

    /// <summary>Hands every fan this session changed back to the BIOS / driver. Never throws.</summary>
    public void RestoreAll()
    {
        lock (_lock)
        {
            foreach (var channel in _touched)
            {
                try
                {
                    channel.RestoreDefault();
                }
                catch
                {
                    // keep going: the other fans must be restored even if one fails
                }
            }
            _touched.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            RestoreAll();
            _computer.Close();
            _disposed = true;
        }
    }

    private void Update() => _computer.Accept(new UpdateVisitor());

    private IEnumerable<ISensor> AllSensors()
    {
        foreach (var hardware in _computer.Hardware)
            foreach (var sensor in SensorsOf(hardware))
                yield return sensor;
    }

    private static IEnumerable<ISensor> SensorsOf(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
            yield return sensor;
        foreach (var sub in hardware.SubHardware)
            foreach (var sensor in SensorsOf(sub))
                yield return sensor;
    }

    private static SensorKind ToKind(SensorType type) => type switch
    {
        SensorType.Temperature => SensorKind.Temperature,
        SensorType.Fan => SensorKind.Fan,
        SensorType.Control => SensorKind.Control,
        SensorType.Power => SensorKind.Power,
        SensorType.Load => SensorKind.Load,
        SensorType.Clock => SensorKind.Clock,
        _ => SensorKind.Other,
    };

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
                sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}
