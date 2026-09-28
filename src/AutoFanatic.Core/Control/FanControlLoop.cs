using AutoFanatic.Core.Analysis;
using AutoFanatic.Core.Calibration;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Core.Control;

public enum LoopState
{
    /// <summary>AutoFanatic drives the fans by the calibrated curves.</summary>
    Running,

    /// <summary>Paused (by the user, or for sleep): the BIOS drives the fans.</summary>
    Paused,

    /// <summary>A temperature limit was crossed: every fan at 100 % until it is safely cool again.</summary>
    CoolingDown,

    /// <summary>A temperature sensor stopped reporting: the BIOS drives the fans; retried after a minute.</summary>
    SensorProblem,
}

/// <param name="Percent">Speed AutoFanatic set; 0 = off; null = the BIOS is in control.</param>
public sealed record FanReading(string Name, double? Percent);

public sealed record LoopStatus(
    DateTimeOffset Time,
    LoopState State,
    double? CpuTemp,
    double? GpuTemp,
    double CpuPower,
    double GpuPower,
    IReadOnlyList<FanReading> Fans);

/// <summary>
/// AutoFanatic in the background: once per second read the sensors, check the safety limits and
/// the sensors, and drive the fans by the calibrated curves. Everything that must never go wrong
/// lives here, not in the tray icon: a crossed limit runs every fan at 100 % until it is safely
/// cool, a lost sensor hands the fans to the BIOS (it reads its own sensors), pausing hands them to
/// the BIOS, and so does stopping.
/// </summary>
public sealed class FanControlLoop : IDisposable
{
    private static readonly TimeSpan SensorRetryAfter = TimeSpan.FromMinutes(1);

    private readonly FanSession _session;
    private CalibrationResult _calibration;
    private List<List<FanChannel>> _channels;
    private CurveController _controller;
    private readonly SafetyLimits _limits = new();
    private readonly KeySensors _keys;
    private readonly Lock _lock = new();

    private SensorPlausibility _plausibility;
    private SafetyRecovery? _recovery;
    private DateTimeOffset _sensorRetryAt;
    private bool _paused;
    private Thread? _thread;
    private CancellationTokenSource? _stop;
    private int _disposed;

    /// <param name="minSpinning">Per calibrated group: the lowest speed at which its fans turn.</param>
    public FanControlLoop(FanSession session, CalibrationResult calibration, IReadOnlyList<double> minSpinning)
    {
        _session = session;
        _calibration = calibration;
        _channels = ChannelsFor(session, calibration);
        _keys = KeySensors.Detect(session.Read());
        _plausibility = new SensorPlausibility(_keys);
        _controller = new CurveController(calibration, minSpinning);
    }

    public LoopState State { get; private set; } = LoopState.Running;

    /// <summary>Per calibrated group: the lowest speed its fans turn at, from what was measured (fans.json).</summary>
    public static IReadOnlyList<double> MinSpinning(CalibrationResult calibration, FanInventory inventory)
    {
        var known = inventory.Groups();
        return calibration.Groups
            .Select(g => (double)(known.FirstOrDefault(k => k.Headers.Select(h => h.ControlId).SequenceEqual(g.ControlIds))?.MinSpinning ?? 30))
            .ToList();
    }

    /// <summary>The latest status, for a display to pick up.</summary>
    public LoopStatus? Last { get; private set; }

    /// <summary>Something the user should know about (a safety stop, a sensor problem). Raised on the loop's thread.</summary>
    public event Action<string>? Alert;

    /// <summary>The curves in use now.</summary>
    public CalibrationResult Calibration => _calibration;

    /// <summary>
    /// Switches to other curves while running (another profile, a curve edited by hand). Takes
    /// effect on the next step, starting right at the new curves.
    /// </summary>
    public void UseCalibration(CalibrationResult calibration, IReadOnlyList<double> minSpinning)
    {
        var channels = ChannelsFor(_session, calibration);
        lock (_lock)
        {
            // a fan that is no longer in the new calibration goes back to the BIOS
            foreach (var channel in _channels.SelectMany(c => c).Except(channels.SelectMany(c => c)))
                _session.RestoreDefault(channel);
            _calibration = calibration;
            _channels = channels;
            _controller = new CurveController(calibration, minSpinning);
        }
    }

    private static List<List<FanChannel>> ChannelsFor(FanSession session, CalibrationResult calibration) =>
        calibration.Groups
            .Select(g => g.ControlIds.Select(id => session.Channels.FirstOrDefault(c => c.Id == id)
                ?? throw new InvalidOperationException($"Fan output {id} no longer exists: find the fans and calibrate again.")).ToList())
            .ToList();

    /// <summary>Paused = the BIOS drives the fans. Resuming starts again right at the curves.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            lock (_lock)
            {
                if (_paused == value)
                    return;
                _paused = value;
                if (value)
                {
                    _session.RestoreAll();
                }
                else
                {
                    _controller.Restart();
                    State = LoopState.Running;
                }
            }
        }
    }

    /// <summary>Runs <see cref="Tick"/> once per (session) second on a background thread.</summary>
    public void Start()
    {
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _thread = new Thread(() =>
        {
            var interval = TimeSpan.FromSeconds(1 / _session.TimeScale);
            while (!token.WaitHandle.WaitOne(interval))
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    // never leave the fans at a fixed speed because of an error: back to the BIOS
                    _session.RestoreAll();
                    Alert?.Invoke($"Error, fans handed back to the BIOS: {ex.Message}");
                    token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                }
            }
        })
        { IsBackground = true, Name = "AutoFanatic fan control", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>One control step: read, check, drive. Public so tests can step it with a fake clock.</summary>
    public LoopStatus Tick()
    {
        lock (_lock)
        {
            var s = _session.Read();
            double? cpu = s.Value(_keys.CpuTemp), gpu = s.Value(_keys.GpuTemp);
            double cpuW = s.Value(_keys.CpuPower) ?? 0, gpuW = s.Value(_keys.GpuPower) ?? 0;
            double?[] speeds = new double?[_channels.Count]; // null = BIOS

            if (_paused)
            {
                State = LoopState.Paused;
            }
            else if (State == LoopState.SensorProblem && s.Time < _sensorRetryAt)
            {
                // still waiting before trying the sensors again; the BIOS has the fans
            }
            else
            {
                if (State == LoopState.SensorProblem)
                {
                    _plausibility = new SensorPlausibility(_keys);
                    _controller.Restart();
                    State = LoopState.Running;
                }

                string? violation = _limits.Check(s, _keys);
                string? sensorError = violation is null ? _plausibility.Check(s) : null;

                if (sensorError is not null)
                {
                    _session.RestoreAll();
                    State = LoopState.SensorProblem;
                    _sensorRetryAt = s.Time + SensorRetryAfter;
                    Alert?.Invoke($"{sensorError}: fans handed back to the BIOS, trying again in a minute.");
                }
                else if (violation is not null || State == LoopState.CoolingDown)
                {
                    if (State != LoopState.CoolingDown)
                    {
                        State = LoopState.CoolingDown;
                        _recovery = new SafetyRecovery(_limits, _keys);
                        Alert?.Invoke($"{violation}: all fans at 100 % until it has cooled down.");
                    }
                    Apply(Enumerable.Repeat(100.0, _channels.Count).ToArray(), speeds);
                    if (_recovery!.IsRecovered(s))
                    {
                        State = LoopState.Running;
                        _controller.Reset();
                    }
                }
                else
                {
                    var temps = new Dictionary<Component, double>();
                    if (cpu is { } c)
                        temps[Component.Cpu] = c;
                    if (gpu is { } g)
                        temps[Component.GpuCore] = g;
                    Apply(_controller.Step(s.Time, temps, cpuW, gpuW), speeds);
                }
            }

            var fans = _calibration.Groups.Select((g, i) => new FanReading(g.Name, speeds[i])).ToList();
            Last = new LoopStatus(s.Time, State, cpu, gpu, cpuW, gpuW, fans);
            return Last;
        }
    }

    private void Apply(double[] percent, double?[] applied)
    {
        for (int g = 0; g < _channels.Count; g++)
        {
            foreach (var channel in _channels[g])
                _session.SetPercent(channel, (float)percent[g]);
            applied[g] = percent[g];
        }
    }

    /// <summary>Stops the loop and hands every fan back to the BIOS. Safe to call more than once (exit, crash, logoff).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _stop?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
        lock (_lock)
            _session.RestoreAll();
        _stop?.Dispose();
    }
}
