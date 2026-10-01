using System.Text.Json;
using AutoFantic.Core.Hardware;

namespace AutoFantic.Core.Calibration;

public sealed record RpmPoint(float Percent, float Rpm);

/// <summary>One fan output as "discover" found it: which RPM sensor follows it and how fast it turns.</summary>
/// <param name="RpmSensorId">The RPM sensor that followed this control; null = nothing reacted (empty header).</param>
/// <param name="Rpm">Measured speed per duty cycle, ascending.</param>
/// <param name="FanCount">How many fans this output drives (a splitter or hub drives several; the user says). 0 on a
/// graphics card's output: the user counted fewer fans than the card has outputs, so this one's are counted with another.</param>
/// <param name="LoudnessDb">The user's correction for how loud these fans are: −5 quiet, 0 normal, +5 loud.</param>
/// <param name="Cools">What the user says these fans cool, where the calibration got it wrong: <see cref="Component.Cpu"/> (the CPU
/// cooler), <see cref="Component.GpuCore"/> (the graphics card) or <see cref="Component.Warmest"/> (case fans: both). Null = as measured.</param>
public sealed record FanHeader(
    int Channel,
    string ControlId,
    string Name,
    string Hardware,
    string? RpmSensorId,
    IReadOnlyList<RpmPoint> Rpm,
    bool IsPump,
    int FanCount = 1,
    double LoudnessDb = 0,
    Component? Cools = null)
{
    internal const float StoppedBelowRpm = 50;

    public bool Connected => RpmSensorId is not null;

    public bool IsGpu => FanChannel.IsGpuId(ControlId);

    /// <summary>True if the fan stood still at a measured speed (a GPU in 0-RPM mode, or a fan that stops at 0 %).</summary>
    public bool CanStop => Rpm.Any(p => p.Rpm < StoppedBelowRpm);

    /// <summary>False while nobody has looked at what the fan does at 0 %: then "can't stop" only means "not known yet".</summary>
    public bool StopMeasured => CanStop || Rpm.Any(p => p.Percent <= 0);

    /// <summary>Highest measured duty cycle at which the fan stood still; null if it never did.</summary>
    public float? HighestStopped => Rpm.Where(p => p.Rpm < StoppedBelowRpm).Select(p => (float?)p.Percent).Max();

    /// <summary>The duty cycle that gives roughly this RPM (inverse of <see cref="RpmAt"/>).</summary>
    public float PercentFor(float rpm) =>
        Enumerable.Range(0, 101).MinBy(p => Math.Abs(RpmAt(p) - rpm));

    /// <summary>Lowest measured duty cycle at which the fan still turned.</summary>
    public float? LowestSpinning => Rpm.Where(p => p.Rpm >= StoppedBelowRpm).Select(p => (float?)p.Percent).Min();

    /// <summary>
    /// The mainboard names some headers after their intended use ("Pump Fan"), whatever is plugged in.
    /// Says "header" when a normal fan sits on a pump header, so nobody mistakes it for a pump.
    /// </summary>
    public string DisplayName =>
        !IsPump && Name.Contains("Pump", StringComparison.OrdinalIgnoreCase) ? $"{Name} header" : Name;

    /// <summary>Estimated RPM at a duty cycle, interpolated between the measured points.</summary>
    public float RpmAt(float percent)
    {
        if (Rpm.Count == 0 || percent <= 0)
            return 0;

        var points = Rpm.OrderBy(p => p.Percent).ToList();
        if (percent <= points[0].Percent)
            return points[0].Rpm < StoppedBelowRpm ? 0 : points[0].Rpm * percent / points[0].Percent;

        for (int i = 1; i < points.Count; i++)
        {
            var (a, b) = (points[i - 1], points[i]);
            if (percent <= b.Percent)
            {
                // a fan that stood still at the lower point: once it spins (the group decides from
                // which speed on), its RPM is roughly proportional to the duty cycle
                if (a.Rpm < StoppedBelowRpm)
                    return b.Rpm * percent / b.Percent;
                return a.Rpm + (b.Rpm - a.Rpm) * (percent - a.Percent) / (b.Percent - a.Percent);
            }
        }
        return points[^1].Rpm;
    }
}

/// <summary>
/// A set of fan outputs that always run at the same speed: all fans of one graphics card, or a
/// single mainboard header (which may itself drive several fans through a splitter or hub).
/// </summary>
public sealed record FanGroup(string Name, IReadOnlyList<FanHeader> Headers)
{
    public bool IsGpu => Headers.All(h => h.IsGpu);

    /// <summary>Allowed to stand still: only fans that showed a 0-RPM mode (typically GPU fans).</summary>
    public bool CanStop => Headers.All(h => h.CanStop);

    /// <summary>False while it isn't measured what one of its fans does at 0 %.</summary>
    public bool StopMeasured => Headers.All(h => h.StopMeasured);

    /// <summary>What the user says these fans cool (<see cref="FanHeader.Cools"/>); null = as the calibration measured it.</summary>
    public Component? Cools => Headers.Select(h => h.Cools).FirstOrDefault(c => c is not null);

    /// <summary>
    /// Lowest speed the group may run at while spinning. A fan that stood still at a measured step
    /// above 0 % (a GPU at 30 %) starts somewhere above it: 40 % is the first guess, which the
    /// calibration checks. A fan that only stops at 0 % spins from its lowest measured speed.
    /// </summary>
    public float MinSpinning => SpinsFrom ?? (Headers.Max(h => h.HighestStopped ?? 0) > 0
        ? Math.Max(40, Headers.Max(h => h.HighestStopped ?? 0) + 10)
        : Math.Max(20, Headers.Max(h => h.LowestSpinning ?? 30)));

    /// <summary>Set once the calibration has seen the fans stand still at a speed: the next level that did spin.</summary>
    public float? SpinsFrom { get; init; }

    public string Channels => string.Join(", ", Headers.Select(h => $"#{h.Channel}"));
}

/// <summary>What "discover" found, saved so later steps only work with headers that really have a fan.</summary>
public sealed record FanInventory(DateTimeOffset Created, IReadOnlyList<FanHeader> Headers)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Headers with a fan on them, pumps excluded: the only ones AuFantic ever experiments with.</summary>
    public IEnumerable<FanHeader> Usable => Headers.Where(h => h.Connected && !h.IsPump);

    /// <summary>GPU fans of one card form one group; every mainboard header is its own group.</summary>
    public IReadOnlyList<FanGroup> Groups()
    {
        var groups = new List<FanGroup>();
        foreach (var header in Usable.Where(h => !h.IsGpu))
            groups.Add(new FanGroup($"{header.DisplayName} (#{header.Channel})", [header]));
        foreach (var card in Usable.Where(h => h.IsGpu).GroupBy(h => h.Hardware))
        {
            var headers = card.ToList();
            string name = headers.Count == 1 ? $"{headers[0].Name} (#{headers[0].Channel})" : $"GPU fans ({string.Join(", ", headers.Select(h => $"#{h.Channel}"))})";
            groups.Add(new FanGroup(name, headers));
        }
        return groups;
    }

    /// <summary>
    /// True if these are still the PC's fan outputs. They change when the PawnIO driver is installed
    /// (the mainboard's outputs appear) or a graphics card or water cooler is swapped. An inventory
    /// from before a water cooler's pump was left alone doesn't fit either. Then the fans have to be found again.
    /// </summary>
    public bool Fits(IReadOnlyList<FanChannel> channels) =>
        Headers.Select(h => h.ControlId).ToHashSet().SetEquals(channels.Select(c => c.Id))
        && Headers.All(h => h.IsPump || !FanChannel.IsCoolerPumpOutput(h.ControlId, h.Name));

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static FanInventory? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<FanInventory>(File.ReadAllText(path), Json) : null;

    /// <summary>A copy with the measured RPM at 0 % added, per channel (from the fans-off test).</summary>
    public FanInventory WithRpmAtZero(IReadOnlyDictionary<int, float> rpmAtZero) =>
        this with
        {
            Headers = Headers.Select(h => rpmAtZero.TryGetValue(h.Channel, out float rpm)
                ? h with { Rpm = h.Rpm.Where(p => p.Percent > 0).Prepend(new RpmPoint(0, rpm)).ToList() }
                : h).ToList(),
        };

    /// <summary>A copy with what the user said about a group's fans: how many there are and how loud they are.</summary>
    public FanInventory WithLoudness(FanGroup group, int fanCount, double loudnessDb)
    {
        // several headers in one group share the count, the first ones take what is left over: a
        // graphics card with 3 fans on 2 outputs is 2 + 1, so the group's fans still add up to 3
        var channels = group.Headers.Select(h => h.Channel).ToList();
        int Part(int channel) => fanCount / channels.Count + (channels.IndexOf(channel) < fanCount % channels.Count ? 1 : 0);
        return this with
        {
            Headers = Headers.Select(h => channels.Contains(h.Channel)
                ? h with { FanCount = Part(h.Channel), LoudnessDb = loudnessDb }
                : h).ToList(),
        };
    }

    /// <summary>A copy with what the user says a group's fans cool (CPU, GPU, or both for case fans); null = as measured again.</summary>
    public FanInventory WithCools(FanGroup group, Component? cools)
    {
        var channels = group.Headers.Select(h => h.Channel).ToHashSet();
        return this with { Headers = Headers.Select(h => channels.Contains(h.Channel) ? h with { Cools = cools } : h).ToList() };
    }

    /// <summary>
    /// A copy that keeps what was known before the fans were found again: what the user said about
    /// each output's fans (how many, how loud, what they cool) and, for the same fan, what was measured at speeds
    /// "Find my fans" didn't test this time (0 % while the PC was busy, a standstill seen during a calibration).
    /// </summary>
    public FanInventory KeepingKnown(FanInventory? before)
    {
        var known = before?.Headers.ToDictionary(h => h.ControlId) ?? [];
        return this with
        {
            Headers = Headers.Select(h => !known.TryGetValue(h.ControlId, out var old) ? h : h with
            {
                FanCount = old.FanCount,
                LoudnessDb = old.LoudnessDb,
                Cools = old.Cools,
                Rpm = h.RpmSensorId is null || h.RpmSensorId != old.RpmSensorId
                    ? h.Rpm
                    : h.Rpm.Concat(old.Rpm.Where(p => h.Rpm.All(now => now.Percent != p.Percent))).OrderBy(p => p.Percent).ToList(),
            }).ToList(),
        };
    }

    /// <summary>A copy noting that these channels' fans stood still at these speeds (seen during a calibration).</summary>
    public FanInventory WithStandstill(IEnumerable<(int Channel, float Percent)> standstill)
    {
        var stops = standstill.GroupBy(x => x.Channel).ToDictionary(g => g.Key, g => g.Max(x => x.Percent));
        return this with
        {
            Headers = Headers.Select(h => stops.TryGetValue(h.Channel, out float percent)
                ? h with { Rpm = h.Rpm.Where(p => p.Percent != percent).Append(new RpmPoint(percent, 0)).OrderBy(p => p.Percent).ToList() }
                : h).ToList(),
        };
    }

    /// <summary>A copy with the given channels marked as pump (or not), e.g. after the user confirmed it.</summary>
    public FanInventory WithPumps(IReadOnlySet<int> pumpChannels) =>
        this with { Headers = Headers.Select(h => h with { IsPump = pumpChannels.Contains(h.Channel) }).ToList() };
}
