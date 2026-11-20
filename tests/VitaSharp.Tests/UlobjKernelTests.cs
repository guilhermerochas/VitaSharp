using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceUlobjMgr (3.74): the user side's revision and start checks and the process record the kernel entries find
/// (none before the start: 0x80010002); object lookups fail after their argument checks (0x80020005) with no kernel
/// object (0x8002000C), since libult keeps its objects in the process's own memory.
/// </summary>
[Collection("guest")]
public sealed class UlobjKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Argument = unchecked((int)0x80020005), NoRecord = unchecked((int)0x80010002);
    private const int NoObject = unchecked((int)0x8002000C);

    private readonly HleHarness _harness = new(fixture, "ulobjk");

    public void Dispose() => _harness.Dispose();

    private DebugPerf Debug => _harness.Module<DebugPerf>();

    [Fact]
    public void EveryExportHasAnEntry()
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Library == "SceUlobjMgrForDriver").ToList();
        Assert.Equal(19, bindings.Count);
        Assert.Equal(19, bindings.Select(b => b.Nid).Distinct().Count());
    }

    [Fact]
    public void TheUserSideChecksRevisionAndOrder()
    {
        Assert.Equal(Argument, Call(Debug, "_sceUlobjMgrRegisterLibultProtocolRevision", 0x1000001u));
        Assert.Equal(Argument, Call(Debug, "_sceUlobjMgrRegisterLibultProtocolRevision", 0x3600000u));
        Assert.Equal(0, Call(Debug, "_sceUlobjMgrRegisterLibultProtocolRevision", 0x3600011u));
        Assert.Equal(unchecked((int)0x80029001), Call(Debug, "_sceUlobjMgrStopSupportingUserlevelObject"));
        Assert.Equal(unchecked((int)0x80020004), Call(Debug, "_sceUlobjMgrStartSupportingUserlevelObject", 0x1000u, 0x3740001u, 0u));
        Assert.Equal(unchecked((int)0x8002710B), Call(Debug, "_sceUlobjMgrStartSupportingUserlevelObject", 0x2000001u, 0x3600011u, 0u));
    }

    [Fact]
    public void TheRecordExistsOnlyAfterTheStart()
    {
        uint words = _harness.Alloc(12);
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_A90A564C", 0, 0u, words + 4, words + 8));
        Assert.Equal(NoRecord, Call(Debug, "ksceUlobjMgr_A90A564C", 0, words, words + 4, words + 8));
        Assert.Equal(unchecked((int)0x80020004), Call(Debug, "ksceUlobjMgr_6F868221"));
        Assert.Equal(0, Call(Debug, "_sceUlobjMgrStartSupportingUserlevelObject", 0x1000u, 0x3600011u, 0u));
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_A90A564C", 0, words, words + 4, words + 8));
        Assert.Equal(0x3600011u, GuestMemory.R32(words));
        Assert.Equal(GuestMemory.R32(words + 8) + 0xC, GuestMemory.R32(words + 4));
        Assert.Equal(0x1000u, GuestMemory.R32(GuestMemory.R32(words + 4)));
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_A48A5B3C", GuestMemory.R32(words + 8)));
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_A48A5B3C", 0u));
        Assert.Equal(0, Call(Debug, "_sceUlobjMgrStopSupportingUserlevelObject"));
        Assert.Equal(NoRecord, Call(Debug, "ksceUlobjMgr_A90A564C", 0, words, words + 4, words + 8));
    }

    [Fact]
    public void ObjectLookupsFindNoKernelObject()
    {
        uint info = _harness.Alloc(0x100);
        uint count = _harness.Alloc(4);
        GuestMemory.W32(count, 0);
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_525A9BDB", 0, 0x81000000u, 0u));
        Assert.Equal(NoObject, Call(Debug, "ksceUlobjMgr_525A9BDB", 0, 0x81000000u, info));
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_6D2C8928", 0, 0x81000000u, info, 0u, count));
        Assert.Equal(NoObject, Call(Debug, "ksceUlobjMgr_6D2C8928", 0, 0x81000000u, info, 4u, count));
        GuestMemory.W32(count, 0xFFFFFFFF);
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_74B9199C", 0, 0x81000000u, info, 4u, count));
        Assert.Equal(NoObject, Call(Debug, "ksceUlobjMgr_CDAD91AD", 0, 0x81000000u, 1u));
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_BB11EB2F", 0, info, 4u, count, 1u));
        Assert.Equal(0u, GuestMemory.R32(count));
    }
}
