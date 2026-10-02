using System.Runtime.InteropServices;

namespace AutoFantic.App;

/// <summary>The monitors as Windows runs them right now (for the hint about the graphics card's idle power).</summary>
internal static class Displays
{
    /// <summary>The refresh rate of every monitor in use, in Hz; a monitor Windows doesn't tell the rate of is left out.</summary>
    public static IReadOnlyList<int> RefreshRates()
    {
        var rates = new List<int>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            // 0 and 1 mean "the hardware's default", not a rate
            if (EnumDisplaySettings(screen.DeviceName, CurrentSettings, ref mode) && mode.DisplayFrequency > 1)
                rates.Add(mode.DisplayFrequency);
        }
        return rates;
    }

    private const int CurrentSettings = -1;

    // DEVMODEW, with the display's half of its union
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);
}
