using System.Globalization;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Spike;

internal static class Format
{
    public static string Unit(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "°C",
        SensorKind.Fan => "rpm",
        SensorKind.Control => "%",
        SensorKind.Power => "W",
        SensorKind.Load => "%",
        SensorKind.Clock => "MHz",
        _ => "",
    };

    public static string Value(float? value, SensorKind kind) =>
        value is { } v ? $"{Number(v, kind)} {Unit(kind)}".TrimEnd() : "–";

    /// <summary>Number only, sized for a narrow column.</summary>
    public static string Number(float? value, SensorKind kind) => value switch
    {
        null => "–",
        { } v when kind is SensorKind.Fan or SensorKind.Clock => v.ToString("0", CultureInfo.InvariantCulture),
        { } v => v.ToString("0.0", CultureInfo.InvariantCulture),
    };

    public static string Csv(float? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";

    public static string CsvText(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
}
