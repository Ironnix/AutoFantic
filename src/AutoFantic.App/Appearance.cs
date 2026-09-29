using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace AutoFantic.App;

public enum Theme
{
    /// <summary>Light or dark as Windows is set (Settings → Personalization → Colors).</summary>
    LikeWindows,
    Light,
    Dark,
}

/// <summary>Whether the window is light, dark or like Windows (the default), and its language.</summary>
/// <param name="Language">"en", "de", or "" for Windows' own language; takes effect at the next start.</param>
internal sealed record AppearanceSettings(Theme Theme = Theme.LikeWindows, string Language = "")
{
    public const string FileName = "appearance.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static AppearanceSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppearanceSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>Switches the whole app (every open window too) to this theme.</summary>
    public void Apply() => System.Windows.Application.Current.ThemeMode = Theme switch
    {
        Theme.Light => ThemeMode.Light,
        Theme.Dark => ThemeMode.Dark,
        _ => ThemeMode.System,
    };
}

/// <summary>
/// For the controls that draw themselves (the charts, the curve editor): they take their colours
/// from the theme while drawing, so after a switch between light and dark they must draw again.
/// This follows one of the theme's brushes and redraws the control when it changes, whether the
/// switch came from Settings or from Windows.
/// </summary>
internal static class ThemeRedraw
{
    private static readonly DependencyProperty FollowedProperty = DependencyProperty.RegisterAttached(
        "Followed", typeof(object), typeof(ThemeRedraw), new PropertyMetadata(null, (d, _) => (d as UIElement)?.InvalidateVisual()));

    public static void Follow(FrameworkElement control) => control.SetResourceReference(FollowedProperty, "TextFillColorPrimaryBrush");
}

/// <summary>
/// The chart colours: the validated categorical palette, with its own steps for dark mode (the
/// same hues, validated against the dark surface; the light violet and red would be too dim
/// on a dark card). Code names a colour by its light step; drawing asks for the step of the
/// theme in use.
/// </summary>
internal static class SeriesColors
{
    private static readonly Dictionary<Color, Color> DarkSteps = new()
    {
        [Rgb(0x2a78d6)] = Rgb(0x3987e5), // blue
        [Rgb(0xeb6834)] = Rgb(0xd95926), // orange
        [Rgb(0x1baf7a)] = Rgb(0x199e70), // aqua
        [Rgb(0xeda100)] = Rgb(0xc98500), // yellow
        [Rgb(0xe87ba4)] = Rgb(0xd55181), // magenta
        [Rgb(0x008300)] = Rgb(0x008300), // green: the same in both
        [Rgb(0x4a3aa7)] = Rgb(0x9085e9), // violet
        [Rgb(0xe34948)] = Rgb(0xe66767), // red
    };

    /// <summary>True while <paramref name="where"/> shows the dark theme (its text is light).</summary>
    public static bool IsDark(FrameworkElement where) =>
        where.TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush { Color: var text } && text.R + text.G + text.B > 3 * 128;

    /// <summary>The step of <paramref name="light"/> for the theme <paramref name="where"/> shows; other colours stay as they are.</summary>
    public static Color For(Color light, FrameworkElement where) =>
        IsDark(where) && DarkSteps.TryGetValue(light, out var dark) ? dark : light;

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
