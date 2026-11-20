using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Tests;

/// <summary>
/// A load that fails after the game's threads were stopped (a module section that does not match, an object that
/// cannot be rebuilt) gives the game back as it was, like Dolphin's "undo load state": the load keeps an in-memory
/// state of the running session, taken with the threads already held, and restores it when the load fails. Before,
/// the game stayed stopped with "restart the game". Runs <see cref="SyntheticTitle"/> (its frame loop: pad read,
/// framebuffer fill, vblank wait) in process.
/// </summary>
[Collection("guest")]
public sealed class SaveStateRollbackTests : IDisposable
{
    private readonly HleHarness _harness;

    public SaveStateRollbackTests(GuestMemoryFixture fixture)
    {
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        // (allocations they never free) would refuse its load
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "ssrollback");
        string app = SyntheticTitle.Install(_harness.Root);
        _harness.Emu.LoadTitle(app);
        _harness.Emu.Start();
    }

    public void Dispose()
    {
        _harness.Emu.Stop();
        _harness.Dispose();
    }

    /// <summary>The main thread's HLE calls: the frame loop makes three per frame.</summary>
    private int MainCalls => _harness.Emu.Kernel.MainThread!.HleSequence;

    private void WaitForFrames(int calls, string what)
    {
        int start = MainCalls;
        HleHarness.Until(() => MainCalls - start >= calls, what);
    }

    /// <summary>Rewrites the state at <paramref name="path"/> with one extra byte at the end of the first module's
    /// section: the layouts still match (the load is accepted with the game running) and the failure only comes while
    /// the sections are restored, after the threads were stopped ("N bytes of its state were not read").</summary>
    private static void CorruptAModuleSection(string path, string corrupted)
    {
        var sections = new List<(string Name, byte[] Data)>();
        SaveStateHeader header;
        using (FileStream file = File.OpenRead(path))
        using (var reader = new SaveStateReader(file))
        {
            header = reader.Header;
            while (reader.Next(out string name, out _))
            {
                sections.Add((name, reader.ReadAll()));
            }
        }
        int module = sections.FindIndex(s => s.Name.StartsWith("hle:", StringComparison.Ordinal));
        Assert.True(module >= 0, "the state has no module section");
        sections[module] = (sections[module].Name, [.. sections[module].Data, 0x5A]);
        using FileStream output = File.Create(corrupted);
        using var writer = new SaveStateWriter(output, header);
        foreach ((string name, byte[] data) in sections)
        {
            writer.Section(name, data);
        }
    }

    [Fact]
    public void AFailedLoadAfterTheThreadsStoppedGivesTheGameBack()
    {
        WaitForFrames(30, "the title's first frames");
        string path = Path.Combine(_harness.Root, "state.vss");
        SaveStates.Save(_harness.Emu, path, timeoutMs: 60_000);
        string corrupted = Path.Combine(_harness.Root, "corrupted.vss");
        CorruptAModuleSection(path, corrupted);

        // the session goes on after the save: a block allocated now is not in the state
        WaitForFrames(30, "frames after the save");
        uint marker = _harness.Emu.Memory.Alloc(64, "rollback marker");
        GuestMemory.W32(marker, 0xC0FFEE11);

        SnapshotException failure = Assert.Throws<SnapshotException>(() => SaveStates.Load(_harness.Emu, corrupted));

        // the session from before the load: its memory (the marker block) and its threads, which run again
        Assert.Equal(0xC0FFEE11u, GuestMemory.R32(marker));
        WaitForFrames(30, "frames after the failed load");
        Assert.Contains("bytes of its state were not read", failure.Message);
        Assert.DoesNotContain("restart the game", failure.Message);

        // and a good state still loads over the rolled-back session
        SaveStates.Load(_harness.Emu, path);
        WaitForFrames(30, "frames after the good load");
    }

    /// <summary>
    /// The load is asked while the main thread is still running guest code (stopped by the host right after an HLE call
    /// returned, with the <see cref="GuestThread.HleReturned"/> seam: a preemption on a loaded host). The hold only asks
    /// the threads to stop at their next safe point, so the load waits for it, as a save does, before it keeps the
    /// running session to go back to. Before, it took that session at once, found "thread 'main' is running guest
    /// code", went on without a way back and the failed load left the game stopped ("restart the game"; one run in six
    /// of <see cref="AFailedLoadAfterTheThreadsStoppedGivesTheGameBack"/> under load).
    /// </summary>
    [Fact]
    public void AFailedLoadAskedWhileAThreadRunsGuestCodeStillGivesTheGameBack()
    {
        WaitForFrames(30, "the title's first frames");
        string path = Path.Combine(_harness.Root, "state.vss");
        SaveStates.Save(_harness.Emu, path, timeoutMs: 60_000);
        string corrupted = Path.Combine(_harness.Root, "corrupted.vss");
        CorruptAModuleSection(path, corrupted);
        WaitForFrames(30, "frames after the save");
        uint marker = _harness.Emu.Memory.Alloc(64, "rollback marker");
        GuestMemory.W32(marker, 0xC0FFEE11);

        GuestThread main = _harness.Emu.Kernel.MainThread!;
        using var stopped = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        SnapshotException failure;
        GuestThread.HleReturned = (thread, _) =>
        {
            if (!ReferenceEquals(thread, main) || release.IsSet)
            {
                return;
            }

            stopped.Set();
            // back in guest code and not at a safe point until the load says it waits for this thread (or stops it)
            while (!release.Wait(1) && !thread.Core.StopRequested)
            {
            }
        };
        SaveStates.LoadWaitingForSafePoint = why =>
        {
            if (why.Contains($"'{main.Name}'", StringComparison.Ordinal))
            {
                release.Set();
            }
        };
        try
        {
            Assert.True(stopped.Wait(60_000), "the main thread never returned from an HLE call");
            failure = Assert.Throws<SnapshotException>(() => SaveStates.Load(_harness.Emu, corrupted));
        }
        finally
        {
            GuestThread.HleReturned = null;
            SaveStates.LoadWaitingForSafePoint = null;
            release.Set();
        }

        Assert.Contains("the game went back to where it was before the load", failure.Message);
        Assert.Equal(0xC0FFEE11u, GuestMemory.R32(marker));
        WaitForFrames(30, "frames after the failed load");
    }

    /// <summary>
    /// A block with pages of one repeated word (the trap fill of a firmware module's code segment) goes into the state
    /// file as its own section kind (<c>memw:</c>, those pages as their word) and a load gives every page back, the
    /// filled ones and the others.
    /// </summary>
    [Fact]
    public void ABlockWithPagesOfOneWordRoundTripsThroughAStateFile()
    {
        WaitForFrames(30, "the title's first frames");
        const uint Size = 4 * GuestMemory.PageSize;
        uint block = _harness.Emu.Memory.Alloc(Size, "word pages");
        for (uint at = 0; at < 3 * GuestMemory.PageSize; at += 4)
        {
            GuestMemory.W32(block + at, 0xE7FDDEFE);
        }

        GuestMemory.W32(block + 3 * GuestMemory.PageSize + 8, 0xC0FFEE11);
        string path = Path.Combine(_harness.Root, "words.vss");
        SaveStates.Save(_harness.Emu, path, timeoutMs: 60_000);
        var names = new List<string>();
        using (FileStream file = File.OpenRead(path))
        using (var reader = new SaveStateReader(file))
        {
            while (reader.Next(out string name, out _))
            {
                names.Add(name);
                reader.ReadAll();
            }
        }

        Assert.Contains("memw:" + block.ToString("X8"), names);
        GuestMemory.Fill(block, 0x5A, Size);
        SaveStates.Load(_harness.Emu, path);
        Assert.Equal(0xE7FDDEFEu, GuestMemory.R32(block));
        Assert.Equal(0xE7FDDEFEu, GuestMemory.R32(block + 3 * GuestMemory.PageSize - 4));
        Assert.Equal(0u, GuestMemory.R32(block + 3 * GuestMemory.PageSize));
        Assert.Equal(0xC0FFEE11u, GuestMemory.R32(block + 3 * GuestMemory.PageSize + 8));
        WaitForFrames(30, "frames after the load");
    }
}
