using AutoFantic.Core.Calibration;

namespace AutoFantic.Core.Control;

/// <summary>
/// Runs the fans by the calibrated curves, for whatever the PC is doing. Each group follows the
/// temperature of the part it cools, smoothed over a few seconds (a CPU sensor jumps with every
/// burst of work). Fans speed up quickly and slow down gently, because changing noise is more
/// annoying than steady noise. At low load and low temperature a group that may stop is switched
/// off, with hysteresis and minimum on/off times so it doesn't keep cycling.
/// </summary>
public sealed class CurveController
{
    /// <summary>A fan may switch off only below this (°C, CPU and GPU core, smoothed) …</summary>
    public const double OffBelow = 55;

    /// <summary>… and switches back on above this.</summary>
    public const double OnAbove = MixOptimizer.StopOnlyBelow;

    public const double RampUpPerSecond = 4;
    public const double RampDownPerSecond = 1;

    /// <summary>A stopped fan gets this for a few seconds so it reliably starts turning.</summary>
    public const double KickPercent = 60;

    public static readonly TimeSpan KickFor = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MinOn = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinOff = TimeSpan.FromSeconds(30);

    private const double SmoothingSeconds = 8;

    private readonly CalibrationResult _calibration;
    private readonly double[] _minSpinning;
    private readonly GroupState[] _state;
    private readonly Dictionary<Component, double> _smoothed = [];
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
    public CurveController(CalibrationResult calibration, IReadOnlyList<double> minSpinning)
    {
        _calibration = calibration;
        _minSpinning = minSpinning.ToArray();
        _state = calibration.Groups.Select(_ => new GroupState()).ToArray();
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

        double cpu = _smoothed.GetValueOrDefault(Component.Cpu), gpu = _smoothed.GetValueOrDefault(Component.GpuCore);
        bool lowLoad = cpuPower <= _calibration.StopCpuWatts && gpuPower <= _calibration.StopGpuWatts;

        var output = new double[_state.Length];
        for (int g = 0; g < _state.Length; g++)
        {
            var group = _calibration.Groups[g];
            var state = _state[g];
            bool mayStop = group.OffAt.Count > 0 && lowLoad;

            if (state.Off)
            {
                bool mustStart = !mayStop || cpu > OnAbove || gpu > OnAbove;
                if (mustStart && (now - state.Since >= MinOff || cpu > OnAbove || gpu > OnAbove))
                {
                    state.Off = false;
                    state.Since = now;
                    state.KickUntil = now + KickFor;
                    state.Percent = _minSpinning[g];
                }
            }
            else if (mayStop && cpu <= OffBelow && gpu <= OffBelow && now - state.Since >= MinOn)
            {
                state.Off = true;
                state.Since = now;
            }

            if (state.Off)
            {
                output[g] = 0;
                continue;
            }

            double target = Math.Max(Interpolate(group.Curve, _smoothed.GetValueOrDefault(group.Follows)), _minSpinning[g]);
            state.Percent = double.IsNaN(state.Percent) ? target
                : target > state.Percent
                ? Math.Min(target, state.Percent + RampUpPerSecond * dt)
                : Math.Max(target, state.Percent - RampDownPerSecond * dt);
            output[g] = now < state.KickUntil ? Math.Max(state.Percent, KickPercent) : state.Percent;
        }
        return output;
    }

    /// <summary>Linear between the curve's points; flat before the first and after the last.</summary>
    public static double Interpolate(IReadOnlyList<CurvePoint> curve, double temperature)
    {
        if (curve.Count == 0)
            return 100;
        if (temperature <= curve[0].Temperature)
            return curve[0].Percent;
        for (int i = 1; i < curve.Count; i++)
        {
            var (a, b) = (curve[i - 1], curve[i]);
            if (temperature <= b.Temperature)
                return a.Percent + (b.Percent - a.Percent) * (temperature - a.Temperature) / Math.Max(1e-9, b.Temperature - a.Temperature);
        }
        return curve[^1].Percent;
    }
}
