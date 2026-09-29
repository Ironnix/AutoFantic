using AutoFantic.Core.Hardware;

namespace AutoFantic.Core.Simulation;

/// <summary>
/// A made-up gaming PC for developing and testing without real hardware (and without admin rights).
/// Temperatures follow a first-order thermal model: each component heads towards
/// ambient + power × R(fan speeds) with a time constant, where R falls steeply at low fan
/// speed and flattens out at high speed, so there is a real knee to find.
///
/// Fans: CPU fan, case fans, pump (barely reacts to duty cycle) and a GPU fan with 0-RPM mode.
/// What the PC is doing (power, load, foreground program) comes from a <see cref="SimLoad"/> schedule.
/// </summary>
public sealed class SimulatedPc : FanSession
{
    private const int CpuFan = 0, CaseFans = 1, Pump = 2, GpuFan = 3;

    private static readonly (string Id, string Name, string Hardware, float Default)[] Fans =
    [
        ("/sim/superio/control/0", "CPU Fan", "Simulated Super I/O", 45),
        ("/sim/superio/control/1", "Case Fans", "Simulated Super I/O", 40),
        ("/sim/superio/control/2", "Pump", "Simulated Super I/O", 100),
        ("/sim/gpu/control/0", "GPU Fan", "Simulated GPU", 55),
    ];

    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, SimLoad> _schedule;
    private readonly DateTimeOffset _start;
    private readonly Random _noise;
    private readonly float[] _percent = new float[Fans.Length];
    private readonly bool[] _software = new bool[Fans.Length];
    private readonly List<FanChannel> _channels;

    private DateTimeOffset _lastUpdate;
    private double _cpuTemp, _gpuTemp;

    /// <param name="timeScale">Simulated seconds per real second (e.g. 10 runs a sweep ten times faster).</param>
    /// <param name="load">What the PC does over time since the start; default: a steady game (<see cref="SimLoad.Game"/>).</param>
    /// <param name="clock">Time source; tests pass a fake one to step the model deterministically.</param>
    public SimulatedPc(
        double timeScale = 1,
        double ambient = 22,
        Func<TimeSpan, SimLoad>? load = null,
        int seed = 1,
        Func<DateTimeOffset>? clock = null)
    {
        Ambient = ambient;
        _schedule = load ?? (_ => SimLoad.Game);
        _noise = new Random(seed);
        TimeScale = timeScale;

        if (clock is null)
        {
            var realStart = DateTimeOffset.Now;
            clock = () => realStart + (DateTimeOffset.Now - realStart) * timeScale;
        }
        _clock = clock;
        _start = _clock();

        for (int i = 0; i < Fans.Length; i++)
            _percent[i] = Fans[i].Default;

        _channels = Fans
            .Select((fan, i) => new FanChannel(
                i, fan.Id, fan.Name, fan.Hardware, 0, 100,
                percent: () => _percent[i],
                isSoftwareControlled: () => _software[i],
                set: value => { _percent[i] = value; _software[i] = true; },
                restoreDefault: () => { _percent[i] = Fans[i].Default; _software[i] = false; }))
            .ToList();

        // start in equilibrium with the BIOS defaults, like a PC that has been running a while
        _cpuTemp = CpuTarget();
        _gpuTemp = GpuTarget();
        _lastUpdate = _start;
    }

    public double Ambient { get; set; }

    /// <summary>How dusty the PC is: 1 = clean; 1.2 = heat gets out 20 % worse (for the cooling health).</summary>
    public double Dust { get; set; } = 1;

    /// <summary>What the PC is doing right now.</summary>
    public SimLoad Load => _testLoad ?? _schedule(_clock() - _start);

    private SimLoad? _testLoad;

    public override IDisposable StartTestLoad(out string description)
    {
        _testLoad = SimLoad.Calibration;
        description = "simulated CPU + GPU load";
        return new StopLoad(this);
    }

    private sealed class StopLoad(SimulatedPc pc) : IDisposable
    {
        public void Dispose() => pc._testLoad = null;
    }

    public override IReadOnlyList<FanChannel> Channels => _channels;

    public override DateTimeOffset Now => _clock();

    public override double TimeScale { get; }

    public override string? Foreground() => Load.Foreground;

    /// <summary>Thermal resistance of the CPU cooler in °C/W for the given fan speeds.</summary>
    public static double CpuResistance(double cpuFan, double caseFans) =>
        0.25 + 9.0 / (cpuFan + 10) + 1.5 / (caseFans + 15);

    /// <summary>Thermal resistance of the graphics card in °C/W for the given fan speeds.</summary>
    public static double GpuResistance(double gpuFan, double caseFans) =>
        0.08 + 5.0 / (gpuFan + 15) + 1.5 / (caseFans + 15);

    protected override Snapshot ReadCore(IReadOnlySet<string>? only)
    {
        var now = _clock();
        Advance((now - _lastUpdate).TotalSeconds);
        _lastUpdate = now;

        var load = Load;
        double cpuW = Jitter(load.CpuPower, 0.02), gpuW = Jitter(load.GpuPower, 0.02);
        double cpuT = _cpuTemp + Noise(0.15), gpuT = _gpuTemp + Noise(0.15);

        const string cpu = "Simulated CPU", superIo = "Simulated Super I/O", gpu = "Simulated GPU";
        List<SensorReading> readings =
        [
            new("/sim/cpu/temperature/0", cpu, "Cpu", SensorKind.Temperature, "Core (Tctl/Tdie)", F(cpuT)),
            new("/sim/cpu/power/0", cpu, "Cpu", SensorKind.Power, "Package", F(cpuW)),
            new("/sim/cpu/load/0", cpu, "Cpu", SensorKind.Load, "CPU Total", F(Math.Clamp(Jitter(load.CpuLoad, 0.05), 0, 100))),

            new("/sim/superio/fan/0", superIo, "SuperIO", SensorKind.Fan, "CPU Fan", Rpm(CpuFan)),
            new("/sim/superio/fan/1", superIo, "SuperIO", SensorKind.Fan, "Case Fan", Rpm(CaseFans)),
            new("/sim/superio/fan/2", superIo, "SuperIO", SensorKind.Fan, "Pump", Rpm(Pump)),
            new(Fans[CpuFan].Id, superIo, "SuperIO", SensorKind.Control, "CPU Fan", _percent[CpuFan]),
            new(Fans[CaseFans].Id, superIo, "SuperIO", SensorKind.Control, "Case Fans", _percent[CaseFans]),
            new(Fans[Pump].Id, superIo, "SuperIO", SensorKind.Control, "Pump", _percent[Pump]),

            new("/sim/gpu/temperature/0", gpu, "GpuNvidia", SensorKind.Temperature, "GPU Core", F(gpuT)),
            new("/sim/gpu/temperature/1", gpu, "GpuNvidia", SensorKind.Temperature, "GPU Hot Spot", F(gpuT + 0.045 * gpuW)),
            new("/sim/gpu/temperature/2", gpu, "GpuNvidia", SensorKind.Temperature, "GPU Memory Junction", F(gpuT + 6)),
            new("/sim/gpu/power/0", gpu, "GpuNvidia", SensorKind.Power, "GPU Package", F(gpuW)),
            new("/sim/gpu/load/0", gpu, "GpuNvidia", SensorKind.Load, "GPU Core", F(Math.Clamp(Jitter(load.GpuLoad, 0.02), 0, 100))),
            new("/sim/gpu/fan/0", gpu, "GpuNvidia", SensorKind.Fan, "GPU Fan", Rpm(GpuFan)),
            new(Fans[GpuFan].Id, gpu, "GpuNvidia", SensorKind.Control, "GPU Fan", _percent[GpuFan]),
        ];
        return new Snapshot(now, only is null ? readings : readings.Where(r => only.Contains(r.Id)).ToList());
    }

    private void Advance(double seconds)
    {
        if (seconds <= 0)
            return;
        // exact solution of the first-order model over the interval, so big steps stay stable
        _cpuTemp += (CpuTarget() - _cpuTemp) * (1 - Math.Exp(-seconds / 25.0));
        _gpuTemp += (GpuTarget() - _gpuTemp) * (1 - Math.Exp(-seconds / 40.0));
    }

    private double CpuTarget() => Ambient + Load.CpuPower * CpuResistance(_percent[CpuFan], _percent[CaseFans]) * Dust;

    private double GpuTarget() => Ambient + Load.GpuPower * GpuResistance(Effective(GpuFan), _percent[CaseFans]) * Dust;

    // The GPU fan stops below 31 % (0-RPM mode), like many real cards.
    private float Effective(int fan) => fan == GpuFan && _percent[fan] <= 30 ? 0 : _percent[fan];

    private float Rpm(int fan) => fan switch
    {
        CpuFan => _percent[fan] < 20 ? 0 : 18 * _percent[fan],
        CaseFans => _percent[fan] < 25 ? 0 : 14 * _percent[fan],
        Pump => 2400 + 6 * _percent[fan],
        _ => Effective(fan) == 0 ? 0 : 30 * _percent[fan],
    };

    private double Jitter(double value, double relative) => value * (1 + Noise(relative));

    private double Noise(double amplitude) => (_noise.NextDouble() * 2 - 1) * amplitude;

    private static float F(double value) => (float)value;
}
