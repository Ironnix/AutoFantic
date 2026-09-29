using System.Reflection;

namespace AutoFantic.Core;

/// <summary>The version of this build, e.g. "0.1.0" (from Directory.Build.props, or the release tag).</summary>
public static class AppVersion
{
    public static string Text { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] // without the commit hash the SDK adds
        ?? "?";
}
