using AutoFantic.Core.Analysis;
using AutoFantic.Core.Hardware;
using AutoFantic.Core.Reports;

namespace AutoFantic.Core.Calibration;

public enum CalibrationStage
{
    FindingFans,
    FansOff,
    WaitingForLoad,
    Measuring,
    Calculating,
    Finished,
    Stopped,
    Failed,
}

/// <summary>One sensor sample during a calibration, for a live chart.</summary>
public sealed record CalibrationSample(double Seconds, double? CpuTemp, double? GpuTemp, double CpuPower, double GpuPower,
    double CpuLoad, double GpuLoad, string? Foreground);

/// <param name="Run">Current run, 1-based (0 before the runs).</param>
/// <param name="Speeds">Fan speed per group while measuring.</param>
/// <param name="Done">Share of the whole calibration done, 0–1 (roughly).</param>
public sealed record CalibrationProgress(CalibrationStage Stage, string Message, int Run, int Runs,
    IReadOnlyList<double>? Speeds, CalibrationSample? Sample, double Done);

/// <param name="BuiltInLoad">True: AutoFantic's own CPU + GPU load. False: whatever the user runs (a game), waited for.</param>
public sealed record CalibrationOptions(double Ambient, Preset Preset, bool BuiltInLoad)
{
    /// <summary>How long each fan setting is held at least (longer while the result is still uncertain).</summary>
    public TimeSpan Hold { get; init; } = TimeSpan.FromSeconds(BuiltInLoad ? 75 : 90);

    public TimeSpan WaitForLoadUpTo { get; init; } = TimeSpan.FromMinutes(15);
}

public sealed record CalibrationOutcome(bool Success, string Message, CalibrationResult? Result, string? Report);

/// <summary>
/// The whole calibration: find the fans (first time), a fans-off test if the PC is idle, wait for
/// load (or start the built-in one), 9 runs with different fan speeds while fitting how the
/// temperatures follow the power, then add the runs to everything measured before and work the
/// result out again. Every fan is back on BIOS control afterwards, whatever happened.
/// </summary>
public sealed class CalibrationRunner(FanSession session, string folder, CalibrationOptions options)
{
    private static readonly Component[] Modelled = [Component.Cpu, Component.GpuCore, Component.GpuHotspot, Component.GpuMemory];

    // the first seconds after a change are fans spinning up or down, not heat moving
    private const double SkipSeconds = 3;

    // "there is load": the GPU or the CPU is clearly busy
    private const double LoadedGpuPercent = 40;
    private const double LoadedCpuPercent = 25;
    private const int RetriesWithoutLoad = 2;
    private static readonly TimeSpan FansOffFor = TimeSpan.FromSeconds(150);
    private const double FansOffWarmAbove = 62;

    private sealed record Sample(double Seconds, Dictionary<Component, double> Temps, double CpuPower, double GpuPower,
        double CpuLoad, double GpuLoad, double[] GroupRpm, string? Foreground);

    private CancellationToken _cancel;
    private KeySensors _keys = null!;
    private SafeWait _wait = null!;
    private Dictionary<Component, string?> _sensorOf = null!;
    private List<FanGroup> _groups = [];
    private List<List<FanChannel>> _channels = [];
    private int _run, _runs;

    /// <summary>Progress for the UI. Raised on the calibration's thread.</summary>
    public event Action<CalibrationProgress>? Progress;

    /// <summary>One line per notable event, in plain words.</summary>
    public event Action<string>? Log;

    /// <summary>Runs the calibration; blocks until it's done, stopped or failed.</summary>
    public CalibrationOutcome Run(CancellationToken cancel)
    {
        _cancel = cancel;
        try
        {
            return RunCore();
        }
        finally
        {
            session.RestoreAll();
        }
    }

    private CalibrationOutcome RunCore()
    {
        var first = session.Read();
        _keys = KeySensors.Detect(first);
        _wait = new SafeWait(session, _keys, _cancel, Say);
        if (!_wait.CheckNow())
            return Fail($"Not starting: {_wait.StopReason}.");

        _sensorOf = new()
        {
            [Component.Cpu] = _keys.CpuTemp,
            [Component.GpuCore] = _keys.GpuTemp,
            [Component.GpuHotspot] = _keys.GpuHotspot,
            [Component.GpuMemory] = _keys.GpuMemory,
        };

        // 0. which headers really have a fan
        string inventoryPath = Path.Combine(folder, CalibrationFiles.Inventory);
        var inventory = FanInventory.Load(inventoryPath);
        if (inventory is null)
        {
            Report(CalibrationStage.FindingFans, "Finding out which headers have a fan on them (the fans change one by one) …", 0);
            inventory = FanDiscovery.Run(session, _cancel, Say, (channel, done) =>
                Report(CalibrationStage.FindingFans, $"Finding fans: {channel}", 0.1 * done));
            if (inventory is null)
                return Stopped("Find my fans didn't finish.");
            inventory.Save(inventoryPath);
        }

        _groups = inventory.Groups().ToList();
        if (_groups.Count == 0)
            return Fail("No header with a fan on it was found.");
        _channels = _groups.Select(g => g.Headers.Select(h => session.Channels.FirstOrDefault(c => c.Id == h.ControlId)
            ?? throw new InvalidOperationException($"Fan output {h.ControlId} no longer exists: find the fans again.")).ToList()).ToList();

        var plan = CalibrationPlan.Runs(_groups);
        _runs = plan.Count;
        bool idleNow = LoadClassifier.Classify(first.Value(_keys.CpuLoad), first.Value(_keys.GpuLoad)) is LoadClass.Idle;
        string fansOffPath = Path.Combine(folder, CalibrationFiles.FansOff);
        var fansOff = FansOffResult.Load(fansOffPath);
        double? idleCpu = fansOff?.CpuPower, idleGpu = fansOff?.GpuPower;
        bool fansOffFresh = false;

        // 1. fans off: only possible while the PC is idle; otherwise the last result is reused
        if (idleNow)
        {
            idleCpu = first.Value(_keys.CpuPower);
            idleGpu = first.Value(_keys.GpuPower);
            var (result, updated, stop) = FansOffTest(inventory);
            if (stop is WaitEnd.Cancelled)
                return Stopped("Stopped during the fans-off test.");
            if (stop is WaitEnd.SensorError)
                return Fail($"{_wait.StopReason}: calibration stopped.");
            if (result is not null)
            {
                fansOff = result;
                fansOffFresh = true;
                fansOff.Save(fansOffPath);
            }
            if (updated != inventory)
            {
                inventory = updated;
                inventory.Save(inventoryPath);
                _groups = inventory.Groups().ToList(); // same groups, now knowing which fans stop at 0 %
            }
            session.RestoreAll(); // BIOS again until the load is there
        }

        // 2. the load: the built-in one, or whatever the user runs (a game)
        string loadDescription = "your own load";
        using var load = options.BuiltInLoad ? session.StartTestLoad(out loadDescription) : null;
        if (options.BuiltInLoad)
        {
            Say($"Load: {loadDescription}");
        }
        else
        {
            var waited = WaitForLoad();
            if (waited is WaitEnd.Cancelled)
                return Stopped("Stopped while waiting for load.");
            if (waited is not WaitEnd.Until)
                return Fail(waited is WaitEnd.SensorError ? $"{_wait.StopReason}: calibration stopped." : "No load came up: start the calibration again while a game runs.");
        }

        // 3. the runs
        var observations = new List<Observation>();
        var loaded = new List<Sample>();
        var skipped = new List<string>();
        var stoppedAt = new double[_groups.Count];
        for (int r = 0; r < plan.Count; r++)
        {
            _run = r + 1;
            var speeds = plan[r].ToArray();
            bool warmup = r == 0, raisedAfterHeat = false;
            for (int attempt = 0; ; attempt++)
            {
                for (int g = 0; g < _groups.Count; g++)
                    foreach (var channel in _channels[g])
                        session.SetPercent(channel, (float)speeds[g]);

                Say($"Run {_run}/{_runs}{(warmup ? " (warming up)" : "")}: {Describe(speeds)}");
                var (samples, fits, stop) = MeasureRun(speeds, warmup);
                if (stop is WaitEnd.Cancelled)
                    return Stopped($"Stopped in run {_run}. The runs so far are not kept.");
                if (stop is WaitEnd.SensorError)
                    return Fail($"{_wait.StopReason}: calibration stopped.");
                if (stop is WaitEnd.TooHot)
                {
                    if (!raisedAfterHeat && RaiseSlowest(speeds))
                    {
                        raisedAfterHeat = true;
                        Say($"Too hot with these speeds: trying again a bit faster ({Describe(speeds)}).");
                        continue;
                    }
                    skipped.Add(Describe(speeds));
                    Say("Still too hot: run skipped.");
                    break;
                }

                if (!options.BuiltInLoad && !IsLoaded(samples.Average(s => s.CpuLoad), samples.Average(s => s.GpuLoad)))
                {
                    if (attempt < RetriesWithoutLoad)
                    {
                        Say("Hardly any load during this run (game paused or closed?): repeating it.");
                        continue;
                    }
                    Say("Still no load: run skipped.");
                    break;
                }

                // effective speed: a fan that stood still counts as 0 %
                var tail = samples.TakeLast(Math.Max(5, samples.Count / 4)).ToList();
                var effective = speeds.Select((speed, g) =>
                {
                    bool spinning = tail.Average(s => s.GroupRpm[g]) >= 50;
                    if (!spinning && speed > 0)
                        stoppedAt[g] = Math.Max(stoppedAt[g], speed);
                    return spinning ? speed : 0;
                }).ToList();

                double cpuW = samples.Average(s => s.CpuPower), gpuW = samples.Average(s => s.GpuPower);
                var finals = fits.ToDictionary(kv => kv.Key, kv => options.Ambient + (ThermalModel.IsCpu(kv.Key) ? cpuW : gpuW) * kv.Value.Resistance);
                observations.Add(new Observation(effective, cpuW, gpuW, finals));
                loaded.AddRange(samples);
                Say($"   at {cpuW:0} W / {gpuW:0} W this holds the CPU at {T(finals, Component.Cpu)} and the GPU at {T(finals, Component.GpuCore)}"
                    + (fits.Values.All(f => f.Reliable) ? "" : " (less certain)"));
                break;
            }
        }
        session.RestoreAll();

        if (observations.Count == 0)
            return Fail("No run finished: nothing to add.");

        // 4. keep what was learned, then work everything out again
        Report(CalibrationStage.Calculating, "Working out the quietest settings …", 0.97);
        var standstill = _groups.SelectMany((g, i) => g.Headers.Select(h => (h.Channel, Percent: (float)stoppedAt[i]))).Where(x => x.Percent > 0).ToList();
        if (standstill.Count > 0)
        {
            inventory = inventory.WithStandstill(standstill);
            inventory.Save(inventoryPath);
        }

        var now = DateTimeOffset.Now;
        var keys = _groups.Select(MeasurementStore.Key).ToList();
        var newRuns = observations
            .Select(o => new StoredRun(now, options.Ambient, keys.Zip(o.Speeds).ToDictionary(p => p.First, p => p.Second), o.CpuPower, o.GpuPower, o.Final))
            .ToList();
        string loadName = options.BuiltInLoad ? $"built-in load ({loadDescription})" : MostlyRunning(loaded) ?? "your own load";
        double topCpu = Percentile(loaded.Select(s => s.CpuPower), 0.95), topGpu = Percentile(loaded.Select(s => s.GpuPower), 0.95);
        var store = CalibrationFiles.LoadStore(folder, _groups, Say)
            .Add(new StoredCalibration(now, loadName, options.Ambient, topCpu, topGpu, idleCpu, idleGpu, newRuns.Count), newRuns);
        store.Save(Path.Combine(folder, CalibrationFiles.Store));
        Say($"{newRuns.Count} runs added to your measurements ({store.Calibrations.Count} calibrations so far).");

        var outcome = CalibrationFiles.Recalculate(folder, inventory, store, options.Preset, options.Ambient, fansOff, fansOffFresh, skipped);
        if (outcome is not { } done)
            return Fail("Not enough runs to work out curves yet.");

        Report(CalibrationStage.Finished, "Done: the new settings are in use.", 1);
        return new CalibrationOutcome(true, $"Calibration done: {newRuns.Count} runs added.", done.Result, done.Report);
    }

    // ── steps ──────────────────────────────────────────────────────────────────────────

    private (FansOffResult? Result, FanInventory Inventory, WaitEnd? Stop) FansOffTest(FanInventory inventory)
    {
        Say($"Fans-off test: all fans stop for up to 2½ minutes (ends early above {FansOffWarmAbove:0} °C) …");
        foreach (var channel in _channels.SelectMany(c => c))
            session.SetPercent(channel, 0);

        var samples = new List<Sample>();
        var start = session.Now;
        bool tooWarm = false, loadCameUp = false;
        var end = _wait.Wait(FansOffFor, s =>
        {
            var sample = ToSample(s, start);
            samples.Add(sample);
            tooWarm = sample.Temps.Any(kv => kv.Key is Component.Cpu or Component.GpuCore && kv.Value > FansOffWarmAbove);
            loadCameUp = IsLoaded(sample.CpuLoad, sample.GpuLoad);
            Report(CalibrationStage.FansOff, "Fans-off test: every fan stands still", 0.12 * sample.Seconds / FansOffFor.TotalSeconds, sample);
        }, until: () => tooWarm || loadCameUp);

        if (end is WaitEnd.Cancelled or WaitEnd.SensorError)
            return (null, inventory, end);
        if (end is WaitEnd.TooHot || loadCameUp)
        {
            Say(loadCameUp ? "The PC got busy during the test (game started?): fans-off result not used this time." : "Too hot during the fans-off test: result not used.");
            return (null, inventory, null);
        }

        var last = _wait.Last!;
        var elapsed = session.Now - start;
        var summary = new List<string>();
        var updated = inventory;
        if (elapsed >= TimeSpan.FromSeconds(20))
        {
            var rpmAtZero = inventory.Usable.ToDictionary(h => h.Channel, h => last.Value(h.RpmSensorId) ?? 0f);
            updated = inventory.WithRpmAtZero(rpmAtZero);
            foreach (var group in updated.Groups())
            {
                float rpm = group.Headers.Average(h => rpmAtZero.GetValueOrDefault(h.Channel));
                summary.Add(rpm < 50 ? $"{group.Name} stops at 0 %" : $"{group.Name} keeps turning at 0 % (about {rpm:0} rpm), so it can't be switched off");
            }
        }

        var finals = new Dictionary<Component, double>();
        foreach (var component in Modelled)
        {
            var series = samples.Where(s => s.Seconds >= SkipSeconds && s.Temps.ContainsKey(component)).Select(s => (s.Seconds, s.Temps[component])).ToList();
            if (StepResponseFit.Fit(series) is { } fit)
                finals[component] = fit.Final;
        }

        double cpuW = samples.Average(s => s.CpuPower), gpuW = samples.Average(s => s.GpuPower);
        if (tooWarm)
        {
            // stopped early while still rising: at least this warm, likely more
            foreach (var c in new[] { Component.Cpu, Component.GpuCore })
                if (finals.TryGetValue(c, out double v))
                    finals[c] = Math.Max(v, FansOffWarmAbove + 3);
            string which = samples[^1].Temps.Where(kv => kv.Key is Component.Cpu or Component.GpuCore && kv.Value > FansOffWarmAbove)
                .Select(kv => kv.Key == Component.Cpu ? "CPU" : "GPU").FirstOrDefault() ?? "CPU/GPU";
            summary.Add($"without fans at idle (CPU {cpuW:0} W, GPU {gpuW:0} W) the {which} passed {FansOffWarmAbove:0} °C after {elapsed:m\\:ss}: too warm to switch everything off at this idle power");
        }
        else
        {
            summary.Add($"without fans at idle (CPU {cpuW:0} W, GPU {gpuW:0} W): CPU heads for {T(finals, Component.Cpu)}, GPU for {T(finals, Component.GpuCore)}");
        }
        Say(summary[^1]);
        return (new FansOffResult(DateTimeOffset.Now, cpuW, gpuW, finals, summary), updated, null);
    }

    /// <summary>Fans on BIOS control until the user's game (or anything) keeps the PC busy for 20 s.</summary>
    private WaitEnd WaitForLoad()
    {
        Say("Now start your game (or anything that makes the PC work) and play normally. The calibration starts by itself once there is load.");
        var start = session.Now;
        int loadedFor = 0;
        return _wait.Wait(options.WaitForLoadUpTo, s =>
        {
            var sample = ToSample(s, start);
            loadedFor = IsLoaded(sample.CpuLoad, sample.GpuLoad) ? loadedFor + 1 : 0;
            Report(CalibrationStage.WaitingForLoad, $"Waiting for load: start your game (CPU {sample.CpuLoad:0} %, GPU {sample.GpuLoad:0} %)", 0.13, sample);
        }, until: () => loadedFor >= 20);
    }

    private (List<Sample> Samples, Dictionary<Component, PowerFit> Fits, WaitEnd? Stop) MeasureRun(double[] speeds, bool warmup)
    {
        var samples = new List<Sample>();
        var start = session.Now;
        var minHold = warmup ? options.Hold * 1.5 : options.Hold;
        var maxHold = warmup ? options.Hold * 3.5 : options.Hold * 2.5;
        void OnSample(Snapshot s)
        {
            var sample = ToSample(s, start);
            samples.Add(sample);
            double inRun = Math.Min(1, sample.Seconds / minHold.TotalSeconds);
            Report(CalibrationStage.Measuring, $"Run {_run} of {_runs}{(warmup ? " (warming up)" : "")}", 0.15 + 0.8 * (_run - 1 + inRun) / _runs, sample, speeds);
        }

        var end = _wait.Wait(minHold, OnSample);
        var fits = end == WaitEnd.Done ? FitAll(samples) : [];
        while (end == WaitEnd.Done && (fits.Count == 0 || fits.Values.Any(f => !f.Reliable)) && session.Now - start < maxHold)
        {
            end = _wait.Wait(TimeSpan.FromSeconds(15), OnSample);
            if (end == WaitEnd.Done)
                fits = FitAll(samples);
        }
        return (samples, fits, end == WaitEnd.Done ? null : end);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────

    private Dictionary<Component, PowerFit> FitAll(List<Sample> samples)
    {
        var fits = new Dictionary<Component, PowerFit>();
        foreach (var component in Modelled)
        {
            var series = samples
                .Where(s => s.Seconds >= SkipSeconds && s.Temps.ContainsKey(component))
                .Select(s => (s.Seconds, s.Temps[component], ThermalModel.IsCpu(component) ? s.CpuPower : s.GpuPower))
                .ToList();
            if (PowerResponseFit.Fit(series, options.Ambient) is { } fit)
                fits[component] = fit;
        }
        return fits;
    }

    /// <summary>After a too-hot run: the groups at their lowest speed go 20 % faster. False if there is none.</summary>
    private bool RaiseSlowest(double[] speeds)
    {
        bool raised = false;
        for (int g = 0; g < _groups.Count; g++)
        {
            if (speeds[g] <= CalibrationPlan.Levels(_groups[g])[2] && speeds[g] < 100)
            {
                speeds[g] = Math.Min(100, speeds[g] + 20);
                raised = true;
            }
        }
        return raised;
    }

    private Sample ToSample(Snapshot s, DateTimeOffset start)
    {
        var temps = new Dictionary<Component, double>();
        foreach (var (component, id) in _sensorOf)
            if (s.Value(id) is { } v)
                temps[component] = v;

        var rpm = _groups.Select(g => (double)g.Headers.Average(h => s.Value(h.RpmSensorId) ?? 0)).ToArray();
        return new Sample((s.Time - start).TotalSeconds, temps, s.Value(_keys.CpuPower) ?? 0, s.Value(_keys.GpuPower) ?? 0,
            s.Value(_keys.CpuLoad) ?? 0, s.Value(_keys.GpuLoad) ?? 0, rpm, session.Foreground());
    }

    private void Report(CalibrationStage stage, string message, double done, Sample? sample = null, double[]? speeds = null) =>
        Progress?.Invoke(new CalibrationProgress(stage, message, _run, _runs, speeds,
            sample is null ? null : new CalibrationSample(sample.Seconds, sample.Temps.TryGetValue(Component.Cpu, out double c) ? c : null,
                sample.Temps.TryGetValue(Component.GpuCore, out double g) ? g : null, sample.CpuPower, sample.GpuPower, sample.CpuLoad, sample.GpuLoad, sample.Foreground),
            Math.Clamp(done, 0, 1)));

    private void Say(string line) => Log?.Invoke(line);

    private CalibrationOutcome Stopped(string message)
    {
        Say(message);
        Report(CalibrationStage.Stopped, message, 0);
        return new CalibrationOutcome(false, message, null, null);
    }

    private CalibrationOutcome Fail(string message)
    {
        Say(message);
        Report(CalibrationStage.Failed, message, 0);
        return new CalibrationOutcome(false, message, null, null);
    }

    private static bool IsLoaded(double cpuLoad, double gpuLoad) => gpuLoad >= LoadedGpuPercent || cpuLoad >= LoadedCpuPercent;

    private string Describe(IReadOnlyList<double> speeds) =>
        string.Join(" · ", _groups.Select((g, i) => $"{(g.IsGpu ? "GPU" : $"#{g.Headers[0].Channel}")} {speeds[i]:0} %"));

    private static double Percentile(IEnumerable<double> values, double share)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[(int)Math.Round((sorted.Count - 1) * share)];
    }

    private static string? MostlyRunning(List<Sample> samples) =>
        samples.Where(s => s.Foreground is not null).GroupBy(s => s.Foreground).MaxBy(g => g.Count())?.Key;

    private static string T(IReadOnlyDictionary<Component, double> temps, Component c) =>
        temps.TryGetValue(c, out double v) ? $"{v:0.0} °C" : "–";
}
