using AutoFantic.Core.Hardware;
using AutoFantic.Core.Logging;

namespace AutoFantic.Spike;

internal static class WatchCommand
{
    public static int Run(FanSession session, string[] args, CancellationToken cancel)
    {
        var options = new Options(args);
        var interval = TimeSpan.FromSeconds(Math.Max(0.25, options.GetDouble("--interval", 1)));

        var first = session.Read();
        var status = new StatusLine(first, session.Channels);
        string? logPath = options.Get("--csv");
        using var csv = logPath is null ? null : new SensorLogWriter(logPath, first);
        if (logPath is not null)
            Console.WriteLine($"Logging to {Path.GetFullPath(logPath)} (replay it later with \"analyze\")");

        Console.WriteLine("Watching (read-only, no fan is changed). Ctrl+C stops.");
        Console.WriteLine(status.Header());
        int lines = 0;

        while (!cancel.IsCancellationRequested)
        {
            var snapshot = session.Read();
            Console.WriteLine(status.Render(snapshot));
            csv?.Write(snapshot, session.Foreground());

            if (++lines % 30 == 0)
                Console.WriteLine(status.Header());

            cancel.WaitHandle.WaitOne(interval / session.TimeScale);
        }

        return 0;
    }
}
