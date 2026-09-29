using AutoFantic.Core.Updates;

namespace AutoFantic.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "autofantic-update-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // ── finding the newest release ─────────────────────────────────────────────────────

    private static string Release(string tag, bool draft = false, string? zip = null, string digest = "sha256:ABC123") => $$"""
        {
          "tag_name": "{{tag}}", "name": "AutoFantic {{tag.TrimStart('v')}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": true,
          "html_url": "https://github.com/Ironnix/AutoFantic/releases/tag/{{tag}}", "body": "notes of {{tag}}", "published_at": "2026-10-01T12:00:00Z",
          "assets": [
            { "name": "{{zip ?? $"AutoFantic-{tag.TrimStart('v')}-win-x64.zip"}}", "size": 111480700, "digest": "{{digest}}",
              "browser_download_url": "https://github.com/Ironnix/AutoFantic/releases/download/{{tag}}/file.zip" }
          ]
        }
        """;

    private static string List(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Fact]
    public void The_highest_version_above_this_one_is_offered()
    {
        var found = UpdateCheck.Newest(List(Release("v0.1.0"), Release("v0.3.0"), Release("v0.2.0")), new Version(0, 1, 0));

        Assert.NotNull(found);
        Assert.Equal(new Version(0, 3, 0), found.Version);
        Assert.Equal("AutoFantic 0.3.0", found.Name);
        Assert.Equal("notes of v0.3.0", found.Notes);
        Assert.Equal("abc123", found.Sha256);
        Assert.Equal(111480700, found.ZipSize);
        Assert.EndsWith("/v0.3.0/file.zip", found.ZipUrl);
    }

    [Fact]
    public void Nothing_is_offered_when_this_is_the_newest()
    {
        Assert.Null(UpdateCheck.Newest(List(Release("v0.1.0")), new Version(0, 1, 0)));
        Assert.Null(UpdateCheck.Newest(List(Release("v0.1.0")), new Version(0, 2, 0)));
        Assert.Null(UpdateCheck.Newest("[]", new Version(0, 1, 0)));
        Assert.Null(UpdateCheck.Newest("""{ "message": "API rate limit exceeded" }""", new Version(0, 1, 0)));
    }

    [Fact]
    public void Drafts_test_tags_and_releases_without_a_checked_zip_are_skipped()
    {
        var found = UpdateCheck.Newest(List(
            Release("v0.5.0", draft: true),
            Release("v0.4.0-rc1"),
            Release("v0.3.0", zip: "source.zip"),
            Release("v0.2.5", digest: ""),
            Release("v0.2.0")), new Version(0, 1, 0));

        Assert.Equal(new Version(0, 2, 0), found?.Version);
    }

    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.2", "0.2.0")]
    [InlineData("V1.10.3", "1.10.3")]
    [InlineData("v0.2.0-rc1", null)]
    [InlineData("0.2.0.1", null)]
    [InlineData("?", null)]
    [InlineData("", null)]
    public void Versions_are_read_from_tags(string tag, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), UpdateCheck.ParseVersion(tag));

    // ── putting it in place ────────────────────────────────────────────────────────────

    private string Folder(string name, params (string Name, string Text)[] files)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var (file, text) in files)
            File.WriteAllText(Path.Combine(folder, file), text);
        return folder;
    }

    [Fact]
    public void Install_puts_the_new_files_in_place_and_keeps_the_old_ones_until_the_clean_up()
    {
        string app = Folder("app", ("AutoFantic.exe", "old exe"), ("README.md", "old readme"), ("my-notes.txt", "mine"));
        string files = Folder("new", ("AutoFantic.exe", "new exe"), ("README.md", "new readme"), ("CHANGELOG.md", "changes"));

        UpdateInstaller.Install(files, app);

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(app, "AutoFantic.exe")));
        Assert.Equal("new readme", File.ReadAllText(Path.Combine(app, "README.md")));
        Assert.Equal("changes", File.ReadAllText(Path.Combine(app, "CHANGELOG.md")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(app, "my-notes.txt"))); // not the update's: untouched
        Assert.Equal(2, Directory.GetFiles(app, "*.old").Length);

        string work = Folder("work", ("leftover.zip", "zip"));
        UpdateInstaller.CleanUp(app, work);

        Assert.Empty(Directory.GetFiles(app, "*.old"));
        Assert.False(Directory.Exists(work));
        Assert.Equal(["AutoFantic.exe", "CHANGELOG.md", "README.md", "my-notes.txt"], Directory.GetFiles(app).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_failed_install_puts_everything_back()
    {
        string app = Folder("app", ("AutoFantic.exe", "old exe"), ("README.md", "old readme"));
        Directory.CreateDirectory(Path.Combine(app, "z-blocked.txt")); // a folder where a file must go: the copy fails
        string files = Folder("new", ("AutoFantic.exe", "new exe"), ("README.md", "new readme"), ("z-blocked.txt", "can't"));

        Assert.ThrowsAny<Exception>(() => UpdateInstaller.Install(files, app));

        Assert.Equal("old exe", File.ReadAllText(Path.Combine(app, "AutoFantic.exe")));
        Assert.Equal("old readme", File.ReadAllText(Path.Combine(app, "README.md")));
        Assert.Empty(Directory.GetFiles(app, "*.old"));
    }

    [Fact]
    public void Only_a_published_build_updates_itself()
    {
        Assert.True(UpdateInstaller.CanInstallInto(Folder("published", ("AutoFantic.exe", "exe"))));
        Assert.False(UpdateInstaller.CanInstallInto(Folder("bin", ("AutoFantic.exe", "host"), ("AutoFantic.dll", "code"))));
        Assert.False(UpdateInstaller.CanInstallInto(Folder("empty")));
    }

    [Fact]
    public void The_setting_to_check_by_itself_is_kept()
    {
        string path = Path.Combine(Folder("data"), UpdateSettings.FileName);
        Assert.True(UpdateSettings.Load(path).CheckDaily); // on until switched off

        new UpdateSettings(CheckDaily: false).Save(path);

        Assert.False(UpdateSettings.Load(path).CheckDaily);
    }
}
