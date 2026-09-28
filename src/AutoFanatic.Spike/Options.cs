using System.Globalization;

namespace AutoFanatic.Spike;

/// <summary>Minimal "--name value" / "--flag" parsing; the spike doesn't need more.</summary>
/// <param name="flags">Options that take no value, e.g. "--force".</param>
internal sealed class Options(string[] args, params string[] flags)
{
    public bool Has(string flag) => args.Contains(flag);

    public string? Get(string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public double GetDouble(string name, double fallback) =>
        Get(name) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;

    /// <summary>Arguments that are neither options nor option values.</summary>
    public IReadOnlyList<string> Positional
    {
        get
        {
            var result = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    result.Add(args[i]);
                else if (!flags.Contains(args[i]))
                    i++; // skip the option's value
            }
            return result;
        }
    }
}
