using System.Diagnostics;

namespace AutoFanatic.Core.Hardware;

/// <summary>
/// Finds other running programs that may drive fans. Two controllers writing the same header
/// fight each other and ruin every measurement, so their fan control has to be off.
/// </summary>
public static class FanToolCheck
{
    // process name (without .exe) → display name
    private static readonly Dictionary<string, string> KnownTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ArgusMonitor"] = "Argus Monitor",
        ["FanControl"] = "Fan Control",
        ["ArmouryCrate"] = "Armoury Crate",
        ["ArmouryCrate.Service"] = "Armoury Crate",
        ["iCUE"] = "Corsair iCUE",
        ["MSI.CentralServer"] = "MSI Center",
        ["NZXT CAM"] = "NZXT CAM",
        ["MSIAfterburner"] = "MSI Afterburner (GPU fan curve)",
        ["GPUTweakIII"] = "ASUS GPU Tweak III (GPU fan curve)",
        ["PrecisionX_x64"] = "EVGA Precision X1 (GPU fan curve)",
        ["SpeedFan"] = "SpeedFan",
        ["LibreHardwareMonitor"] = "LibreHardwareMonitor",
    };

    public static IReadOnlyList<string> Running()
    {
        var found = new SortedSet<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (KnownTools.TryGetValue(process.ProcessName, out var name))
                    found.Add(name);
            }
        }
        return found.ToList();
    }
}
