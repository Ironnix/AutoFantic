using System.Globalization;
using AutoFantic.Core.Hardware;

namespace AutoFantic.Spike;

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

    /// <summary>A comma-separated list of numbers, e.g. "--steps 100,80,60".</summary>
    public IReadOnlyList<float> GetNumbers(string name, IReadOnlyList<float> fallback)
    {
        if (Get(name) is not { } text)
            return fallback;

        var numbers = new List<float>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!float.TryParse(part.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                throw new UsageException($"\"{part}\" in {name} is not a number.");
            numbers.Add(value);
        }
        return numbers;
    }

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

    /// <summary>
    /// Fan channels from "2", "#2", "0,1,3" or full ids. Throws with a helpful message
    /// if one doesn't exist, so a typo never ends up driving the wrong fan.
    /// </summary>
    public static IReadOnlyList<FanChannel> ParseChannels(FanSession session, string text)
    {
        var channels = new List<FanChannel>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string trimmed = part.TrimStart('#');
            var channel = int.TryParse(trimmed, out int index)
                ? session.Channels.FirstOrDefault(c => c.Index == index)
                : session.Channels.FirstOrDefault(c => c.Id == part);

            if (channel is null)
                throw new UsageException($"No fan channel \"{part}\". Run \"list\" to see them.");
            if (!channels.Contains(channel))
                channels.Add(channel);
        }
        return channels;
    }
}

/// <summary>A mistake on the command line: printed as a plain message, without a stack trace.</summary>
internal sealed class UsageException(string message) : Exception(message);
