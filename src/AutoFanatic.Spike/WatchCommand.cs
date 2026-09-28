using System.Text;
using AutoFanatic.Core.Hardware;

namespace AutoFanatic.Spike;

internal static class WatchCommand
{
    public static int Run(HardwareSession session, string[] args, CancellationToken cancel)
    {
        var options = new Options(args);
        var interval = TimeSpan.FromSeconds(Math.Max(0.25, options.GetDouble("--interval", 1)));

        var first = session.Read();
        var status = new StatusLine(first, session.Channels);
        using var csv = options.Get("--csv") is { } path ? new CsvLog(path, first) : null;

        Console.WriteLine("Watching (read-only, no fan is changed). Ctrl+C stops.");
        Console.WriteLine(status.Header());
        int lines = 0;

        while (!cancel.IsCancellationRequested)
        {
            var snapshot = session.Read();
            Console.WriteLine(status.Render(snapshot));
            csv?.Write(snapshot);

            if (++lines % 30 == 0)
                Console.WriteLine(status.Header());

            cancel.WaitHandle.WaitOne(interval);
        }

        return 0;
    }
}

/// <summary>Wide CSV: one row per sample, one column per sensor (header = "hardware | name | id").</summary>
internal sealed class CsvLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly List<string> _ids;

    public CsvLog(string path, Snapshot first)
    {
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _ids = first.Readings.Select(r => r.Id).ToList();
        _writer.WriteLine("time," + string.Join(',', first.Readings.Select(r => Format.CsvText($"{r.Hardware} | {r.Name} | {r.Id}"))));
        Console.WriteLine($"Logging to {Path.GetFullPath(path)}");
    }

    public void Write(Snapshot snapshot)
    {
        var values = _ids.Select(id => Format.Csv(snapshot.Value(id)));
        _writer.WriteLine($"{snapshot.Time:yyyy-MM-dd HH:mm:ss}," + string.Join(',', values));
        _writer.Flush();
    }

    public void Dispose() => _writer.Dispose();
}
