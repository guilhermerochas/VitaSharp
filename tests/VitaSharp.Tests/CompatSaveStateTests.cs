using System.Buffers.Binary;
using VitaSharp.Core;
using VitaSharp.Core.Compat;
using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.Compat.Ge;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Save states with the PSP compat core running (docs/notes/save-states.md, SceCompat): a synthetic MIPS boot on the
/// Allegrex counts hardware timer interrupts in a busy loop, starts a GE list that calls a sub-list and stops at a
/// stall address inside it, reads the system time, then spins at a fixed point until the test lets it go; then it
/// moves the stall past the list's end and reads back the GE's and its own registers into its results. Up to the fixed
/// point the run is a function of the core's cycles only (no halt), and after it nothing depends on time, so the
/// moment of the save does not matter: a run saved at the fixed point and the state loaded into another emulator must
/// end with the same results as a run never saved: the core (registers, PC, interrupt state), its time, the GE's list
/// in progress (stalled in the sub-list, its return address) and SceCompat's side come back exactly. Before, the
/// compat core was not in the state at all.
/// </summary>
[Collection("guest")]
public sealed class CompatSaveStateTests
{
    private const uint ResultsOffset = 0x200000;    // window offset of the results (PSP 0x88200000)
    private const uint ListOffset = 0x100000;       // the GE's main list (PSP 0x08100000)
    private const uint SubListOffset = 0x100100;    // the sub-list it calls (PSP 0x08100100)
    private const uint StallAddress = 0x08100108;   // inside the sub-list, before its last state command
    private const int KickCount = 5, DoneCount = 300;
    private const int SpinningWord = 10, GoWord = 11; // the core spins at its fixed point until the test sets GoWord
    private const uint DoneMarker = 0xD0E;
    private const int ResultWords = 10;
    private const int S0 = 16, S1 = 17, S2 = 18, S3 = 19, S4 = 20;

    private readonly GuestMemoryFixture _fixture;

    public CompatSaveStateTests(GuestMemoryFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>The title's segment is fixed at the start of user memory: blocks other tests (or the session before)
    /// of the shared memory left there go first.</summary>
    private static void FreeTitleRange(GuestMemoryFixture fixture)
    {
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }
    }

    /// <summary>A running synthetic title whose process may use SceCompat (a system program, as the PSP emulator).</summary>
    private sealed class Session : IDisposable
    {
        private readonly GuestMemoryFixture _fixture;

        public Session(GuestMemoryFixture fixture, string name)
        {
            _fixture = fixture;
            FreeTitleRange(fixture);
            Harness = new HleHarness(fixture, name);
            Emu = Harness.Emu;
            Emu.LoadTitle(SyntheticTitle.Install(Harness.Root));
            Emu.Start();
            Emu.IsSystemProgram = true;
        }

        public HleHarness Harness { get; }

        public Emulator Emu { get; }

        public Compat Compat => Harness.Module<Compat>();

        public uint Results => Compat.Window + ResultsOffset;

        public uint Word(int index)
        {
            using (AddressSpace.Use(Emu.Space))
            {
                return GuestMemory.R32(Results + (uint)(index * 4));
            }
        }

        /// <summary>Waits for the core to reach its fixed point (the time-dependent part done, the GE list stalled).</summary>
        public void WaitUntilSpinning()
        {
            Until(() => Word(SpinningWord) == 1, "the PSP boot to reach its fixed point");
        }

        /// <summary>Lets the core go on from its fixed point.</summary>
        public void Go()
        {
            using (AddressSpace.Use(Emu.Space))
            {
                GuestMemory.W32(Results + (uint)(GoWord * 4), 1);
            }
        }

        public uint[] WaitForResults()
        {
            Until(() => Word(6) == DoneMarker, "the PSP boot to finish");
            uint[] results = new uint[ResultWords];
            for (int i = 0; i < ResultWords; i++)
            {
                results[i] = Word(i);
            }

            Assert.Null(Compat.Runner?.Failure);
            return results;
        }

        public void Dispose()
        {
            uint window = Compat.Window;
            Emu.Stop();
            Harness.Dispose();
            FreeTitleRange(_fixture);
            if (window != 0 && _fixture.Memory.FindBlock(window) is { } block)
            {
                _fixture.Memory.Free(block.Address); // the 64 MB window: the next session's load puts it back
            }
        }
    }

    /// <summary>The boot (main at 0x88600000, the interrupt handler at EBase 0x88601000).</summary>
    private static byte[] Boot()
    {
        int framebufferFormat = 0x800 + GeCommand.FrameBufferPixelFormat * 4;
        int framebufferWidth = 0x800 + GeCommand.FrameBufferWidth * 4;
        uint[] main =
        [
            AllegrexAsm.Lui(AllegrexAsm.T0, 0x8860),
            AllegrexAsm.Ori(AllegrexAsm.T0, AllegrexAsm.T0, 0x1000),
            AllegrexAsm.Mtc0(AllegrexAsm.T0, AllegrexCore.Cop0EBase),
            AllegrexAsm.Lui(S0, 0xBC50),
            AllegrexAsm.Addiu(AllegrexAsm.T0, AllegrexAsm.Zero, -1),
            AllegrexAsm.Sw(AllegrexAsm.T0, S0, 8), // prescaler -1/-1: 48 MHz
            AllegrexAsm.Sw(AllegrexAsm.T0, S0, 12),
            AllegrexAsm.Lui(AllegrexAsm.T0, 0x80C0), // reset overflow | counting | interrupt enable
            AllegrexAsm.Ori(AllegrexAsm.T0, AllegrexAsm.T0, 4800), // 100 us
            AllegrexAsm.Sw(AllegrexAsm.T0, S0, 0),
            AllegrexAsm.Lui(AllegrexAsm.T1, 0xBC30),
            AllegrexAsm.Ori(AllegrexAsm.T0, AllegrexAsm.Zero, 1 << 15),
            AllegrexAsm.Sw(AllegrexAsm.T0, AllegrexAsm.T1, 8), // enable interrupt 15 (timer 0)
            AllegrexAsm.Lui(S4, 0x8820),
            AllegrexAsm.Lui(S1, 0xBD40),
            AllegrexAsm.Lui(AllegrexAsm.T0, 0x0810),
            AllegrexAsm.Sw(AllegrexAsm.T0, S1, 0x108), // LIST
            AllegrexAsm.Ori(AllegrexAsm.T0, AllegrexAsm.T0, (int)(StallAddress & 0xFFFF)),
            AllegrexAsm.Sw(AllegrexAsm.T0, S1, 0x10C), // STALL, inside the sub-list
            AllegrexAsm.Ori(AllegrexAsm.T0, AllegrexAsm.Zero, 0x0401), // IM2 | IE
            AllegrexAsm.Mtc0(AllegrexAsm.T0, AllegrexCore.Cop0Status),

            // until KickCount interrupts: count loops in s3, then start the GE
            AllegrexAsm.Ori(S2, AllegrexAsm.Zero, KickCount),
            AllegrexAsm.Lw(AllegrexAsm.T0, S4, 0),
            AllegrexAsm.Bne(AllegrexAsm.T0, S2, -2),
            AllegrexAsm.Addiu(S3, S3, 1),
            AllegrexAsm.Ori(AllegrexAsm.T0, AllegrexAsm.Zero, 1),
            AllegrexAsm.Sw(AllegrexAsm.T0, S1, 0x100), // EXEC: runs to the stall
            AllegrexAsm.Sw(S3, S4, 28),

            // until DoneCount: interrupts and the timer off, what depends on the core's time into the results
            AllegrexAsm.Ori(S2, AllegrexAsm.Zero, DoneCount),
            AllegrexAsm.Lw(AllegrexAsm.T0, S4, 0),
            AllegrexAsm.Bne(AllegrexAsm.T0, S2, -2),
            AllegrexAsm.Addiu(S3, S3, 1),
            AllegrexAsm.Mtc0(AllegrexAsm.Zero, AllegrexCore.Cop0Status),
            AllegrexAsm.Sw(AllegrexAsm.Zero, S0, 0),
            AllegrexAsm.Sw(S3, S4, 4),
            AllegrexAsm.Lui(AllegrexAsm.T1, 0xBC60),
            AllegrexAsm.Lw(AllegrexAsm.T0, AllegrexAsm.T1, 0), // the system time
            AllegrexAsm.Sw(AllegrexAsm.T0, S4, 16),
            AllegrexAsm.Lw(AllegrexAsm.T0, S1, 0x10C), // STALL, still inside the sub-list
            AllegrexAsm.Sw(AllegrexAsm.T0, S4, 32),

            // spin until the test's go word: the state is taken here, with the list stalled in the sub-list
            AllegrexAsm.Ori(AllegrexAsm.T0, AllegrexAsm.Zero, 1),
            AllegrexAsm.Sw(AllegrexAsm.T0, S4, SpinningWord * 4),
            AllegrexAsm.Lw(AllegrexAsm.T0, S4, GoWord * 4),
            AllegrexAsm.Beq(AllegrexAsm.T0, AllegrexAsm.Zero, -2),
            AllegrexAsm.Nop,

            // move the stall past the list's end: the list goes on in the sub-list, returns and finishes
            AllegrexAsm.Lui(AllegrexAsm.T0, 0x0810),
            AllegrexAsm.Ori(AllegrexAsm.T0, AllegrexAsm.T0, 0x0200),
            AllegrexAsm.Sw(AllegrexAsm.T0, S1, 0x10C),
            AllegrexAsm.Lw(AllegrexAsm.T0, S1, 0x304), // the GE's interrupt status
            AllegrexAsm.Sw(AllegrexAsm.T0, S4, 8),
            AllegrexAsm.Lw(AllegrexAsm.T0, S1, 0x100), // EXEC
            AllegrexAsm.Sw(AllegrexAsm.T0, S4, 12),
            AllegrexAsm.Lw(AllegrexAsm.T0, S1, framebufferFormat), // set after the stall moved
            AllegrexAsm.Sw(AllegrexAsm.T0, S4, 20),
            AllegrexAsm.Lw(AllegrexAsm.T0, S1, framebufferWidth), // set before the call
            AllegrexAsm.Sw(AllegrexAsm.T0, S4, 36),
            AllegrexAsm.Ori(AllegrexAsm.T0, AllegrexAsm.Zero, (int)DoneMarker),
            AllegrexAsm.Sw(AllegrexAsm.T0, S4, 24),
            AllegrexAsm.Halt,
            AllegrexAsm.Beq(AllegrexAsm.Zero, AllegrexAsm.Zero, -2),
            AllegrexAsm.Nop,
        ];
        uint[] handler =
        [
            AllegrexAsm.Lw(AllegrexAsm.K0, S0, 0), // the timer's acknowledge
            AllegrexAsm.Lw(AllegrexAsm.K1, S4, 0),
            AllegrexAsm.Addiu(AllegrexAsm.K1, AllegrexAsm.K1, 1),
            AllegrexAsm.Sw(AllegrexAsm.K1, S4, 0),
            AllegrexAsm.Eret,
        ];
        byte[] image = new byte[0x2000];
        for (int i = 0; i < main.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(i * 4), main[i]);
        }

        for (int i = 0; i < handler.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x1000 + i * 4), handler[i]);
        }

        return image;
    }

    private static uint Command(int command, uint argument)
    {
        return ((uint)command << 24) | (argument & 0xFFFFFF);
    }

    /// <summary>The window, the boot images, InitEx, the GE's lists, then the core out of reset (without the start
    /// path's license checks, which the HLE's retail unit does not pass).</summary>
    private static void StartCore(Session session)
    {
        string HostPath(string guest)
        {
            string host = session.Emu.Vfs.Resolve(guest, forWrite: true)!;
            Directory.CreateDirectory(Path.GetDirectoryName(host)!);
            return host;
        }

        File.WriteAllBytes(HostPath("os0:kd/pcbc.bin"), Boot());
        byte[] pcff = new byte[0x100];
        BinaryPrimitives.WriteUInt32LittleEndian(pcff, 0x46465350); // "PSFF"
        File.WriteAllBytes(HostPath("vs0:app/NPXS10028/pcff.bin"), pcff);
        session.Harness.OnGuest(_ =>
        {
            Compat compat = session.Compat;
            uint bases = session.Harness.Alloc(8);
            Assert.Equal(0, Call(compat, "sceCompatAllocCdramWithHole", bases));
            Assert.Equal(0, Call(compat, "sceCompatInitEx", 1));
            uint window = GuestMemory.R32(bases);
            uint[] list =
            [
                Command(GeCommand.Base, 0x080000),
                Command(GeCommand.FrameBufferWidth, 512),
                Command(GeCommand.Call, SubListOffset),
                Command(GeCommand.Finish, 0),
                Command(GeCommand.End, 0),
            ];
            uint[] subList =
            [
                Command(GeCommand.ShadeMode, 1),
                Command(GeCommand.VertexType, 0x800102),
                Command(GeCommand.FrameBufferPixelFormat, 3), // after the stall
                Command(GeCommand.Return, 0),
            ];
            for (int i = 0; i < list.Length; i++)
            {
                GuestMemory.W32(window + ListOffset + (uint)(i * 4), list[i]);
            }

            for (int i = 0; i < subList.Length; i++)
            {
                GuestMemory.W32(window + SubListOffset + (uint)(i * 4), subList[i]);
            }

            Assert.Equal(0, compat.ReleaseCoreForTests());
        });
    }

    private static List<GuestThread> CoreThreads(Emulator emu)
    {
        return emu.Kernel.Threads.Where(t => t.Name == CompatCoreRunner.ThreadName && t.IsLive).ToList();
    }

    [Fact]
    public void ACoreSavedMidRunAndLoadedElsewhereEndsAsARunNeverSaved()
    {
        string statePath = Path.Combine(Path.GetTempPath(), $"vitasharp-compatstate-{Guid.NewGuid():N}.vss");
        try
        {
            uint[] neverSaved;
            using (Session baseline = new(_fixture, "compatss-base"))
            {
                StartCore(baseline);
                baseline.WaitUntilSpinning();
                baseline.Go();
                neverSaved = baseline.WaitForResults();
            }

            Assert.Equal((uint)DoneCount, neverSaved[0]);
            Assert.Equal(StallAddress, neverSaved[8]);
            Assert.Equal(Command(GeCommand.FrameBufferPixelFormat, 3), neverSaved[5]); // the sub-list's command after the stall ran
            Assert.Equal(Command(GeCommand.FrameBufferWidth, 512), neverSaved[9]);

            uint[] savedAndGoneOn;
            int savedThread;
            using (Session saved = new(_fixture, "compatss-save"))
            {
                StartCore(saved);
                saved.WaitUntilSpinning();
                savedThread = Assert.Single(CoreThreads(saved.Emu)).Uid;
                SaveStates.Save(saved.Emu, statePath, timeoutMs: 60_000);
                saved.Go();
                savedAndGoneOn = saved.WaitForResults();
            }

            Assert.Equal(neverSaved, savedAndGoneOn);

            using Session loaded = new(_fixture, "compatss-load");
            Assert.Empty(CoreThreads(loaded.Emu));
            SaveStates.Load(loaded.Emu, statePath);
            GuestThread core = Assert.Single(CoreThreads(loaded.Emu));
            Assert.Equal(savedThread, core.Uid);
            Assert.NotNull(loaded.Compat.Runner);

            // the GE's list came back in progress: running, stalled in the called sub-list
            GeEngine ge = loaded.Compat.Hardware.Ge!;
            Assert.Equal(GeEngine.ExecRunning | GeEngine.ExecDepth1, ge.Read32(GeEngine.PhysicalBase + 0x100) & (GeEngine.ExecRunning | GeEngine.ExecDepth1));
            Assert.Equal(StallAddress, ge.Read32(GeEngine.PhysicalBase + 0x108));
            loaded.Go();
            uint[] afterLoad = loaded.WaitForResults();
            Assert.Equal(neverSaved, afterLoad);
        }
        finally
        {
            File.Delete(statePath);
        }
    }

    /// <summary>A pause stops the compat core between two slices (before, it ran on while the game was paused), and a
    /// resume lets it go on.</summary>
    [Fact]
    public void APauseStopsTheCoreBetweenTwoSlices()
    {
        using Session session = new(_fixture, "compatss-pause");
        StartCore(session);
        Until(() => session.Word(0) > 2, "the core to run");
        CompatCoreRunner runner = session.Compat.Runner!;
        GuestThread thread = Assert.Single(CoreThreads(session.Emu));
        bool paused = EmulationPause.Pause("compat pause test");
        try
        {
            Until(() => thread.IsParked, "the core's thread to park");
            long cycles = runner.Cycles;
            for (int i = 0; i < 20 && thread.IsParked; i++)
            {
                Thread.Sleep(5);
            }

            Assert.Equal(cycles, runner.Cycles);
        }
        finally
        {
            if (paused)
            {
                EmulationPause.Resume("compat pause test");
            }
        }

        long resumed = runner.Cycles;
        Until(() => runner.Cycles > resumed, "the core to run again");
    }
}
