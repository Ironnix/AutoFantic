using AutoFantic.Core.Calibration;
using AutoFantic.Core.Control;
using AutoFantic.Core.Hardware;

namespace AutoFantic.Core.Monitoring;

/// <summary>
/// Turns every step of the fan control into the monitor's history: temperatures, power, load, and
/// per fan group the speed AuFantic set and how fast it really turns. Checks the warnings and
/// notices game sessions on the way. Fed on the fan control's thread (<see cref="FanControlLoop.Sampled"/>).
/// </summary>
public sealed class HistoryRecorder(HistoryStore store, KeySensors keys)
{
    public static readonly Series CpuTemp = new("cpu.temp", "CPU", SeriesKind.Temperature);
    public static readonly Series GpuTemp = new("gpu.temp", "GPU", SeriesKind.Temperature);
    public static readonly Series GpuHotspot = new("gpu.hotspot", "GPU hotspot", SeriesKind.Temperature);
    public static readonly Series GpuMemory = new("gpu.memory", "GPU memory", SeriesKind.Temperature);
    public static readonly Series CpuPower = new("cpu.power", "CPU", SeriesKind.Power);
    public static readonly Series GpuPower = new("gpu.power", "GPU", SeriesKind.Power);
    public static readonly Series CpuLoad = new("cpu.load", "CPU", SeriesKind.Load);
    public static readonly Series GpuLoad = new("gpu.load", "GPU", SeriesKind.Load);

    private readonly Lock _lock = new();
    private readonly WarningWatch _warnings = new();
    private readonly SessionTracker _sessions = new();
    private IReadOnlyList<(FanGroup Group, Series Percent, Series Rpm)> _fans = [];

    public HistoryStore Store => store;

    /// <summary>A warning was reached (message in plain words). Raised on the fan control's thread.</summary>
    public event Action<string>? Warning;

    /// <summary>A game session ended and was stored. Raised on the fan control's thread.</summary>
    public event Action<GameSession>? SessionEnded;

    /// <summary>The preset in use, stored with each session.</summary>
    public string Preset { get; set; } = "";

    /// <summary>The session running now (program, since when), if any.</summary>
    public (string Program, DateTimeOffset Start)? CurrentSession
    {
        get
        {
            lock (_lock)
                return _sessions.Current;
        }
    }

    public WarningSettings Warnings
    {
        get
        {
            lock (_lock)
                return _warnings.Settings;
        }
        set
        {
            lock (_lock)
                _warnings.Settings = value;
        }
    }

    /// <summary>The fan groups found (fans.json); their RPM sensors are what <see cref="SensorIds"/> adds.</summary>
    public void UseFans(IReadOnlyList<FanGroup> groups)
    {
        var fans = groups.Select(g => (g, FanSeries(g, SeriesKind.FanPercent), FanSeries(g, SeriesKind.FanRpm))).ToList();
        lock (_lock)
            _fans = fans;
    }

    /// <summary>The sensors the fan control has to read for the monitor, besides its own: every fan's RPM.</summary>
    public IEnumerable<string> SensorIds
    {
        get
        {
            lock (_lock)
                return [.. _fans.SelectMany(f => f.Group.Headers).Select(h => h.RpmSensorId).OfType<string>()];
        }
    }

    public static Series FanSeries(FanGroup group, SeriesKind kind) =>
        new($"fan.{MeasurementStore.Key(group)}.{(kind == SeriesKind.FanRpm ? "rpm" : "percent")}", group.Name, kind);

    public void Record(Snapshot snapshot, LoopStatus status) => Record(snapshot, status, null);

    /// <param name="foreground">The program in the foreground (for the game sessions).</param>
    public void Record(Snapshot snapshot, LoopStatus status, string? foreground)
    {
        var values = new List<(Series, double)>();
        void Add(Series series, double? value)
        {
            if (value is { } v)
                values.Add((series, v));
        }
        Add(CpuTemp, snapshot.Value(keys.CpuTemp));
        Add(GpuTemp, snapshot.Value(keys.GpuTemp));
        Add(GpuHotspot, snapshot.Value(keys.GpuHotspot));
        Add(GpuMemory, snapshot.Value(keys.GpuMemory));
        Add(CpuPower, snapshot.Value(keys.CpuPower));
        Add(GpuPower, snapshot.Value(keys.GpuPower));
        Add(CpuLoad, snapshot.Value(keys.CpuLoad));
        Add(GpuLoad, snapshot.Value(keys.GpuLoad));

        IReadOnlyList<string> warnings;
        (string Program, DateTimeOffset Start, DateTimeOffset End)? ended;
        lock (_lock)
        {
            ended = _sessions.Feed(snapshot.Time, foreground, snapshot.Value(keys.CpuLoad) ?? 0, snapshot.Value(keys.GpuLoad) ?? 0);
            var fans = new List<FanSample>();
            foreach (var (group, percentSeries, rpmSeries) in _fans)
            {
                var rpms = group.Headers.Select(h => (double?)snapshot.Value(h.RpmSensorId)).OfType<double>().ToList();
                double? rpm = rpms.Count > 0 ? rpms.Average() : null;
                var reading = status.Fans.FirstOrDefault(f => f.Name == group.Name);
                double? percent = reading?.Percent;
                Add(percentSeries, percent ?? reading?.BiosPercent); // a fan given to the BIOS: what the BIOS runs it at
                Add(rpmSeries, rpm);
                fans.Add(new FanSample(group.Name, percent, rpm));
            }

            store.Add(snapshot.Time, values);
            var byKey = values.ToDictionary(v => v.Item1.Key, v => v.Item2);
            var known = values.Select(v => v.Item1).ToDictionary(s => s.Key);
            warnings = _warnings.Check(snapshot.Time, byKey, fans, key => known.GetValueOrDefault(key));
        }
        foreach (var message in warnings)
            Warning?.Invoke(message);
        if (ended is { } session)
            SessionEnded?.Invoke(store.AddSession(session.Program, session.Start, session.End, Preset));
    }

    /// <summary>At exit: keeps the session still running, if it's long enough.</summary>
    public void FinishSession()
    {
        (string Program, DateTimeOffset Start, DateTimeOffset End)? running;
        lock (_lock)
            running = _sessions.Finish();
        if (running is { } session)
            SessionEnded?.Invoke(store.AddSession(session.Program, session.Start, session.End, Preset));
    }
}
