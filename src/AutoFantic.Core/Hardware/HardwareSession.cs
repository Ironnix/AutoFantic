using System.Reflection;
using LibreHardwareMonitor.Hardware;

namespace AutoFantic.Core.Hardware;

/// <summary>
/// Direct access to sensors and fan headers through LibreHardwareMonitorLib.
/// Needs admin rights (the library loads the PawnIO kernel driver).
/// </summary>
public sealed class HardwareSession : FanSession
{
    private readonly Computer _computer;
    private readonly List<FanChannel> _channels;
    private readonly Dictionary<FanChannel, IHardware> _hardwareOf = [];
    private readonly Dictionary<ISensor, (string Id, string Hardware, string HardwareType, SensorKind Kind)> _names = [];

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

        _channels = [];
        foreach (var sensor in AllSensors().Where(s => s.SensorType == SensorType.Control && s.Control is not null))
        {
            var channel = ToChannel(_channels.Count, sensor);
            _channels.Add(channel);
            _hardwareOf[channel] = sensor.Hardware;
        }
    }

    public override IReadOnlyList<FanChannel> Channels => _channels;

    /// <summary>True if the mainboard has fan outputs the library can drive (a supported fan chip, the PawnIO driver loaded).</summary>
    public bool HasMainboardFans => _hardwareOf.Values.Any(h => h.HardwareType == HardwareType.SuperIO);

    protected override IReadOnlyList<ChipState> CaptureChips(IReadOnlyCollection<FanChannel> channels)
    {
        var states = new List<ChipState>();
        foreach (var hardware in channels.Select(c => _hardwareOf.GetValueOrDefault(c)).OfType<IHardware>().Distinct())
            if (ChipOf(hardware) is { } chip && ChipMemory.Capture(hardware.Identifier.ToString(), chip) is { } state)
                states.Add(state);
        return states;
    }

    /// <summary>
    /// First the fan chips get their memory of the BIOS setup back (from the file), then every fan
    /// is handed back: the library then restores exactly what the BIOS had set, instead of the stuck
    /// speed it would find on its own. GPU fans need no memory: their driver takes over again.
    /// </summary>
    public override IReadOnlyList<string> HandBack(HandbackFile file)
    {
        var lines = new List<string>();
        foreach (var state in file.Chips)
        {
            var hardware = _hardwareOf.Values.FirstOrDefault(h => h.Identifier.ToString() == state.HardwareId);
            bool injected = false;
            try
            {
                injected = hardware is not null && ChipOf(hardware) is { } chip && ChipMemory.Inject(chip, state);
            }
            catch (Exception)
            {
                // a library update may have changed the chip's fields: the plain hand-back below still runs
            }
            if (!injected)
                lines.Add($"{state.HardwareId}: couldn't restore the BIOS setup (restart the PC if a fan stays at one speed)");
        }
        lines.AddRange(base.HandBack(file));
        return lines;
    }

    // the library keeps the fan chip (Nct677X, IT87XX …) private inside its Super I/O hardware
    private static object? ChipOf(IHardware hardware) =>
        hardware.GetType().GetField("_superIO", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(hardware);

    protected override Snapshot ReadCore()
    {
        Update();
        var readings = new List<SensorReading>(_names.Count);
        foreach (var s in AllSensors())
        {
            // a sensor's id and names never change: made once, not every second (less garbage)
            if (!_names.TryGetValue(s, out var n))
                _names[s] = n = (s.Identifier.ToString(), s.Hardware.Name, s.Hardware.HardwareType.ToString(), ToKind(s.SensorType));
            readings.Add(new SensorReading(n.Id, n.Hardware, n.HardwareType, n.Kind, s.Name, s.Value));
        }
        return new Snapshot(DateTimeOffset.Now, readings);
    }

    protected override void DisposeCore() => _computer.Close();

    private static FanChannel ToChannel(int index, ISensor sensor)
    {
        var control = sensor.Control;
        return new FanChannel(
            index,
            sensor.Identifier.ToString(),
            sensor.Name,
            sensor.Hardware.Name,
            control.MinSoftwareValue,
            control.MaxSoftwareValue,
            percent: () => sensor.Value,
            isSoftwareControlled: () => control.ControlMode == ControlMode.Software,
            set: control.SetSoftware,
            restoreDefault: control.SetDefault);
    }

    private void Update() => _computer.Accept(new UpdateVisitor());

    private IEnumerable<ISensor> AllSensors()
    {
        foreach (var hardware in _computer.Hardware)
            foreach (var sensor in SensorsOf(hardware))
                yield return sensor;
    }

    // Includes sub-hardware: the mainboard's Super I/O chip, which carries the fan headers, is one.
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
