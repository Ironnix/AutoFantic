namespace AutoFanatic.Core.Hardware;

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
