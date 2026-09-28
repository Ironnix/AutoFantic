using System.IO;
using AutoFantic.Core;
using AutoFantic.Core.Calibration;
using AutoFantic.Core.Control;
using AutoFantic.Core.Hardware;
using AutoFantic.Core.Reports;
using AutoFantic.Core.Simulation;

namespace AutoFantic.App;

/// <summary>
/// Everything the window and the tray icon act on: the hardware, the running fan control, the
/// recommended curves for the chosen preset, the user's own curves on top, and calibrating. No UI
/// in here; events are raised on whatever thread did the work.
/// </summary>
internal sealed class AppController : IDisposable
{
    private CancellationTokenSource? _calibrating;
    private bool _userPaused, _sleeping;

    private AppController(FanSession session, FanControlLoop loop, string runs, FanInventory inventory, CalibrationResult recommended, CurveOverrides overrides)
    {
        Session = session;
        Loop = loop;
        RunsPath = runs;
        Inventory = inventory;
        Recommended = recommended;
        Overrides = overrides;
        Reload();
    }

    public FanSession Session { get; }

    public FanControlLoop Loop { get; }

    public string RunsPath { get; }

    public FanInventory Inventory { get; private set; }

    public MeasurementStore Store { get; private set; } = MeasurementStore.Empty;

    public FansOffResult? FansOff { get; private set; }

    /// <summary>The calibrated curves for the current preset.</summary>
    public CalibrationResult Recommended { get; private set; }

    public CurveOverrides Overrides { get; private set; }

    /// <summary>What the fans run by: the recommended curves with the user's own curves in place.</summary>
    public CalibrationResult Effective => Overrides.ApplyTo(Recommended);

    public Preset Preset => Preset.For(Recommended.Profile);

    public string PagePath => Path.Combine(RunsPath, CalibrationFiles.Page);

    /// <summary>Presets can be worked out again only from stored measurements.</summary>
    public bool CanSwitchPreset => Store.Calibrations.Count > 0;

    public bool Calibrating => _calibrating is not null;

    /// <summary>The curves changed (preset, a curve set by hand, a calibration). Raised on the thread that changed them.</summary>
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
            _userPaused = value;
            UpdatePause();
        }
    }

    /// <summary>The PC is going to sleep: the BIOS has the fans until it wakes.</summary>
    public bool Sleeping
    {
        get => _sleeping;
        set
        {
            _sleeping = value;
            UpdatePause();
        }
    }

    private void UpdatePause() => Loop.Paused = _userPaused || _sleeping || Calibrating;

    /// <summary>Loads the calibration and starts controlling. Null with the reason if that's not possible.</summary>
    /// <param name="simSpeed">Simulated seconds per real second (only with <paramref name="simulate"/>; for self-tests).</param>
    public static AppController? Create(bool simulate, out string? problem, double simSpeed = 1)
    {
        string runs = DataFolder.Default(simulate);
        var calibration = CalibrationResult.Load(Path.Combine(runs, CalibrationFiles.Result));
        var inventory = FanInventory.Load(Path.Combine(runs, CalibrationFiles.Inventory));
        if (calibration is null || inventory is null)
        {
            problem = "There is no calibration yet.\n\nFor now the first calibration runs in the test tool: start it with \"autofantic-spike test\" (1 Find my fans, 2 Calibrate). After that AutoFantic does everything here.";
            return null;
        }

        var overrides = CurveOverrides.Load(Path.Combine(runs, CalibrationFiles.Curves));
        FanSession session = simulate ? new SimulatedPc(timeScale: simSpeed, load: SimLoad.Session) : new HardwareSession();
        try
        {
            var effective = overrides.ApplyTo(calibration);
            var loop = new FanControlLoop(session, effective, FanControlLoop.MinSpinning(effective, inventory));
            problem = null;
            return new AppController(session, loop, runs, inventory, calibration, overrides);
        }
        catch (InvalidOperationException ex)
        {
            session.Dispose();
            problem = ex.Message;
            return null;
        }
    }

    private void Reload()
    {
        Store = MeasurementStore.Load(Path.Combine(RunsPath, CalibrationFiles.Store)) ?? MeasurementStore.Empty;
        FansOff = FansOffResult.Load(Path.Combine(RunsPath, CalibrationFiles.FansOff));
    }

    // ── curves and presets ─────────────────────────────────────────────────────────────

    public bool IsCustom(int group) => Overrides.For(Recommended.Groups[group]) is not null;

    /// <summary>The group's fans measurably stood still at 0 % (fans.json): only then can they be switched off.</summary>
    public bool CanStop(int group) =>
        Inventory.Groups().FirstOrDefault(k => k.Headers.Select(h => h.ControlId).SequenceEqual(Recommended.Groups[group].ControlIds))?.CanStop == true;

    /// <summary>The calibration itself switches this group off at idle (the fans-off test found that safe).</summary>
    public bool RecommendsStop(int group) => Recommended.Groups[group].OffAt.Count > 0;

    public bool AllowsStop(int group) => Effective.Groups[group].OffAt.Count > 0;

    /// <summary>Works the curves out again for another preset from the stored measurements and uses them.</summary>
    public bool SwitchPreset(Preset preset) => Recalculate(preset);

    public void SetCurve(int group, IReadOnlyList<CurvePoint> curve, bool allowStop)
    {
        Overrides = Overrides.With(Recommended.Groups[group], new CurveOverride(curve, allowStop && CanStop(group)));
        Overrides.Save(Path.Combine(RunsPath, CalibrationFiles.Curves));
        Apply();
    }

    public void ResetCurve(int group)
    {
        Overrides = Overrides.Without(Recommended.Groups[group]);
        Overrides.Save(Path.Combine(RunsPath, CalibrationFiles.Curves));
        Apply();
    }

    /// <summary>What the user says about a group's fans (how many, how loud); the quietest mix is worked out again.</summary>
    public void SetLoudness(int group, int fanCount, double loudnessDb)
    {
        var groups = Inventory.Groups();
        if (group >= groups.Count)
            return;
        Inventory = Inventory.WithLoudness(groups[group], fanCount, loudnessDb);
        Inventory.Save(Path.Combine(RunsPath, CalibrationFiles.Inventory));
        Recalculate(Preset);
    }

    private bool Recalculate(Preset preset)
    {
        if (Store.Calibrations.Count == 0)
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
        var effective = Effective;
        Loop.UseCalibration(effective, FanControlLoop.MinSpinning(effective, Inventory));
        CurvesChanged?.Invoke();
    }

    public IReadOnlyList<LevelInsight> Levels() => CalibrationInsights.Levels(Recommended, Inventory, Preset.Profile);

    public IReadOnlyList<RunInsight> Runs() => CalibrationInsights.Runs(Store, Inventory.Groups(), Preset.Profile);

    // ── calibrating ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a calibration in the background. The fan control pauses (the calibration drives the
    /// fans itself) and takes over again with the new result.
    /// </summary>
    public void StartCalibration(double ambient, bool builtInLoad)
    {
        if (_calibrating is not null)
            return;
        _calibrating = new CancellationTokenSource();
        var cancel = _calibrating.Token;
        UpdatePause();

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
            var effective = Effective;
            Loop.UseCalibration(effective, FanControlLoop.MinSpinning(effective, Inventory));
            UpdatePause();
            CurvesChanged?.Invoke();
            CalibrationEnded?.Invoke(outcome);
        })
        { IsBackground = true, Name = "AutoFantic calibration" };
        thread.Start();
    }

    public void StopCalibration() => _calibrating?.Cancel();

    /// <summary>
    /// "Find my fans" again, in the background: runs every fan output through a few speeds. What
    /// the user said about the fans (how many, how loud) is kept. If the fans are different now,
    /// the curves still run by the old calibration until the next one.
    /// </summary>
    public void StartFindFans()
    {
        if (_calibrating is not null)
            return;
        _calibrating = new CancellationTokenSource();
        var cancel = _calibrating.Token;
        UpdatePause();

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
                else
                {
                    // keep what the user said about each output's fans
                    var said = Inventory.Headers.ToDictionary(h => h.ControlId);
                    found = found with
                    {
                        Headers = found.Headers.Select(h => said.TryGetValue(h.ControlId, out var old) ? h with { FanCount = old.FanCount, LoudnessDb = old.LoudnessDb } : h).ToList(),
                    };
                    bool same = found.Groups().Select(MeasurementStore.Key).SequenceEqual(Inventory.Groups().Select(MeasurementStore.Key));
                    found.Save(Path.Combine(RunsPath, CalibrationFiles.Inventory));
                    Inventory = found;
                    outcome = new CalibrationOutcome(true, same
                        ? $"Found the same {found.Groups().Count} fan groups as before."
                        : $"Your fans are different now ({string.Join(", ", found.Groups().Select(g => g.Name))}): please calibrate again.", null, null);
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
    }
}
