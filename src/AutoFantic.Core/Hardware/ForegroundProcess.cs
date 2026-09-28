using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoFantic.Core.Hardware;

/// <summary>Name of the program whose window has focus (e.g. the running game).</summary>
public static class ForegroundProcess
{
    public static string? Name()
    {
        try
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return null;

            _ = GetWindowThreadProcessId(window, out uint processId);
            if (processId == 0)
                return null;

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            // the process may exit between the two calls; "unknown" is fine for a log column
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
