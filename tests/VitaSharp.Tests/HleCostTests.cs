using VitaSharp.Core.Hle;

namespace VitaSharp.Tests;

/// <summary>
/// The emulated CPU cost charged per HLE call (<see cref="HleFunction.Cost"/>): the display's waits block in the
/// scheduler (a syscall and a context switch), its getters and setters are a kernel entry only. A vcount polling loop
/// (Wipeout 2048's flip callback) paid the wait's 12,000 cycles for every poll.
/// </summary>
public sealed class HleCostTests
{
    private static long CostOf(string name) => new HleFunction { Nid = 0, Name = name }.Cost;

    [Theory]
    [InlineData("sceDisplayGetVcount")]
    [InlineData("sceDisplayGetVcountInternal")]
    [InlineData("sceDisplayGetFrameBuf")]
    [InlineData("sceDisplaySetFrameBuf")]
    [InlineData("sceDisplayGetRefreshRate")]
    public void DisplayGettersAndSettersCostAKernelEntry(string name)
    {
        Assert.Equal(CostOf("sceKernelLockMutex"), CostOf(name));
    }

    [Theory]
    [InlineData("sceDisplayWaitVblankStart")]
    [InlineData("sceDisplayWaitVblankStartMultiCB")]
    [InlineData("sceDisplayWaitSetFrameBuf")]
    [InlineData("sceDisplayWaitSetFrameBufMulti")]
    public void DisplayWaitsCostASchedulerWait(string name)
    {
        Assert.Equal(CostOf("sceKernelWaitSema"), CostOf(name));
        Assert.True(CostOf(name) > CostOf("sceDisplayGetVcount"));
    }
}
