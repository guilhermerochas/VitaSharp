using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The process object's tables and counters of FW 3.74 processmgr (decompiled, study only): the +0x20 words, the
/// random words, the debugging tables the object points at (behind the dipsw bit) and the two counters with their
/// limits and the kernel process refused.
/// </summary>
[Collection("guest")]
public sealed class KernelProcessTablesTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80020005), IllegalAddr = unchecked((int)0x80020006),
        NotAllowed = unchecked((int)0x80029008), KernelRefused = unchecked((int)0x80029002),
        IllegalCount = unchecked((int)0x8002000F), Overflow = unchecked((int)0x8002450D);

    private const int KernelPid = 0x10005;

    private readonly HleHarness _harness = new(fixture, "kproctab");

    public void Dispose() => _harness.Dispose();

    private ProcessmgrKernel Processmgr => _harness.Module<ProcessmgrKernel>();

    private int Pid => _harness.Emu.Pid;

    private uint Block() => (uint)Invoke(Processmgr, "ksceKernelGetProcessKernelBuf", Pid)!;

    [Fact]
    public void TheWordsAt0x20AndTheRandomWords()
    {
        uint block = Block();
        GuestMemory.W32(block + 0x20, 0x11);
        GuestMemory.W32(block + 0x24, 0x22);
        uint output = _harness.Alloc(8);
        Assert.Equal(IllegalAddr, Call(Processmgr, "NID_5BDA978F", Pid, 0u));
        Assert.Equal(0, Call(Processmgr, "NID_5BDA978F", Pid, output));
        Assert.Equal(0x11u, GuestMemory.R32(output));
        Assert.Equal(0x22u, GuestMemory.R32(output + 4));
        uint words = _harness.Alloc(0x40);
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_2CEB1C7A", Pid, words + 1, 0x10u));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_2CEB1C7A", Pid, words, 0x11u));
        Assert.Equal(0, Call(Processmgr, "NID_2CEB1C7A", Pid, words, 0x40u));
        Assert.Contains(Enumerable.Range(0, 16).Select(i => GuestMemory.R32(words + (uint)i * 4)), w => w != 0);
    }

    [Fact]
    public void TheDebuggingTablesAreBehindTheDipswBit()
    {
        uint first = _harness.Alloc(4);
        uint second = _harness.Alloc(4);
        Assert.Equal(NotAllowed, Call(Processmgr, "NID_060DA466", Pid, 0u, first, second));
        Assert.Equal(NotAllowed, Call(Processmgr, "NID_FC80A606", Pid, 5, 1u, 2u));
        Invoke(_harness.Module<SystemServicesVsh>(), "ksceKernelSetDipsw", 0xE4u);
        uint block = Block();
        uint table = GuestMemory.R32(block + 0xC);
        Assert.NotEqual(0u, table);
        GuestMemory.W32(table + 8 + 4 * 8, 0x44);
        GuestMemory.W32(table + 0xC + 4 * 8, 0x55);
        Assert.Equal(0, Call(Processmgr, "NID_060DA466", Pid, 4u, first, second));
        Assert.Equal(0x44u, GuestMemory.R32(first));
        Assert.Equal(0x55u, GuestMemory.R32(second));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_060DA466", Pid, 5u, first, second));
        Assert.Equal(IllegalAddr, Call(Processmgr, "NID_6898F7A7", Pid, 0u, 0u, second));
        Assert.Equal(0, Call(Processmgr, "NID_6898F7A7", Pid, 3u, first, second));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_6898F7A7", Pid, 4u, first, second));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_FC80A606", Pid, 4, 1u, 2u));
        Assert.Equal(0, Call(Processmgr, "NID_FC80A606", Pid, 5, 0x66u, 0x77u));
        Assert.Equal(0, Call(Processmgr, "NID_E025F5A9", Pid, 5, first, second));
        Assert.Equal(0x66u, GuestMemory.R32(first));
        Assert.Equal(0x77u, GuestMemory.R32(second));
    }

    [Fact]
    public void TheCountersStopAtTheirLimitsAndRefuseTheKernel()
    {
        Assert.Equal(KernelRefused, Call(Processmgr, "NID_8B4086AF", KernelPid, 0));
        Assert.Equal(IllegalCount, Call(Processmgr, "NID_F160B4D8", Pid, 1));
        for (int i = 0; i < 0x20; i++)
        {
            Assert.Equal(0, Call(Processmgr, "NID_8B4086AF", Pid, 1));
        }

        Assert.Equal(Overflow, Call(Processmgr, "NID_8B4086AF", Pid, 1));
        Assert.Equal(0x20, GuestMemory.Read<ushort>(Block() + 0x40E));
        Assert.Equal(0, Call(Processmgr, "NID_F160B4D8", Pid, 1));
        Assert.Equal(0, Call(Processmgr, "NID_8B4086AF", Pid, 0));
        Assert.Equal(1, GuestMemory.Read<ushort>(Block() + 0x40C));
        Assert.Equal(0, Call(Processmgr, "NID_F160B4D8", Pid, 0));
        Assert.Equal(IllegalCount, Call(Processmgr, "NID_F160B4D8", Pid, 0));
    }
}
