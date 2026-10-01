using AutoFantic.Core.Logging;

namespace AutoFantic.Core.Tests;

public sealed class ActivityLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "autofantic-log-" + Guid.NewGuid().ToString("N"));

    public ActivityLogTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Entries_are_kept_in_a_readable_file_and_read_back_at_the_next_start()
    {
        var log = new ActivityLog(_folder);
        log.Add(LogKind.Safety, "GPU core 86 °C over 85 °C: all fans at 100 % until it has cooled down.");
        log.Add(LogKind.Fans, "CPU Fan (#0) off: idle and cool");

        string text = File.ReadAllText(Path.Combine(_folder, ActivityLog.FileName));
        Assert.Contains("| Safety | GPU core 86 °C", text);

        var again = new ActivityLog(_folder);
        Assert.Equal(["GPU core 86 °C over 85 °C: all fans at 100 % until it has cooled down.", "CPU Fan (#0) off: idle and cool"],
            again.Entries.Select(e => e.Text));
        Assert.Equal([LogKind.Safety, LogKind.Fans], again.Entries.Select(e => e.Kind));
    }

    [Fact]
    public void A_line_break_in_a_message_stays_one_line()
    {
        var log = new ActivityLog(_folder);
        log.Add(LogKind.Warning, "first\nsecond");

        Assert.Single(File.ReadAllLines(Path.Combine(_folder, ActivityLog.FileName)));
    }

    [Fact]
    public void Only_the_latest_entries_stay_in_memory()
    {
        var log = ActivityLog.InMemoryOnly();
        for (int i = 0; i < 700; i++)
            log.Add(LogKind.Info, $"entry {i}");

        Assert.Equal(500, log.Entries.Count);
        Assert.Equal("entry 699", log.Entries[^1].Text);
    }

    [Fact]
    public void Lines_it_doesnt_understand_are_skipped()
    {
        File.WriteAllLines(Path.Combine(_folder, ActivityLog.FileName), ["garbage", "2026-09-29 10:00:00 | Info | AuFantic started"]);

        var log = new ActivityLog(_folder);

        Assert.Equal("AuFantic started", Assert.Single(log.Entries).Text);
    }
}
