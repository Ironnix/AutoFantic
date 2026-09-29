namespace AutoFantic.Core.Calibration;

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
        return rpm < 50 ? double.NegativeInfinity
            : 50 * Math.Log10(rpm / 1000.0) + (header.IsGpu ? GpuPenalty : 0)
              + 10 * Math.Log10(Math.Max(1, header.FanCount)) + header.LoudnessDb;
    }

    private static double Sum(IEnumerable<double> levels)
    {
        double power = levels.Where(double.IsFinite).Sum(l => Math.Pow(10, l / 10));
        return power <= 0 ? double.NegativeInfinity : 10 * Math.Log10(power);
    }
}

/// <summary>A temperature target: Max 80 = CPU and GPU core at most 80 °C; hotspot and memory keep fixed limits.</summary>
/// <param name="MaxCooling">Not "quietest under a target" but "coolest without pointless noise": every fan up to its knee.</param>
public sealed record Profile(string Name, double Cpu, double GpuCore, double GpuHotspot = 95, double GpuMemory = 95, bool MaxCooling = false)
{
    /// <summary>
    /// "Max 80" / "Max 90". A target never sits right on a safety limit (CPU 90, GPU core 85):
    /// it stays 3 °C below, or every load spike would trip the safety stop.
    /// </summary>
    public static Profile Max(double limit)
    {
        var safety = new Analysis.SafetyLimits();
        return new($"Max {limit:0}", Math.Min(limit, safety.CpuMax - 3), Math.Min(limit, safety.GpuCoreMax - 3),
            safety.GpuHotspotMax - 5, safety.GpuMemoryMax - 5);
    }

    /// <summary>The lowest temperatures the cooling can reach without pointless noise (the design's "Max Cooling").</summary>
    public static Profile Coolest(string name = "Max cooling") => Max(90) with { Name = name, MaxCooling = true };

    public double Target(Component component) => component switch
    {
        Component.Cpu => Cpu,
        Component.GpuCore => GpuCore,
        Component.GpuHotspot => GpuHotspot,
        _ => GpuMemory,
    };
}

/// <summary>A choice for the whole PC: how the fans trade temperature for noise.</summary>
public sealed record Preset(string Id, string Name, string Description, Profile Profile)
{
    public static IReadOnlyList<Preset> All { get; } =
    [
        new("silent", "Silent", "The quietest\nup to 87 °C", Profile.Max(90) with { Name = "Silent" }),
        new("balanced", "Balanced", "Quiet and cool\nup to 80 °C", Profile.Max(80) with { Name = "Balanced" }),
        new("cool", "Cool", "A bit louder\nup to 70 °C", Profile.Max(70) with { Name = "Cool" }),
        new("max", "Max cooling", "The coolest\nwithout pointless noise", Profile.Coolest()),
    ];

    /// <summary>By id ("silent"), name ("Silent"), an old profile name ("Max 90") or a number ("80"); Balanced if unknown.</summary>
    public static Preset For(string? key)
    {
        string k = (key ?? "").Trim();
        return All.FirstOrDefault(p => p.Id.Equals(k, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(k, StringComparison.OrdinalIgnoreCase))
            ?? k switch
            {
                "Max 90" or "90" => All[0],
                "Max 70" or "70" => All[2],
                "max" or "Max Cooling" => All[3],
                _ => All[1],
            };
    }
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
    /// Fans may be off (0-RPM) while the CPU and GPU core stay at or below this. The calibration
    /// measures "all fans off" only at idle, so beyond this the model would be guessing; graphics
    /// cards' own 0-RPM modes switch their fans on around here too.
    /// </summary>
    public const double StopOnlyBelow = 60;

    public IReadOnlyList<FanGroup> Groups => groups;

    /// <summary>
    /// Fans may only be off while the load is low: CPU and GPU power at or below these. Off is for
    /// idle and light use, not for a game that happens to run cool.
    /// </summary>
    public (double Cpu, double Gpu) StopOnlyUpTo { get; init; } = (double.PositiveInfinity, double.PositiveInfinity);

    /// <summary>
    /// Measured °C per watt with every fan that can stop standing still (the fans-off test). The
    /// model is fitted to spinning fans only; for "everything off" this measurement is used instead.
    /// </summary>
    public IReadOnlyDictionary<Component, double>? AllOffResistance { get; init; }

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
        if (profile.MaxCooling)
            return Coolest(cpuPower, gpuPower);

        bool lowLoad = cpuPower <= StopOnlyUpTo.Cpu && gpuPower <= StopOnlyUpTo.Gpu;
        var choices = groups.Select(g => Choices(g).Where(s => s > 0 || lowLoad).ToList()).ToList();
        var speeds = new double[groups.Count];
        Mix? best = null;
        (double Excess, double Noise, double[] Speeds)? coolest = null;

        void Search(int g)
        {
            if (g == groups.Count)
            {
                var temps = Predict(speeds, cpuPower, gpuPower);
                if (speeds.Any(s => s == 0) && temps.Any(kv => kv.Key is Component.Cpu or Component.GpuCore && kv.Value > StopOnlyBelow))
                    return;
                double excess = temps.Max(kv => kv.Value - (profile.Target(kv.Key) - MarginFor(kv.Key)));
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

    /// <summary>Headroom below a target: at least <see cref="Margin"/>, more where the model fits worse (up to 6 °C).</summary>
    public double MarginFor(Component component) =>
        Math.Clamp(model.Rms.GetValueOrDefault(component), Margin, 6);

    /// <summary>
    /// Max cooling: start with every fan at its slowest spinning speed, then keep giving +5 % to the
    /// fan that cools CPU and GPU most for it, as long as that still gains at least 0.5 °C (about
    /// 1 °C per +10 %, the knee). Past that, more speed is only noise. Hotspot and memory limits
    /// are always met, whatever it costs.
    /// </summary>
    private Mix Coolest(double cpuPower, double gpuPower)
    {
        const double KneeGain = 0.5;
        var choices = groups.Select(g => Choices(g).Where(s => s > 0).ToList()).ToList();
        var index = new int[groups.Count];
        double[] Speeds() => index.Select((i, g) => choices[g][i]).ToArray();
        double Heat(double[] speeds)
        {
            var t = Predict(speeds, cpuPower, gpuPower);
            return t.GetValueOrDefault(Component.Cpu) + t.GetValueOrDefault(Component.GpuCore);
        }
        bool Unsafe(double[] speeds) =>
            Predict(speeds, cpuPower, gpuPower).Any(kv => kv.Key is Component.GpuHotspot or Component.GpuMemory && kv.Value > profile.Target(kv.Key) - MarginFor(kv.Key));

        while (true)
        {
            var current = Speeds();
            double heat = Heat(current);
            int best = -1;
            double bestGain = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                if (index[g] + 1 >= choices[g].Count)
                    continue;
                var next = (double[])current.Clone();
                next[g] = choices[g][index[g] + 1];
                double gain = (heat - Heat(next)) * Step / (next[g] - current[g]);
                if (gain > bestGain)
                {
                    best = g;
                    bestGain = gain;
                }
            }
            if (best < 0 || (bestGain < KneeGain && !Unsafe(current)))
                break;
            index[best]++;
        }

        var speeds = Speeds();
        var temps = Predict(speeds, cpuPower, gpuPower);
        bool meets = temps.All(kv => kv.Value <= profile.Target(kv.Key));
        return new Mix(speeds, temps, NoiseModel.Total(groups, speeds), meets);
    }

    public Dictionary<Component, double> Predict(IReadOnlyList<double> speeds, double cpuPower, double gpuPower)
    {
        var temps = model.Components.ToDictionary(c => c, c => model.Predict(c, speeds, cpuPower, gpuPower));
        if (AllOffResistance is { } off && IsAllOff(speeds))
        {
            foreach (var (component, r) in off)
                temps[component] = model.Ambient + (ThermalModel.IsCpu(component) ? cpuPower : gpuPower) * r;
        }
        return temps;
    }

    // every group that can stop is off, the others at their slowest: the state the fans-off test measured
    private bool IsAllOff(IReadOnlyList<double> speeds) =>
        groups.Select((g, i) => g.CanStop ? speeds[i] == 0 : speeds[i] <= g.MinSpinning).All(x => x)
        && groups.Where((g, i) => g.CanStop).Any();

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
