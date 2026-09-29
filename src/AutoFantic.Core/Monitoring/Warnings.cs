using System.Text.Json;
using static AutoFantic.Core.Texts;

namespace AutoFantic.Core.Monitoring;

/// <summary>"Tell me when …": a value above (or below) a limit for a while.</summary>
/// <param name="Series">The series key, e.g. "cpu.temp".</param>
/// <param name="Seconds">How long it has to stay past the limit, so a short spike doesn't warn.</param>
public sealed record WarningRule(string Series, bool Above, double Limit, int Seconds = 10, bool Enabled = true);

/// <param name="FanStopped">Warn when a fan stands still although AutoFantic runs it (blocked, broken, unplugged).</param>
public sealed record WarningSettings(bool FanStopped, IReadOnlyList<WarningRule> Rules)
{
    public const string FileName = "warnings.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static WarningSettings Default { get; } = new(true, []);

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static WarningSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<WarningSettings>(File.ReadAllText(path), Json) ?? Default : Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }
}

/// <summary>A fan group in one sample: what AutoFantic set (null = the BIOS has it) and how fast it turns.</summary>
public sealed record FanSample(string Name, double? Percent, double? Rpm);

/// <summary>
/// Checks every sample against the warning rules. A rule warns once when its value has been past the
/// limit for its seconds, and again only after it was clearly back (a few units the other way), so
/// a value wobbling around the limit doesn't warn every few seconds.
/// </summary>
public sealed class WarningWatch
{
    /// <summary>A fan that turns slower than this counts as standing still.</summary>
    public const double StoppedBelowRpm = 50;

    public static readonly TimeSpan FanStoppedFor = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, (DateTimeOffset? Since, bool Warned)> _state = [];
    private WarningSettings _settings = WarningSettings.Default;

    public WarningSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            _state.Clear();
        }
    }

    /// <summary>What to warn about now (usually nothing).</summary>
    /// <param name="values">This sample's values by series key.</param>
    /// <param name="describe">Name and unit of a series key, for the message.</param>
    public IReadOnlyList<string> Check(DateTimeOffset time, IReadOnlyDictionary<string, double> values, IReadOnlyList<FanSample> fans, Func<string, Series?> describe)
    {
        var warnings = new List<string>();
        foreach (var rule in _settings.Rules.Where(r => r.Enabled))
        {
            if (!values.TryGetValue(rule.Series, out double value))
                continue;
            var series = describe(rule.Series);
            double margin = series?.Kind == SeriesKind.FanRpm ? 100 : 2;
            bool past = rule.Above ? value > rule.Limit : value < rule.Limit;
            bool back = rule.Above ? value <= rule.Limit - margin : value >= rule.Limit + margin;
            string key = $"{rule.Series}|{rule.Above}|{rule.Limit}";
            if (Step(key, time, past, back, TimeSpan.FromSeconds(rule.Seconds)))
            {
                string unit = series?.Unit ?? "";
                // a fan's name is the hardware's; CPU, GPU hotspot … are said in the chosen language
                string name = series is null ? rule.Series : series.Kind is SeriesKind.FanPercent or SeriesKind.FanRpm ? series.Name : T(series.Name);
                warnings.Add(rule.Above
                    ? T($"{name} above {rule.Limit:0} {unit} for {rule.Seconds} s (now {value:0} {unit}).")
                    : T($"{name} below {rule.Limit:0} {unit} for {rule.Seconds} s (now {value:0} {unit})."));
            }
        }

        if (_settings.FanStopped)
        {
            foreach (var fan in fans)
            {
                bool shouldTurn = fan.Percent is > 0;
                bool standing = fan.Rpm is < StoppedBelowRpm;
                if (Step($"stopped|{fan.Name}", time, shouldTurn && standing, !shouldTurn || fan.Rpm is >= StoppedBelowRpm, FanStoppedFor))
                    warnings.Add(T($"{fan.Name} stands still although AutoFantic runs it at {fan.Percent:0} %: blocked, broken or unplugged?"));
            }
        }
        return warnings;
    }

    private bool Step(string key, DateTimeOffset time, bool past, bool back, TimeSpan after)
    {
        var (since, warned) = _state.GetValueOrDefault(key);
        if (back)
        {
            _state[key] = (null, false);
            return false;
        }
        if (!past)
        {
            _state[key] = (null, warned); // between the limit and "clearly back": no new warning yet
            return false;
        }
        since ??= time;
        bool now = !warned && time - since >= after;
        _state[key] = (since, warned || now);
        return now;
    }
}
