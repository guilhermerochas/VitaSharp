using VitaSharp.Core.Kernel;
using VitaSharp.Core.Localization;
using VitaSharp.Core.SaveState;
using VitaSharp.Mobile.Core.Library;

namespace VitaSharp.Tests;

/// <summary>
/// The Android game page's save states and recordings (Mobile.Core GameStates) and the start in a state both apps
/// share (<see cref="SaveStates.BootExecutable"/>, <see cref="SaveStates.StartLoadFrames"/>): a slot tapped on the game
/// page opens the game already in that state, as the desktop's --load-state.
/// </summary>
public sealed class AndroidGameStatesTests : IDisposable
{
    private const string TitleId = "PCSA00001";
    private const int AppPid = 0x40010003;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-game-states-" + Guid.NewGuid().ToString("N"));

    public AndroidGameStatesTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    /// <summary>A state file with this build's header and a kernel object table whose modules are
    /// <paramref name="modules"/> (uid, path); nothing else (enough for the header and ProgramOf).</summary>
    private static void WriteState(string path, string titleId, byte[]? thumbnail = null, string? schema = null, params (int Uid, string Path, int OwnerPid)[] modules)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var header = new SaveStateHeader
        {
            EmulatorVersion = "test",
            Schema = schema ?? SnapshotSchema.Hash,
            TitleId = titleId,
            Title = "Test",
            CreatedUtc = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc),
            Thumbnail = thumbnail ?? [],
        };
        using FileStream file = File.Create(path);
        using var writer = new SaveStateWriter(file, header);
        var objects = new SnapshotWriter();
        objects.Write(1);
        objects.Write(modules.Length);
        foreach ((int uid, string modulePath, int ownerPid) in modules)
        {
            objects.Write(KernelSnapshot.Module);
            objects.Write(uid);
            objects.Write("module");
            objects.Write(ownerPid);
            objects.Write(0);
            int block = objects.BeginBlock();
            objects.Write(modulePath);
            objects.EndBlock(block);
        }

        writer.Section("kernel.objects", objects);
    }

    // ---------------------------------------------------------------- the executable to boot

    [Fact]
    public void BootExecutableIsTheProgramALoadExecStarted()
    {
        string path = Path.Combine(_root, "jak.vss");
        // the program's own module has the lowest uid; the libraries it loaded come after it
        WriteState(path, TitleId, modules: [(0x40010003, "app0:sce_module/libc.suprx", AppPid), (0x40010001, "app0:Jak1.self", AppPid)]);
        Assert.Equal("Jak1.self", SaveStates.BootExecutable(path));
    }

    [Fact]
    public void BootExecutableIsEbootForTheGamesOwnExecutableAndForUnreadableStates()
    {
        string eboot = Path.Combine(_root, "eboot.vss");
        WriteState(eboot, TitleId, modules: [(0x40010001, "app0:/eboot.bin", AppPid)]);
        Assert.Equal("eboot.bin", SaveStates.BootExecutable(eboot));

        string outside = Path.Combine(_root, "outside.vss");
        WriteState(outside, TitleId, modules: [(0x40010001, "vs0:vsh/shell/shell.self", AppPid)]);
        Assert.Equal("eboot.bin", SaveStates.BootExecutable(outside));

        string garbage = Path.Combine(_root, "garbage.vss");
        File.WriteAllText(garbage, "not a state");
        Assert.Equal("eboot.bin", SaveStates.BootExecutable(garbage));
        Assert.Equal("eboot.bin", SaveStates.BootExecutable(Path.Combine(_root, "missing.vss")));
    }

    [Fact]
    public void BootExecutableLeavesOutTheKernelProcessModules()
    {
        // a session with the kernel process (kernel plugins on) loads the firmware kernel modules and the plugins at
        // system boot, with lower uids than the game's eboot: the state was still saved in the game's executable
        string path = Path.Combine(_root, "kernel.vss");
        WriteState(path, TitleId, modules: [(0x40010005, "os0:kd/bootimage.skprx/SceClockgen", KernelSpace.KernelPid),
            (0x40010101, "ur0:tai/kubridge.skprx", KernelSpace.KernelPid), (0x40010201, "app0:Jak1.self", AppPid),
            (0x40010203, "app0:sce_module/libc.suprx", AppPid)]);
        Assert.Equal("app0:Jak1.self", SaveStates.ProgramOf(path));
        Assert.Equal("Jak1.self", SaveStates.BootExecutable(path));
    }

    [Fact]
    public void BothAppsLoadAStartStateAfterTheSameFrames()
    {
        // the desktop's --load-state default and the Android start slot (EmulationSession.LoadStartSlot) use this
        Assert.Equal(120, SaveStates.StartLoadFrames);
    }

    // ---------------------------------------------------------------- slots

    [Fact]
    public void SlotsListsAllSixWithPictureDateAndProblems()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G'];
        WriteState(SaveStates.SlotPath(_root, TitleId, 1), TitleId, png, modules: [(1, "app0:eboot.bin", AppPid)]);
        WriteState(SaveStates.SlotPath(_root, TitleId, 3), "PCSB99999", modules: [(1, "app0:eboot.bin", AppPid)]);
        WriteState(SaveStates.SlotPath(_root, TitleId, 4), TitleId, schema: "another build", modules: [(1, "app0:eboot.bin", AppPid)]);
        File.WriteAllText(SaveStates.SlotPath(_root, TitleId, 5), "broken");
        // the quick slot (desktop F5) is not one of the six
        WriteState(SaveStates.SlotPath(_root, TitleId, 0), TitleId, modules: [(1, "app0:eboot.bin", AppPid)]);

        IReadOnlyList<StateSlot> slots = GameStates.Slots(_root, TitleId);

        Assert.Equal(GameStates.SlotCount, slots.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6], slots.Select(s => s.Slot));
        Assert.True(slots[0].CanLoad);
        Assert.Equal(png, slots[0].Header!.Thumbnail);
        Assert.Equal(new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc), slots[0].Header!.CreatedUtc);
        Assert.False(slots[1].Exists);
        Assert.False(slots[1].CanLoad);
        Assert.True(slots[2].Exists);
        Assert.False(slots[2].CanLoad);
        Assert.Contains("PCSB99999", slots[2].Problem);
        Assert.False(slots[3].CanLoad);
        Assert.NotNull(slots[3].Problem);
        Assert.Equal(Loc.Get("StateUnreadable"), slots[4].Problem);
        Assert.Null(slots[4].Header);
        Assert.False(slots[5].Exists);
    }

    [Fact]
    public void SlotsOfAGameWithNoStatesAreAllEmpty()
    {
        Assert.All(GameStates.Slots(_root, TitleId), s => Assert.False(s.Exists));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(7, 0)]
    public void LaunchSlotIsOneOfTheSixOrNone(int requested, int expected)
    {
        Assert.Equal(expected, GameStates.LaunchSlot(requested));
    }

    // ---------------------------------------------------------------- recordings

    [Fact]
    public void RecordingsAreTheGamesZipsNewestFirst()
    {
        string captures = Path.Combine(_root, "captures");
        Directory.CreateDirectory(captures);
        string older = Path.Combine(captures, $"{TitleId}_20261114_100000_tudo.zip");
        string newer = Path.Combine(captures, $"{TitleId.ToLowerInvariant()}_20261115_100000_tudo.zip");
        File.WriteAllBytes(older, new byte[10]);
        File.WriteAllBytes(newer, new byte[20]);
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc));
        File.WriteAllBytes(Path.Combine(captures, "PCSB99999_20261115_100000_tudo.zip"), new byte[5]);
        File.WriteAllBytes(Path.Combine(captures, $"{TitleId}0_20261115_100000_tudo.zip"), new byte[5]);
        Directory.CreateDirectory(Path.Combine(captures, $"{TitleId}_20261115_110000_tudo")); // still being saved
        File.WriteAllBytes(Path.Combine(captures, $"{TitleId}_notes.txt"), new byte[5]);

        IReadOnlyList<GameRecording> list = GameStates.Recordings(captures, TitleId);

        Assert.Equal([newer, older], list.Select(r => r.Path));
        Assert.Equal([20L, 10L], list.Select(r => r.Bytes));
        Assert.Equal(Path.GetFileName(newer), list[0].Name);
    }

    [Fact]
    public void RecordingsOfAMissingFolderAreEmpty()
    {
        Assert.Empty(GameStates.Recordings(Path.Combine(_root, "none"), TitleId));
    }
}
