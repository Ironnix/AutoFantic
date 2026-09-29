using System.IO;
using AutoFantic.Core;
using AutoFantic.Core.Calibration;
using AutoFantic.Core.Control;
using AutoFantic.Core.Hardware;
using AutoFantic.Core.Logging;
using AutoFantic.Core.Reports;
using AutoFantic.Core.Simulation;

namespace AutoFantic.App;

/// <summary>
/// Everything the window and the tray icon act on: the hardware, the running fan control, the
/// recommended curves for the chosen preset, the user's own curves on top, calibrating, the checks
/// and the log. Before the first calibration there are no curves (<see cref="IsSetUp"/> is false):
/// the BIOS keeps the fans and the window offers "Find my fans" and "Calibrate". No UI in here;
/// events are raised on whatever thread did the work.
/// </summary>
internal sealed class AppController : IDisposable
{
    private CancellationTokenSource? _calibrating;
    private bool _userPaused, _sleeping;

    private AppController(FanSession session, FanControlLoop loop, string runs, ActivityLog log, FanInventory? inventory, CalibrationResult? recommended, CurveOverrides overrides)
    {
        Session = session;
        Loop = loop;
        RunsPath = runs;
        Log = log;
        Inventory = inventory;
        Recommended = recommended;
        Overrides = overrides;
        Reload();
        RefreshChecks();
    }

    public FanSession Session { get; }

    public FanControlLoop Loop { get; }

    public string RunsPath { get; }

    /// <summary>What AutoFantic did: safety stops, sensor problems, fans off and on, calibrations …</summary>
    public ActivityLog Log { get; }

    /// <summary>What "Find my fans" found; null before it ran.</summary>
    public FanInventory? Inventory { get; private set; }

    public MeasurementStore Store { get; private set; } = MeasurementStore.Empty;

    public FansOffResult? FansOff { get; private set; }

    /// <summary>The calibrated curves for the current preset; null before the first calibration.</summary>
    public CalibrationResult? Recommended { get; private set; }

    /// <summary>True once there is a calibration: then AutoFantic drives the fans.</summary>
    public bool IsSetUp => Recommended is not null;

    public CurveOverrides Overrides { get; private set; }

    /// <summary>What the fans run by: the recommended curves with the user's own curves in place; null before the first calibration.</summary>
    public CalibrationResult? Effective => Recommended is null ? null : Overrides.ApplyTo(Recommended);

    /// <summary>The fan groups the window shows: the calibrated ones, or before that what "Find my fans" found.</summary>
    public IReadOnlyList<string> GroupNames =>
        Recommended?.Groups.Select(g => g.Name).ToList() ?? Inventory?.Groups().Select(g => g.Name).ToList() ?? [];

    /// <summary>The preset in use; Balanced before the first calibration.</summary>
    public Preset Preset => Preset.For(Recommended?.Profile);

    public string PagePath => Path.Combine(RunsPath, CalibrationFiles.Page);

    /// <summary>Presets can be worked out again only from stored measurements.</summary>
    public bool CanSwitchPreset => IsSetUp && Store.Calibrations.Count > 0;

    public bool Calibrating => _calibrating is not null;

    /// <summary>What the start-up checks found (fan chip and driver, GPU fans, sensors, other fan programs).</summary>
    public IReadOnlyList<SetupCheck> Checks { get; private set; } = [];

    /// <summary>The curves changed (preset, a curve set by hand, a calibration, the fans found). Raised on the thread that changed them.</summary>
    public event Action? CurvesChanged;

    public event Action<CalibrationProgress>? CalibrationProgress;

    public event Action<string>? CalibrationLog;

    /// <summary>A calibration ended; the message says how. Raised on the calibration's thread.</summary>
    public event Action<CalibrationOutcome>? CalibrationEnded;

    /// <summary>Paused by the user: the BIOS has the fans until resumed.</summary>
    public bool UserPaused
    {
        get => _userPaused;
        set
        {
            if (_userPaused == value)
                return;
            _userPaused = value;
            UpdatePause();
            Log.Add(LogKind.Info, value ? "Paused: the BIOS controls the fans." : "Resumed: AutoFantic controls the fans again.");
        }
    }

    /// <summary>The PC is going to sleep: the BIOS has the fans until it wakes.</summary>
    public bool Sleeping
    {
        get => _sleeping;
        set
        {
            if (_sleeping == value)
                return;
            _sleeping = value;
            UpdatePause();
            Log.Add(LogKind.Info, value ? "Going to sleep: the fans are handed to the BIOS." : "Woke up: AutoFantic takes the fans back.");
        }
    }

    private void UpdatePause() => Loop.Paused = _userPaused || _sleeping || Calibrating;

    /// <summary>
    /// Opens the hardware, hands back whatever a crashed run left behind, loads the calibration (if
    /// there is one) and prepares the fan control. Null with the reason if the hardware can't be used.
    /// </summary>
    /// <param name="simSpeed">Simulated seconds per real second (only with <paramref name="simulate"/>; for self-tests).</param>
    public static AppController? Create(bool simulate, out string? problem, double simSpeed = 1)
    {
        string runs = DataFolder.Default(simulate);
        var log = new ActivityLog(runs);

        FanSession session;
        try
        {
            session = simulate ? new SimulatedPc(timeScale: simSpeed, load: SimLoad.Session) : new HardwareSession();
        }
        catch (Exception ex)
        {
            problem = $"AutoFantic can't open the hardware: {ex.Message}";
            log.Add(LogKind.Warning, problem);
            return null;
        }

        // the watchdog normally did this already; if it didn't run, a crashed run's fans go back now
        Handback.RecoverIfNeeded(runs, session, log, "AutoFantic at its start");
        session.HandbackPath = Handback.PathIn(runs);

        var inventory = FanInventory.Load(Path.Combine(runs, CalibrationFiles.Inventory));
        var calibration = inventory is null ? null : CalibrationResult.Load(Path.Combine(runs, CalibrationFiles.Result));
        var overrides = CurveOverrides.Load(Path.Combine(runs, CalibrationFiles.Curves));

        FanControlLoop loop;
        try
        {
            var effective = calibration is null ? null : overrides.ApplyTo(calibration);
            loop = new FanControlLoop(session, effective, effective is null ? [] : FanControlLoop.MinSpinning(effective, inventory!), log);
        }
        catch (InvalidOperationException ex)
        {
            // the fans changed since the calibration (another mainboard, a GPU swapped): set up again
            log.Add(LogKind.Warning, $"{ex.Message} Until then the BIOS controls the fans.");
            calibration = null;
            inventory = null;
            loop = new FanControlLoop(session, null, [], log);
        }

        problem = null;
        var app = new AppController(session, loop, runs, log, inventory, calibration, overrides)
        {
            UnexpectedEnd = LastRunEndedBadly(log),
        };
        log.Add(LogKind.Info, $"AutoFantic started{(simulate ? " (simulated PC)" : "")}: "
            + (app.IsSetUp ? $"{app.Preset.Name}, {app.Effective!.Groups.Count} fan groups." : "not set up yet, the BIOS controls the fans."));
        foreach (var check in app.Checks.Where(c => c.Result >= CheckResult.Warning))
            log.Add(LogKind.Warning, $"{check.Title}: {check.Detail}");
        app.UpgradeIfOld();
        return app;
    }

    /// <summary>Set when the run before this one ended without handing the fans back (and the watchdog or this start did it): what happened.</summary>
    public string? UnexpectedEnd { get; private init; }

    // a watchdog entry after the previous run's start: that run was killed or crashed
    private static string? LastRunEndedBadly(ActivityLog log)
    {
        var entries = log.Entries;
        int previousStart = -1;
        for (int i = entries.Count - 1; i >= 0 && previousStart < 0; i--)
            if (entries[i].Kind == LogKind.Info && entries[i].Text.StartsWith("AutoFantic started", StringComparison.Ordinal))
                previousStart = i;
        return entries.Skip(previousStart + 1).LastOrDefault(e => e.Kind == LogKind.Watchdog)?.Text;
    }

    /// <summary>
    /// A result made with older rules (steeper curves, case fans on the CPU only) is worked out
    /// again from the stored measurements, so an update takes effect without calibrating again.
    /// </summary>
    private void UpgradeIfOld()
    {
        if (Recommended is not { } old || old.Version >= CalibrationResult.CurrentVersion || !CanSwitchPreset)
            return;
        if (Recalculate(Preset))
            Log.Add(LogKind.Info, "The curves were worked out again with this version's rules: smoother curves, case fans follow the warmer of CPU and GPU.");
    }

    private void Reload()
    {
        Store = MeasurementStore.Load(Path.Combine(RunsPath, CalibrationFiles.Store)) ?? MeasurementStore.Empty;
        FansOff = FansOffResult.Load(Path.Combine(RunsPath, CalibrationFiles.FansOff));
    }

    /// <summary>Runs the start-up checks again (after installing a driver, closing another fan program).</summary>
    public void RefreshChecks() =>
        Checks = SystemCheck.Run(Session, Loop.Keys, Session is SimulatedPc || SystemCheck.PawnIoInstalled(), FanToolCheck.Running());

    // ── curves and presets ─────────────────────────────────────────────────────────────

    public bool IsCustom(int group) => Recommended is { } r && Overrides.For(r.Groups[group]) is not null;

    /// <summary>The group's fans measurably stood still at 0 % (fans.json): only then can they be switched off.</summary>
    public bool CanStop(int group) =>
        Recommended is { } r && Inventory?.Groups().FirstOrDefault(k => k.Headers.Select(h => h.ControlId).SequenceEqual(r.Groups[group].ControlIds))?.CanStop == true;

    /// <summary>The calibration itself switches this group off at idle (the fans-off test found that safe).</summary>
    public bool RecommendsStop(int group) => Recommended?.Groups[group].OffAt.Count > 0;

    public bool AllowsStop(int group) => Effective?.Groups[group].OffAt.Count > 0;

    /// <summary>Works the curves out again for another preset from the stored measurements and uses them.</summary>
    public bool SwitchPreset(Preset preset)
    {
        bool done = Recalculate(preset);
        if (done)
            Log.Add(LogKind.Info, $"Preset {preset.Name}: {preset.Description}");
        return done;
    }

    public void SetCurve(int group, IReadOnlyList<CurvePoint> curve, bool allowStop)
    {
        if (Recommended is not { } r)
            return;
        Overrides = Overrides.With(r.Groups[group], new CurveOverride(curve, allowStop && CanStop(group)));
        Overrides.Save(Path.Combine(RunsPath, CalibrationFiles.Curves));
        Apply();
    }

    public void ResetCurve(int group)
    {
        if (Recommended is not { } r)
            return;
        Overrides = Overrides.Without(r.Groups[group]);
        Overrides.Save(Path.Combine(RunsPath, CalibrationFiles.Curves));
        Apply();
    }

    /// <summary>What the user says about a group's fans (how many, how loud); the quietest mix is worked out again.</summary>
    public void SetLoudness(int group, int fanCount, double loudnessDb)
    {
        var groups = Inventory?.Groups();
        if (groups is null || group >= groups.Count)
            return;
        Inventory = Inventory!.WithLoudness(groups[group], fanCount, loudnessDb);
        Inventory.Save(Path.Combine(RunsPath, CalibrationFiles.Inventory));
        Recalculate(Preset);
    }

    private bool Recalculate(Preset preset)
    {
        if (Store.Calibrations.Count == 0 || Inventory is null || Recommended is null)
            return false;
        var done = CalibrationFiles.Recalculate(RunsPath, Inventory, Store, preset, Recommended.Ambient, FansOff);
        if (done is not { } result)
            return false;
        Recommended = result.Result;
        Apply();
        return true;
    }

    private void Apply()
    {
        if (Effective is { } effective && Inventory is not null)
            Loop.UseCalibration(effective, FanControlLoop.MinSpinning(effective, Inventory));
        CurvesChanged?.Invoke();
    }

    public IReadOnlyList<LevelInsight> Levels() =>
        Recommended is { } r && Inventory is not null ? CalibrationInsights.Levels(r, Inventory, Preset.Profile) : [];

    public IReadOnlyList<RunInsight> Runs() =>
        Inventory is not null ? CalibrationInsights.Runs(Store, Inventory.Groups(), Preset.Profile) : [];

    // ── calibrating ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a calibration in the background. The fan control pauses (the calibration drives the
    /// fans itself) and takes over again with the new result. The first one also finds the fans
    /// if that hasn't been done.
    /// </summary>
    public void StartCalibration(double ambient, bool builtInLoad)
    {
        if (_calibrating is not null)
            return;
        _calibrating = new CancellationTokenSource();
        var cancel = _calibrating.Token;
        UpdatePause();
        Log.Add(LogKind.Calibration, $"Calibration started with {(builtInLoad ? "the built-in load" : "your own load (a game)")}, room {ambient:0} °C.");

        var runner = new CalibrationRunner(Session, RunsPath, new CalibrationOptions(ambient, Preset, builtInLoad));
        runner.Progress += p => CalibrationProgress?.Invoke(p);
        runner.Log += line => CalibrationLog?.Invoke(line);

        var thread = new Thread(() =>
        {
            CalibrationOutcome outcome;
            try
            {
                outcome = runner.Run(cancel);
            }
            catch (Exception ex)
            {
                outcome = new CalibrationOutcome(false, $"Calibration failed: {ex.Message}", null, null);
            }

            Inventory = FanInventory.Load(Path.Combine(RunsPath, CalibrationFiles.Inventory)) ?? Inventory;
            Reload();
            if (outcome.Result is { } result)
                Recommended = result;

            _calibrating.Dispose();
            _calibrating = null;
            if (Effective is { } effective && Inventory is not null)
                Loop.UseCalibration(effective, FanControlLoop.MinSpinning(effective, Inventory));
            UpdatePause();
            Log.Add(outcome.Success ? LogKind.Calibration : LogKind.Warning, outcome.Message);
            CurvesChanged?.Invoke();
            CalibrationEnded?.Invoke(outcome);
        })
        { IsBackground = true, Name = "AutoFantic calibration" };
        thread.Start();
    }

    public void StopCalibration() => _calibrating?.Cancel();

    /// <summary>
    /// "Find my fans" in the background: runs every fan output through a few speeds. What the user
    /// said about the fans (how many, how loud) is kept. If the fans are different now, the curves
    /// still run by the old calibration until the next one.
    /// </summary>
    public void StartFindFans()
    {
        if (_calibrating is not null)
            return;
        _calibrating = new CancellationTokenSource();
        var cancel = _calibrating.Token;
        UpdatePause();
        Log.Add(LogKind.Calibration, "Find my fans started.");

        var thread = new Thread(() =>
        {
            CalibrationOutcome outcome;
            try
            {
                var found = FanDiscovery.Run(Session, cancel, line => CalibrationLog?.Invoke(line), (channel, done) =>
                    CalibrationProgress?.Invoke(new CalibrationProgress(CalibrationStage.FindingFans, $"Finding fans: {channel}", 0, 0, null, null, done)));
                if (found is null)
                {
                    outcome = new CalibrationOutcome(false, "Find my fans didn't finish. Nothing changed.", null, null);
                }
                else if (found.Groups().Count == 0)
                {
                    outcome = new CalibrationOutcome(false, "No fan reacted on any output. Are the fans connected to the mainboard (not to a separate fan hub with its own software)?", null, null);
                }
                else
                {
                    // keep what the user said about each output's fans
                    var said = Inventory?.Headers.ToDictionary(h => h.ControlId) ?? [];
                    found = found with
                    {
                        Headers = found.Headers.Select(h => said.TryGetValue(h.ControlId, out var old) ? h with { FanCount = old.FanCount, LoudnessDb = old.LoudnessDb } : h).ToList(),
                    };
                    bool first = Inventory is null;
                    bool same = !first && found.Groups().Select(MeasurementStore.Key).SequenceEqual(Inventory!.Groups().Select(MeasurementStore.Key));
                    found.Save(Path.Combine(RunsPath, CalibrationFiles.Inventory));
                    Inventory = found;
                    string names = string.Join(", ", found.Groups().Select(g => g.Name));
                    outcome = new CalibrationOutcome(true,
                        first ? $"Found {found.Groups().Count} fan groups: {names}. Next: calibrate."
                        : same ? $"Found the same {found.Groups().Count} fan groups as before."
                        : $"Your fans are different now ({names}): please calibrate again.", null, null);
                }
            }
            catch (Exception ex)
            {
                outcome = new CalibrationOutcome(false, $"Find my fans failed: {ex.Message}", null, null);
            }

            Session.RestoreAll();
            _calibrating.Dispose();
            _calibrating = null;
            UpdatePause();
            Log.Add(outcome.Success ? LogKind.Calibration : LogKind.Warning, outcome.Message);
            CurvesChanged?.Invoke();
            CalibrationEnded?.Invoke(outcome);
        })
        { IsBackground = true, Name = "AutoFantic find fans" };
        thread.Start();
    }

    /// <summary>Every sensor with its current value, as text (for the developer view).</summary>
    public string SensorList()
    {
        var snapshot = Session.Read();
        var text = new System.Text.StringBuilder($"AutoFantic sensors · {snapshot.Time:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{Environment.NewLine}");
        foreach (var hardware in snapshot.Readings.GroupBy(r => (r.Hardware, r.HardwareType)))
        {
            text.AppendLine($"== {hardware.Key.Hardware} ({hardware.Key.HardwareType})");
            foreach (var r in hardware.OrderBy(r => r.Kind))
                text.AppendLine($"   {r.Kind,-11} {r.Name,-28} {(r.Value is { } v ? v.ToString("0.0") : "–"),10}   {r.Id}");
            text.AppendLine();
        }
        text.AppendLine("== Fan outputs");
        foreach (var channel in Session.Channels)
            text.AppendLine($"   #{channel.Index,-2} {channel.Hardware} / {channel.Name,-24} {channel.Percent:0} %   {(channel.IsSoftwareControlled ? "AutoFantic" : "BIOS")}   {channel.Id}");
        return text.ToString();
    }

    public void Dispose()
    {
        _calibrating?.Cancel();
        Loop.Dispose();
        Session.Dispose();
        Log.Add(LogKind.Info, "AutoFantic exited: the fans are back on BIOS control.");
    }
}
