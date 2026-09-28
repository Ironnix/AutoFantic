using Timer = System.Threading.Timer;

namespace AutoFantic.App;

/// <summary>
/// Keeps the memory of a program that sits next to the clock all day small. .NET sizes its
/// youngest generation by the CPU cache: on a CPU with a very large cache (a Ryzen X3D: 96 MB)
/// it lets about 48 MB of garbage pile up before it cleans up, and Task Manager counts that as
/// memory in use. A collection every 30 s (well under a millisecond with this little data) keeps
/// it at a few MB. The runtime's own setting for this (DOTNET_GCgen0size) is only read from an
/// environment variable, which a program started at logon can't be given.
/// </summary>
internal static class MemoryUpkeep
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private static Timer? _collect, _release;

    public static void Start()
    {
        _collect = new Timer(_ => GC.Collect(0), null, Every, Every);
        _release = new Timer(_ => GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true));
        ReleaseSoon(TimeSpan.FromMinutes(1)); // what starting up left behind
    }

    /// <summary>
    /// Gives memory that is no longer needed (a closed window, the start-up) back to Windows, after
    /// a moment so the window is really gone.
    /// </summary>
    public static void ReleaseSoon(TimeSpan? after = null) =>
        _release?.Change(after ?? TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
}
