using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoFantic.App;

/// <summary>
/// How much memory and CPU AuFantic itself uses, for Settings → Developer. Memory is what Task
/// Manager shows as "Memory" (the private working set). The window alone costs about 40–50 MB while
/// it's open, so the value with the window closed is remembered too (the tray icon measures it
/// once a minute while the window is closed).
/// </summary>
internal static class SelfUsage
{
    private static TimeSpan _lastCpu;
    private static DateTime _lastAt;

    /// <summary>The memory with the window closed, and when it was measured; null before the first measurement.</summary>
    public static (long Bytes, DateTime At)? WindowClosed { get; private set; }

    /// <summary>Task Manager's "Memory" of this process, in bytes (the whole working set on a Windows that can't tell the private part).</summary>
    public static long Memory()
    {
        var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        if (GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size))
            return (long)(counters.PrivateWorkingSetSize != 0 ? counters.PrivateWorkingSetSize : counters.WorkingSetSize);
        return Environment.WorkingSet;
    }

    /// <summary>CPU in % of the whole PC since the last call (0 at the first), and on average since the start.</summary>
    public static (double Now, double Average) Cpu()
    {
        using var self = Process.GetCurrentProcess();
        var cpu = self.TotalProcessorTime;
        var now = DateTime.Now;
        double Share(TimeSpan used, TimeSpan over) => over.TotalSeconds <= 0 ? 0 : used.TotalSeconds / over.TotalSeconds / Environment.ProcessorCount * 100;
        double recent = _lastAt == default ? 0 : Share(cpu - _lastCpu, now - _lastAt);
        _lastCpu = cpu;
        _lastAt = now;
        return (recent, Share(cpu, now - self.StartTime));
    }

    /// <summary>Called while the window is closed.</summary>
    public static void RememberWindowClosed() => WindowClosed = (Memory(), DateTime.Now);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters // PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);
}
