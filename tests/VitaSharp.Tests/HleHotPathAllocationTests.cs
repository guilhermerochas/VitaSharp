using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// HLE functions games call every frame allocate nothing, called through their generated binders as guest code calls
/// them (the PERF allocators line of Dead Space's gameplay on the phone: sceGxmProgramFindParameterByName 15-36 KB per
/// frame, a managed copy of every name looked up, sceKernelWaitThreadEndCB 7-8 KB, a closure and a string per call).
/// Every young GC stops all threads (17-29 ms on the phone), and the allocation rate sets how often one comes.
/// </summary>
[Collection("guest")]
public sealed unsafe class HleHotPathAllocationTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Calls = 200;
    private readonly HleHarness _harness = new(fixture, "hle-hot-alloc");

    public void Dispose() => _harness.Dispose();

    /// <summary>The generated binder bound to <paramref name="nid"/> (vita-headers NIDs), as a guest call reaches it.</summary>
    private Action<HleContext> Binder(uint nid) =>
        _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not bound");

    /// <summary>A program with three named parameters (the GXP header layout of GxmHleTests.ProgramParameterSemantics);
    /// returns the parameter entries' addresses.</summary>
    private static uint[] WriteProgram(uint program, string[] names)
    {
        GuestMemory.Fill(program, 0, 0x200);
        GuestMemory.W32(program + 8, 0x200);
        GuestMemory.W32(program + 0x24, (uint)names.Length);
        GuestMemory.W32(program + 0x28, 0x100 - 0x28);
        GuestMemory.W32(program + 0x2C, 0x1C0 - 0x2C);
        uint nameAt = 0x180;
        var entries = new uint[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            uint entry = program + 0x100 + (uint)i * 16;
            entries[i] = entry;
            GuestMemory.W32(entry, program + nameAt - entry);
            GuestMemory.WriteCString(program + nameAt, names[i]);
            nameAt += (uint)names[i].Length + 1;
            GuestMemory.Write(entry + 4, (byte)1); // uniform
            GuestMemory.Write(entry + 5, (byte)4);
            GuestMemory.W32(entry + 8, 1);
        }

        return entries;
    }

    [Fact]
    public void FindParameterByNameMatchesExactlyAndAllocatesNothing()
    {
        uint program = _harness.Alloc(0x200);
        uint[] entries = WriteProgram(program, ["wvp", "uColor", "uColorScale"]);
        uint color = _harness.Str("uColor");
        uint prefix = _harness.Str("uCol");
        uint longer = _harness.Str("uColorX");
        uint scale = _harness.Str("uColorScale");
        uint empty = _harness.Str("");
        Action<HleContext> find = Binder(0x277794C4); // sceGxmProgramFindParameterByName
        CpuState* cpu = CpuState.Allocate();
        try
        {
            var context = new HleContext { Cpu = cpu, Emu = _harness.Emu };
            uint Find(uint prog, uint name)
            {
                cpu->R[0] = prog;
                cpu->R[1] = name;
                find(context);
                return cpu->R[0];
            }

            Assert.Equal(entries[1], Find(program, color));
            Assert.Equal(entries[2], Find(program, scale));
            Assert.Equal(entries[0], Find(program, _harness.Str("wvp")));
            Assert.Equal(0u, Find(program, prefix));   // a prefix of a name is not the name
            Assert.Equal(0u, Find(program, longer));   // nor a longer string that starts with it
            Assert.Equal(0u, Find(program, empty));
            Assert.Equal(0u, Find(0, color));

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Calls; i++)
            {
                Find(program, (i & 1) == 0 ? color : scale);
                Find(program, longer);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            CpuState.Free(cpu);
        }
    }

    [Fact]
    public void CStringEqualsIsReadCStringEquality()
    {
        string[] samples = ["", "a", "uColor", "uColor2", "uColo", "ação", "name[0].x"];
        foreach (string guest in samples)
        {
            uint at = _harness.Str(guest);
            foreach (string value in samples)
            {
                Assert.Equal(GuestMemory.ReadCString(at) == value, GuestMemory.CStringEquals(at, value));
            }
        }

        Assert.True(GuestMemory.CStringEquals(0, ""));
        Assert.False(GuestMemory.CStringEquals(0, "a"));
    }

    [Fact]
    public void WaitThreadEndOnAnEndedThreadAllocatesNothing()
    {
        ThreadMgr threads = _harness.Module<ThreadMgr>();
        Action<HleContext> waitEnd = Binder(0xDDB395A9); // sceKernelWaitThreadEnd
        _harness.OnGuest(self =>
        {
            int uid = Call(threads, "sceKernelCreateThread", "ended", 0x81000001u, 0x10000100, 0x4000, 0u, 0, 0u);
            Assert.True(uid > 0);
            uint stat = _harness.Word(0xFFFF_FFFF);
            CpuState* cpu = CpuState.Allocate();
            try
            {
                var context = new HleContext { Cpu = cpu, Emu = _harness.Emu, Thread = self };
                int Wait()
                {
                    cpu->R[0] = (uint)uid;
                    cpu->R[1] = stat;
                    cpu->R[2] = 0;
                    waitEnd(context);
                    return (int)cpu->R[0];
                }

                // a created thread that never ran is dormant: the wait returns its exit status at once
                Assert.Equal(0, Wait());
                Assert.Equal(0u, GuestMemory.R32(stat));
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < Calls; i++)
                {
                    Wait();
                }

                Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            finally
            {
                CpuState.Free(cpu);
            }
        });
    }
}
