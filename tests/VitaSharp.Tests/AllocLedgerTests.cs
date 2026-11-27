using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>The "PERF allocators" line (Util.AllocLedger): who allocates managed memory per frame, on the phone too.</summary>
public sealed class AllocLedgerTests
{
    [Fact]
    public void TheBusiestSitesComeFirstPerFrameWithTheRestUnattributed()
    {
        var before = new Dictionary<string, long> { ["HLE sceGxmDraw"] = 1000, ["guest code (CPU backend)"] = 0 };
        var now = new Dictionary<string, long> { ["HLE sceGxmDraw"] = 1000 + 10 * 2048, ["guest code (CPU backend)"] = 10 * 4096, ["GPU worker DrawCommand"] = 10 * 1024 };

        string line = AllocLedger.Format(10, 10 * 10240, now, before, 8);

        Assert.Equal("PERF allocators: 10.0 KB/frame over 10 frames; guest code (CPU backend) 4.0, HLE sceGxmDraw 2.0, GPU worker DrawCommand 1.0; not attributed (other threads) 3.0", line);
    }

    [Fact]
    public void ASiteKeepsWhatItsThreadAllocates()
    {
        var site = AllocLedger.Site("test site " + Guid.NewGuid());
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(new byte[10_000]);
        AllocLedger.Add(site, GC.GetAllocatedBytesForCurrentThread() - a0);

        Assert.InRange(site.Value, 10_000, 11_000);
    }
}
