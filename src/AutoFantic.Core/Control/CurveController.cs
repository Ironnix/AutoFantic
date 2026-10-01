using AutoFantic.Core.Calibration;
using static AutoFantic.Core.Texts;

namespace AutoFantic.Core.Control;

/// <summary>
/// Runs the fans by the calibrated curves, for whatever the PC is doing. Each group follows the
/// temperature of the part it cools, smoothed over a few seconds (a CPU sensor jumps with every
/// burst of work). Fans speed up quickly and slow down gently, because changing noise is more
/// annoying than steady noise. At low load a group that may stop is switched off while the part
/// it cools is cool and nothing else is warm, with hysteresis and minimum on/off times so it
/// doesn't keep cycling. In quiet mode (away, at night) every fan runs at its slowest and every fan
/// that can stop is off, whatever the load, as long as it stays cool. If the user switched it on
/// (<see cref="ReactEarly"/>), a jump in the graphics card's power speeds the fans up before the
/// temperature has followed.
/// </summary>
public sealed class CurveController
{
    /// <summary>A fan may switch off while the part it follows is at most this warm (°C, smoothed), unless its curve says otherwise …</summary>
    public const double OffBelow = 55;

    /// <summary>… and switches back on this much above where it switched off.</summary>
    public const double Hysteresis = 5;

    /// <summary>No fan is off while the CPU or the GPU is warmer than this; above this + <see cref="Hysteresis"/> every fan runs.</summary>
    public const double OthersBelow = MixOptimizer.StopOnlyBelow;

    public const double RampUpPerSecond = 4;
    public const double RampDownPerSecond = 1;

    /// <summary>A stopped fan gets this for a few seconds so it reliably starts turning.</summary>
    public const double KickPercent = 60;

    public static readonly TimeSpan KickFor = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MinOn = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinOff = TimeSpan.FromSeconds(30);

    /// <summary>Quiet mode: every fan at its slowest while the part it follows is at most this warm …</summary>
    public const double QuietBelow = 60;

    /// <summary>… then back to its curve over this many °C, so a render while you're away is still cooled.</summary>
    public const double QuietBlend = 10;

    private const double SmoothingSeconds = 8;

    /// <summary>Reacting early: a jump in power that would warm the GPU by less than this is ignored (a game's power is never steady) …</summary>
    public const double EarlyFrom = 2;

    /// <summary>… and a curve is read at most this many °C ahead of the temperature.</summary>
    public const double EarlyAtMost = 15;

    // the GPU's power "now" (a few seconds, so a single spike doesn't count) against "before" (about as
    // long as the card takes to warm up: by then the temperature itself says what the power did)
    private const double PowerNowSeconds = 3;
    private const double PowerBeforeSeconds = 45;

    private readonly CalibrationResult _calibration;
    private readonly ThermalModel? _model;
    private double _gpuNow = double.NaN, _gpuBefore = double.NaN;
    private readonly double[] _minSpinning;
    private readonly bool[] _canStop;
    private readonly GroupState[] _state;
    private readonly FanStatus[] _status;
    private readonly Dictionary<Component, double> _smoothed = [];
    private readonly List<FanSwitch> _switches = [];
    private DateTimeOffset? _last;

    private sealed class GroupState
    {
        // NaN until the first step: then it starts right at the curve instead of ramping down from 100 %
        public double Percent = double.NaN;
        public bool Off;
        public DateTimeOffset Since = DateTimeOffset.MinValue;
        public DateTimeOffset KickUntil = DateTimeOffset.MinValue;
    }

    /// <param name="minSpinning">Per calibrated group: the lowest speed at which its fans turn.</param>
    /// <param name="canStop">Per calibrated group: its fans stand still at 0 % (quiet mode switches them off). Default: none.</param>
    public CurveController(CalibrationResult calibration, IReadOnlyList<double> minSpinning, IReadOnlyList<bool>? canStop = null)
    {
        _calibration = calibration;
        _minSpinning = minSpinning.ToArray();
        _canStop = calibration.Groups.Select((_, g) => canStop is not null && g < canStop.Count && canStop[g]).ToArray();
        _state = calibration.Groups.Select(_ => new GroupState()).ToArray();
        _status = calibration.Groups.Select(_ => new FanStatus(null, null, FanNote.OnCurve)).ToArray();
        // what the calibration measured says how many °C a watt is worth; a result without it can't react early
        if (calibration.Model.TryGetValue(Component.GpuCore, out var gpu) && gpu.Length == calibration.Groups.Count + 1)
            _model = ThermalModel.FromCoefficients(calibration.Ambient, calibration.Groups.Count, calibration.Model);
    }

    /// <summary>
    /// React to the graphics card's power before its temperature follows (the user switches it on).
    /// When the power jumps (a game starts, a heavy scene), the fans that cool the GPU read their
    /// curve at the temperature the calibration expects the jump to add, instead of waiting for it:
    /// they go early to about the speed they would end up at anyway. The lead fades as the temperature
    /// catches up. Only upwards, only for the GPU (a CPU's power jumps all the time), never in quiet mode.
    /// </summary>
    public bool ReactEarly { get; set; }

    /// <summary>Per group, after the last step: the temperature it followed, what the curve says there, and why it runs at the speed it does.</summary>
    public IReadOnlyList<FanStatus> Status => _status;

    /// <summary>The groups that switched off or on again in the last step, and why (for the log).</summary>
    public IReadOnlyList<FanSwitch> Switches => _switches;

    /// <summary>Why the fans are as quiet as possible now ("away", "night"); null = normal.</summary>
    public string? Quiet { get; set; }

    /// <summary>
    /// Where a group that may stop switches off: at or below the last of its curve's leading 0 %
    /// points (the user drew "off" there), otherwise at <see cref="OffBelow"/>. Never above
    /// <see cref="OthersBelow"/>: off is only for a cool PC.
    /// </summary>
    public static double OffTemperature(CalibratedGroup group)
    {
        var off = group.Curve.TakeWhile(p => p.Percent <= 0).ToList();
        return off.Count > 0 ? Math.Clamp(off[^1].Temperature, 30, OthersBelow) : OffBelow;
    }

    /// <summary>Smoothed temperatures the controller is working with.</summary>
    public IReadOnlyDictionary<Component, double> Smoothed => _smoothed;

    /// <summary>After a pause (BIOS in control): start again right at the curve, as on the first step.</summary>
    public void Restart()
    {
        foreach (var s in _state)
        {
            s.Percent = double.NaN;
            s.Off = false;
            s.Since = DateTimeOffset.MinValue;
        }
        _gpuNow = _gpuBefore = double.NaN; // whatever the power did meanwhile is no jump
    }

    /// <summary>After the safety system ran every fan at 100 %: carry on from there.</summary>
    public void Reset()
    {
        foreach (var s in _state)
        {
            s.Percent = 100;
            s.Off = false;
            s.Since = DateTimeOffset.MinValue;
        }
    }

    /// <summary>One control step (about once per second). Returns the speed per group in % (0 = off).</summary>
    public double[] Step(DateTimeOffset now, IReadOnlyDictionary<Component, double> temps, double cpuPower, double gpuPower)
    {
        double dt = _last is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 10) : 1;
        _last = now;
        foreach (var (component, value) in temps)
        {
            double a = 1 - Math.Exp(-dt / SmoothingSeconds);
            _smoothed[component] = _smoothed.TryGetValue(component, out double old) ? old + (value - old) * a : value;
        }

        double warmest = Math.Max(_smoothed.GetValueOrDefault(Component.Cpu), _smoothed.GetValueOrDefault(Component.GpuCore));
        bool lowLoad = cpuPower <= _calibration.StopCpuWatts && gpuPower <= _calibration.StopGpuWatts;
        double lead = Lead(dt, gpuPower);
        _switches.Clear();

        var output = new double[_state.Length];
        for (int g = 0; g < _state.Length; g++)
        {
            var group = _calibration.Groups[g];
            var state = _state[g];
            double temperature = group.Follows == Component.Warmest ? warmest : _smoothed.GetValueOrDefault(group.Follows);
            // quiet: any fan that can stop may, at any load, up to where nothing may be off anyway
            bool quietOff = Quiet is not null && _canStop[g];
            double offAt = quietOff ? OthersBelow : OffTemperature(group);
            bool mayStop = quietOff || (group.OffAt.Count > 0 && lowLoad);

            if (state.Off)
            {
                // warm again: on right away; load came: on once it has been off a while (a blip doesn't start it)
                bool warm = temperature > offAt + Hysteresis || warmest > OthersBelow + Hysteresis;
                if (warm || (!mayStop && now - state.Since >= MinOff))
                {
                    state.Off = false;
                    state.Since = now;
                    state.KickUntil = now + KickFor;
                    state.Percent = _minSpinning[g];
                    _switches.Add(new FanSwitch(g, false,
                        temperature > offAt + Hysteresis ? $"{CalibrationInsights.Name(Warmer(group.Follows))} {temperature:0} °C"
                        : warm ? $"{CalibrationInsights.Name(Warmer(Component.Warmest))} {warmest:0} °C"
                        : T($"load came (CPU {cpuPower:0} W, GPU {gpuPower:0} W)")));
                }
            }
            else if (mayStop && temperature <= offAt && warmest <= OthersBelow && now - state.Since >= MinOn)
            {
                state.Off = true;
                state.Since = now;
                _switches.Add(new FanSwitch(g, true, quietOff
                    ? T($"quiet ({T(Quiet!)}) and cool ({CalibrationInsights.Name(group.Follows)} {temperature:0} °C, CPU {cpuPower:0} W, GPU {gpuPower:0} W)")
                    : T($"idle and cool ({CalibrationInsights.Name(group.Follows)} {temperature:0} °C, CPU {cpuPower:0} W, GPU {gpuPower:0} W)")));
            }

            double curve = Interpolate(group.Curve, temperature);
            if (state.Off)
            {
                output[g] = 0;
                _status[g] = new FanStatus(temperature, curve, FanNote.Off);
                continue;
            }

            double onCurve = Math.Max(curve, _minSpinning[g]);
            // reacting early: the curve as it will be read once the GPU has warmed up by what its power jumped
            double early = lead <= 0 ? temperature : group.Follows switch
            {
                Component.GpuCore => temperature + lead,
                Component.Warmest => Math.Max(temperature, _smoothed.GetValueOrDefault(Component.GpuCore) + lead),
                _ => temperature,
            };
            double target = early > temperature ? Math.Max(onCurve, Interpolate(group.Curve, early)) : onCurve;
            bool ahead = target > onCurve + 0.5;
            if (Quiet is not null)
            {
                // the slowest speed while cool, then gradually the curve again
                double blend = Math.Clamp((temperature - QuietBelow) / QuietBlend, 0, 1);
                target = _minSpinning[g] + (target - _minSpinning[g]) * blend;
            }
            state.Percent = double.IsNaN(state.Percent) ? target
                : target > state.Percent
                ? Math.Min(target, state.Percent + RampUpPerSecond * dt)
                : Math.Max(target, state.Percent - RampDownPerSecond * dt);
            bool kick = now < state.KickUntil;
            output[g] = kick ? Math.Max(state.Percent, KickPercent) : state.Percent;
            _status[g] = new FanStatus(temperature, curve,
                kick ? FanNote.Starting
                : Quiet is not null && target < onCurve - 0.5 && state.Percent <= target + 0.5 ? FanNote.Quiet
                : ahead ? FanNote.Early
                : state.Percent > target + 0.5 ? FanNote.SlowingDown
                : state.Percent < target - 0.5 ? FanNote.SpeedingUp
                : curve < _minSpinning[g] - 0.5 ? FanNote.Slowest
                : FanNote.OnCurve);
        }
        return output;
    }

    /// <summary>
    /// How many °C ahead of the temperature the GPU's fans read their curve right now
    /// (<see cref="ReactEarly"/>): what the model says the jump in power will add at the fans' present
    /// speeds, 0 while the power is steady or falling. Keeps track of the power either way, so
    /// switching it on doesn't take the last minute for a jump.
    /// </summary>
    private double Lead(double dt, double gpuPower)
    {
        if (double.IsNaN(_gpuNow))
            _gpuNow = _gpuBefore = gpuPower;
        _gpuNow += (gpuPower - _gpuNow) * (1 - Math.Exp(-dt / PowerNowSeconds));
        _gpuBefore += (gpuPower - _gpuBefore) * (1 - Math.Exp(-dt / PowerBeforeSeconds));
        if (!ReactEarly || Quiet is not null || _model is null || _gpuNow <= _gpuBefore)
            return 0;

        // a fan that is off now runs once there is load: counted at its slowest, or the model would expect an oven
        var speeds = _state.Select((s, g) => s.Off || double.IsNaN(s.Percent) ? _minSpinning[g] : Math.Max(s.Percent, _minSpinning[g])).ToList();
        double warmer = _model.Predict(Component.GpuCore, speeds, 0, _gpuNow) - _model.Predict(Component.GpuCore, speeds, 0, _gpuBefore);
        return Math.Clamp(warmer - EarlyFrom, 0, EarlyAtMost);
    }

    /// <summary>Linear between the curve's points; flat before the first and after the last.</summary>
    public static double Interpolate(IReadOnlyList<CurvePoint> curve, double temperature) => CalibrationResult.Interpolate(curve, temperature);

    // "the warmer of the two" in a log line: whichever it is right now
    private Component Warmer(Component follows) =>
        follows != Component.Warmest ? follows
        : _smoothed.GetValueOrDefault(Component.Cpu) >= _smoothed.GetValueOrDefault(Component.GpuCore) ? Component.Cpu : Component.GpuCore;
}

/// <param name="Group">Index of the calibrated group.</param>
/// <param name="Off">True: switched off; false: on again.</param>
/// <param name="Why">In plain words, e.g. "idle and cool (CPU 44 °C …)" or "GPU 63 °C".</param>
public sealed record FanSwitch(int Group, bool Off, string Why);

/// <summary>Why a fan runs at the speed it does right now.</summary>
public enum FanNote
{
    /// <summary>Exactly what the curve says.</summary>
    OnCurve,

    /// <summary>Above the curve: slowing down gently after it was warmer (a sudden drop in noise is as noticeable as a rise).</summary>
    SlowingDown,

    /// <summary>Below the curve: speeding up to it.</summary>
    SpeedingUp,

    /// <summary>The curve asks for less than the fan can turn at: the slowest speed it reliably spins.</summary>
    Slowest,

    /// <summary>Switched off: low load, cool.</summary>
    Off,

    /// <summary>Just switched on again: a short push so it reliably starts turning.</summary>
    Starting,

    /// <summary>Below its curve on purpose: quiet mode (away, at night).</summary>
    Quiet,

    /// <summary>Above its curve on purpose: the graphics card's power just jumped and the temperature will follow (<see cref="CurveController.ReactEarly"/>).</summary>
    Early,
}

/// <param name="Temperature">The smoothed temperature the group follows (what the curve is read at).</param>
/// <param name="CurvePercent">What the curve says at that temperature.</param>
public sealed record FanStatus(double? Temperature, double? CurvePercent, FanNote Note);
