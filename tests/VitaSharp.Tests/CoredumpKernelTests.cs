using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceCoredumpForDriver (SceCoredump 3.74): the CAF contexts are the VSH bridge's (one table, sizes as arguments), the
/// IV comes from the random generator (16 bytes only), the crash report is ux0:data/crash_report.caf (removed by the
/// delete), and dump requests are refused without the QA flag (0x80020008).
/// </summary>
[Collection("guest")]
public sealed class CoredumpKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "coredumpk");

    public void Dispose() => _harness.Dispose();

    private VshBridge Bridge => _harness.Module<VshBridge>();

    [Fact]
    public void EveryExportHasAnEntry()
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Library == "SceCoredumpForDriver").ToList();
        Assert.Equal(21, bindings.Count);
        Assert.Equal(21, bindings.Select(b => b.Nid).Distinct().Count());
    }

    [Fact]
    public void KernelAndBridgeShareTheContexts()
    {
        int ctx = Call(Bridge, "ksceCoredumpCafContextCreate");
        Assert.Equal(0, Call(Bridge, "ksceCoredumpCafHeaderInit", ctx, 1u));
        uint data = _harness.Alloc(0x40);
        GuestMemory.Fill(data, 0x5A, 0x40);
        Assert.Equal(0, Call(Bridge, "ksceCoredumpCafHeaderTransform", ctx, data, 0x40u));
        uint kernelDigest = _harness.Alloc(0x20);
        Assert.Equal(0, Call(Bridge, "ksceCoredumpCafHeaderFinal", ctx, kernelDigest, 0x20u));

        // the bridge's calls on the same context give the same HMAC for the same bytes
        Assert.Equal(0, Call(Bridge, "vshCoredumpCafHeaderInit", ctx, 1u));
        Assert.Equal(0, Call(Bridge, "_vshCoredumpCafHeaderTransform", ctx, data, _harness.Word(0x40)));
        uint bridgeDigest = _harness.Alloc(0x20);
        Assert.Equal(0, Call(Bridge, "_vshCoredumpCafHeaderFinal", ctx, bridgeDigest, _harness.Word(0x20)));
        Assert.Equal(GuestMemory.ReadSpan(kernelDigest, 0x20).ToArray(), GuestMemory.ReadSpan(bridgeDigest, 0x20).ToArray());

        uint iv = _harness.Alloc(0x10);
        Assert.Equal(0, Call(Bridge, "ksceCoredumpCafSegmentInit", ctx, 2u, 1, 3u, iv, 0x10u));
        uint output = _harness.Alloc(0x40);
        Assert.Equal(0, Call(Bridge, "ksceCoredumpCafSegmentTransform", ctx, data, output, 0x40u));
        Assert.NotEqual(GuestMemory.ReadSpan(data, 0x40).ToArray(), GuestMemory.ReadSpan(output, 0x40).ToArray());
        Assert.Equal(0, Call(Bridge, "ksceCoredumpCafSegmentFinal", ctx, kernelDigest, 0x20u));
        Assert.Equal(0, Call(Bridge, "ksceCoredumpCafContextDestroy", ctx));
        Assert.Equal(SceError.UNKNOWN_UID, Call(Bridge, "ksceCoredumpCafContextDestroy", ctx));
        Assert.Equal(SceError.UNKNOWN_UID, Call(Bridge, "ksceCoredumpCafHeaderTransform", ctx, data, 0x10u));
    }

    [Fact]
    public void TheIvIsSixteenRandomBytes()
    {
        uint iv = _harness.Alloc(0x10);
        GuestMemory.Fill(iv, 0, 0x10);
        Assert.Equal(unchecked((int)0x80010016), Call(Bridge, "ksceCoredumpCafCreateIv", iv, 8u));
        Assert.Equal(unchecked((int)0x80010016), Call(Bridge, "ksceCoredumpCafCreateIv", 0u, 0x10u));
        Assert.Equal(0, Call(Bridge, "ksceCoredumpCafCreateIv", iv, 0x10u));
        Assert.Contains(GuestMemory.ReadSpan(iv, 0x10).ToArray(), b => b != 0);
    }

    [Fact]
    public void TheCrashReportIsRemovedByBothCalls()
    {
        string? host = _harness.Emu.Vfs.Resolve("ux0:data/crash_report.caf", true);
        Assert.NotNull(host);
        Directory.CreateDirectory(Path.GetDirectoryName(host!)!);
        File.WriteAllText(host!, "caf");
        Assert.Equal(0, Call(Bridge, "ksceCoredumpDeleteCrashReportCaf"));
        Assert.False(File.Exists(host));
        Assert.Equal(SceError.IO_ENOENT, Call(Bridge, "ksceCoredumpDeleteCrashReportCaf"));
        File.WriteAllText(host!, "caf");
        Assert.Equal(0, Call(Bridge, "vshCoredumpDeleteCrashReportCaf"));
        Assert.False(File.Exists(host));
    }

    [Fact]
    public void DumpsAreRefusedAndTheSessionIsKept()
    {
        Assert.Equal(SceError.ILLEGAL_PERMISSION, Call(Bridge, "ksceCoredumpCreateDump", 0, 0u, 0u, 0u, 0u));
        Assert.Equal(unchecked((int)0x80010016), Call(Bridge, "ksceCoredump_A7D214A7", 0, 0u, 0u, 0u)); // no request
        Assert.Equal(unchecked((int)0x80010069), Call(Bridge, "ksceCoredump_EF20949F", 0u));
        Assert.Equal(1, Call(Bridge, "ksceCoredump_097AA37D"));
        int session = Call(Bridge, "ksceCoredump_13EF8516", 0u);
        Assert.True(session > 0);
        Assert.Equal(0, Call(Bridge, "ksceCoredump_12392973", session, 0u, 0u, 0u));
        Assert.Equal(0, Call(Bridge, "ksceCoredump_10863B61", session));
        Assert.Equal(0, Call(Bridge, "ksceCoredumpCafInit"));
        Assert.Equal(0, Call(Bridge, "ksceCoredumpCafFinal"));
    }

    /// <summary>FW 3.74 SceCoredump checks a dump request before it queues it (size word, the name and data buffers of
    /// a 0x34-byte request, the dump level 0/1/2/10 and its buffer, then the target): an invalid one is 0x80010016,
    /// a valid one is refused because no core file is kept (0x80020008). Before, every request was 0x80020008.</summary>
    [Theory]
    [InlineData("ksceCoredump_A7D214A7")]
    [InlineData("ksceCoredump_D064F6DC")]
    public void DumpRequestsAreCheckedAsTheFirmware(string name)
    {
        const int Invalid = unchecked((int)0x80010016);
        uint request = _harness.Alloc(0x40);
        uint buffer = _harness.Alloc(0x10);
        void Request(uint size, uint level = 0, params (int Word, uint Value)[] words)
        {
            GuestMemory.Fill(request, 0, 0x40);
            GuestMemory.W32(request, size);
            GuestMemory.W32(request + 8, level);
            foreach ((int word, uint value) in words)
            {
                GuestMemory.W32(request + (uint)word * 4, value);
            }
        }

        Request(3);
        Assert.Equal(Invalid, Call(Bridge, name, 0, 0u, buffer, request));
        Request(4);
        Assert.Equal(Invalid, Call(Bridge, name, 0, 0u, 0u, request));             // no target
        Assert.Equal(SceError.ILLEGAL_PERMISSION, Call(Bridge, name, 0, 0u, buffer, request));
        Request(0x14, 3);
        Assert.Equal(Invalid, Call(Bridge, name, 0, 0u, buffer, request));         // level 3
        Request(0x10, 3);
        Assert.Equal(SceError.ILLEGAL_PERMISSION, Call(Bridge, name, 0, 0u, buffer, request)); // too short to carry a level
        Request(0x14, 10, (3, 0x401), (4, buffer));
        Assert.Equal(Invalid, Call(Bridge, name, 0, 0u, buffer, request));         // level 10, more than 0x400
        Request(0x14, 10, (3, 0x10));
        Assert.Equal(Invalid, Call(Bridge, name, 0, 0u, buffer, request));         // level 10 without its buffer
        Request(0x14, 10, (3, 0x10), (4, buffer));
        Assert.Equal(SceError.ILLEGAL_PERMISSION, Call(Bridge, name, 0, 0u, buffer, request));
        Request(0x34, 2, (5, 0xB), (6, buffer));
        Assert.Equal(Invalid, Call(Bridge, name, 0, 0u, buffer, request));         // 11 names
        Request(0x34, 2, (5, 1));
        Assert.Equal(Invalid, Call(Bridge, name, 0, 0u, buffer, request));         // names without their buffer
        Request(0x34, 2, (7, 0x81), (8, buffer));
        Assert.Equal(Invalid, Call(Bridge, name, 0, 0u, buffer, request));         // more than 0x80 bytes of data
        Request(0x34, 2, (5, 1), (6, buffer), (7, 0x80), (8, buffer));
        Assert.Equal(SceError.ILLEGAL_PERMISSION, Call(Bridge, name, 0, 0u, buffer, request));
    }
}
