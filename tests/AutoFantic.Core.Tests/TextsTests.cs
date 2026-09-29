using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoFantic.Core.Tests;

/// <summary>The German interface: how keys are made, and that every translation fits its English text.</summary>
public partial class TextsTests
{
    [Fact]
    public void An_interpolated_text_becomes_a_key_with_numbered_holes_and_its_values()
    {
        var text = new TextHandler(20, 3);
        text.AppendFormatted(7);
        text.AppendLiteral(" of ");
        text.AppendFormatted(60);
        text.AppendLiteral(" steady minutes {braces} at ");
        text.AppendFormatted(22.456, "0.0");

        Assert.Equal("{0} of {1} steady minutes {{braces}} at {2:0.0}", text.Key);
        Assert.Equal([7, 60, 22.456], text.Args);
    }

    [Fact]
    public void In_English_a_text_stays_as_it_is_with_its_values_filled_in()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("7 of 60 steady minutes at 22.5 °C", Texts.T($"{7} of {60} steady minutes at {22.456:0.0} °C"));
            Assert.Equal("a text without a translation", Texts.T("a text without a translation"));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [GeneratedRegex(@"(?<!\{)\{(\d+)(,-?\d+)?(:[^{}]*)?\}(?!\})")]
    private static partial Regex Hole();

    private static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AutoFantic.sln")))
                return Path.Combine([dir.FullName, .. parts]);
        throw new DirectoryNotFoundException("AutoFantic.sln not found above the test folder");
    }

    private static IEnumerable<(string File, Dictionary<string, string> Texts)> LanguageFiles() =>
        Directory.EnumerateFiles(RepoFile("src"), "*.json", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "Lang" && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (f, JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f)) ?? []));

    [Fact]
    public void Every_German_text_has_the_same_holes_as_its_English_one_and_none_is_empty()
    {
        var problems = new List<string>();
        foreach (var (file, texts) in LanguageFiles())
        {
            foreach (var (english, german) in texts)
            {
                if (string.IsNullOrWhiteSpace(german))
                    problems.Add($"{Path.GetFileName(file)}: empty for \"{english}\"");
                var want = Hole().Matches(english).Select(m => m.Value).Order().ToList();
                var have = Hole().Matches(german).Select(m => m.Value).Order().ToList();
                if (!want.SequenceEqual(have))
                    problems.Add($"{Path.GetFileName(file)}: \"{english}\" has {string.Join(" ", want)}, the German one {string.Join(" ", have)}");
                if (want.Count > 0)
                {
                    // the same text as string.Format sees it: its braces must make sense (texts as values: formats don't matter)
                    try
                    {
                        _ = string.Format(CultureInfo.InvariantCulture, german, Enumerable.Repeat<object?>("x", want.Count + 1).ToArray());
                    }
                    catch (FormatException)
                    {
                        problems.Add($"{Path.GetFileName(file)}: \"{german}\" doesn't format");
                    }
                }
            }
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void The_same_English_text_is_translated_the_same_way_everywhere()
    {
        var seen = new Dictionary<string, (string German, string File)>();
        var different = new List<string>();
        foreach (var (file, texts) in LanguageFiles())
        {
            foreach (var (english, german) in texts)
            {
                if (seen.TryGetValue(english, out var other) && other.German != german)
                    different.Add($"\"{english}\": \"{other.German}\" ({Path.GetFileName(other.File)}) vs \"{german}\" ({Path.GetFileName(file)})");
                seen.TryAdd(english, (german, file));
            }
        }
        Assert.Empty(different);
    }

    [Fact]
    public void The_German_texts_are_built_into_the_program()
    {
        var core = LanguageFiles().Where(f => f.File.Contains($"AutoFantic.Core{Path.DirectorySeparatorChar}Lang")).SelectMany(f => f.Texts).ToList();

        Assert.NotEmpty(core);
        Assert.All(core.Take(50), t => Assert.Equal(t.Value, Texts.ToGerman(t.Key)));
    }
}
