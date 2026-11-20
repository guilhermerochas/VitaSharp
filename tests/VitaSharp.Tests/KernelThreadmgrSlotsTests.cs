using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The thread manager's six handler slots (2-7) as FW 3.74 threadmgr has them (decompiled, study only): the first free
/// one is taken and answered, all six taken is 0x80028081, freeing checks the slot (0x80028082) and that it is not busy
/// (0x80028083), and a freed slot is taken again first.
/// </summary>
[Collection("guest")]
public sealed class KernelThreadmgrSlotsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NoFreeSlot = unchecked((int)0x80028081), NotTaken = unchecked((int)0x80028082),
        Busy = unchecked((int)0x80028083);

    private readonly HleHarness _harness = new(fixture, "kthrslots");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    [Fact]
    public void SlotsAreTakenInOrderAndFreedWithTheirChecks()
    {
        for (int slot = 2; slot < 8; slot++)
        {
            Assert.Equal(slot, Call(Threadmgr, "NID_B645C7EF", 0x81000001u + (uint)slot, 0xAu, 0xBu));
        }

        Assert.Equal(NoFreeSlot, Call(Threadmgr, "NID_B645C7EF", 1u, 2u, 3u));
        uint globals = (uint)Invoke(Threadmgr, "NID_19440C83")!;
        Assert.Equal(0x81000005u, GuestMemory.R32(globals + 0x2260 + 4 * 0x14 + 0xC));
        Assert.Equal(NotTaken, Call(Threadmgr, "NID_8C3C1F78", 1));
        Assert.Equal(NotTaken, Call(Threadmgr, "NID_8C3C1F78", 8));
        GuestMemory.W32(globals + 0x2260 + 4 * 0x14 + 8, 1); // the scheduler is in its handler
        Assert.Equal(Busy, Call(Threadmgr, "NID_8C3C1F78", 4));
        GuestMemory.W32(globals + 0x2260 + 4 * 0x14 + 8, 0);
        Assert.Equal(0, Call(Threadmgr, "NID_8C3C1F78", 4));
        Assert.Equal(NotTaken, Call(Threadmgr, "NID_8C3C1F78", 4));
        Assert.Equal(4, Call(Threadmgr, "NID_B645C7EF", 1u, 2u, 3u));
    }
}
