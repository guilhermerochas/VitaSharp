using VitaSharp.Mobile.Core.Storage;

namespace VitaSharp.Tests;

/// <summary>
/// The Android app's data folder (Mobile.Core/Storage/DataRoot.cs, DataMigration.cs): which folder is used with and
/// without root and the all-files permission, and the move of the old app-specific folder to the visible one
/// (/storage/emulated/0/.vitasharp), which never loses a file: interrupted and resumed, copy errors, a full disk, a
/// short copy, files the target already has, and the final check.
/// </summary>
public sealed class AndroidDataRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-dataroot-tests-" + Guid.NewGuid().ToString("N"));

    private string Old => Path.Combine(_root, "storage", "Android", "data", "io.github.vitasharp", "files");

    private string Shared => Path.Combine(_root, "storage");

    private string Visible => Path.Combine(Shared, DataRoot.VisibleFolderName);

    private string Internal => Path.Combine(_root, "internal");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // ---------------------------------------------------------------- resolution

    [Fact]
    public void RootedDeviceKeepsTheAppSpecificFolder()
    {
        WriteOldData();
        DataRootPlan plan = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, HasRoot: true, HasAllFilesAccess: true));
        Assert.Equal(Old, plan.Root);
        Assert.False(plan.NeedsPermission);
        Assert.False(plan.NeedsMigration);
    }

    [Fact]
    public void RootedDeviceWithoutThePermissionStillKeepsTheAppSpecificFolderAndAsksNothing()
    {
        DataRootPlan plan = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, HasRoot: true, HasAllFilesAccess: false));
        Assert.Equal(Old, plan.Root);
        Assert.False(plan.NeedsPermission);
    }

    [Fact]
    public void WithoutRootAndWithoutThePermissionTheOldFolderIsUsedAndThePermissionAsked()
    {
        WriteOldData();
        DataRootPlan plan = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, HasRoot: false, HasAllFilesAccess: false));
        Assert.Equal(Old, plan.Root);
        Assert.True(plan.NeedsPermission);
        Assert.False(plan.NeedsMigration);
        Assert.Equal(Visible, plan.VisibleRoot);
    }

    [Fact]
    public void WithThePermissionAndOldDataTheMoveComesFirst()
    {
        WriteOldData();
        DataRootPlan plan = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, HasRoot: false, HasAllFilesAccess: true));
        Assert.Equal(Old, plan.Root);
        Assert.Equal(Old, plan.MigrateFrom);
        Assert.Equal(Visible, plan.VisibleRoot);
        Assert.False(plan.NeedsPermission);
    }

    [Fact]
    public void WithThePermissionAndNothingToMoveTheVisibleFolderIsUsed()
    {
        Directory.CreateDirectory(Old); // Android recreates the empty app-specific folder
        DataRootPlan plan = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, HasRoot: false, HasAllFilesAccess: true));
        Assert.Equal(Visible, plan.Root);
        Assert.False(plan.NeedsMigration);
        DataRootPlan fresh = DataRoot.Resolve(new DataRootInputs(Path.Combine(_root, "never-created"), Internal, Shared, false, true));
        Assert.Equal(Visible, fresh.Root);
    }

    [Fact]
    public void AnInterruptedMoveIsStillPendingWithAnEmptySource()
    {
        Directory.CreateDirectory(Old);
        Directory.CreateDirectory(Visible);
        File.WriteAllText(Path.Combine(Visible, DataMigration.JournalFileName), "3\tlogs/a.log\n");
        DataRootPlan plan = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, HasRoot: false, HasAllFilesAccess: true));
        Assert.True(plan.NeedsMigration);
        Assert.Equal(Old, plan.Root);
    }

    [Fact]
    public void WithoutSharedStorageTheAppSpecificOrInternalFolderIsUsed()
    {
        Assert.Equal(Old, DataRoot.Resolve(new DataRootInputs(Old, Internal, null, false, true)).Root);
        DataRootPlan none = DataRoot.Resolve(new DataRootInputs(null, Internal, null, false, false));
        Assert.Equal(Internal, none.Root);
        Assert.False(none.NeedsPermission);
    }

    [Fact]
    public void RootIsDetectedBySuInPathOrAKnownFolder()
    {
        Assert.True(RootDetector.HasSu("/product/bin:/apex/x/bin:/opt/su-here", path => path == "/opt/su-here/su"));
        Assert.True(RootDetector.HasSu("/product/bin", path => path == "/system/xbin/su"));
        Assert.True(RootDetector.HasSu(null, path => path == "/debug_ramdisk/su"));
        Assert.False(RootDetector.HasSu("/product/bin:/system/bin", path => path.EndsWith("/sudo", StringComparison.Ordinal)));
        Assert.False(RootDetector.HasSu("", _ => false));
    }

    // ---------------------------------------------------------------- migration

    [Fact]
    public void TheMoveTakesEverythingWithTheSameLayout()
    {
        Dictionary<string, string> before = WriteOldData();
        MigrationResult result = new DataMigration(Old, Visible).Run();
        Assert.Equal(MigrationStatus.Done, result.Status);
        Assert.Equal(before.Count, result.Files);
        Assert.Equal(before, Snapshot(Visible));
        Assert.True(Directory.Exists(Path.Combine(Visible, "fs", "ux0", "data", "empty")));
        Assert.False(Directory.Exists(Old));
        Assert.False(File.Exists(Path.Combine(Visible, DataMigration.JournalFileName)));
        Assert.False(DataMigration.IsPending(Old, Visible));
    }

    [Fact]
    public void BetweenTwoMountsEachFileIsCopiedCheckedAndThenDeleted()
    {
        Dictionary<string, string> before = WriteOldData();
        var migration = new ScriptedMigration(Old, Visible) { RenameWorks = false };
        var reports = new List<MigrationProgress>();
        MigrationResult result = migration.Run(new SyncProgress(reports.Add));
        Assert.Equal(MigrationStatus.Done, result.Status);
        Assert.Equal(before, Snapshot(Visible));
        Assert.Equal(before.Count, migration.Copies);
        Assert.False(Directory.Exists(Old));
        Assert.Empty(Directory.EnumerateFiles(Visible, "*" + DataMigration.PartSuffix, SearchOption.AllDirectories));
        MigrationProgress last = reports[^1];
        Assert.Equal(last.FilesTotal, last.FilesDone);
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.Equal(before.Values.Sum(v => (long)v.Length), last.BytesTotal);
    }

    [Fact]
    public void ACopyErrorStopsWithEveryFileInOneOfTheTwoFoldersAndTheNextRunFinishes()
    {
        Dictionary<string, string> before = WriteOldData();
        var failing = new ScriptedMigration(Old, Visible) { RenameWorks = false, FailCopyNumber = 3 };
        MigrationResult first = failing.Run();
        Assert.Equal(MigrationStatus.Failed, first.Status);
        AssertNothingLost(before);
        Assert.True(DataMigration.IsPending(Old, Visible));

        MigrationResult second = new ScriptedMigration(Old, Visible) { RenameWorks = false }.Run();
        Assert.Equal(MigrationStatus.Done, second.Status);
        Assert.Equal(before, Snapshot(Visible));
        Assert.False(Directory.Exists(Old));
    }

    [Fact]
    public void AMoveKilledInTheMiddleResumesFromWhereItWas()
    {
        Dictionary<string, string> before = WriteOldData();
        using var cancel = new CancellationTokenSource();
        var migration = new ScriptedMigration(Old, Visible) { RenameWorks = false };
        int reportsSeen = 0;
        Assert.Throws<OperationCanceledException>(() => migration.Run(new SyncProgress(_ =>
        {
            reportsSeen++;
            if (reportsSeen == 3)
            {
                cancel.Cancel();
            }
        }), cancel.Token));
        AssertNothingLost(before);
        Assert.True(File.Exists(Path.Combine(Visible, DataMigration.JournalFileName)));

        // a file written meanwhile in the old folder (the old-root process kept logging) goes too
        File.WriteAllText(Path.Combine(Old, "logs", "late.log"), "late");
        before["logs/late.log"] = "late";
        MigrationResult result = new ScriptedMigration(Old, Visible) { RenameWorks = false }.Run();
        Assert.Equal(MigrationStatus.Done, result.Status);
        Assert.Equal(before, Snapshot(Visible));
    }

    [Fact]
    public void AnInterruptedCopyLeavesOnlyAPartFileThatTheNextRunReplaces()
    {
        Dictionary<string, string> before = WriteOldData();
        Directory.CreateDirectory(Path.Combine(Visible, "config"));
        File.WriteAllText(Path.Combine(Visible, "config", "env.txt" + DataMigration.PartSuffix), "VITA"); // the killed copy
        MigrationResult result = new ScriptedMigration(Old, Visible) { RenameWorks = false }.Run();
        Assert.Equal(MigrationStatus.Done, result.Status);
        Assert.Equal(before, Snapshot(Visible));
    }

    [Fact]
    public void AShortCopyIsNotTakenAndTheSourceStays()
    {
        Dictionary<string, string> before = WriteOldData();
        MigrationResult result = new ScriptedMigration(Old, Visible) { RenameWorks = false, ShortCopyNumber = 1 }.Run();
        Assert.Equal(MigrationStatus.Failed, result.Status);
        AssertNothingLost(before);
        Assert.Empty(Directory.EnumerateFiles(Visible, "*" + DataMigration.PartSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public void AFullDiskStopsBeforeCopyingAndKeepsTheSource()
    {
        Dictionary<string, string> before = WriteOldData();
        MigrationResult result = new ScriptedMigration(Old, Visible) { RenameWorks = false, Free = 1 }.Run();
        Assert.Equal(MigrationStatus.NoSpace, result.Status);
        Assert.Equal(before, Snapshot(Old));
    }

    [Fact]
    public void AFileTheTargetAlreadyHasStaysAndTheMovedOneGoesBesideIt()
    {
        Dictionary<string, string> before = WriteOldData();
        Directory.CreateDirectory(Path.Combine(Visible, "config"));
        File.WriteAllText(Path.Combine(Visible, "config", "env.txt"), "VITASHARP_USER_EDIT=1");
        // the same content (a resumed copy that was not deleted yet): the source copy just goes
        Directory.CreateDirectory(Path.Combine(Visible, "logs"));
        File.WriteAllText(Path.Combine(Visible, "logs", "last.log"), before["logs/last.log"]);
        MigrationResult result = new DataMigration(Old, Visible).Run();
        Assert.Equal(MigrationStatus.Done, result.Status);
        // the visible folder's own file is never replaced (it may be the user's save or settings)
        Assert.Equal("VITASHARP_USER_EDIT=1", File.ReadAllText(Path.Combine(Visible, "config", "env.txt")));
        Assert.Equal(before["config/env.txt"], File.ReadAllText(Path.Combine(Visible, "config", "env.txt" + DataMigration.KeptSuffix)));
        Dictionary<string, string> after = Snapshot(Visible);
        after.Remove("config/env.txt" + DataMigration.KeptSuffix);
        after["config/env.txt"] = before["config/env.txt"];
        Assert.Equal(before, after);
    }

    // ---------------------------------------------------------------- after a reinstall

    [Fact]
    public void AfterAReinstallWithoutThePermissionTheAppSaysWhereTheDataIsInsteadOfStartingEmpty()
    {
        WriteVisibleData();
        Directory.CreateDirectory(Old); // the new install's empty app-specific folder
        DataRootPlan plan = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, HasRoot: false, HasAllFilesAccess: false));
        Assert.Equal(StorageQuestion.GrantForSharedData, plan.Question);
        Assert.True(plan.NeedsPermission);
        Assert.Equal(Old, plan.Root);
        Assert.Equal(Visible, plan.VisibleRoot);
    }

    [Fact]
    public void OnlyFoldersMarkTheVisibleDataSoItIsSeenWithoutThePermission()
    {
        // Android 11+ without "All files access" shows the folders of another install but not its files
        Directory.CreateDirectory(Path.Combine(Visible, "fs", "ux0", "app", "PCSB00001"));
        Assert.True(DataInventory.LooksLikeData(Visible));
        Assert.Equal(StorageQuestion.GrantForSharedData,
            DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, false, false)).Question);
    }

    [Fact]
    public void WithoutVisibleDataTheFirstStartOnlyAsksForThePermission()
    {
        Directory.CreateDirectory(Visible); // an empty folder (or another app's) is not VitaSharp data
        DataRootPlan plan = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, false, false));
        Assert.Equal(StorageQuestion.None, plan.Question);
        Assert.True(plan.NeedsPermission);
        Assert.False(DataInventory.LooksLikeData(Path.Combine(_root, "never-created")));
    }

    [Fact]
    public void GrantedAfterAReinstallTheOldFolderIsUsedAgainWithNothingReplaced()
    {
        Dictionary<string, string> visible = WriteVisibleData();
        // what the new install wrote by itself before the permission: the layout's empty folders and its log
        Directory.CreateDirectory(Path.Combine(Old, "fs", "ux0", "app"));
        Directory.CreateDirectory(Path.Combine(Old, "config", "games"));
        Directory.CreateDirectory(Path.Combine(Old, "logs"));
        File.WriteAllText(Path.Combine(Old, "logs", "app.log"), "new install log");
        Assert.False(DataInventory.HasUserData(Old));
        DataRootPlan plan = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, false, true));
        Assert.Equal(StorageQuestion.None, plan.Question);
        Assert.Equal(Old, plan.MigrateFrom);
        MigrationResult result = new DataMigration(plan.MigrateFrom!, plan.VisibleRoot!).Run();
        Assert.Equal(MigrationStatus.Done, result.Status);
        Dictionary<string, string> after = Snapshot(Visible);
        Assert.Equal("new install log", after["logs/app.log" + DataMigration.KeptSuffix]);
        after.Remove("logs/app.log" + DataMigration.KeptSuffix);
        Assert.Equal(visible, after);
        Assert.Equal(Visible, DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, false, true)).Root);
    }

    [Fact]
    public void WithTheUsersDataInBothFoldersNothingMovesUntilTheUserPicks()
    {
        Dictionary<string, string> visible = WriteVisibleData();
        Dictionary<string, string> old = WriteOldData();
        DataRootPlan plan = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, false, true));
        Assert.Equal(StorageQuestion.ChooseBetweenFolders, plan.Question);
        Assert.False(plan.NeedsMigration);
        Assert.Equal(Old, plan.Root);
        Assert.Equal(visible, Snapshot(Visible));
        Assert.Equal(old, Snapshot(Old));
    }

    [Fact]
    public void TheMergeOfTwoUsedFoldersOverwritesNothing()
    {
        Dictionary<string, string> visible = WriteVisibleData();
        Dictionary<string, string> old = WriteOldData();
        // the same save in both, with other content: both stay
        File.WriteAllText(Path.Combine(Old, "fs", "ux0", "user", "00", "savedata", "PCSB00001", "SAVEDATA.BIN"), "newer save");
        old["fs/ux0/user/00/savedata/PCSB00001/SAVEDATA.BIN"] = "newer save";
        MigrationResult result = new ScriptedMigration(Old, Visible) { RenameWorks = false }.Run();
        Assert.Equal(MigrationStatus.Done, result.Status);
        Dictionary<string, string> after = Snapshot(Visible);
        foreach (KeyValuePair<string, string> file in visible)
        {
            Assert.Equal(file.Value, after[file.Key]); // every file of the visible folder as it was
        }

        foreach (KeyValuePair<string, string> file in old)
        {
            bool same = after.TryGetValue(file.Key, out string? there) && there == file.Value;
            bool beside = after.TryGetValue(file.Key + DataMigration.KeptSuffix, out string? kept) && kept == file.Value;
            Assert.True(same || beside, $"{file.Key} of the app folder lost");
        }

        Assert.False(Directory.Exists(Old));
    }

    [Fact]
    public void AMergeStoppedAfterTheTargetKeptItsFileFinishesWithoutAFalseProblem()
    {
        WriteVisibleData();
        WriteOldData();
        File.WriteAllText(Path.Combine(Visible, "config", "app.json"), "{\"user\":true}");
        // the first run stops at a copy after some files went beside the target's ones
        MigrationResult first = new ScriptedMigration(Old, Visible) { RenameWorks = false, FailCopyNumber = 4 }.Run();
        Assert.Equal(MigrationStatus.Failed, first.Status);
        DataRootPlan plan = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, false, true));
        Assert.Equal(Old, plan.MigrateFrom); // an interrupted run resumes, whatever the folders hold
        MigrationResult second = new ScriptedMigration(Old, Visible) { RenameWorks = false }.Run();
        Assert.Equal(MigrationStatus.Done, second.Status);
        Assert.Equal("{\"user\":true}", File.ReadAllText(Path.Combine(Visible, "config", "app.json")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(Visible, "config", "app.json" + DataMigration.KeptSuffix)));
    }

    [Fact]
    public void ThePickedFolderIsUsedAndTheOtherLeftAsItIs()
    {
        Dictionary<string, string> visible = WriteVisibleData();
        Dictionary<string, string> old = WriteOldData();
        DataRootPlan shared = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, false, true, StorageChoice.SharedFolder));
        Assert.Equal(Visible, shared.Root);
        Assert.False(shared.NeedsMigration);
        Assert.Equal(StorageQuestion.None, shared.Question);
        DataRootPlan app = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, false, true, StorageChoice.AppFolder));
        Assert.Equal(Old, app.Root);
        Assert.True(app.SharedDataUnused);
        Assert.False(app.NeedsMigration);
        // without the permission the app-folder choice asks nothing either
        DataRootPlan denied = DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, false, false, StorageChoice.AppFolder));
        Assert.Equal(StorageQuestion.None, denied.Question);
        Assert.False(denied.NeedsPermission);
        Assert.Equal(visible, Snapshot(Visible));
        Assert.Equal(old, Snapshot(Old));
        // the visible-folder choice cannot be used without the permission: the warning comes back
        Assert.Equal(StorageQuestion.GrantForSharedData,
            DataRoot.Resolve(new DataRootInputs(Old, Internal, Shared, false, false, StorageChoice.SharedFolder)).Question);
    }

    [Fact]
    public void TheSummaryShowsWhatEachFolderHolds()
    {
        WriteVisibleData();
        Directory.CreateDirectory(Path.Combine(Visible, "fs", "ux0", "app", "PCSB00002"));
        DataSummary summary = DataInventory.Summarize(Visible);
        Assert.Equal(2, summary.Games);
        Assert.Equal(1, summary.Saves);
        Assert.Equal(1, summary.SaveStateTitles);
        Assert.Equal(5, summary.Files);
        Assert.True(summary.Bytes > 0);
        DataSummary none = DataInventory.Summarize(Path.Combine(_root, "never-created"));
        Assert.Equal(0, none.Files);
        Assert.Null(none.FirmwareVersion);
    }

    [Fact]
    public void ThePointerForTheScriptsIsBesideTheAppSpecificFolder()
    {
        Directory.CreateDirectory(Old);
        Assert.True(DataRootPointer.Write(Old, Visible));
        string pointer = Path.Combine(Path.GetDirectoryName(Old)!, DataRootPointer.FileName);
        Assert.Equal(pointer, DataRootPointer.PathFor(Old));
        Assert.Equal(Path.GetFullPath(Visible).Replace('\\', '/'), File.ReadAllText(pointer).Trim());
        // outside the data folder: it never makes the app-specific folder look like data to move
        Assert.False(DataMigration.IsPending(Old, Visible));
        Assert.Null(DataRootPointer.PathFor(null));
    }

    [Fact]
    public void AFileMissingInBothFoldersIsReportedAndTheRestFinishes()
    {
        Dictionary<string, string> before = WriteOldData();
        Directory.CreateDirectory(Visible);
        File.WriteAllText(Path.Combine(Visible, DataMigration.JournalFileName), "5\tsavedata/lost.bin\n");
        MigrationResult result = new DataMigration(Old, Visible).Run();
        Assert.Equal(MigrationStatus.VerifyFailed, result.Status);
        Assert.Contains("savedata/lost.bin", result.Message);
        Assert.Contains("savedata/lost.bin", File.ReadAllText(Path.Combine(Visible, DataMigration.ProblemsFileName)));
        Dictionary<string, string> after = Snapshot(Visible);
        after.Remove(DataMigration.ProblemsFileName);
        Assert.Equal(before, after);
        Assert.False(DataMigration.IsPending(Old, Visible));
    }

    [Fact]
    public void AFileChangedAfterTheListingIsCheckedWithItsSizeWhenItMoved()
    {
        Dictionary<string, string> before = WriteOldData();
        Directory.CreateDirectory(Visible);
        // an interrupted run listed last.log shorter; it grew before it moved
        File.WriteAllText(Path.Combine(Visible, DataMigration.JournalFileName), "2\tlogs/last.log\n");
        File.AppendAllText(Path.Combine(Old, "logs", "last.log"), " more");
        before["logs/last.log"] += " more";
        MigrationResult result = new ScriptedMigration(Old, Visible) { RenameWorks = false }.Run();
        Assert.Equal(MigrationStatus.Done, result.Status);
        Assert.Equal(before, Snapshot(Visible));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>An old data folder like the app's: games, firmware, saves, states, captures, logs, env.txt, an empty folder.</summary>
    private Dictionary<string, string> WriteOldData()
    {
        Dictionary<string, string> files = new()
        {
            ["fs/ux0/app/PCSB00001/eboot.bin"] = new string('e', 3000),
            ["fs/ux0/app/PCSB00001/sce_sys/param.sfo"] = "sfo",
            ["fs/vs0/vsh/shell/shell.self"] = new string('s', 5000),
            ["fs/ux0/user/00/savedata/PCSB00001/SAVEDATA.BIN"] = "save",
            ["savestates/PCSB00001/slot1.state"] = new string('t', 2000),
            ["captures/PCSB00001-1.zip"] = "zip",
            ["logs/last.log"] = "log line",
            ["config/env.txt"] = "VITASHARP_X=1",
            ["config/app.json"] = "{}",
            ["kernel_plugins/config.txt"] = "*KERNEL",
        };
        foreach (KeyValuePair<string, string> file in files)
        {
            string path = Path.Combine(Old, file.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Value);
        }

        Directory.CreateDirectory(Path.Combine(Old, "fs", "ux0", "data", "empty"));
        return files;
    }

    /// <summary>The visible folder of an earlier install: a game, a save, a save state, settings, a log.</summary>
    private Dictionary<string, string> WriteVisibleData()
    {
        Dictionary<string, string> files = new()
        {
            ["fs/ux0/app/PCSB00001/eboot.bin"] = new string('v', 1500),
            ["fs/ux0/user/00/savedata/PCSB00001/SAVEDATA.BIN"] = "the user's save",
            ["savestates/PCSB00001/slot1.vss"] = "state",
            ["config/app.json"] = "{\"language\":\"pt-BR\"}",
            ["logs/app.log"] = "earlier log",
        };
        foreach (KeyValuePair<string, string> file in files)
        {
            string path = Path.Combine(Visible, file.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Value);
        }

        return files;
    }

    private static Dictionary<string, string> Snapshot(string folder)
    {
        Dictionary<string, string> files = new();
        if (!Directory.Exists(folder))
        {
            return files;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative != DataMigration.JournalFileName && !relative.EndsWith(DataMigration.PartSuffix, StringComparison.Ordinal))
            {
                files[relative] = File.ReadAllText(file);
            }
        }

        return files;
    }

    /// <summary>Every original file is, with its content, in the old folder or in the new one.</summary>
    private void AssertNothingLost(Dictionary<string, string> before)
    {
        Dictionary<string, string> old = Snapshot(Old);
        Dictionary<string, string> moved = Snapshot(Visible);
        foreach (KeyValuePair<string, string> file in before)
        {
            bool kept = old.TryGetValue(file.Key, out string? inOld) && inOld == file.Value;
            bool there = moved.TryGetValue(file.Key, out string? inNew) && inNew == file.Value;
            Assert.True(kept || there, $"{file.Key} lost");
        }
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; this one reports on the calling thread.</summary>
    private sealed class SyncProgress(Action<MigrationProgress> report) : IProgress<MigrationProgress>
    {
        public void Report(MigrationProgress value) => report(value);
    }

    /// <summary>A migration whose rename, copy and free space are scripted (another mount, a failing or short copy).</summary>
    private sealed class ScriptedMigration(string source, string target) : DataMigration(source, target)
    {
        public bool RenameWorks { get; init; } = true;
        public int FailCopyNumber { get; init; }
        public int ShortCopyNumber { get; init; }
        public long Free { get; init; } = long.MaxValue;
        public int Copies { get; private set; }

        protected override bool TryRename(string from, string to, bool isDirectory)
        {
            return RenameWorks && base.TryRename(from, to, isDirectory);
        }

        protected override void CopyFile(string from, string to)
        {
            Copies++;
            if (Copies == FailCopyNumber)
            {
                File.WriteAllText(to, "x");
                throw new IOException("I/O error (scripted)");
            }

            base.CopyFile(from, to);
            if (Copies == ShortCopyNumber)
            {
                using FileStream file = new(to, FileMode.Open);
                file.SetLength(Math.Max(0, file.Length - 1));
            }
        }

        protected override long FreeBytes(string path) => Free;
    }
}
