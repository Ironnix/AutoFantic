using static AutoFantic.Core.Texts;

namespace AutoFantic.Core.Hardware;

/// <summary>
/// Sensors plus fan control, real (<see cref="HardwareSession"/>) or simulated.
/// The base class owns the one safety guarantee every session must keep: each fan it changed
/// is handed back to BIOS / driver control on <see cref="RestoreAll"/> and on dispose.
/// </summary>
public abstract class FanSession : IDisposable
{
    private readonly HashSet<FanChannel> _touched = [];
    private DateTimeOffset _touchedSince;
    private bool _disposed;

    protected Lock Sync { get; } = new();

    /// <summary>
    /// Where to keep fans-in-use.json (<see cref="HandbackFile"/>) while this session drives any fan,
    /// so a watchdog can hand them back if the process dies; null keeps no file.
    /// </summary>
    public string? HandbackPath { get; set; }

    public abstract IReadOnlyList<FanChannel> Channels { get; }

    /// <summary>The mainboard's make and model ("ASRock X870 Steel Legend WiFi"); null if the PC doesn't say (or is simulated).</summary>
    public virtual string? Board => null;

    /// <summary>
    /// The session's clock, the same one that stamps each <see cref="Snapshot"/>. Real time on
    /// real hardware; a sped-up simulation runs ahead of the wall clock.
    /// </summary>
    public virtual DateTimeOffset Now => DateTimeOffset.Now;

    /// <summary>Session seconds per real second. Waits divide by this, so a sped-up simulation still samples once per session second.</summary>
    public virtual double TimeScale => 1;

    /// <summary>Name of the program in the foreground (e.g. the running game); null if unknown.</summary>
    public virtual string? Foreground() => ForegroundProcess.Name();

    /// <summary>
    /// Starts the built-in steady CPU + GPU load for the quick calibration; disposing stops it.
    /// <paramref name="description"/> says what runs (or what didn't start).
    /// </summary>
    public virtual IDisposable StartTestLoad(out string description)
    {
        var load = new Load.TestLoad(cpuShare: 0.6, gpuShare: 0.9);
        Thread.Sleep(500); // give the GPU thread a moment to report its card or an error
        description = load.GpuError is { } error
            ? $"CPU on all cores; GPU load failed ({error}), so start a game or benchmark yourself"
            : $"CPU on all cores + {load.GpuName ?? "GPU"}";
        return load;
    }

    /// <summary>Every sensor (finding the fans, a calibration, the developer view).</summary>
    public Snapshot Read()
    {
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadCore(null);
        }
    }

    /// <summary>
    /// Only these sensors, for what runs every second (the fan control, the monitor): only the
    /// hardware they belong to is read, and no values are made for the others. Pass the same set
    /// each time; the session may keep what it worked out for it.
    /// </summary>
    public Snapshot Read(IReadOnlySet<string> only)
    {
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadCore(only);
        }
    }

    /// <summary>Sets a fan to a fixed duty cycle, clamped to what the channel allows.</summary>
    public float SetPercent(FanChannel channel, float percent)
    {
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            float value = Math.Clamp(percent, channel.MinPercent, channel.MaxPercent);
            bool first = _touched.Add(channel);
            channel.Set(value);
            if (first)
                WriteHandback(); // after Set: that's when the chip remembers what the BIOS had
            return value;
        }
    }

    public void RestoreDefault(FanChannel channel)
    {
        lock (Sync)
        {
            channel.RestoreDefault();
            if (_touched.Remove(channel))
                WriteHandback();
        }
    }

    /// <summary>
    /// Hands a fan back even if this session never changed it (e.g. after another run crashed):
    /// takes control at the current speed first, so the hand-back really runs. That makes the
    /// graphics driver re-apply its automatic curve. A mainboard header is only as good as what
    /// the chip reports now: after a crash that may be the stuck speed, and a restart is needed.
    /// </summary>
    public void ForceRestore(FanChannel channel)
    {
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            float current = Math.Clamp(channel.Percent ?? 100, channel.MinPercent, channel.MaxPercent);
            channel.Set(current);
            channel.RestoreDefault();
            if (_touched.Remove(channel))
                WriteHandback();
        }
    }

    /// <summary>
    /// Gives back the fans a process that ended without doing so had driven (from its
    /// fans-in-use.json). Returns one line per fan, in plain words. Never throws.
    /// </summary>
    public virtual IReadOnlyList<string> HandBack(HandbackFile file)
    {
        var lines = new List<string>();
        foreach (var id in file.Channels)
        {
            var channel = Channels.FirstOrDefault(c => c.Id == id);
            if (channel is null)
            {
                lines.Add(T($"{id}: not found"));
                continue;
            }
            try
            {
                ForceRestore(channel);
                lines.Add(T($"{channel.Name}: back to BIOS"));
            }
            catch (Exception ex)
            {
                lines.Add(T($"{channel.Name}: failed ({ex.Message})"));
            }
        }
        return lines;
    }

    /// <summary>What the fan chips of these channels remember of the BIOS setup (real hardware only).</summary>
    protected virtual IReadOnlyList<ChipState> CaptureChips(IReadOnlyCollection<FanChannel> channels) => [];

    // called with Sync held, whenever the set of driven fans changes: that's rare (start, pause,
    // resume, exit), so this is no disk write every second
    private void WriteHandback()
    {
        if (HandbackPath is not { } path)
            return;
        try
        {
            if (_touched.Count == 0)
            {
                File.Delete(path);
                return;
            }
            if (_touched.Count == 1)
                _touchedSince = DateTimeOffset.Now;
            HandbackFile.ForThisProcess([.. _touched.Select(c => c.Id)], CaptureChips(_touched), _touchedSince).Save(path);
        }
        catch (Exception)
        {
            // the file only helps after a crash; failing to write it must never stop the fan control
        }
    }

    /// <summary>Hands every fan this session changed back to the BIOS / driver. Never throws.</summary>
    public void RestoreAll()
    {
        lock (Sync)
        {
            var failed = new List<FanChannel>();
            foreach (var channel in _touched)
            {
                try
                {
                    channel.RestoreDefault();
                }
                catch
                {
                    // keep going: the other fans must be restored even if one fails
                    failed.Add(channel);
                }
            }
            bool any = _touched.Count > 0;
            _touched.Clear();
            if (any && failed.Count == 0)
                WriteHandback();
        }
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (_disposed)
                return;
            RestoreAll();
            DisposeCore();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    /// <param name="only">The sensor ids wanted; null = all.</param>
    protected abstract Snapshot ReadCore(IReadOnlySet<string>? only);

    protected virtual void DisposeCore() { }
}
