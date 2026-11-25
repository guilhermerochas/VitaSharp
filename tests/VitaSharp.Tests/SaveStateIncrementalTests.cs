using System.Security.Cryptography;
using VitaSharp.Core;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// Incremental save states (the "Gravar tudo" pre-roll's ring, <see cref="SnapshotPages"/>): while the game is
/// stopped only the pages written since the previous state are read from guest memory (the dirty-page tracker), the
/// others come from the previous state's image, and the file is the same as a full state's. A page written between two
/// states is in the second; a state loads back with every page as it was; and the pause it costs is measured against a
/// full state's (printed: the measurement, not a condition of the test).
/// </summary>
[Collection("guest")]
public sealed class SaveStateIncrementalTests(GuestMemoryFixture fixture, ITestOutputHelper output)
{
    /// <summary>The block's pages, incrementally, give the same bytes as the full copy; after a write only the written
    /// page (and the one after it: a store may reach past a page end) is read again.</summary>
    [Fact]
    public void IncrementalPagesAreTheFullCopyAndOnlyTheWrittenPagesAreRead()
    {
        GuestMemory memory = fixture.Memory;
        const uint Size = 64 * GuestMemory.PageSize;
        uint address = memory.Alloc(Size, "test:incremental");
        try
        {
            GuestMemory.Block block = memory.FindBlock(address)!;
            var rng = new Random(11);
            byte[] fill = new byte[Size / 2];
            rng.NextBytes(fill);
            fill.CopyTo(GuestMemory.Span(address, fill.Length)); // half random, half zero pages
            GuestMemory.W32(address + 40 * GuestMemory.PageSize, 0x12345678); // a lone word in a zero page

            long read = 0, reused = 0;
            uint epoch1 = DirtyPages.Sweep();
            ReadOnlyMemory<byte>[] first = memory.CapturePages(block, null, 0, ref read, ref reused);
            Assert.Equal((64L, 0L), (read, reused));
            Assert.Equal(memory.CopyContents(block, out bool w0), GuestMemory.SerializePages(first, out bool w1));
            Assert.Equal(w0, w1);

            // nothing written since the first capture's sweep (exact: nothing was running): every page is the image's
            uint epoch2 = DirtyPages.Sweep();
            read = reused = 0;
            ReadOnlyMemory<byte>[] second = memory.CapturePages(block, first, epoch1, ref read, ref reused);
            Assert.Equal((0L, 64L), (read, reused));
            Assert.Equal(GuestMemory.SerializePages(first, out _), GuestMemory.SerializePages(second, out _));

            // one page written: only it (and the next one) is read
            GuestMemory.W32(address + 10 * GuestMemory.PageSize + 100, 0xCAFEF00D);
            uint epoch3 = DirtyPages.Sweep();
            read = reused = 0;
            ReadOnlyMemory<byte>[] third = memory.CapturePages(block, second, epoch2, ref read, ref reused);
            Assert.InRange(read, 1, 2);
            Assert.Equal(64 - read, reused);
            Assert.Equal(memory.CopyContents(block, out _), GuestMemory.SerializePages(third, out _));
            Assert.NotEqual(GuestMemory.SerializePages(second, out _), GuestMemory.SerializePages(third, out _));
            Assert.True(epoch3 > epoch2 && epoch2 > epoch1);
        }
        finally
        {
            memory.Free(address);
        }
    }

    private void FreeTitleRange()
    {
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }
    }

    private static uint ReadWord(Emulator emu, uint address)
    {
        using (AddressSpace.Use(emu.Space))
        {
            return GuestMemory.R32(address);
        }
    }

    private static void WriteWord(Emulator emu, uint address, uint value)
    {
        using (AddressSpace.Use(emu.Space))
        {
            GuestMemory.W32(address, value);
        }
    }

    /// <summary>
    /// A running title with 48 MB of non-zero memory: a full state, then incremental ones. The second incremental
    /// state reads a small part of the pages (what the counting thread and the system wrote), a word written between
    /// two states is in the second, and that state loads back with it. The pause of each (threads held, then the
    /// capture with the game paused) is printed.
    /// </summary>
    [Fact]
    public void AnIncrementalStateReadsTheWrittenPagesAndLoadsBack()
    {
        FreeTitleRange();
        using var h = new HleHarness(fixture, "incstate");
        Emulator emu = h.Emu;
        string statePath = Path.Combine(h.Root, "incremental.vss");
        try
        {
            emu.LoadTitle(SyntheticTitle.Install(h.Root, "VSTS00003"));
            emu.Start();
            uint counter = SaveStateBootFromStateTests.StartCounter(emu, "VsTestIncCounter");
            HleHarness.Until(() => SaveStateBootFromStateTests.Read(emu, counter) > 5, "the first counts");
            uint big, marker;
            using (ProcessScope.Enter(emu))
            {
                big = emu.Memory.Alloc(48u << 20, "test:big heap");
                marker = emu.Memory.Alloc(GuestMemory.PageSize, "test:marker");
                byte[] noise = RandomNumberGenerator.GetBytes(48 << 20);
                noise.CopyTo(GuestMemory.Span(big, noise.Length));
            }

            using (var full = new MemoryStream())
            {
                SaveStates.Save(emu, full);
                output.WriteLine($"full state: held {SaveStates.LastStop.HeldMs:F1} ms, capture {SaveStates.LastStop.CaptureMs:F1} ms, {full.Length >> 20} MB");
            }

            var image = new SnapshotPages();
            using (var first = new MemoryStream())
            {
                SaveStates.Save(emu, first, image);
                output.WriteLine($"incremental 1 (no image yet): held {SaveStates.LastStop.HeldMs:F1} ms, capture {SaveStates.LastStop.CaptureMs:F1} ms, {image.LastRead} pages read");
            }

            using (var second = new MemoryStream())
            {
                SaveStates.Save(emu, second, image);
                output.WriteLine($"incremental 2: held {SaveStates.LastStop.HeldMs:F1} ms, capture {SaveStates.LastStop.CaptureMs:F1} ms, {image.LastRead} pages read, {image.LastReused} unchanged");
            }

            // most of the 48 MB (12288 pages) and the rest of memory is unchanged since the previous state
            Assert.True(image.LastReused > 12288 && image.LastRead < image.LastReused / 10, $"{image.LastRead} read, {image.LastReused} reused");

            WriteWord(emu, marker, 0xB0B0_0001);
            using (var third = new MemoryStream())
            {
                SaveStates.Save(emu, third, image);
                output.WriteLine($"incremental 3: held {SaveStates.LastStop.HeldMs:F1} ms, capture {SaveStates.LastStop.CaptureMs:F1} ms, {image.LastRead} pages read, {image.LastReused} unchanged");
                File.WriteAllBytes(statePath, third.ToArray());
            }

            WriteWord(emu, marker, 0xB0B0_0002);
            uint noiseWord = ReadWord(emu, big + 4096 * 7 + 12);
            WriteWord(emu, big + 4096 * 7 + 12, ~noiseWord);
            SaveStates.Load(emu, statePath);
            Assert.Equal(0xB0B0_0001u, ReadWord(emu, marker)); // written between the states: in the third
            Assert.Equal(noiseWord, ReadWord(emu, big + 4096 * 7 + 12)); // unchanged since the full read: from the image
            uint loaded = SaveStateBootFromStateTests.Read(emu, counter);
            HleHarness.Until(() => SaveStateBootFromStateTests.Read(emu, counter) > loaded + 3, "the counting thread to run on after the load");
        }
        finally
        {
            emu.Stop();
        }
    }
}
