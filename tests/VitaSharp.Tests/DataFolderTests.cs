using VitaSharp.Core.Storage;
using VitaSharp.Mobile.Core.Storage;

namespace VitaSharp.Tests;

/// <summary>
/// The data folder choice (Core/Storage/DataFolder.cs, DataFolderTransfer.cs; Mobile.Core/Storage/DataRoot.cs,
/// SafTreePath.cs): which folder each choice resolves to (default, portable beside the executable, a picked folder,
/// --data), a pointer that cannot be honoured falling back to the default with a warning, the saved choice, and the
/// copy/move of the data that never overwrites a file of the new folder and checks the free space first. Every path is
/// in a temporary folder: nothing touches the user's own data folder.
/// </summary>
public sealed class DataFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-datafolder-tests-" + Guid.NewGuid().ToString("N"));

    private string ExeDir => Path.Combine(_root, "app");

    private string Pointer => Path.Combine(_root, "config", DataFolder.PointerFileName);

    private string DefaultRoot => Path.Combine(_root, "appdata", "VitaSharp");

    private string Custom => Path.Combine(_root, "games-drive", "VitaSharpData");

    public DataFolderTests()
    {
        Directory.CreateDirectory(ExeDir);
    }

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

    private DataFolderResolution Resolve(string? forced = null)
    {
        return DataFolder.Resolve(new DataFolderInputs(forced, ExeDir, Pointer, DefaultRoot));
    }

    // ---------------------------------------------------------------- resolution: the three choices and --data

    [Fact]
    public void NothingChosenIsTheDefaultFolder()
    {
        DataFolderResolution resolved = Resolve();
        Assert.Equal(Path.GetFullPath(DefaultRoot), resolved.Root);
        Assert.Equal(DataFolderKind.Default, resolved.Kind);
        Assert.Equal("", resolved.Warning);
    }

    [Fact]
    public void PortableMarkerBesideTheExecutableUsesTheFolderBesideIt()
    {
        File.WriteAllText(Path.Combine(ExeDir, DataFolder.PortableMarkerName), "");
        DataFolderResolution resolved = Resolve();
        Assert.Equal(Path.GetFullPath(Path.Combine(ExeDir, DataFolder.PortableFolderName)), resolved.Root);
        Assert.Equal(DataFolderKind.Portable, resolved.Kind);
    }

    [Fact]
    public void PointerNamesThePickedFolder()
    {
        Directory.CreateDirectory(Custom);
        DataFolder.WritePointer(Pointer, Custom);
        DataFolderResolution resolved = Resolve();
        Assert.Equal(Path.GetFullPath(Custom), resolved.Root);
        Assert.Equal(DataFolderKind.Custom, resolved.Kind);
        Assert.Equal("", resolved.Warning);
    }

    [Fact]
    public void PortableWinsOverThePointerAndDataWinsOverBoth()
    {
        Directory.CreateDirectory(Custom);
        DataFolder.WritePointer(Pointer, Custom);
        File.WriteAllText(Path.Combine(ExeDir, DataFolder.PortableMarkerName), "");
        Assert.Equal(DataFolderKind.Portable, Resolve().Kind);
        string forced = Path.Combine(_root, "forced");
        DataFolderResolution resolved = Resolve(forced);
        Assert.Equal(DataFolderKind.Override, resolved.Kind);
        Assert.Equal(Path.GetFullPath(forced), resolved.Root);
    }

    [Fact]
    public void PointerToAMissingFolderFallsBackToTheDefaultWithAWarning()
    {
        DataFolder.WritePointer(Pointer, Custom); // the drive is not connected: the folder is not there
        DataFolderResolution resolved = Resolve();
        Assert.Equal(Path.GetFullPath(DefaultRoot), resolved.Root);
        Assert.Equal(DataFolderKind.Default, resolved.Kind);
        Assert.Contains(Path.GetFullPath(Custom), resolved.Warning);
        Assert.Contains("does not exist", resolved.Warning);
        Assert.False(Directory.Exists(Custom), "resolving must not create the missing folder");
    }

    [Fact]
    public void RelativeOrEmptyPointerFallsBackToTheDefault()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Pointer)!);
        File.WriteAllText(Pointer, "relative/folder\n");
        DataFolderResolution relative = Resolve();
        Assert.Equal(DataFolderKind.Default, relative.Kind);
        Assert.Contains("not an absolute path", relative.Warning);

        File.WriteAllText(Pointer, "# nothing\n\n");
        DataFolderResolution empty = Resolve();
        Assert.Equal(DataFolderKind.Default, empty.Kind);
        Assert.Equal("", empty.Warning);
    }

    [Fact]
    public void PointerToAFileInsteadOfAFolderFallsBackToTheDefault()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Custom)!);
        File.WriteAllText(Custom, "not a folder");
        DataFolder.WritePointer(Pointer, Custom);
        DataFolderResolution resolved = Resolve();
        Assert.Equal(DataFolderKind.Default, resolved.Kind);
        Assert.NotEqual("", resolved.Warning);
    }

    // ---------------------------------------------------------------- the saved choice

    [Fact]
    public void SavingEachChoiceResolvesToItsFolderOnTheNextStart()
    {
        DataFolder.SaveChoice(DataFolderKind.Custom, Custom, Pointer, ExeDir);
        Assert.Equal(Path.GetFullPath(Custom), Resolve().Root);
        Assert.True(Directory.Exists(Custom));

        DataFolder.SaveChoice(DataFolderKind.Portable, null, Pointer, ExeDir);
        Assert.Equal(DataFolder.PortableRoot(ExeDir), Resolve().Root);

        // the custom folder again: the portable marker (which would win) goes away
        DataFolder.SaveChoice(DataFolderKind.Custom, Custom, Pointer, ExeDir);
        Assert.Equal(DataFolderKind.Custom, Resolve().Kind);
        Assert.False(File.Exists(Path.Combine(ExeDir, DataFolder.PortableMarkerName)));

        DataFolder.SaveChoice(DataFolderKind.Default, null, Pointer, ExeDir);
        Assert.Equal(DataFolderKind.Default, Resolve().Kind);
        Assert.False(File.Exists(Pointer));
        // the data folders themselves stay where they are
        Assert.True(Directory.Exists(Custom));
        Assert.True(Directory.Exists(DataFolder.PortableRoot(ExeDir)));
    }

    [Fact]
    public void TargetOfEachChoice()
    {
        Assert.Equal(Path.GetFullPath(DefaultRoot), DataFolder.TargetOf(DataFolderKind.Default, null, ExeDir, DefaultRoot));
        Assert.Equal(DataFolder.PortableRoot(ExeDir), DataFolder.TargetOf(DataFolderKind.Portable, null, ExeDir, DefaultRoot));
        Assert.Equal(Path.GetFullPath(Custom), DataFolder.TargetOf(DataFolderKind.Custom, Custom, ExeDir, DefaultRoot));
        Assert.Throws<ArgumentException>(() => DataFolder.TargetOf(DataFolderKind.Custom, null, ExeDir, DefaultRoot));
    }

    [Fact]
    public void TheCaptureLineNamesTheKindNeverThePath()
    {
        DataFolderResolution? before = DataFolder.Active;
        try
        {
            DataFolder.Active = new DataFolderResolution(Custom, DataFolderKind.Custom);
            string line = DataFolder.CaptureLine();
            Assert.StartsWith("data folder: <data> (", line);
            Assert.Contains(DataFolder.PointerFileName, line);
            Assert.DoesNotContain(_root, line);
        }
        finally
        {
            DataFolder.Active = before;
        }
    }

    // ---------------------------------------------------------------- caches in the internal folder (Android)

    private string CacheShared => Path.Combine(_root, "shared");

    private string CacheInternalRoot => Path.Combine(_root, "internal");

    private static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void WithTheDataFolderInSharedStorageTheCachesAreInTheInternalFolder()
    {
        // a new install: nothing in the old place, the caches go to the internal folder at once
        var layout = new DataLayout(CacheShared, CacheInternalRoot);
        Assert.True(layout.CachesInternal);
        Assert.Equal(Path.Combine(Path.GetFullPath(CacheInternalRoot), "cache", "shadercache"), layout.ShaderCache);
        Assert.Equal(Path.Combine(Path.GetFullPath(CacheInternalRoot), "cache", "codecache"), layout.CodeCache);
        // games, saves, logs and settings stay in the data folder
        Assert.StartsWith(Path.GetFullPath(CacheShared), layout.SaveData);
        Assert.StartsWith(Path.GetFullPath(CacheShared), layout.Config);
        Assert.StartsWith(Path.GetFullPath(CacheShared), layout.Logs);

        // the data folder is the internal folder itself: the caches stay where they were
        var single = new DataLayout(CacheInternalRoot);
        Assert.False(single.CachesInternal);
        Assert.Equal(Path.Combine(Path.GetFullPath(CacheInternalRoot), "shadercache"), single.ShaderCache);
        Assert.Equal(Path.Combine(Path.GetFullPath(CacheInternalRoot), "codecache"), single.CodeCache);
        Assert.Equal(default, CacheMigration.Run(single));
    }

    [Fact]
    public void CachesInTheOldPlaceAreUsedUntilMovedThenMovedWhole()
    {
        var layout = new DataLayout(CacheShared, CacheInternalRoot);
        string pack = Path.Combine(layout.LegacyShaderCache, "titles", "PCSA00126", "shaders.pack");
        string astc = Path.Combine(layout.LegacyShaderCache, "titles", "PCSA00126", "astc", "00", "level.bin");
        string code = Path.Combine(layout.LegacyCodeCache, "0123456789abcdef.vsjc");
        WriteFile(pack, "pack");
        WriteFile(astc, "astc");
        WriteFile(code, "code");
        // a session that starts before the move still finds its warm caches
        Assert.Equal(layout.LegacyShaderCache, layout.ShaderCache);
        Assert.Equal(layout.LegacyCodeCache, layout.CodeCache);
        // a copy cut short by an earlier run is redone, not merged
        WriteFile(Path.Combine(layout.InternalShaderCache + CacheMigration.PartialSuffix, "stale.bin"), "half");

        CacheMigration.Result result = CacheMigration.Run(layout);
        Assert.Equal("", result.Error);
        Assert.Equal(2, result.Moved);
        Assert.Equal(3, result.Files);
        Assert.Equal(layout.InternalShaderCache, layout.ShaderCache);
        Assert.Equal(layout.InternalCodeCache, layout.CodeCache);
        Assert.False(Directory.Exists(layout.LegacyShaderCache)); // moved, not left twice
        Assert.False(Directory.Exists(layout.LegacyCodeCache));
        Assert.False(Directory.Exists(layout.InternalShaderCache + CacheMigration.PartialSuffix));
        Assert.Equal("pack", File.ReadAllText(Path.Combine(layout.ShaderCache, "titles", "PCSA00126", "shaders.pack")));
        Assert.Equal("astc", File.ReadAllText(Path.Combine(layout.ShaderCache, "titles", "PCSA00126", "astc", "00", "level.bin")));
        Assert.Equal("code", File.ReadAllText(Path.Combine(layout.CodeCache, "0123456789abcdef.vsjc")));
        Assert.False(File.Exists(Path.Combine(layout.ShaderCache, "stale.bin")));
        Assert.Contains("2 moved (3 files", CacheMigration.Describe(result, layout));

        // a session that was still writing the old place left a copy behind: the new place wins, the old copy goes
        WriteFile(code, "older");
        CacheMigration.Result again = CacheMigration.Run(layout);
        Assert.Equal((0, 1), (again.Moved, again.RemovedStale));
        Assert.False(Directory.Exists(layout.LegacyCodeCache));
        Assert.Equal("code", File.ReadAllText(Path.Combine(layout.CodeCache, "0123456789abcdef.vsjc")));
        Assert.Equal("", CacheMigration.Describe(CacheMigration.Run(layout), layout));
    }

    [Fact]
    public void TheCaptureNamesWhereTheCachesAreNeverThePath()
    {
        DataFolderResolution? before = DataFolder.Active;
        (string, string)? beforeCaches = DataFolder.Caches;
        try
        {
            DataFolder.Active = new DataFolderResolution(CacheShared, DataFolderKind.Default);
            DataFolder.Caches = null;
            Assert.Equal("caches: shader cache <data>/shadercache, code cache <data>/codecache", DataFolder.CacheLine());

            var layout = new DataLayout(CacheShared, CacheInternalRoot);
            DataFolder.Caches = (layout.ShaderCache, layout.CodeCache);
            Assert.Equal("caches: shader cache <internal>/cache/shadercache, code cache <internal>/cache/codecache", DataFolder.CacheLine());

            DataFolder.Caches = (layout.LegacyShaderCache, layout.InternalCodeCache);
            string line = DataFolder.CacheLine();
            Assert.Equal("caches: shader cache <data>/shadercache, code cache <internal>/cache/codecache", line);
            Assert.DoesNotContain(_root, line);
        }
        finally
        {
            DataFolder.Active = before;
            DataFolder.Caches = beforeCaches;
        }
    }

    // ---------------------------------------------------------------- copy and move

    private string Old => Path.Combine(_root, "old");

    private string New => Path.Combine(_root, "new");

    private void WriteOldData()
    {
        Write(Old, "fs/ux0/app/ABCD00001/eboot.bin", "game");
        Write(Old, "fs/ux0/user/00/savedata/ABCD00001/save.bin", "save");
        Write(Old, "launcher.json", "{\"theme\":\"dark\"}");
        Write(Old, "logs/last.log", "log");
        Directory.CreateDirectory(Path.Combine(Old, "fs", "ux0", "data", "empty"));
    }

    private static void Write(string root, string relative, string text)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
        {
            return files;
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files[Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')] = File.ReadAllText(file);
        }

        return files;
    }

    [Fact]
    public void CopyLeavesTheOldFolderAsItIs()
    {
        WriteOldData();
        Dictionary<string, string> before = Snapshot(Old);
        DataTransferResult result = new DataFolderTransfer(Old, New, DataTransferMode.Copy).Run();
        Assert.Equal(DataTransferStatus.Done, result.Status);
        Assert.Equal(before, Snapshot(Old));
        Assert.Equal(before, Snapshot(New));
        Assert.True(Directory.Exists(Path.Combine(New, "fs", "ux0", "data", "empty")));
        Assert.Empty(result.Kept);
    }

    [Fact]
    public void MoveBetweenVolumesCopiesEachFileBeforeDeletingIt()
    {
        WriteOldData();
        Dictionary<string, string> before = Snapshot(Old);
        var transfer = new NoRenameTransfer(Old, New, DataTransferMode.Move);
        DataTransferResult result = transfer.Run();
        Assert.Equal(DataTransferStatus.Done, result.Status);
        Assert.Equal(before, Snapshot(New));
        Assert.Empty(Snapshot(Old));
        Assert.True(Directory.Exists(Old), "the old folder itself is never deleted");
        Assert.Equal(before.Count, transfer.Copies);
    }

    [Fact]
    public void MoveOnTheSameVolumeRenamesWholeEntries()
    {
        WriteOldData();
        Dictionary<string, string> before = Snapshot(Old);
        DataTransferResult result = new DataFolderTransfer(Old, New, DataTransferMode.Move).Run();
        Assert.Equal(DataTransferStatus.Done, result.Status);
        Assert.Equal(before, Snapshot(New));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Old));
    }

    [Theory]
    [InlineData(DataTransferMode.Copy)]
    [InlineData(DataTransferMode.Move)]
    public void NothingInTheNewFolderIsOverwritten(DataTransferMode mode)
    {
        WriteOldData();
        Write(New, "launcher.json", "{\"theme\":\"light\"}"); // the new folder's own settings
        Write(New, "logs/last.log", "log"); // the same content: counts as already there
        Write(New, "fs/ux0/user/00/savedata/OTHER0001/save.bin", "other save");
        DataTransferResult result = new NoRenameTransfer(Old, New, mode).Run();
        Assert.Equal(DataTransferStatus.Done, result.Status);
        Dictionary<string, string> after = Snapshot(New);
        Assert.Equal("{\"theme\":\"light\"}", after["launcher.json"]);
        Assert.Equal("other save", after["fs/ux0/user/00/savedata/OTHER0001/save.bin"]);
        Assert.Equal("save", after["fs/ux0/user/00/savedata/ABCD00001/save.bin"]);
        Assert.Equal(new[] { "launcher.json" }, result.Kept);
        // the old folder's different copy is never lost: it stays where it was
        Assert.Equal("{\"theme\":\"dark\"}", File.ReadAllText(Path.Combine(Old, "launcher.json")));
        if (mode == DataTransferMode.Move)
        {
            Assert.Equal(new[] { "launcher.json" }, Snapshot(Old).Keys.ToArray());
        }
    }

    [Fact]
    public void NoRoomChangesNothing()
    {
        WriteOldData();
        Dictionary<string, string> before = Snapshot(Old);
        DataTransferResult result = new NoRenameTransfer(Old, New, DataTransferMode.Move) { Free = 10 }.Run();
        Assert.Equal(DataTransferStatus.NoSpace, result.Status);
        Assert.Equal(before, Snapshot(Old));
        Assert.Empty(Snapshot(New));
    }

    [Fact]
    public void ShortCopyStopsKeepsTheSourceAndLeavesNoHalfFile()
    {
        WriteOldData();
        Dictionary<string, string> before = Snapshot(Old);
        DataTransferResult result = new NoRenameTransfer(Old, New, DataTransferMode.Move) { Truncate = true }.Run();
        Assert.Equal(DataTransferStatus.Failed, result.Status);
        Assert.Equal(before, Snapshot(Old));
        Assert.Empty(Directory.EnumerateFiles(New, "*" + DataFolderTransfer.PartSuffix, SearchOption.AllDirectories));

        // run again: resumes and ends
        DataTransferResult again = new NoRenameTransfer(Old, New, DataTransferMode.Move).Run();
        Assert.Equal(DataTransferStatus.Done, again.Status);
        Assert.Equal(before, Snapshot(New));
    }

    [Fact]
    public void ATargetInsideTheSourceIsRefused()
    {
        WriteOldData();
        DataTransferResult result = new DataFolderTransfer(Old, Path.Combine(Old, "fs", "copy"), DataTransferMode.Copy).Run();
        Assert.Equal(DataTransferStatus.InvalidTarget, result.Status);
        DataTransferResult same = new DataFolderTransfer(Old, Old, DataTransferMode.Move).Run();
        Assert.Equal(DataTransferStatus.InvalidTarget, same.Status);
        Assert.False(Directory.Exists(Path.Combine(Old, "fs", "copy")));
    }

    /// <summary>Folders on two volumes (no rename), a given free space, a copy cut short.</summary>
    private sealed class NoRenameTransfer(string source, string target, DataTransferMode mode) : DataFolderTransfer(source, target, mode)
    {
        public long Free { get; init; } = long.MaxValue;

        public bool Truncate { get; init; }

        public int Copies { get; private set; }

        protected override bool TryRename(string from, string to, bool isDirectory) => false;

        protected override long FreeBytes(string path) => Free;

        protected override void CopyFile(string from, string to)
        {
            Copies++;
            base.CopyFile(from, to);
            if (Truncate)
            {
                using FileStream stream = new(to, FileMode.Open, FileAccess.Write);
                stream.SetLength(Math.Max(0, stream.Length - 1));
            }
        }
    }

    // ---------------------------------------------------------------- Android: the picked folder

    private string Shared => Path.Combine(_root, "storage");

    private string AppSpecific => Path.Combine(Shared, "Android", "data", "io.github.vitasharp", "files");

    [Fact]
    public void AndroidPickedFolderIsUsedWhenItCanBeWritten()
    {
        Directory.CreateDirectory(Custom);
        DataRootPlan plan = DataRoot.Resolve(new DataRootInputs(AppSpecific, Path.Combine(_root, "internal"), Shared, false, true,
            StorageChoice.CustomFolder, Custom));
        Assert.Equal(Path.GetFullPath(Custom), plan.Root);
        Assert.False(plan.NeedsMigration);
        Assert.Equal("", plan.Warning);
    }

    [Fact]
    public void AndroidPickedFolderThatIsGoneFallsBackToTheDefaultRulesWithAWarning()
    {
        DataRootInputs inputs = new(AppSpecific, Path.Combine(_root, "internal"), Shared, false, true, StorageChoice.CustomFolder, Custom);
        DataRootPlan plan = DataRoot.Resolve(inputs);
        DataRootPlan expected = DataRoot.Resolve(inputs with { Choice = StorageChoice.None, CustomFolder = null });
        Assert.Equal(expected.Root, plan.Root);
        Assert.Contains("does not exist", plan.Warning);

        DataRootPlan noPath = DataRoot.Resolve(inputs with { CustomFolder = null });
        Assert.Equal(expected.Root, noPath.Root);
        Assert.NotEqual("", noPath.Warning);
    }

    [Theory]
    [InlineData("primary:Games/VitaSharp", "/storage/emulated/0/Games/VitaSharp")]
    [InlineData("primary:", "/storage/emulated/0")]
    [InlineData("1234-ABCD:VitaSharp", "/storage/1234-ABCD/VitaSharp")]
    [InlineData("home:VitaSharp", "/storage/emulated/0/Documents/VitaSharp")]
    public void AndroidTreeIdsOfTheDeviceStorageBecomePaths(string treeId, string path)
    {
        Assert.Equal(path, SafTreePath.ToPath(SafTreePath.ExternalStorageAuthority, treeId, "/storage/emulated/0"));
    }

    [Theory]
    [InlineData("com.google.android.apps.docs.storage", "doc=abc")]
    [InlineData(SafTreePath.ExternalStorageAuthority, "noColon")]
    [InlineData(SafTreePath.ExternalStorageAuthority, "primary:a/../../etc")]
    [InlineData(SafTreePath.ExternalStorageAuthority, "weird volume:x")]
    public void AndroidTreesWithoutAPathAreRefused(string authority, string treeId)
    {
        Assert.Null(SafTreePath.ToPath(authority, treeId, "/storage/emulated/0"));
    }

    [Fact]
    public void AndroidFoldersOutsideTheAppNeedAllFilesAccess()
    {
        string[] appFolders = [AppSpecific];
        Assert.False(SafTreePath.NeedsAllFilesAccess(Path.Combine(AppSpecific, "data"), appFolders));
        Assert.True(SafTreePath.NeedsAllFilesAccess(Custom, appFolders));
    }
}
