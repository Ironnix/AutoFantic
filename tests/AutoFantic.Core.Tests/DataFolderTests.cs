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

        // a second start doesn't copy over what AuFantic has written since
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

    // ── a folder the user chose ────────────────────────────────────────────────────────

    /// <summary>A Windows account's standard folder with these files; AuFantic's log is always there.</summary>
    private string Account(string name, params (string Name, string Text)[] files)
    {
        string folder = Path.Combine(_root, name, "AutoFantic");
        foreach (var (file, text) in files.Append(("activity.log", $"{name}'s log")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, file))!);
            File.WriteAllText(Path.Combine(folder, file), text);
        }
        return folder;
    }

    private static string Text(string folder, string name) => File.ReadAllText(Path.Combine(folder, name));

    [Fact]
    public void Without_a_choice_the_account_s_own_folder_is_used()
    {
        string own = Account("ralf", ("fans.json", "fans"));

        var use = DataFolder.Resolve(own);

        Assert.Equal(own, use.Root);
        Assert.False(use.Chosen);
        Assert.Null(use.Unusable);
    }

    [Fact]
    public void A_chosen_empty_folder_gets_all_the_data_at_the_next_start_and_the_old_folder_stays()
    {
        string own = Account("ralf", ("fans.json", "fans"), ("calibration.json", "curves"), ("history.db", "history"), (Path.Combine("sim", "fans.json"), "sim fans"));
        string shared = Path.Combine(_root, "shared");

        Assert.Equal(FolderChange.Copy, DataFolder.Choose(DataFolder.Resolve(own), shared));
        Assert.False(File.Exists(Path.Combine(shared, "fans.json"))); // not while AuFantic still has the files open

        var use = DataFolder.Resolve(own);

        Assert.Equal(shared, use.Root);
        Assert.True(use.Chosen);
        Assert.Equal(own, use.CopiedFrom);
        Assert.Equal("curves", Text(shared, "calibration.json"));
        Assert.Equal("history", Text(shared, "history.db"));
        Assert.Equal("sim fans", Text(shared, Path.Combine("sim", "fans.json")));
        Assert.Equal("fans", Text(own, "fans.json"));                                          // never deleted
        Assert.False(File.Exists(Path.Combine(shared, DataFolderChoice.FileName)));     // the choice is the account's, not the folder's

        // the start after: the same folder, and nothing is copied over what AuFantic wrote since
        File.WriteAllText(Path.Combine(shared, "calibration.json"), "newer curves");
        use = DataFolder.Resolve(own);
        Assert.Equal(shared, use.Root);
        Assert.Null(use.CopiedFrom);
        Assert.Equal("newer curves", Text(shared, "calibration.json"));
    }

    [Fact]
    public void A_second_account_that_chooses_the_same_folder_uses_what_is_there()
    {
        string ralf = Account("ralf", ("fans.json", "fans"), ("calibration.json", "curves"));
        string work = Account("work", ("appearance.json", "dark"));
        string shared = Path.Combine(_root, "shared");
        DataFolder.Choose(DataFolder.Resolve(ralf), shared);
        DataFolder.Resolve(ralf);

        Assert.Equal(FolderChange.UseAsIs, DataFolder.Choose(DataFolder.Resolve(work), shared));
        var use = DataFolder.Resolve(work);

        Assert.Equal(shared, use.Root);
        Assert.Null(use.CopiedFrom);
        Assert.Equal("curves", Text(shared, "calibration.json"));
        Assert.Equal("ralf's log", Text(shared, "activity.log"));               // nothing of the second account was copied over it
        Assert.False(File.Exists(Path.Combine(shared, "appearance.json")));
        Assert.Equal("dark", Text(work, "appearance.json"));
    }

    [Fact]
    public void The_calibrated_account_s_data_wins_also_when_the_other_account_chose_the_folder_first()
    {
        string work = Account("work", ("history.db", "work's history"), ("history.db-wal", "work's journal"));
        string ralf = Account("ralf", ("fans.json", "fans"), ("calibration.json", "curves"), ("history.db", "ralf's history"));
        string shared = Path.Combine(_root, "shared");
        DataFolder.Choose(DataFolder.Resolve(work), shared);
        Assert.Equal(shared, DataFolder.Resolve(work).Root);
        Assert.Equal("work's history", Text(shared, "history.db"));

        // the folder is AuFantic's, but has no fans and no calibration: the data that has them replaces it
        Assert.Equal(FolderChange.Copy, DataFolder.Choose(DataFolder.Resolve(ralf), shared));
        Assert.Equal(shared, DataFolder.Resolve(ralf).Root);

        Assert.Equal("curves", Text(shared, "calibration.json"));
        Assert.Equal("ralf's history", Text(shared, "history.db"));
        Assert.False(File.Exists(Path.Combine(shared, "history.db-wal")));      // another database's journal would break this one
        Assert.Equal(shared, DataFolder.Resolve(work).Root);                    // and the first account now has them too
    }

    [Fact]
    public void A_folder_with_other_files_or_inside_the_data_folder_can_t_be_chosen()
    {
        string own = Account("ralf", ("fans.json", "fans"));
        string documents = Path.Combine(_root, "documents");
        Directory.CreateDirectory(documents);
        File.WriteAllText(Path.Combine(documents, "warnings.json"), "somebody else's");
        var now = DataFolder.Resolve(own);

        Assert.Equal(FolderChange.OtherFiles, DataFolder.Choose(now, documents));
        Assert.Equal(FolderChange.Nested, DataFolder.Choose(now, Path.Combine(own, "sub")));
        Assert.Equal(FolderChange.Nested, DataFolder.Choose(now, Path.GetDirectoryName(own)!));
        Assert.Equal(FolderChange.None, DataFolder.Choose(now, own + Path.DirectorySeparatorChar));

        Assert.Equal(own, DataFolder.Resolve(own).Root);
        Assert.False(File.Exists(Path.Combine(own, DataFolderChoice.FileName)));
        Assert.Equal("somebody else's", Text(documents, "warnings.json"));
    }

    [Fact]
    public void A_chosen_folder_that_is_gone_is_left_out_for_this_start_only()
    {
        string own = Account("ralf", ("fans.json", "fans"));
        string shared = Path.Combine(_root, "shared");
        DataFolder.Choose(DataFolder.Resolve(own), shared);
        DataFolder.Resolve(own);
        Directory.Move(shared, shared + "-away"); // like a drive that isn't plugged in

        var use = DataFolder.Resolve(own);

        Assert.Equal(own, use.Root);
        Assert.Equal(shared, use.Unusable);
        Assert.False(use.CopyFailed);
        Assert.Null(use.Why); // nothing went wrong, it just isn't there
        Assert.False(Directory.Exists(shared)); // not made again: empty, it would look like a PC that was never set up

        Directory.Move(shared + "-away", shared);
        Assert.Equal(shared, DataFolder.Resolve(own).Root);
    }

    [Fact]
    public void A_copy_that_fails_undoes_the_choice_and_leaves_nothing_half_copied()
    {
        string own = Account("ralf", ("calibration.json", "curves"), ("fans.json", "fans"), ("history.db", "history"));
        string shared = Path.Combine(_root, "shared");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "history.db"), "in use");
        DataFolder.Choose(DataFolder.Resolve(own), shared);

        DataFolderUse use;
        using (new FileStream(Path.Combine(shared, "history.db"), FileMode.Open, FileAccess.Read, FileShare.None)) // another AuFantic has it open
            use = DataFolder.Resolve(own);

        Assert.Equal(own, use.Root);
        Assert.Equal(shared, use.Unusable);
        Assert.True(use.CopyFailed);
        Assert.False(DataFolder.HasData(shared)); // a later try must not take it for a set-up folder
        Assert.Equal("in use", Text(shared, "history.db"));

        use = DataFolder.Resolve(own); // the next start doesn't try again by itself
        Assert.Equal(own, use.Root);
        Assert.Null(use.Unusable);
    }

    [Fact]
    public void Choosing_the_standard_folder_again_goes_back_to_it()
    {
        string own = Account("ralf", ("fans.json", "fans"));
        string shared = Path.Combine(_root, "shared");
        DataFolder.Choose(DataFolder.Resolve(own), shared);
        var now = DataFolder.Resolve(own);
        File.WriteAllText(Path.Combine(shared, "fans.json"), "newer fans");

        Assert.Equal(FolderChange.UseAsIs, DataFolder.Choose(now, own));
        var use = DataFolder.Resolve(own);

        Assert.Equal(own, use.Root);
        Assert.False(use.Chosen);
        Assert.Equal("fans", Text(own, "fans.json"));           // as it was left: it has data of its own
        Assert.Equal("newer fans", Text(shared, "fans.json"));  // and the shared folder stays for the other account
    }
}
