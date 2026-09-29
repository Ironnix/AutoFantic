using AutoFantic.Core.Analysis;
using AutoFantic.Core.Calibration;
using AutoFantic.Core.Hardware;
using AutoFantic.Core.Logging;

namespace AutoFantic.Core.Control;

public enum LoopState
{
    /// <summary>AutoFantic drives the fans by the calibrated curves.</summary>
    Running,

    /// <summary>Paused (by the user, or for sleep): the BIOS drives the fans.</summary>
    Paused,

    /// <summary>A temperature limit was crossed: every fan at 100 % until it is safely cool again.</summary>
    CoolingDown,

    /// <summary>A temperature sensor stopped reporting: the BIOS drives the fans; retried after a minute.</summary>
    SensorProblem,

    /// <summary>No calibration yet: the BIOS drives the fans; AutoFantic only reads the sensors.</summary>
    NotSetUp,
}

/// <param name="Percent">Speed AutoFantic set; 0 = off; null = the BIOS is in control.</param>
/// <param name="Status">While running by the curves: the temperature followed, the curve's speed there and why the fan differs from it.</param>
public sealed record FanReading(string Name, double? Percent, FanStatus? Status = null);

public sealed record LoopStatus(
    DateTimeOffset Time,
    LoopState State,
    double? CpuTemp,
    double? GpuTemp,
    double CpuPower,
    double GpuPower,
    IReadOnlyList<FanReading> Fans);

/// <summary>
/// AutoFantic in the background: once per second read the sensors, check the safety limits and
/// the sensors, and drive the fans by the calibrated curves. Everything that must never go wrong
/// lives here, not in the tray icon: a crossed limit runs every fan at 100 % until it is safely
/// cool, a lost sensor hands the fans to the BIOS (it reads its own sensors), pausing hands them to
/// the BIOS, and so does stopping. Without a calibration it only reads the sensors (the BIOS keeps
/// the fans) until the first one is done.
/// </summary>
public sealed class FanControlLoop : IDisposable
{
    private static readonly TimeSpan SensorRetryAfter = TimeSpan.FromMinutes(1);

    private readonly FanSession _session;
    private readonly ActivityLog _log;
    private CalibrationResult? _calibration;
    private List<List<FanChannel>> _channels;
    private CurveController? _controller;
    private readonly SafetyLimits _limits = new();
    private readonly KeySensors _keys;
    private readonly Lock _lock = new();

    private SensorPlausibility _plausibility;
    private SafetyRecovery? _recovery;
    private DateTimeOffset _sensorRetryAt;
    private bool _retryingSensors;
    private bool _paused;
    private Thread? _thread;
    private CancellationTokenSource? _stop;
    private int _disposed;

    /// <param name="calibration">The curves; null = not calibrated yet (the BIOS keeps the fans until <see cref="UseCalibration"/>).</param>
    /// <param name="minSpinning">Per calibrated group: the lowest speed at which its fans turn.</param>
    /// <param name="log">Where safety stops, sensor problems and fans switching off and on are written.</param>
    public FanControlLoop(FanSession session, CalibrationResult? calibration, IReadOnlyList<double> minSpinning, ActivityLog? log = null)
    {
        _session = session;
        _log = log ?? ActivityLog.InMemoryOnly();
        _calibration = calibration;
        _channels = calibration is null ? [] : ChannelsFor(session, calibration);
        _keys = KeySensors.Detect(session.Read());
        _plausibility = new SensorPlausibility(_keys);
        _controller = calibration is null ? null : new CurveController(calibration, minSpinning);
        State = calibration is null ? LoopState.NotSetUp : LoopState.Running;
    }

    public LoopState State { get; private set; }

    /// <summary>The key sensors found at the start (CPU and GPU temperature and power …).</summary>
    public KeySensors Keys => _keys;

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

    /// <summary>The curves in use now; null while not calibrated.</summary>
    public CalibrationResult? Calibration => _calibration;

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
            if (State == LoopState.NotSetUp)
                State = LoopState.Running;
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
                    _controller?.Restart();
                    State = _calibration is null ? LoopState.NotSetUp : LoopState.Running;
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
                    _log.Add(LogKind.Warning, $"Error, fans handed back to the BIOS for 10 s: {ex.Message}");
                    Alert?.Invoke($"Error, fans handed back to the BIOS: {ex.Message}");
                    token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                }
            }
        })
        { IsBackground = true, Name = "AutoFantic fan control", Priority = ThreadPriority.AboveNormal };
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
            IReadOnlyList<FanStatus>? status = null;

            if (_calibration is null || _controller is null)
            {
                State = LoopState.NotSetUp;
            }
            else if (_paused)
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
                    _retryingSensors = true;
                }

                string? violation = _limits.Check(s, _keys);
                string? sensorError = violation is null ? _plausibility.Check(s) : null;

                if (sensorError is not null)
                {
                    _session.RestoreAll();
                    State = LoopState.SensorProblem;
                    _sensorRetryAt = s.Time + SensorRetryAfter;
                    if (!_retryingSensors)
                        _log.Add(LogKind.Sensor, $"{sensorError}: fans handed back to the BIOS; trying again every minute.");
                    _retryingSensors = false;
                    Alert?.Invoke($"{sensorError}: fans handed back to the BIOS, trying again in a minute.");
                }
                else if (violation is not null || State == LoopState.CoolingDown)
                {
                    if (State != LoopState.CoolingDown)
                    {
                        State = LoopState.CoolingDown;
                        _recovery = new SafetyRecovery(_limits, _keys);
                        _log.Add(LogKind.Safety, $"{violation}: all fans at 100 % until it has cooled down.");
                        Alert?.Invoke($"{violation}: all fans at 100 % until it has cooled down.");
                    }
                    Apply(Enumerable.Repeat(100.0, _channels.Count).ToArray(), speeds);
                    if (_recovery!.IsRecovered(s))
                    {
                        State = LoopState.Running;
                        _controller.Reset();
                        _log.Add(LogKind.Safety, $"Cooled down again (CPU {cpu:0} °C, GPU {gpu:0} °C): the fans follow their curves again.");
                    }
                }
                else
                {
                    var temps = new Dictionary<Component, double>();
                    if (cpu is { } c)
                        temps[Component.Cpu] = c;
                    if (gpu is { } g)
                        temps[Component.GpuCore] = g;
                    if (_retryingSensors)
                    {
                        _retryingSensors = false;
                        _log.Add(LogKind.Sensor, "The sensors work again: AutoFantic controls the fans again.");
                    }
                    Apply(_controller.Step(s.Time, temps, cpuW, gpuW), speeds);
                    status = _controller.Status;
                    foreach (var change in _controller.Switches)
                        _log.Add(LogKind.Fans, $"{_calibration.Groups[change.Group].Name} {(change.Off ? "off" : "on again")}: {change.Why}");
                }
            }

            var fans = _calibration is null ? [] : _calibration.Groups.Select((g, i) => new FanReading(g.Name, speeds[i], status?[i])).ToList();
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
