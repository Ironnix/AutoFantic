using System.IO;
using System.Text.Json;

namespace AutoFantic.App;

/// <summary>The lines the user put together in the Monitor's "Compare" chart, in the order they were picked.</summary>
/// <param name="Series">Keys of the monitor's series, e.g. "gpu.memory" or a fan's rpm.</param>
internal sealed record CompareSettings(IReadOnlyList<string> Series)
{
    public const string FileName = "compare.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public CompareSettings() : this([])
    {
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static CompareSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<CompareSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
