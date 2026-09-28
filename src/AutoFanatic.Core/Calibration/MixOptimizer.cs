namespace AutoFanatic.Core.Calibration;

/// <summary>
/// Relative loudness in dB, from fan physics ("How it learns → Noise"): about 50·log10 of the RPM,
/// GPU fans a few dB louder at the same RPM (small, fast blades), and several fans add up as
/// 10·log10(Σ 10^(L/10)). Only differences matter, so 0 dB is arbitrary.
/// </summary>
public static class NoiseModel
{
    private const double GpuPenalty = 5;

    public static double Group(FanGroup group, double speed) =>
        Sum(group.Headers.Select(h => Fan(h, speed)));

    public static double Total(IReadOnlyList<FanGroup> groups, IReadOnlyList<double> speeds) =>
        Sum(groups.Select((g, i) => Group(g, speeds[i])));

    private static double Fan(FanHeader header, double speed)
    {
        float rpm = header.RpmAt((float)speed);
        return rpm < 50 ? double.NegativeInfinity : 50 * Math.Log10(rpm / 1000.0) + (header.IsGpu ? GpuPenalty : 0);
    }

    private static double Sum(IEnumerable<double> levels)
    {
        double power = levels.Where(double.IsFinite).Sum(l => Math.Pow(10, l / 10));
        return power <= 0 ? double.NegativeInfinity : 10 * Math.Log10(power);
    }
}

/// <summary>A temperature target: Max 80 = CPU and GPU core at most 80 °C; hotspot and memory keep fixed limits.</summary>
public sealed record Profile(string Name, double Cpu, double GpuCore, double GpuHotspot = 95, double GpuMemory = 95)
{
    public static Profile Max(double limit) =>
        new($"Max {limit:0}", Math.Min(limit, 90), Math.Min(limit, 85));

    public double Target(Component component) => component switch
    {
        Component.Cpu => Cpu,
        Component.GpuCore => GpuCore,
        Component.GpuHotspot => GpuHotspot,
        _ => GpuMemory,
    };
}

/// <param name="Speeds">Speed per fan group in %.</param>
/// <param name="MeetsTarget">False if even the coolest mix can't hold the profile at this load.</param>
public sealed record Mix(IReadOnlyList<double> Speeds, IReadOnlyDictionary<Component, double> Temperatures, double Noise, bool MeetsTarget);

/// <summary>
/// "From the model to the profile": for a given CPU and GPU power, try every combination of fan
/// speeds and keep the quietest one whose predicted temperatures stay below the profile. The
/// search space is small (a handful of groups × ~15 speeds), so a full search takes milliseconds.
/// </summary>
public sealed class MixOptimizer(ThermalModel model, IReadOnlyList<FanGroup> groups, Profile profile)
{
    /// <summary>Stay this far below the target, for model error and a little headroom.</summary>
    public const double Margin = 2;

    private const double Step = 5;

    /// <summary>
    /// A fan may only stand still (0-RPM mode) while every temperature stays at or below this. The
    /// calibration never measures stopped fans under load, so the model would be guessing beyond it;
    /// graphics cards' own 0-RPM modes switch the fans on around here too.
    /// </summary>
    public const double StopOnlyBelow = 55;

    public IReadOnlyList<FanGroup> Groups => groups;

    public Profile Profile => profile;

    /// <summary>Speeds each group may use: its minimum spinning speed up to 100 %, plus 0 for fans with a 0-RPM mode.</summary>
    public IReadOnlyList<double> Choices(FanGroup group)
    {
        var choices = new List<double>();
        if (group.CanStop)
            choices.Add(0);
        for (double s = Math.Ceiling(group.MinSpinning / Step) * Step; s <= 100; s += Step)
            choices.Add(s);
        if (choices[^1] < 100)
            choices.Add(100);
        return choices;
    }

    public Mix Best(double cpuPower, double gpuPower)
    {
        var choices = groups.Select(Choices).ToList();
        var speeds = new double[groups.Count];
        Mix? best = null;
        (double Excess, double Noise, double[] Speeds)? coolest = null;

        void Search(int g)
        {
            if (g == groups.Count)
            {
                var temps = Predict(speeds, cpuPower, gpuPower);
                if (speeds.Any(s => s == 0) && temps.Values.Any(t => t > StopOnlyBelow))
                    return;
                double excess = temps.Max(kv => kv.Value - (profile.Target(kv.Key) - Margin));
                double noise = NoiseModel.Total(groups, speeds);
                if (excess <= 0)
                {
                    if (best is null || noise < best.Noise - 1e-9)
                        best = new Mix(speeds.ToArray(), temps, noise, true);
                }
                else if (coolest is null || excess < coolest.Value.Excess - 1e-9 || (Math.Abs(excess - coolest.Value.Excess) < 1e-9 && noise < coolest.Value.Noise))
                {
                    coolest = (excess, noise, speeds.ToArray());
                }
                return;
            }
            foreach (var s in choices[g])
            {
                speeds[g] = s;
                Search(g + 1);
            }
        }

        Search(0);
        if (best is not null)
            return best;

        var fallback = coolest!.Value.Speeds;
        return new Mix(fallback, Predict(fallback, cpuPower, gpuPower), NoiseModel.Total(groups, fallback), false);
    }

    public Dictionary<Component, double> Predict(IReadOnlyList<double> speeds, double cpuPower, double gpuPower) =>
        model.Components.ToDictionary(c => c, c => model.Predict(c, speeds, cpuPower, gpuPower));

    /// <summary>
    /// The quietest mix for each load level, from light to heavy, made monotone: a fan never runs
    /// slower at a higher load than at a lower one, so a controller following the table never revs
    /// down while the load climbs. (More airflow never hurts in the model, so the targets still hold.)
    /// </summary>
    public IReadOnlyList<Mix> Table(IReadOnlyList<(double CpuPower, double GpuPower)> loads)
    {
        var mixes = loads.Select(l => Best(l.CpuPower, l.GpuPower)).ToList();
        var floor = new double[groups.Count];
        for (int i = 0; i < mixes.Count; i++)
        {
            var speeds = mixes[i].Speeds.Select((s, g) => Math.Max(s, floor[g])).ToArray();
            speeds.CopyTo(floor, 0);
            var temps = Predict(speeds, loads[i].CpuPower, loads[i].GpuPower);
            mixes[i] = new Mix(speeds, temps, NoiseModel.Total(groups, speeds), mixes[i].MeetsTarget);
        }
        return mixes;
    }
}
