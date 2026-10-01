using System.IO;
using AutoFantic.Core;
using AutoFantic.Core.Calibration;
using AutoFantic.Core.Control;
using AutoFantic.Core.Hardware;
using AutoFantic.Core.Logging;
using AutoFantic.Core.Monitoring;
using AutoFantic.Core.Reports;
using AutoFantic.Core.Simulation;
using AutoFantic.Core.Updates;
using static AutoFantic.Core.Texts;

namespace AutoFantic.App;

/// <summary>
/// Everything the window and the tray icon act on: the hardware, the running fan control, the
/// recommended curves for the chosen preset, the user's own curves on top, calibrating, the checks,
/// the log and the monitor's history. Before the first calibration there are no curves (<see cref="IsSetUp"/> is false):
/// the BIOS keeps the fans and the window offers "Find my fans" and "Calibrate". No UI in here;
/// events are raised on whatever thread did the work.
/// </summary>
internal sealed class AppController : IDisposable
{
    private CancellationTokenSource? _calibrating;
    private bool _userPaused, _sleeping;
    private string? _quietReason;
    private System.Threading.Timer? _healthTimer, _updateTimer;
    private Version? _loggedUpdate;
    private readonly bool _pawnIoAtStart;

    private AppController(FanSession session, FanControlLoop loop, string runs, ActivityLog log, HistoryRecorder monitor, FanInventory? inventory, CalibrationResult? recommended, CurveOverrides overrides, bool pawnIoAtStart)
    {
        _pawnIoAtStart = pawnIoAtStart;
        Session = session;
        Loop = loop;
        RunsPath = runs;
        Log = log;
        Monitor = monitor;
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

    /// <summary>The history of temperatures, power and fans (history.db), and the user's warnings.</summary>
    public HistoryRecorder Monitor { get; }

    /// <summary>Something to show next to the clock right away (a warning the user set). Raised on any thread.</summary>
    public event Action<string>? Alert;

    /// <summary>When the fans should be extra quiet (away, at night).</summary>
    public QuietSettings Quiet { get; private set; } = new();

    /// <summary>Why the fans are extra quiet right now ("away", "night"); null = normal.</summary>
    public string? QuietReason => _quietReason;

    /// <summary>The health of every day so far was worked out again (in the background).</summary>
    public event Action? HealthUpdated;

    /// <summary>
    /// The calibration the cooling health is measured against: the time of the latest measured
    /// calibration (switching presets doesn't change it; calibrating again does).
    /// </summary>
    public DateTimeOffset? HealthReference =>
        (Store.Calibrations.Count > 0 ? Store.Calibrations.Max(c => c.Time) : Recommended?.Created) is { } time
            ? DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds()) // whole seconds, as stored with each day
            : null;

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
            Log.Add(LogKind.Info, value ? T("Paused: the BIOS controls the fans.") : T("Resumed: AutoFantic controls the fans again."));
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
            Log.Add(LogKind.Info, value ? T("Going to sleep: the fans are handed to the BIOS.") : T("Woke up: AutoFantic takes the fans back."));
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

        // the hardware is opened once: a driver installed later takes a restart of AutoFantic
        bool pawnIo = simulate || SystemCheck.PawnIoInstalled();
        FanSession session;
        try
        {
            session = simulate ? new SimulatedPc(timeScale: simSpeed, load: SimLoad.Session) : new HardwareSession();
        }
        catch (Exception ex)
        {
            problem = T($"AutoFantic can't open the hardware: {ex.Message}");
            log.Add(LogKind.Warning, problem);
            return null;
        }

        // the watchdog normally did this already; if it didn't run, a crashed run's fans go back now
        Handback.RecoverIfNeeded(runs, session, log, T("AutoFantic at its start"));
        session.HandbackPath = Handback.PathIn(runs);

        var inventory = FanInventory.Load(Path.Combine(runs, CalibrationFiles.Inventory));
        if (inventory?.Fits(session.Channels) == false)
        {
            // e.g. found without the PawnIO driver (no mainboard outputs yet), or a graphics card or water cooler swapped
            log.Add(LogKind.Warning, T("The PC's fan outputs are not the ones found before (a driver, a graphics card or a water cooler changed): find the fans and calibrate again. Until then the BIOS controls the fans."));
            inventory = null;
        }
        var calibration = inventory is null ? null : CalibrationResult.Load(Path.Combine(runs, CalibrationFiles.Result));
        var overrides = CurveOverrides.Load(Path.Combine(runs, CalibrationFiles.Curves));

        FanControlLoop loop;
        try
        {
            var effective = calibration is null ? null : overrides.ApplyTo(calibration);
            loop = new FanControlLoop(session, effective, effective is null ? [] : FanControlLoop.MinSpinning(effective, inventory!), log,
                effective is null ? null : FanControlLoop.CanStop(effective, inventory!));
        }
        catch (InvalidOperationException ex)
        {
            // the fans changed since the calibration (another mainboard, a GPU swapped): set up again
            log.Add(LogKind.Warning, T($"{ex.Message} Until then the BIOS controls the fans."));
            calibration = null;
            inventory = null;
            loop = new FanControlLoop(session, null, [], log);
        }

        problem = null;
        var app = new AppController(session, loop, runs, log, OpenMonitor(runs, loop, log), inventory, calibration, overrides, pawnIo)
        {
            UnexpectedEnd = LastRunEndedBadly(log),
        };
        log.Add(LogKind.Info, T($"AutoFantic {AppVersion.Text} started{(simulate ? T(" (simulated PC)") : "")}: "
            + $"{(app.IsSetUp ? T($"{T(app.Preset.Name)}, {app.Effective!.Groups.Count} fan groups.") : T("not set up yet, the BIOS controls the fans."))}"));
        foreach (var check in app.Checks.Where(c => c.Result >= CheckResult.Warning))
            log.Add(LogKind.Warning, $"{check.Title}: {check.Detail}");
        app.UpgradeIfOld();
        app.Monitor.Warning += message =>
        {
            app.Log.Add(LogKind.Warning, message);
            app.Alert?.Invoke(message);
        };
        app.UseFansInMonitor();
        app.Quiet = QuietSettings.Load(Path.Combine(runs, QuietSettings.FileName));
        app.Updates = UpdateSettings.Load(Path.Combine(runs, UpdateSettings.FileName));
        app.Appearance = AppearanceSettings.Load(Path.Combine(runs, AppearanceSettings.FileName));
        UpdateInstaller.CleanUp(AppContext.BaseDirectory, app.UpdateWork); // what an update left behind
        app.Monitor.SessionEnded += ended => app.Log.Add(LogKind.Info, Describe(ended));
        loop.Sampled += (snapshot, status) =>
        {
            app.Monitor.Preset = app.Recommended?.Profile ?? "";
            app.Monitor.Record(snapshot, status, session.Foreground());
            app.UpdateQuiet();
        };
        // the days since the calibration: now (in the background) and every hour after
        app.FanWearSettings = FanWearSettings.Load(Path.Combine(runs, FanWearSettings.FileName));
        app._healthTimer = new System.Threading.Timer(_ =>
        {
            app.UpdateHealth();
            app.UpdateFanWear();
        }, null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(1));
        return app;
    }

    /// <summary>A session in one line for the log: "VALORANT: 42 min, GPU 74 °C on average, hotspot up to 88 °C".</summary>
    private static string Describe(GameSession session)
    {
        string Avg(Series series) => session.Stats.TryGetValue(series.Key, out var s) ? $"{s.Avg:0} {series.Unit}" : "–";
        string Max(Series series) => session.Stats.TryGetValue(series.Key, out var s) ? $"{s.Max:0} {series.Unit}" : "–";
        return T($"{Sessions.Pretty(session.Program)}: {session.Length.TotalMinutes:0} min, GPU {Avg(HistoryRecorder.GpuTemp)} on average, "
            + $"hotspot up to {Max(HistoryRecorder.GpuHotspot)}, CPU {Avg(HistoryRecorder.CpuTemp)} on average ({T(session.Preset)}).");
    }

    // ── extra quiet ────────────────────────────────────────────────────────────────────

    public void SaveQuiet(QuietSettings quiet)
    {
        Quiet = quiet;
        quiet.Save(Path.Combine(RunsPath, QuietSettings.FileName));
        UpdateQuiet();
    }

    /// <summary>After every step: away or night starts or ends the quiet mode.</summary>
    private void UpdateQuiet()
    {
        string? reason = IsSetUp ? Quiet.Reason(DateTime.Now, UserIdle.For()) : null;
        if (reason == _quietReason)
            return;
        _quietReason = reason;
        Loop.Quiet = reason;
        Log.Add(LogKind.Fans, reason switch
        {
            "away" => T($"Extra quiet: nobody at the PC for {Quiet.AwayMinutes} min. Every fan at its slowest, the ones that can stop off, while it stays cool."),
            "night" => T($"Extra quiet for the night (until {Quiet.NightTo}). Every fan at its slowest, the ones that can stop off, while it stays cool."),
            _ => T("Normal again: the fans follow their curves."),
        });
    }

    // ── appearance ─────────────────────────────────────────────────────────────────────

    /// <summary>Light, dark or like Windows.</summary>
    public AppearanceSettings Appearance { get; private set; } = new();

    public void SaveAppearance(AppearanceSettings appearance)
    {
        Appearance = appearance;
        appearance.Save(Path.Combine(RunsPath, AppearanceSettings.FileName));
        appearance.Apply();
    }

    // ── updates ────────────────────────────────────────────────────────────────────────

    /// <summary>Whether AutoFantic checks for a new version by itself.</summary>
    public UpdateSettings Updates { get; private set; } = new();

    /// <summary>A newer version on GitHub, from the last check that worked; null if there is none.</summary>
    public Release? UpdateAvailable { get; private set; }

    /// <summary>When the last check ended; null before the first.</summary>
    public DateTimeOffset? UpdateChecked { get; private set; }

    /// <summary>Why the last check failed (no internet, GitHub didn't answer); null if it worked.</summary>
    public string? UpdateProblem { get; private set; }

    /// <summary>The version this one was updated from, right after an update; null otherwise.</summary>
    public string? UpdatedFrom { get; set; }

    /// <summary>A check ended. Raised on any thread.</summary>
    public event Action? UpdateStateChanged;

    public void SaveUpdateSettings(UpdateSettings updates)
    {
        Updates = updates;
        updates.Save(Path.Combine(RunsPath, UpdateSettings.FileName));
    }

    /// <summary>Checks a minute after the start and then once a day, while <see cref="UpdateSettings.CheckDaily"/> is on.</summary>
    public void StartUpdateChecks() =>
        _updateTimer ??= new System.Threading.Timer(_ =>
        {
            if (Updates.CheckDaily)
                _ = CheckForUpdateAsync();
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));

    /// <summary>Asks GitHub for a newer version; the result is also in <see cref="UpdateAvailable"/>.</summary>
    /// <param name="url">GitHub's list of releases (another one only for the self-test).</param>
    public async Task<Release?> CheckForUpdateAsync(string url = UpdateCheck.ReleasesUrl)
    {
        try
        {
            UpdateAvailable = UpdateCheck.Current is { } current
                ? await UpdateCheck.NewerAsync(current, url)
                : throw new InvalidOperationException(T("this build has no version number"));
            UpdateProblem = null;
            if (UpdateAvailable is { } found && found.Version != _loggedUpdate)
            {
                _loggedUpdate = found.Version;
                Log.Add(LogKind.Info, T($"{found.Name} is available (you have {AppVersion.Text}): Settings → Updates."));
            }
        }
        catch (Exception ex)
        {
            UpdateProblem = ex is TaskCanceledException ? T("GitHub didn't answer in time.") : T($"The check didn't work ({ex.Message}).");
        }
        UpdateChecked = DateTimeOffset.Now;
        UpdateStateChanged?.Invoke();
        return UpdateAvailable;
    }

    /// <summary>
    /// Downloads <paramref name="release"/>, checks it and puts it in place of this program; then
    /// <see cref="StartNewVersion"/> and exit. If it fails (the message says why), nothing changed.
    /// </summary>
    public async Task InstallUpdateAsync(Release release, IProgress<double>? progress)
    {
        if (Calibrating)
            throw new InvalidOperationException(T("A calibration is running: finish or stop it first."));
        if (!UpdateInstaller.CanInstallInto(AppContext.BaseDirectory))
            throw new InvalidOperationException(T("This AutoFantic runs from the compiler's output, not a published build: build it again instead."));
        try
        {
            Log.Add(LogKind.Info, T($"Updating to {release.Version}: downloading {release.ZipSize / 1e6:0} MB from GitHub …"));
            string files = await UpdateInstaller.DownloadAsync(release, UpdateWork, progress);
            // once AutoFantic is signed, only the same publisher's exe may replace it
            Signature.CheckSamePublisher(Path.Combine(AppContext.BaseDirectory, UpdateInstaller.ExeName), Path.Combine(files, UpdateInstaller.ExeName));
            UpdateInstaller.Install(files, AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            Log.Add(LogKind.Warning, T($"The update to {release.Version} didn't work: {ex.Message} AutoFantic {AppVersion.Text} keeps running."));
            throw;
        }
        Log.Add(LogKind.Info, T($"{release.Name} is installed. AutoFantic restarts into it; the BIOS has the fans for those few seconds."));
    }

    /// <summary>
    /// Starts the AutoFantic.exe that is in place now (the new version). It waits until this one has
    /// ended and handed the fans back, so the caller exits right after. Admin rights carry over.
    /// </summary>
    public void StartNewVersion(params string[] args) => Start(Path.Combine(AppContext.BaseDirectory, UpdateInstaller.ExeName), AppVersion.Text, args);

    /// <summary>Starts this AutoFantic again (e.g. in another language); like <see cref="StartNewVersion"/>, the caller exits right after.</summary>
    public void StartAgain(params string[] args) => Start(Environment.ProcessPath!, null, args);

    private void Start(string exe, string? updatedFrom, string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add(WaitForArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (updatedFrom is not null)
        {
            start.ArgumentList.Add(UpdatedFromArgument);
            start.ArgumentList.Add(updatedFrom);
        }
        if (Session is SimulatedPc)
            start.ArgumentList.Add("--simulate");
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        System.Diagnostics.Process.Start(start)?.Dispose();
    }

    public const string WaitForArgument = "--wait-for";
    public const string UpdatedFromArgument = "--updated-from";

    /// <summary>Where a download is unpacked; removed at the next start.</summary>
    private string UpdateWork => Path.Combine(RunsPath, "update");

    // ── cooling health ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Works out the cooling health of every finished day since the calibration (at most the last
    /// 30, as long as minute values are kept) that isn't stored yet. Runs on a timer thread.
    /// </summary>
    public void UpdateHealth()
    {
        try
        {
            if (Recommended is not { } calibration || HealthReference is not { } reference)
                return;
            var store = Monitor.Store;
            var done = store.HealthDays().Where(d => d.Calibration == reference).Select(d => d.Day).ToHashSet();
            var today = DateOnly.FromDateTime(DateTime.Now);
            var first = DateOnly.FromDateTime(reference.LocalDateTime.Date);
            if (today.AddDays(-29) > first)
                first = today.AddDays(-29);
            bool any = false;
            for (var day = first; day < today; day = day.AddDays(1))
            {
                if (done.Contains(day))
                    continue;
                var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue));
                var result = CoolingHealth.Analyze(calibration, CoolingHealth.Minutes(store, calibration, from, from.AddDays(1)))
                    ?? new HealthResult(0, 0, null, null); // stored anyway, so an empty day isn't worked out again
                store.SaveHealth(new HealthDay(day, reference, result));
                any = true;
            }
            if (any)
                HealthUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Add(LogKind.Warning, T($"Cooling health: {ex.Message}"));
        }
    }

    // ── worn fans ──────────────────────────────────────────────────────────────────────

    /// <summary>Per fan: since when its first week counts, and the last warning given.</summary>
    public FanWearSettings FanWearSettings { get; private set; } = new();

    /// <summary>Every fan (as found), how it turns against its first week, and its days for the chart.</summary>
    public IReadOnlyList<(FanGroup Group, FanWearResult Result, bool HasFirstWeek, IReadOnlyList<(DateOnly Day, double Change, int Minutes)> Daily)> FanWearNow()
    {
        var days = Monitor.Store.FanDays();
        var today = DateOnly.FromDateTime(DateTime.Now);
        return [.. (Inventory?.Groups() ?? []).Select(group =>
        {
            string key = MeasurementStore.Key(group);
            var mine = days.Where(d => d.Fan == key).ToList();
            var since = FanWearSettings.SinceFor(key);
            return (group, FanWear.Now(mine, since, today), FanWear.FirstWeek(mine, since).Start is not null, FanWear.Daily(mine, since));
        })];
    }

    /// <summary>After cleaning or replacing a fan: its first week starts again today.</summary>
    public void StartFanAgain(FanGroup group)
    {
        string key = MeasurementStore.Key(group);
        FanWearSettings = FanWearSettings.StartAgain(key, DateOnly.FromDateTime(DateTime.Now));
        FanWearSettings.Save(Path.Combine(RunsPath, FanWearSettings.FileName));
        Log.Add(LogKind.Fans, T($"{group.Name}: a new first week starts today for the worn fan detection (cleaned or replaced)."));
        HealthUpdated?.Invoke();
    }

    /// <summary>
    /// Works out every fan's finished days (at most the last 30, as long as minute values are kept)
    /// that aren't stored yet, and says once per level when a fan turns clearly slower. Runs on a timer thread.
    /// </summary>
    public void UpdateFanWear()
    {
        try
        {
            if (Inventory is null)
                return;
            var store = Monitor.Store;
            var done = store.FanDays().Select(d => (d.Day, d.Fan)).ToHashSet();
            var today = DateOnly.FromDateTime(DateTime.Now);
            bool any = false;
            foreach (var group in Inventory.Groups())
            {
                string key = MeasurementStore.Key(group);
                var percent = HistoryRecorder.FanSeries(group, SeriesKind.FanPercent);
                var rpm = HistoryRecorder.FanSeries(group, SeriesKind.FanRpm);
                for (var day = today.AddDays(-29); day < today; day = day.AddDays(1))
                {
                    if (done.Contains((day, key)))
                        continue;
                    var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue));
                    store.SaveFanDay(new FanDay(day, key, FanWear.Steps(store, percent, rpm, from, from.AddDays(1)))); // stored even if empty: not worked out again
                    any = true;
                }
            }

            foreach (var (group, result, _, _) in FanWearNow())
            {
                string key = MeasurementStore.Key(group);
                string? level = result.Change <= FanWear.Worn ? "worn" : result.Change <= FanWear.Check ? "check" : null;
                string? warned = FanWearSettings.Warned?.GetValueOrDefault(key);
                if (level is null || level == warned || (level == "check" && warned == "worn"))
                    continue;
                FanWearSettings = FanWearSettings.WithWarning(key, level);
                FanWearSettings.Save(Path.Combine(RunsPath, FanWearSettings.FileName));
                string message = T($"{group.Name} turns {-result.Change!.Value:0} % slower than in its first week at the same setting: "
                    + $"{(level == "worn" ? T("probably worn or blocked. Clean it, or replace it soon.") : T("clean it and listen for grinding or rattling."))}");
                Log.Add(LogKind.Warning, message);
                Alert?.Invoke(message);
            }
            if (any)
                HealthUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Add(LogKind.Warning, T($"Worn fan detection: {ex.Message}"));
        }
    }

    /// <summary>The last 7 days as one result (including today so far); null if there isn't enough data yet.</summary>
    public HealthResult? HealthNow()
    {
        if (Recommended is not { } calibration)
            return null;
        var to = Loop.Last?.Time ?? DateTimeOffset.Now;
        return CoolingHealth.Analyze(calibration, CoolingHealth.Minutes(Monitor.Store, calibration, to.AddDays(-7), to));
    }

    /// <summary>Steady minutes in the last 7 days (to say how far the first answer is).</summary>
    public int HealthMinutes()
    {
        if (Recommended is not { } calibration)
            return 0;
        var to = Loop.Last?.Time ?? DateTimeOffset.Now;
        return CoolingHealth.Minutes(Monitor.Store, calibration, to.AddDays(-7), to).Count;
    }

    // a broken or locked history file must never stop AutoFantic: then the history stays in memory
    private static HistoryRecorder OpenMonitor(string runs, FanControlLoop loop, ActivityLog log)
    {
        HistoryStore store;
        try
        {
            store = new HistoryStore(Path.Combine(runs, HistoryStore.FileName));
        }
        catch (Exception ex)
        {
            log.Add(LogKind.Warning, T($"The history ({HistoryStore.FileName}) couldn't be opened, so it isn't kept this time: {ex.Message}"));
            store = new HistoryStore(null);
        }
        return new HistoryRecorder(store, loop.Keys) { Warnings = WarningSettings.Load(Path.Combine(runs, WarningSettings.FileName)) };
    }

    /// <summary>The monitor records every fan found; the fan control reads their RPM for it.</summary>
    private void UseFansInMonitor()
    {
        Monitor.UseFans(Inventory?.Groups() ?? []);
        Loop.Watch(Monitor.SensorIds);
    }

    public void SaveWarnings(WarningSettings warnings)
    {
        Monitor.Warnings = warnings;
        warnings.Save(Path.Combine(RunsPath, WarningSettings.FileName));
    }

    /// <summary>Set when the run before this one ended without handing the fans back (and the watchdog or this start did it): what happened.</summary>
    public string? UnexpectedEnd { get; private init; }

    // a watchdog entry after the previous run's start: that run was killed or crashed
    // (the start line in English or in German, Lang\de-app.json: the run before may have used the other language)
    private static string? LastRunEndedBadly(ActivityLog log)
    {
        var entries = log.Entries;
        int previousStart = -1;
        for (int i = entries.Count - 1; i >= 0 && previousStart < 0; i--)
            if (entries[i].Kind == LogKind.Info && entries[i].Text.StartsWith("AutoFantic ", StringComparison.Ordinal)
                && (entries[i].Text.Contains(" started", StringComparison.Ordinal) || entries[i].Text.Contains(" gestartet", StringComparison.Ordinal)))
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
            Log.Add(LogKind.Info, T("The curves were worked out again with this version's rules: smoother curves, case fans follow the warmer of CPU and GPU."));
    }

    private void Reload()
    {
        Store = MeasurementStore.Load(Path.Combine(RunsPath, CalibrationFiles.Store)) ?? MeasurementStore.Empty;
        FansOff = FansOffResult.Load(Path.Combine(RunsPath, CalibrationFiles.FansOff));
    }

    /// <summary>Runs the start-up checks again (after installing a driver, closing another fan program).</summary>
    public void RefreshChecks() =>
        Checks = SystemCheck.Run(Session, Loop.Keys,
            _pawnIoAtStart ? PawnIo.Installed : SystemCheck.PawnIoInstalled() ? PawnIo.InstalledSinceStart : PawnIo.Missing, FanToolCheck.Running());

    /// <summary>
    /// Installs the PawnIO driver after the user said yes: downloads its official installer, checks
    /// it and runs it without a window. Afterwards AutoFantic has to start again to use the driver
    /// (<see cref="StartAgain"/>). True if Windows wants a restart of the PC first; throws with the
    /// reason if it didn't work.
    /// </summary>
    public async Task<bool> InstallPawnIoAsync()
    {
        string work = Path.Combine(RunsPath, "pawnio");
        Log.Add(LogKind.Info, T($"Installing the PawnIO driver {PawnIoSetup.Version}: downloading it from GitHub …"));
        try
        {
            string installer = await PawnIoSetup.DownloadAsync(work);
            bool restartPc = await PawnIoSetup.RunAsync(installer);
            Log.Add(LogKind.Info, restartPc
                ? T("The PawnIO driver is installed. Windows wants a restart of the PC to finish it.")
                : T("The PawnIO driver is installed. AutoFantic starts again to use it."));
            return restartPc;
        }
        catch (Exception ex)
        {
            Log.Add(LogKind.Warning, T($"The PawnIO driver couldn't be installed: {ex.Message}"));
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the installer is still in use for a moment: it stays until the next install
            }
        }
    }

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
            Log.Add(LogKind.Info, T($"Preset {T(preset.Name)}: {T(preset.Description)}"));
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

    /// <summary>True if the user gave this group's fans to the BIOS.</summary>
    public bool IsBios(int group) => Recommended is { } r && Overrides.IsBios(r.Groups[group]);

    /// <summary>
    /// Gives one group's fans to the BIOS (AutoFantic hands them back and leaves them alone, the
    /// others carry on) or takes them back. Its curve is kept either way.
    /// </summary>
    public void SetBios(int group, bool bios)
    {
        if (Recommended is not { } r || IsBios(group) == bios)
            return;
        Overrides = Overrides.WithBios(r.Groups[group], bios);
        Overrides.Save(Path.Combine(RunsPath, CalibrationFiles.Curves));
        Log.Add(LogKind.Fans, bios
            ? T($"{r.Groups[group].Name}: given to the BIOS. AutoFantic leaves these fans alone; the other fans stay with AutoFantic.")
            : T($"{r.Groups[group].Name}: AutoFantic controls these fans again."));
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
            Loop.UseCalibration(effective, FanControlLoop.MinSpinning(effective, Inventory), FanControlLoop.CanStop(effective, Inventory));
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
        Log.Add(LogKind.Calibration, T($"Calibration started with {(builtInLoad ? T("the built-in load") : T("your own load (a game)"))}, room {ambient:0} °C."));

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
                outcome = new CalibrationOutcome(false, T($"Calibration failed: {ex.Message}"), null, null);
            }

            Inventory = FanInventory.Load(Path.Combine(RunsPath, CalibrationFiles.Inventory)) ?? Inventory;
            Reload();
            if (outcome.Result is { } result)
                Recommended = result;

            _calibrating.Dispose();
            _calibrating = null;
            if (Effective is { } effective && Inventory is not null)
                Loop.UseCalibration(effective, FanControlLoop.MinSpinning(effective, Inventory), FanControlLoop.CanStop(effective, Inventory));
            UseFansInMonitor();
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
        Log.Add(LogKind.Calibration, T("Find my fans started."));

        var thread = new Thread(() =>
        {
            CalibrationOutcome outcome;
            try
            {
                var found = FanDiscovery.Run(Session, cancel, line => CalibrationLog?.Invoke(line), (channel, done) =>
                    CalibrationProgress?.Invoke(new CalibrationProgress(CalibrationStage.FindingFans, T($"Finding fans: {channel}"), 0, 0, null, null, done)));
                if (found is null)
                {
                    outcome = new CalibrationOutcome(false, T("Find my fans didn't finish. Nothing changed."), null, null);
                }
                else if (found.Groups().Count == 0)
                {
                    outcome = new CalibrationOutcome(false, T("No fan reacted on any output. Are the fans connected to the mainboard (not to a separate fan hub with its own software)?"), null, null);
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
                        first ? T($"Found {found.Groups().Count} fan groups: {names}. Next: calibrate.")
                        : same ? T($"Found the same {found.Groups().Count} fan groups as before.")
                        : T($"Your fans are different now ({names}): please calibrate again."), null, null);
                }
            }
            catch (Exception ex)
            {
                outcome = new CalibrationOutcome(false, T($"Find my fans failed: {ex.Message}"), null, null);
            }

            Session.RestoreAll();
            _calibrating.Dispose();
            _calibrating = null;
            UseFansInMonitor();
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
        _healthTimer?.Dispose();
        _updateTimer?.Dispose();
        Loop.Dispose();
        Monitor.FinishSession();
        Monitor.Store.Dispose();
        Session.Dispose();
        Log.Add(LogKind.Info, T("AutoFantic exited: the fans are back on BIOS control."));
    }
}
