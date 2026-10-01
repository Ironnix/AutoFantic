using AutoFantic.Core.Hardware;

namespace AutoFantic.Core.Tests;

/// <summary>
/// A PC as AutoFantic's first other user had it: no PawnIO driver, so no mainboard outputs and a
/// CPU that reads 0 °C and 0 W. What is still there: the graphics card's two fans and the pump of
/// an NZXT Kraken (its own USB controller). Time runs 1000 times faster, so finding the fans takes a moment.
/// </summary>
internal sealed class PcWithoutDriver : FanSession
{
    public const string Pump = "/nzxt/krakenv3/1/control/0";

    private static readonly (string Id, string Name, string Hardware)[] Outputs =
    [
        ("/gpu-nvidia/0/control/1", "GPU Fan 1", "NVIDIA GeForce RTX 4070 Ti SUPER"),
        ("/gpu-nvidia/0/control/2", "GPU Fan 2", "NVIDIA GeForce RTX 4070 Ti SUPER"),
        (Pump, "Pump Control", "NZXT Kraken X"),
    ];

    private readonly DateTimeOffset _start = DateTimeOffset.Now;
    private readonly float[] _percent = [40, 40, 80];
    private readonly List<FanChannel> _channels;

    /// <param name="cpuTemp">What the CPU's sensor reads: 0 without the driver.</param>
    public PcWithoutDriver(float cpuTemp = 0)
    {
        CpuTemp = cpuTemp;
        _channels = Outputs
            .Select((o, i) => new FanChannel(i, o.Id, o.Name, o.Hardware, 0, 100,
                percent: () => _percent[i],
                isSoftwareControlled: () => false,
                set: value =>
                {
                    _percent[i] = value;
                    Set.Add(o.Id);
                },
                restoreDefault: () => { }))
            .ToList();
    }

    public float CpuTemp { get; }

    /// <summary>Every output AutoFantic changed the speed of.</summary>
    public List<string> Set { get; } = [];

    public override IReadOnlyList<FanChannel> Channels => _channels;

    public override double TimeScale => 1000;

    public override DateTimeOffset Now => _start + (DateTimeOffset.Now - _start) * TimeScale;

    public override string? Foreground() => null;

    protected override Snapshot ReadCore(IReadOnlySet<string>? only)
    {
        const string cpu = "AMD Ryzen 7 9800X3D", gpu = "NVIDIA GeForce RTX 4070 Ti SUPER", kraken = "NZXT Kraken X";
        List<SensorReading> readings =
        [
            new("/amdcpu/0/temperature/2", cpu, "Cpu", SensorKind.Temperature, "Core (Tctl/Tdie)", CpuTemp),
            new("/amdcpu/0/power/0", cpu, "Cpu", SensorKind.Power, "Package", 0),
            new("/gpu-nvidia/0/temperature/0", gpu, "GpuNvidia", SensorKind.Temperature, "GPU Core", 56),
            new("/gpu-nvidia/0/temperature/2", gpu, "GpuNvidia", SensorKind.Temperature, "GPU Hot Spot", 68),
            new("/gpu-nvidia/0/power/0", gpu, "GpuNvidia", SensorKind.Power, "GPU Package", 50),
            new("/gpu-nvidia/0/fan/1", gpu, "GpuNvidia", SensorKind.Fan, "GPU Fan 1", _percent[0] <= 30 ? 0 : 30 * _percent[0]),
            new("/gpu-nvidia/0/fan/2", gpu, "GpuNvidia", SensorKind.Fan, "GPU Fan 2", _percent[1] <= 30 ? 0 : 30 * _percent[1]),
            new("/nzxt/krakenv3/1/fan/0", kraken, "Cooler", SensorKind.Fan, "Pump", 28 * _percent[2]),
        ];
        return new Snapshot(Now, only is null ? readings : readings.Where(r => only.Contains(r.Id)).ToList());
    }
}
