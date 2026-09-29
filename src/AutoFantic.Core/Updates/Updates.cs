using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using static AutoFantic.Core.Texts;

namespace AutoFantic.Core.Updates;

/// <summary>A published version on GitHub, with the zip to download and its SHA-256.</summary>
/// <param name="Sha256">The zip's checksum as GitHub reports it (lowercase hex).</param>
public sealed record Release(Version Version, string Name, string Notes, string PageUrl, string ZipUrl, long ZipSize, string Sha256, DateTimeOffset? Published);

/// <summary>Whether AutoFantic checks for a new version by itself (once a day and at every start).</summary>
public sealed record UpdateSettings(bool CheckDaily = true)
{
    public const string FileName = "updates.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static UpdateSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}

/// <summary>
/// Asks GitHub for AutoFantic's releases and finds one newer than this build. It's one small
/// request to GitHub's public API; nothing about the PC is sent. Drafts don't show up (GitHub
/// hides them), pre-releases do (every 0.x version is one).
/// </summary>
public static class UpdateCheck
{
    public const string ReleasesUrl = "https://api.github.com/repos/Ironnix/AutoFantic/releases?per_page=20";

    /// <summary>This build's version (a dev build's "0.2.0-dev" counts as 0.2.0); null for a build without a proper one.</summary>
    public static Version? Current => ParseVersion(AppVersion.Text.Split('-')[0]);

    /// <summary>The newest release above <paramref name="current"/>, or null if this is the newest.</summary>
    public static async Task<Release?> NewerAsync(Version current, string url = ReleasesUrl, CancellationToken cancel = default)
    {
        using var http = Client(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
        return Newest(await response.Content.ReadAsStringAsync(cancel), current);
    }

    /// <summary>
    /// From GitHub's list of releases: the highest version above <paramref name="current"/> that has
    /// the program's zip with a checksum. Drafts, tags that aren't a plain version ("v0.2.0-test")
    /// and releases without the zip are skipped.
    /// </summary>
    public static Release? Newest(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        Release? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (Bool(release, "draft") || ParseVersion(Text(release, "tag_name")) is not { } version || version <= Normalize(current))
                continue;
            if (best is not null && version <= best.Version)
                continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var asset in assets.EnumerateArray())
            {
                string name = Text(asset, "name");
                string digest = Text(asset, "digest");
                string url = Text(asset, "browser_download_url");
                if (!name.StartsWith("AutoFantic-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)
                    || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || url.Length == 0)
                    continue;
                best = new Release(version, Text(release, "name") is { Length: > 0 } title ? title : $"AutoFantic {version}", Text(release, "body"),
                    Text(release, "html_url"), url, asset.TryGetProperty("size", out var size) && size.TryGetInt64(out long bytes) ? bytes : 0,
                    digest["sha256:".Length..].ToLowerInvariant(),
                    DateTimeOffset.TryParse(Text(release, "published_at"), out var published) ? published : null);
                break;
            }
        }
        return best;
    }

    /// <summary>"v0.2.0" or "0.2.0" → 0.2.0; null for anything else (a test tag like "v0.2.0-rc1", "?").</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim().TrimStart('v', 'V');
        return text.Count(c => c == '.') is 1 or 2 && Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    /// <summary>Always three parts, so 0.2 and 0.2.0 compare equal.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    internal static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AutoFantic/{AppVersion.Text}"); // GitHub's API refuses requests without one
        return http;
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Puts a new version in place of the running one. A running exe can't be overwritten, but it can
/// be renamed: each old file becomes "name.&lt;time&gt;.old" and the new one is copied in; the next
/// start removes the .old files. If any step fails, every file is put back as it was.
/// </summary>
public static class UpdateInstaller
{
    public const string ExeName = "AutoFantic.exe";
    private const string OldPattern = "*.old";

    /// <summary>
    /// Downloads the release's zip into <paramref name="work"/> (emptied first), checks it against
    /// the SHA-256 GitHub reported and unpacks it. Returns the folder with the new files.
    /// </summary>
    /// <param name="progress">0…1 while downloading.</param>
    public static async Task<string> DownloadAsync(Release release, string work, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, "download.zip");

        using (var http = UpdateCheck.Client(TimeSpan.FromMinutes(30)))
        using (var response = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.ZipSize;
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var target = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0)
                    progress?.Report(Math.Min(1, (double)done / total));
            }
        }

        string actual;
        await using (var check = File.OpenRead(zip))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, cancel));
        if (actual != release.Sha256)
        {
            Directory.Delete(work, recursive: true);
            throw new InvalidDataException(T("The download doesn't match GitHub's checksum (damaged or changed on the way). Nothing was changed; try again."));
        }

        string files = Path.Combine(work, "files");
        ZipFile.ExtractToDirectory(zip, files); // refuses entries that would land outside the folder
        File.Delete(zip);
        if (!File.Exists(Path.Combine(files, ExeName)))
            throw new InvalidDataException(T($"The download has no {ExeName}. Nothing was changed."));
        return files;
    }

    /// <summary>
    /// Copies every file of <paramref name="files"/> (not subfolders) into <paramref name="appFolder"/>;
    /// a file that is already there is renamed to .old first. All or nothing.
    /// </summary>
    public static void Install(string files, string appFolder)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var done = new List<(string Target, string? Old)>();
        try
        {
            foreach (string file in Directory.GetFiles(files))
            {
                string target = Path.Combine(appFolder, Path.GetFileName(file));
                string? old = File.Exists(target) ? $"{target}.{stamp}.old" : null;
                if (old is not null)
                    File.Move(target, old);
                done.Add((target, old));
                File.Copy(file, target);
            }
        }
        catch
        {
            done.Reverse();
            foreach (var (target, old) in done)
            {
                if (File.Exists(target))
                    File.Delete(target);
                if (old is not null)
                    File.Move(old, target);
            }
            throw;
        }
    }

    /// <summary>
    /// After an update: removes the old files (those still in use, like the old watchdog's exe,
    /// stay until the next start) and what the download left in <paramref name="work"/>.
    /// </summary>
    public static void CleanUp(string appFolder, string work)
    {
        foreach (string old in Directory.EnumerateFiles(appFolder, OldPattern))
            TryDelete(() => File.Delete(old));
        if (Directory.Exists(work))
            TryDelete(() => Directory.Delete(work, recursive: true));
    }

    /// <summary>
    /// Only a published AutoFantic (one exe) can update itself: not a build that runs from the
    /// compiler's output (AutoFantic.dll next to it), which would end up half old, half new.
    /// </summary>
    public static bool CanInstallInto(string appFolder) =>
        File.Exists(Path.Combine(appFolder, ExeName)) && !File.Exists(Path.Combine(appFolder, "AutoFantic.dll"));

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still in use: the next start tries again
        }
    }
}
