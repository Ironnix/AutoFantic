using System.Text.Json;

namespace AutoFantic.Core.Control;

/// <summary>How the fan control behaves beyond the curves, as the user chose it.</summary>
/// <param name="ReactEarly">The fans speed up as soon as the graphics card's power jumps, before its temperature follows
/// (<see cref="CurveController.ReactEarly"/>). Off unless switched on.</param>
public sealed record ControlSettings(bool ReactEarly = false)
{
    public const string FileName = "control.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static ControlSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ControlSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
