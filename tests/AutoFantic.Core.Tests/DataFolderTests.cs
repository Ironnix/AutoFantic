namespace AutoFantic.Core.Tests;

public sealed class DataFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "autofantic-data-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Old(params (string Name, string Text)[] files)
    {
        string folder = Path.Combine(_root, "runs");
        foreach (var (name, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, name))!);
            File.WriteAllText(Path.Combine(folder, name), text);
        }
        return folder;
    }

    [Fact]
    public void The_old_runs_folder_is_copied_once_and_left_as_it_was()
    {
        string old = Old(("fans.json", "fans"), ("calibration.json", "curves"), (Path.Combine("sim", "fans.json"), "sim fans"));
        string next = Path.Combine(_root, "AppData", "AutoFantic");

        Assert.True(DataFolder.MigrateOnce(old, next));

        Assert.Equal("curves", File.ReadAllText(Path.Combine(next, "calibration.json")));
        Assert.Equal("sim fans", File.ReadAllText(Path.Combine(next, "sim", "fans.json")));
        Assert.True(File.Exists(Path.Combine(next, DataFolder.MigratedNote)));
        Assert.True(File.Exists(Path.Combine(old, "fans.json")));      // never deleted
        Assert.False(File.Exists(Path.Combine(old, DataFolder.MigratedNote)));

        // a second start doesn't copy over what AutoFantic has written since
        File.WriteAllText(Path.Combine(next, "calibration.json"), "newer curves");
        Assert.False(DataFolder.MigrateOnce(old, next));
        Assert.Equal("newer curves", File.ReadAllText(Path.Combine(next, "calibration.json")));
    }

    [Fact]
    public void Nothing_is_copied_over_data_that_is_already_there()
    {
        string old = Old(("fans.json", "old fans"));
        string next = Path.Combine(_root, "AppData", "AutoFantic");
        Directory.CreateDirectory(next);
        File.WriteAllText(Path.Combine(next, "fans.json"), "new fans");

        Assert.False(DataFolder.MigrateOnce(old, next));
        Assert.Equal("new fans", File.ReadAllText(Path.Combine(next, "fans.json")));
    }

    [Fact]
    public void An_empty_or_missing_old_folder_is_ignored()
    {
        string next = Path.Combine(_root, "AppData", "AutoFantic");
        Assert.False(DataFolder.MigrateOnce(Path.Combine(_root, "nothing"), next));

        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        Assert.False(DataFolder.MigrateOnce(Path.Combine(_root, "empty"), next));
        Assert.False(File.Exists(Path.Combine(next, DataFolder.MigratedNote)));
    }
}
