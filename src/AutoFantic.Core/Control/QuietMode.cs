using System.Runtime.InteropServices;
using System.Text.Json;

namespace AutoFantic.Core.Control;

/// <summary>
/// When the fans should be as quiet as possible: when nobody has touched the PC for a while
/// (away), and in a time window every night. Quiet means every fan at its slowest and fans that can
/// stop off (the graphics card's too), as long as it stays cool; see <see cref="CurveController.Quiet"/>.
/// </summary>
/// <param name="NightFrom">Start of the night, local time ("23:00").</param>
/// <param name="NightTo">End of the night ("07:00"); earlier than the start means over midnight.</param>
public sealed record QuietSettings(bool WhenAway = false, int AwayMinutes = 10, bool AtNight = false, string NightFrom = "23:00", string NightTo = "07:00")
{
    public const string FileName = "quiet.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static QuietSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<QuietSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>Why it's quiet now ("away", "night"), or null.</summary>
    /// <param name="idleFor">Since the last key press or mouse move.</param>
    public string? Reason(DateTime localNow, TimeSpan idleFor)
    {
        if (AtNight && TimeOnly.TryParse(NightFrom, out var from) && TimeOnly.TryParse(NightTo, out var to))
        {
            var now = TimeOnly.FromDateTime(localNow);
            bool night = from <= to ? now >= from && now < to : now >= from || now < to;
            if (night)
                return "night";
        }
        return WhenAway && idleFor >= TimeSpan.FromMinutes(AwayMinutes) ? "away" : null;
    }
}

/// <summary>How long the user hasn't touched keyboard or mouse (Windows' own count, for this session).</summary>
public static class UserIdle
{
    public static TimeSpan For()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
