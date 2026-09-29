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
/// <param name="BiosPercent">For a fan the user gave to the BIOS: the speed the BIOS runs it at (read back), if the hardware says.</param>
public sealed record FanReading(string Name, double? Percent, FanStatus? Status = null, double? BiosPercent = null)
{
    /// <summary>The user gave this fan to the BIOS (<see cref="CalibratedGroup.Bios"/>).</summary>
    public bool Bios { get; init; }
}

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
    private IReadOnlySet<string> _needed;
    private string? _quiet;
    private string? _monitorError;
    private Thread? _thread;
    private CancellationTokenSource? _stop;
    private int _disposed;

    /// <param name="calibration">The curves; null = not calibrated yet (the BIOS keeps the fans until <see cref="UseCalibration"/>).</param>
    /// <param name="minSpinning">Per calibrated group: the lowest speed at which its fans turn.</param>
    /// <param name="log">Where safety stops, sensor problems and fans switching off and on are written.</param>
    /// <param name="canStop">Per calibrated group: its fans stand still at 0 % (for quiet mode).</param>
    public FanControlLoop(FanSession session, CalibrationResult? calibration, IReadOnlyList<double> minSpinning, ActivityLog? log = null, IReadOnlyList<bool>? canStop = null)
    {
        _session = session;
        _log = log ?? ActivityLog.InMemoryOnly();
        _calibration = calibration;
        _channels = calibration is null ? [] : ChannelsFor(session, calibration);
        _keys = KeySensors.Detect(session.Read());
        _plausibility = new SensorPlausibility(_keys);
        _controller = calibration is null ? null : new CurveController(calibration, minSpinning, canStop);
        State = calibration is null ? LoopState.NotSetUp : LoopState.Running;
        _needed = Needed([]);
    }

    /// <summary>
    /// Every step reads only the key sensors plus these (e.g. the fans' RPM for the monitor), not
    /// all of them: less work and less memory churn every second.
    /// </summary>
    public void Watch(IEnumerable<string> sensorIds)
    {
        var needed = Needed(sensorIds);
        lock (_lock)
            _needed = needed;
    }

    private HashSet<string> Needed(IEnumerable<string> extra) =>
        [.. new[] { _keys.CpuTemp, _keys.CpuPower, _keys.GpuTemp, _keys.GpuHotspot, _keys.GpuMemory, _keys.GpuPower, _keys.CpuLoad, _keys.GpuLoad }.OfType<string>(), .. extra];

    /// <summary>After every step: what was read and what the fan control made of it (for the monitor). Raised on the loop's thread.</summary>
    public event Action<Snapshot, LoopStatus>? Sampled;

    public LoopState State { get; private set; }

    /// <summary>The key sensors found at the start (CPU and GPU temperature and power …).</summary>
    public KeySensors Keys => _keys;

    /// <summary>Per calibrated group: its fans stand still at 0 % (fans.json).</summary>
    public static IReadOnlyList<bool> CanStop(CalibrationResult calibration, FanInventory inventory)
    {
        var known = inventory.Groups();
        return calibration.Groups
            .Select(g => known.FirstOrDefault(k => k.Headers.Select(h => h.ControlId).SequenceEqual(g.ControlIds))?.CanStop == true)
            .ToList();
    }

    /// <summary>Why the fans are as quiet as possible now ("away", "night"); null = normal. Takes effect on the next step.</summary>
    public string? Quiet
    {
        get => _quiet;
        set
        {
            lock (_lock)
            {
                _quiet = value;
                if (_controller is not null)
                    _controller.Quiet = value;
            }
        }
    }

    /// <summary>
    /// True while nothing is going on (low load, cool): then the loop reads every
    /// <see cref="RelaxedInterval"/> instead of every second. Anything else and it's back to every second.
    /// </summary>
    public bool Relaxed { get; private set; }

    public static readonly TimeSpan RelaxedInterval = TimeSpan.FromSeconds(2);

    // "nothing going on": both loads below this (%), both temperatures at most this (°C)
    private const double RelaxedBelowLoad = 15;
    private const double RelaxedBelow = 55;

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
    /// Switches to other curves while running (another profile, a curve edited by hand, a fan
    /// given to the BIOS or taken back). Takes effect on the next step, starting right at the new curves.
    /// </summary>
    public void UseCalibration(CalibrationResult calibration, IReadOnlyList<double> minSpinning, IReadOnlyList<bool>? canStop = null)
    {
        var channels = ChannelsFor(_session, calibration);
        lock (_lock)
        {
            // a fan AutoFantic drove that is no longer in the new calibration, or that the user gave to the BIOS, goes back to it
            var drivenNow = Driven(calibration, channels).ToHashSet();
            foreach (var channel in Driven(_calibration, _channels).Where(c => !drivenNow.Contains(c)).ToList())
                _session.RestoreDefault(channel);
            _calibration = calibration;
            _channels = channels;
            _controller = new CurveController(calibration, minSpinning, canStop) { Quiet = _quiet };
            if (State == LoopState.NotSetUp)
                State = LoopState.Running;
        }
    }

    // the channels of every group AutoFantic drives (not the ones the user gave to the BIOS)
    private static IEnumerable<FanChannel> Driven(CalibrationResult? calibration, List<List<FanChannel>> channels) =>
        calibration is null ? [] : channels.Where((_, g) => !calibration.Groups[g].Bios).SelectMany(c => c);

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
            while (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds((Relaxed ? RelaxedInterval.TotalSeconds : 1) / _session.TimeScale)))
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
        var (snapshot, status) = Step();
        try
        {
            Sampled?.Invoke(snapshot, status);
        }
        catch (Exception ex)
        {
            // the monitor (history, warnings) must never be taken for a fan control error; said once
            if (ex.Message != _monitorError)
                _log.Add(LogKind.Warning, $"Monitor: {ex.Message}");
            _monitorError = ex.Message;
        }
        return status;
    }

    private (Snapshot, LoopStatus) Step()
    {
        lock (_lock)
        {
            var s = _session.Read(_needed);
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
                    foreach (var change in _controller.Switches.Where(c => !_calibration.Groups[c.Group].Bios))
                        _log.Add(LogKind.Fans, $"{_calibration.Groups[change.Group].Name} {(change.Off ? "off" : "on again")}: {change.Why}");
                }
            }

            var fans = _calibration is null ? [] : _calibration.Groups
                .Select((g, i) => g.Bios ? new FanReading(g.Name, null, null, BiosSpeed(i)) { Bios = true } : new FanReading(g.Name, speeds[i], status?[i]))
                .ToList();
            Last = new LoopStatus(s.Time, State, cpu, gpu, cpuW, gpuW, fans);
            // nothing going on: every 2 s is plenty (a game or a limit brings it back to every second)
            double cpuLoad = s.Value(_keys.CpuLoad) ?? 100, gpuLoad = s.Value(_keys.GpuLoad) ?? 100;
            Relaxed = State is LoopState.Running or LoopState.Paused or LoopState.NotSetUp
                && cpuLoad < RelaxedBelowLoad && gpuLoad < RelaxedBelowLoad && (cpu ?? 100) <= RelaxedBelow && (gpu ?? 100) <= RelaxedBelow;
            return (s, Last);
        }
    }

    private void Apply(double[] percent, double?[] applied)
    {
        for (int g = 0; g < _channels.Count; g++)
        {
            if (_calibration!.Groups[g].Bios)
                continue; // the user gave it to the BIOS: left alone, also at a safety limit (the BIOS's own curve applies)
            foreach (var channel in _channels[g])
                _session.SetPercent(channel, (float)percent[g]);
            applied[g] = percent[g];
        }
    }

    // what the BIOS runs a group at: its channels' duty as read back from the hardware
    private double? BiosSpeed(int group)
    {
        var read = _channels[group].Select(c => c.Percent).OfType<float>().ToList();
        return read.Count > 0 ? read.Average() : null;
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
