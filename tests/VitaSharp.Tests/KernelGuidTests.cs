using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSysmemForDriver's object (GUID) calls as the FW 3.74 SceSysmem has them: the GUID form check, the missing
/// output pointer, the class and body of kernel-defined and HLE objects, names (read, renamed, found by name) and
/// references limited by level.
/// </summary>
[Collection("guest")]
public sealed class KernelGuidTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "kguid");

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    private int S(string function, params object?[] args) => Call(Sysmem, function, args);

    /// <summary>A kernel class (SceClass, 0x2C bytes) made with ksceKernelCreateClass.</summary>
    private uint NewClass(string name)
    {
        uint cls = _harness.Alloc(0x30);
        Assert.Equal(0, S("ksceKernelCreateClass", cls, _harness.Str(name), 0u, 0x10u, 0u, 0u));
        return cls;
    }

    [Fact]
    public void AGuidOfTheWrongFormOrAMissingPointerIsRefused()
    {
        uint out_ = _harness.Word(0);
        Assert.Equal(SceError.INVALID_UID, S("ksceGUIDGetObject", 2, out_));
        Assert.Equal(SceError.INVALID_UID, S("ksceGUIDGetName", -3, out_));
        Assert.Equal(SceError.INVALID_UID, S("ksceGUIDGetClass", 0, out_));
        int block = S("ksceKernelAllocMemBlock", _harness.Str("guid-block"), 0x0C20D060u, 0x1000u, 0u);
        Assert.True(block > 0);
        Assert.Equal(SceError.INVALID_ARGUMENT, S("ksceGUIDGetObject", block, 0u));
        Assert.Equal(SceError.INVALID_ARGUMENT, S("ksceGUIDGetName", block, 0u));
        Assert.Equal(SceError.INVALID_ARGUMENT, S("ksceGUIDReferObjectWithClassLevel", block, 0u, 8u, out_));
    }

    [Fact]
    public void KernelObjectsHaveTheirClassBodyAndName()
    {
        _harness.OnGuest(_ =>
        {
            uint cls = NewClass("SceTestClass");
            uint objOut = _harness.Word(0);
            int guid = S("ksceGUIDKernelCreate", cls, _harness.Str("SceTestObject"), objOut);
            Assert.True(guid > 0);
            uint body = GuestMemory.R32(objOut);
            uint out_ = _harness.Word(0);
            Assert.Equal(0, S("ksceGUIDGetClass", guid, out_));
            Assert.Equal(cls, GuestMemory.R32(out_));
            Assert.Equal(0, S("ksceGUIDGetObject", guid, out_));
            Assert.Equal(body, GuestMemory.R32(out_));
            Assert.Equal(0, S("ksceGUIDGetName", guid, out_));
            Assert.Equal("SceTestObject", GuestMemory.ReadCString(GuestMemory.R32(out_)));
            Assert.Equal("SceTestObject", GuestMemory.ReadCString((uint)Invoke(Sysmem, "ksceGUIDName", guid)!));
            Assert.Equal(0u, (uint)Invoke(Sysmem, "ksceGUIDName", guid + 2000)!);

            Assert.Equal(0, S("ksceGUIDSetName", guid, _harness.Str("SceRenamed")));
            Assert.Equal("SceRenamed", GuestMemory.ReadCString((uint)Invoke(Sysmem, "ksceGUIDName", guid)!));
            Assert.Equal(guid, S("ksceGUIDOpenByName", _harness.Str("SceRenamed")));
            Assert.Equal(SceError.UID_CANNOT_FIND_BY_NAME, S("ksceGUIDOpenByName", _harness.Str("SceTestObject")));
            Assert.Equal(SceError.INVALID_ARGUMENT, S("ksceGUIDOpenByName", 0u));

            // a reference with the class (or a subclass) at a level below 8
            Assert.Equal(0, S("ksceGUIDReferObjectWithClassLevel", guid, cls, 7u, out_));
            Assert.Equal(body, GuestMemory.R32(out_));
            Assert.Equal(0, S("ksceGUIDReferObjectWithSubclass", guid, cls, out_));
            Assert.Equal(SceError.UID_NOT_OF_SUBCLASS, S("ksceGUIDReferObjectWithSubclass", guid, NewClass("SceOther"), out_));
        });
    }

    /// <summary>The HLE's objects have the class sysmem gives the firmware's own: a memory block the memory block
    /// class (SceUIDMemBlockClass, which derives from the root), any other HLE object (here a semaphore) the root
    /// class.</summary>
    [Fact]
    public void HleObjectsHaveTheFirmwaresClass()
    {
        uint root = (uint)Invoke(Sysmem, "ksceKernelGetUidClass")!;
        uint memBlockClass = (uint)Invoke(Sysmem, "ksceKernelGetUIDMemBlockClass")!;
        Assert.NotEqual(root, memBlockClass);

        int block = S("ksceKernelAllocMemBlock", _harness.Str("root-block"), 0x0C20D060u, 0x1000u, 0u);
        uint out_ = _harness.Word(0);
        Assert.Equal(0, S("ksceGUIDGetClass", block, out_));
        Assert.Equal(memBlockClass, GuestMemory.R32(out_));
        Assert.Equal(0, S("kscePUIDGetClass", 0, block, out_));
        Assert.Equal(memBlockClass, GuestMemory.R32(out_));
        Assert.Equal(0, S("kscePUIDGetObject", 0, block, out_));
        Assert.NotEqual(0u, GuestMemory.R32(out_));
        // the memory block class is a subclass of the root
        Assert.Equal(0, S("ksceGUIDReferObjectWithSubclass", block, root, out_));

        int sema = Call(_harness.Module<ThreadMgr>(), "sceKernelCreateSema", "root-sema", 0u, 0, 1, 0u);
        Assert.True(sema > 0);
        Assert.Equal(0, S("ksceGUIDGetClass", sema, out_));
        Assert.Equal(root, GuestMemory.R32(out_));
    }
}
