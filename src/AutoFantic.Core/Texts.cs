using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AutoFantic.Core;

/// <summary>
/// The interface in English or German. Every text the user sees goes through <c>T(…)</c>: the
/// English text is the key, the German one comes from the <c>Lang\*.json</c> files built into
/// the program. An interpolated string keeps its values: <c>T($"{n} of {m} minutes")</c> looks up
/// "{0} of {1} minutes" and fills in n and m, so the German sentence may put them in another order.
/// A text without a translation stays English. Data that is stored or compared (preset ids, series
/// names, log kinds) stays English and is translated where it's shown.
/// </summary>
public static class Texts
{
    private static readonly List<Assembly> Sources = [typeof(Texts).Assembly];
    private static readonly Lock Sync = new();
    private static Dictionary<string, string>? _german;
    private static readonly HashSet<string> MissingKeys = [];

    /// <summary>True while the interface is German.</summary>
    public static bool German { get; private set; }

    /// <summary>
    /// Chooses the language: "de", "en", or "" for Windows' own language (German if Windows is
    /// German). <paramref name="more"/> are assemblies with their own Lang\*.json (the window's).
    /// The German texts are read when the first one is needed.
    /// </summary>
    public static void Use(string? language, params Assembly[] more)
    {
        lock (Sync)
        {
            foreach (var assembly in more)
                if (!Sources.Contains(assembly))
                    Sources.Add(assembly);
            _german = null;
        }
        German = language switch
        {
            "de" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de",
        };
    }

    /// <summary>For month and day names in charts: German, or English.</summary>
    public static CultureInfo Culture => German ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.InvariantCulture;

    /// <summary>A fixed text in the chosen language.</summary>
    public static string T(string english) => German ? Lookup(english) : english;

    /// <summary>A text with values in the chosen language: the values are filled in after the translation.</summary>
    public static string T(TextHandler text) =>
        string.Format(CultureInfo.CurrentCulture, German ? Lookup(text.Key) : text.Key, text.Args);

    /// <summary>Texts asked for in German that have no translation yet (for developers: find what's left).</summary>
    public static IReadOnlyCollection<string> Missing
    {
        get
        {
            lock (Sync)
                return [.. MissingKeys];
        }
    }

    /// <summary>The German text for a key, whatever language is chosen (for tests: no switching of the shared language).</summary>
    internal static string ToGerman(string english) => Lookup(english);

    private static string Lookup(string english)
    {
        var german = _german ?? Load();
        if (german.TryGetValue(english, out var text))
            return text;
        lock (Sync)
            MissingKeys.Add(english);
        return english;
    }

    private static Dictionary<string, string> Load()
    {
        lock (Sync)
        {
            if (_german is not null)
                return _german;
            var texts = new Dictionary<string, string>();
            foreach (var assembly in Sources)
            {
                foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Lang.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
                {
                    using var stream = assembly.GetManifestResourceStream(name)!;
                    foreach (var (english, german) in JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [])
                        texts.TryAdd(english, german);
                }
            }
            return _german = texts;
        }
    }
}

/// <summary>
/// Turns <c>$"{n} of {m} minutes"</c> into the key "{0} of {1} minutes" and the values [n, m]
/// (with their formats, e.g. "{0:0} °C"), so <see cref="Texts.T(TextHandler)"/> can translate the
/// sentence first and fill in the values after.
/// </summary>
[InterpolatedStringHandler]
public ref struct TextHandler
{
    private readonly StringBuilder _key;
    private readonly object?[] _args;
    private int _count;

    public TextHandler(int literalLength, int formattedCount)
    {
        _key = new StringBuilder(literalLength + formattedCount * 4);
        _args = new object?[formattedCount];
        _count = 0;
    }

    public readonly void AppendLiteral(string text) => _key.Append(text.Replace("{", "{{").Replace("}", "}}"));

    public void AppendFormatted<TValue>(TValue value) => Hole(value, null, null);

    public void AppendFormatted<TValue>(TValue value, string? format) => Hole(value, null, format);

    public void AppendFormatted<TValue>(TValue value, int alignment) => Hole(value, alignment, null);

    public void AppendFormatted<TValue>(TValue value, int alignment, string? format) => Hole(value, alignment, format);

    private void Hole(object? value, int? alignment, string? format)
    {
        _key.Append('{').Append(_count);
        if (alignment is { } a)
            _key.Append(',').Append(a);
        if (format is not null)
            _key.Append(':').Append(format);
        _key.Append('}');
        _args[_count++] = value;
    }

    internal readonly string Key => _key.ToString();

    internal readonly object?[] Args => _args;
}
