using System.IO;
using System.Text.Json;

namespace AutoFantic.App;

/// <summary>The hints the user still wants to see.</summary>
/// <param name="IdlePower">On the Overview: the graphics card draws far too much while the PC is idle, and what helps. Off after "Don't show again".</param>
internal sealed record HintSettings(bool IdlePower = true)
{
    public const string FileName = "hints.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static HintSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<HintSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
