using System.Globalization;
using System.Text;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Core.Logging;

/// <summary>One logged sample: all sensor values plus the program that was in the foreground.</summary>
public sealed record LoggedSample(Snapshot Snapshot, string? Foreground);

/// <summary>
/// Wide CSV: one row per sample (time, foreground program, then one column per sensor). Each
/// sensor's header is "hardware | type | kind | name | id", which is everything needed to
/// rebuild the <see cref="Snapshot"/> later, so a recorded gaming session can be replayed
/// through the same analysis code the live service will use.
/// </summary>
public sealed class SensorLogWriter : IDisposable
{
    internal const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";
    internal const string Separator = " | ";

    private readonly StreamWriter _writer;
    private readonly List<string> _ids;

    public SensorLogWriter(string path, Snapshot first)
    {
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _ids = first.Readings.Select(r => r.Id).ToList();
        var columns = first.Readings.Select(r => Quote(string.Join(Separator, r.Hardware, r.HardwareType, r.Kind, r.Name, r.Id)));
        _writer.WriteLine("time,foreground," + string.Join(',', columns));
    }

    public void Write(Snapshot snapshot, string? foreground)
    {
        var values = _ids.Select(id => snapshot.Value(id)?.ToString("0.###", CultureInfo.InvariantCulture) ?? "");
        _writer.WriteLine($"{snapshot.Time.ToString(TimeFormat, CultureInfo.InvariantCulture)},{Quote(foreground ?? "")}," + string.Join(',', values));
        _writer.Flush();
    }

    public void Dispose() => _writer.Dispose();

    internal static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
}

public static class SensorLogReader
{
    /// <summary>Reads a log written by <see cref="SensorLogWriter"/>. Throws <see cref="FormatException"/> for anything else.</summary>
    public static IEnumerable<LoggedSample> Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        var header = reader.ReadLine() is { } line ? SplitCsv(line) : throw new FormatException("The log is empty.");
        if (header.Count < 2 || header[0] != "time" || header[1] != "foreground")
            throw new FormatException("Not an AutoFanatic watch log (expected \"time,foreground,…\" in the first line).");

        var sensors = header.Skip(2).Select(ParseColumn).ToList();

        int lineNumber = 1;
        while (reader.ReadLine() is { } row)
        {
            lineNumber++;
            if (row.Length == 0)
                continue;

            var cells = SplitCsv(row);
            if (!DateTime.TryParseExact(cells[0], [SensorLogWriter.TimeFormat, "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time))
                throw new FormatException($"Line {lineNumber}: \"{cells[0]}\" is not a time.");

            var readings = new List<SensorReading>(sensors.Count);
            for (int i = 0; i < sensors.Count; i++)
            {
                string cell = i + 2 < cells.Count ? cells[i + 2] : "";
                float? value = float.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : null;
                readings.Add(sensors[i] with { Value = value });
            }

            string foreground = cells.Count > 1 ? cells[1] : "";
            yield return new LoggedSample(new Snapshot(new DateTimeOffset(time), readings), foreground.Length == 0 ? null : foreground);
        }
    }

    private static SensorReading ParseColumn(string column)
    {
        var parts = column.Split(SensorLogWriter.Separator);
        if (parts.Length < 5 || !Enum.TryParse<SensorKind>(parts[^3], out var kind))
            throw new FormatException(
                $"Column \"{column}\" has no sensor type. The log was probably written by an older version: record it again with \"watch --csv\".");

        // from the right: the hardware name is the only part that could itself contain the separator
        string hardware = string.Join(SensorLogWriter.Separator, parts[..^4]);
        return new SensorReading(parts[^1], hardware, parts[^4], kind, parts[^2], null);
    }

    internal static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                    cell.Append(c);
            }
            else if (c == '"')
                quoted = true;
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
                cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }
}
