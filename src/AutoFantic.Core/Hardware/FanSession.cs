namespace AutoFantic.Core.Hardware;

/// <summary>
/// Sensors plus fan control, real (<see cref="HardwareSession"/>) or simulated.
/// The base class owns the one safety guarantee every session must keep: each fan it changed
/// is handed back to BIOS / driver control on <see cref="RestoreAll"/> and on dispose.
/// </summary>
public abstract class FanSession : IDisposable
{
    private readonly HashSet<FanChannel> _touched = [];
    private bool _disposed;

    protected Lock Sync { get; } = new();

    public abstract IReadOnlyList<FanChannel> Channels { get; }

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

    public Snapshot Read()
    {
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadCore();
        }
    }

    /// <summary>Sets a fan to a fixed duty cycle, clamped to what the channel allows.</summary>
    public float SetPercent(FanChannel channel, float percent)
    {
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            float value = Math.Clamp(percent, channel.MinPercent, channel.MaxPercent);
            _touched.Add(channel);
            channel.Set(value);
            return value;
        }
    }

    public void RestoreDefault(FanChannel channel)
    {
        lock (Sync)
        {
            channel.RestoreDefault();
            _touched.Remove(channel);
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
            _touched.Remove(channel);
        }
    }

    /// <summary>Hands every fan this session changed back to the BIOS / driver. Never throws.</summary>
    public void RestoreAll()
    {
        lock (Sync)
        {
            foreach (var channel in _touched)
            {
                try
                {
                    channel.RestoreDefault();
                }
                catch
                {
                    // keep going: the other fans must be restored even if one fails
                }
            }
            _touched.Clear();
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

    protected abstract Snapshot ReadCore();

    protected virtual void DisposeCore() { }
}
