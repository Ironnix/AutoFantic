using System.IO;
using AutoFanatic.Core;
using AutoFanatic.Core.Calibration;
using AutoFanatic.Core.Control;
using AutoFanatic.Core.Hardware;
using AutoFanatic.Core.Simulation;

namespace AutoFanatic.Tray;

/// <summary>
/// Everything the window and the tray icon act on: the hardware, the running fan control, the
/// recommended curves for the chosen profile, and the user's own curves on top. No UI in here.
/// </summary>
internal sealed class AppController : IDisposable
{
    private AppController(FanSession session, FanControlLoop loop, string runs, FanInventory inventory, MeasurementStore? store,
        FansOffResult? fansOff, CalibrationResult recommended, CurveOverrides overrides)
    {
        Session = session;
        Loop = loop;
        RunsPath = runs;
        Inventory = inventory;
        Store = store;
        FansOff = fansOff;
        Recommended = recommended;
        Overrides = overrides;
    }

    public FanSession Session { get; }

    public FanControlLoop Loop { get; }

    public string RunsPath { get; }

    public FanInventory Inventory { get; }

    public MeasurementStore? Store { get; }

    public FansOffResult? FansOff { get; }

    /// <summary>The calibrated curves for the current profile.</summary>
    public CalibrationResult Recommended { get; private set; }

    public CurveOverrides Overrides { get; private set; }

    /// <summary>What the fans run by: the recommended curves with the user's own curves in place.</summary>
    public CalibrationResult Effective => Overrides.ApplyTo(Recommended);

    public string PagePath => Path.Combine(RunsPath, "calibration.html");

    /// <summary>Max 80 / Max 90 can be worked out again only from stored measurements.</summary>
    public bool CanSwitchProfile => Store is { Calibrations.Count: > 0 };

    public double ProfileLimit => double.TryParse(Recommended.Profile.Split(' ')[^1], out double limit) ? limit : 80;

    public event Action? CurvesChanged;

    private bool _userPaused, _sleeping;

    /// <summary>Paused by the user: the BIOS has the fans until resumed.</summary>
    public bool UserPaused
    {
        get => _userPaused;
        set
        {
            _userPaused = value;
            Loop.Paused = _userPaused || _sleeping;
        }
    }

    /// <summary>The PC is going to sleep: the BIOS has the fans until it wakes.</summary>
    public bool Sleeping
    {
        get => _sleeping;
        set
        {
            _sleeping = value;
            Loop.Paused = _userPaused || _sleeping;
        }
    }

    /// <summary>Loads the calibration and starts controlling. Null with the reason if that's not possible.</summary>
    public static AppController? Create(bool simulate, out string? problem)
    {
        string runs = DataFolder.Default(simulate);
        var calibration = CalibrationResult.Load(Path.Combine(runs, "calibration.json"));
        var inventory = FanInventory.Load(Path.Combine(runs, "fans.json"));
        if (calibration is null || inventory is null)
        {
            problem = "There is no calibration yet.\n\nStart Start-Test.cmd, then \"1 Find my fans\" and \"2 Calibrate\"; after that AutoFanatic can run in the background.";
            return null;
        }

        var overrides = CurveOverrides.Load(Path.Combine(runs, "curves.json"));
        FanSession session = simulate ? new SimulatedPc(load: SimLoad.Session) : new HardwareSession();
        try
        {
            var effective = overrides.ApplyTo(calibration);
            var loop = new FanControlLoop(session, effective, FanControlLoop.MinSpinning(effective, inventory));
            problem = null;
            return new AppController(session, loop, runs, inventory,
                MeasurementStore.Load(Path.Combine(runs, "measurements.json")),
                FansOffResult.Load(Path.Combine(runs, "fans-off.json")),
                calibration, overrides);
        }
        catch (InvalidOperationException ex)
        {
            session.Dispose();
            problem = ex.Message;
            return null;
        }
    }

    public bool IsCustom(int group) => Overrides.For(Recommended.Groups[group]) is not null;

    /// <summary>The fans-off test found stopping this group safe at idle (only then may the user allow it).</summary>
    public bool CanStop(int group) => Recommended.Groups[group].OffAt.Count > 0;

    public bool AllowsStop(int group) => Effective.Groups[group].OffAt.Count > 0;

    /// <summary>Works the curves out again for Max 80 / Max 90 from the stored measurements and uses them.</summary>
    public bool SwitchProfile(double limit)
    {
        if (Store is null)
            return false;
        var result = CalibrationCalculator.Calculate(Store, Inventory, Profile.Max(limit), Recommended.Ambient, FansOff);
        if (result is null)
            return false;
        Recommended = result;
        result.Save(Path.Combine(RunsPath, "calibration.json"));
        Apply();
        return true;
    }

    public void SetCurve(int group, IReadOnlyList<CurvePoint> curve, bool allowStop)
    {
        Overrides = Overrides.With(Recommended.Groups[group], new CurveOverride(curve, allowStop && CanStop(group)));
        Overrides.Save(Path.Combine(RunsPath, "curves.json"));
        Apply();
    }

    public void ResetCurve(int group)
    {
        Overrides = Overrides.Without(Recommended.Groups[group]);
        Overrides.Save(Path.Combine(RunsPath, "curves.json"));
        Apply();
    }

    private void Apply()
    {
        var effective = Effective;
        Loop.UseCalibration(effective, FanControlLoop.MinSpinning(effective, Inventory));
        CurvesChanged?.Invoke();
    }

    public void Dispose()
    {
        Loop.Dispose();
        Session.Dispose();
    }
}
