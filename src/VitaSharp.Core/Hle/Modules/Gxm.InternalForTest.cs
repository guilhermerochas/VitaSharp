using VitaSharp.Core.Gxm;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceGxmInternalForTest (libgxm_es4.suprx FW 3.74, read in the module, study only): the test entries of libgxm, NIDs
// without public names. Six set fields of an immediate context outside the public setters (two of them are the public
// sceGxmSetVisibilityBuffer / sceGxmSetYuvProfile without the scene check and the old-SDK leniency); one allocates and
// maps device memory ("devicemem", host / vertex USSE / fragment USSE) behind a small handle, which three getters read
// and one releases; four are empty.

public sealed partial class Gxm
{
    /// <summary>The kinds of NID_D412F27B: 0 or 1 host memory (1 in CDRAM, 256 KiB granules), 2 vertex USSE, 3
    /// fragment USSE.</summary>
    const uint TestMemoryCdram = 1, TestMemoryVertexUsse = 2, TestMemoryFragmentUsse = 3;

    /// <summary>The memblock types libgxm asks for: CDRAM read/write and main memory uncached read/write.</summary>
    const uint TestMemoryCdramType = 0x09408060, TestMemoryMainType = 0x0C208060;

    /// <summary>The 0x14-byte handle: +0 the memblock, +4 its base, +8 the USSE offset, +0xC the size asked, +0x10 the
    /// kind.</summary>
    const uint TestMemoryHandleSize = 0x14;

    /// <summary>The features libgxm 3.74 turns off for programs built with an SDK from 0x02500000 on (bit 13 of its
    /// compatibility word): before that, NID_D352F90A takes any mode and sceGxmSetYuvProfile any profile.</summary>
    const uint LenientSdkLimit = 0x02500000;

    bool LenientSdk
    {
        get
        {
            uint processParam = Emu.Linker.ProcessParam;
            uint sdk = processParam != 0 && GuestMemory.R32(processParam) >= 0x14 ? GuestMemory.R32(processParam + 0x10) : 0x03740011;
            return sdk < LenientSdkLimit;
        }
    }

    /// <summary>An immediate context for a test setter: NULL or unknown 0x805B0004, a deferred one 0x805B0003.</summary>
    GxmContext? TestContext(uint ctx, out int error)
    {
        GxmContext? context = Ctx(ctx);
        error = context == null ? ERR_INVALID_POINTER : context.Deferred ? ERR_INVALID_VALUE : 0;
        return error == 0 ? context : null;
    }

    /// <summary>NID_C65CED80(ctx, uint value): the context word at +0x530 (0x80 after sceGxmCreateContext); only bit 7
    /// may be set (else 0x805B0003).</summary>
    [Hle("NID_C65CED80", Nid = 0xC65CED80, Library = "SceGxmInternalForTest")]
    int GxmTestSetControl(uint ctx, uint value)
    {
        GxmContext? context = TestContext(ctx, out int error);
        if (context == null)
        {
            return error;
        }

        if ((value & ~0x80u) != 0)
        {
            return ERR_INVALID_VALUE;
        }

        context.TestControl = value;
        return 0;
    }

    /// <summary>NID_65FF05BC(ctx, uint low, int high): the two halves at +0x536 / +0x534 (0 and 1000 after
    /// sceGxmCreateContext): low at most 16, high 1..0x7FFF (else 0x805B0003).</summary>
    [Hle("NID_65FF05BC", Nid = 0x65FF05BC, Library = "SceGxmInternalForTest")]
    int GxmTestSetRange(uint ctx, uint low, int high)
    {
        GxmContext? context = TestContext(ctx, out int error);
        if (context == null)
        {
            return error;
        }

        if (low > 0x10 || (uint)(high - 1) >= 0x7FFF)
        {
            return ERR_INVALID_VALUE;
        }

        context.TestRangeLow = (ushort)low;
        context.TestRangeHigh = (ushort)high;
        return 0;
    }

    /// <summary>NID_46D6C306(ctx, int a, int b): two signed bytes (-128..127) packed a &lt;&lt; 8 | b at +0x538 (0xE9
    /// after sceGxmCreateContext). A value out of range is 0x805B0004, as a NULL context.</summary>
    [Hle("NID_46D6C306", Nid = 0x46D6C306, Library = "SceGxmInternalForTest")]
    int GxmTestSetBytePair(uint ctx, int a, int b)
    {
        GxmContext? context = Ctx(ctx);
        if (context != null && context.Deferred)
        {
            return ERR_INVALID_VALUE;
        }

        if (context == null || (uint)(a + 0x80) >= 0x100 || (uint)(b + 0x80) >= 0x100)
        {
            return ERR_INVALID_POINTER;
        }

        context.TestBytePair = (uint)((a & 0xFF) << 8 | (b & 0xFF));
        return 0;
    }

    /// <summary>NID_D352F90A(ctx, uint mode, uint value): the words at +0x53C / +0x540 (1 and 0 after
    /// sceGxmCreateContext). A value needs mode 1, the value below 0x400, and the mode below 2 for programs of a recent
    /// SDK (else 0x805B0003).</summary>
    [Hle("NID_D352F90A", Nid = 0xD352F90A, Library = "SceGxmInternalForTest")]
    int GxmTestSetMode(uint ctx, uint mode, uint value)
    {
        GxmContext? context = TestContext(ctx, out int error);
        if (context == null)
        {
            return error;
        }

        if ((mode != 1 && value != 0) || value >= 0x400 || (mode >= 2 && !LenientSdk))
        {
            return ERR_INVALID_VALUE;
        }

        context.TestMode = mode;
        context.TestModeValue = value;
        return 0;
    }

    /// <summary>NID_1D68FB73(ctx, void *base, uint stride): sceGxmSetVisibilityBuffer without its scene check: both
    /// 16-byte aligned (else 0x805B0005).</summary>
    [Hle("NID_1D68FB73", Nid = 0x1D68FB73, Library = "SceGxmInternalForTest")]
    int GxmTestSetVisibilityBuffer(uint ctx, uint bufferBase, uint stride)
    {
        GxmContext? context = TestContext(ctx, out int error);
        if (context == null)
        {
            return error;
        }

        if ((bufferBase & (VisibilityAlignment - 1)) != 0 || (stride & (VisibilityAlignment - 1)) != 0)
        {
            return ERR_INVALID_ALIGNMENT;
        }

        context.VisBuffer = bufferBase;
        context.VisStride = stride;
        return 0;
    }

    /// <summary>NID_6E9565D5(ctx, uint index, uint profile): sceGxmSetYuvProfile without its scene check and without the
    /// old-SDK leniency: index 0..1, profile 0..3 (else 0x805B0003).</summary>
    [Hle("NID_6E9565D5", Nid = 0x6E9565D5, Library = "SceGxmInternalForTest")]
    int GxmTestSetYuvProfile(uint ctx, uint index, uint profile)
    {
        GxmContext? context = TestContext(ctx, out int error);
        if (context == null)
        {
            return error;
        }

        if (index >= 2 || profile >= 4)
        {
            return ERR_INVALID_VALUE;
        }

        context.YuvProfile[index] = (byte)profile;
        return 0;
    }

    /// <summary>NID_D412F27B(uint kind, uint attribs, uint size, uint alignment, void **handle): a "devicemem" memblock
    /// (CDRAM in 256 KiB granules for kind 1, else uncached main memory in pages) mapped for the GPU: as host memory
    /// with <paramref name="attribs"/> (kinds 0 and 1), or as vertex / fragment USSE memory (2, 3), the offset kept.
    /// Kind above 3 or attribs outside 1..3: 0x805B0003; alignment not a power of two: 0x805B0005; NULL handle:
    /// 0x805B0004. The memblock and mapping errors pass through.</summary>
    [Hle("NID_D412F27B", Nid = 0xD412F27B, Library = "SceGxmInternalForTest")]
    int GxmTestAllocDeviceMemory(uint kind, uint attribs, uint size, uint alignment, uint handleOut)
    {
        if (kind > 3 || (attribs & ~3u) != 0 || attribs == 0)
        {
            return ERR_INVALID_VALUE;
        }

        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            return ERR_INVALID_ALIGNMENT;
        }

        if (handleOut == 0)
        {
            return ERR_INVALID_POINTER;
        }

        uint rounded = kind == TestMemoryCdram ? (size + 0x3FFFF) & ~0x3FFFFu : (size + 0xFFF) & ~0xFFFu;
        uint handle = AllocObj(TestMemoryHandleSize, "SceGxmInternalForTest memory");
        GuestMemory.W32(handle + 0xC, size);
        SysMem sysmem = Emu.Hle.Modules.OfType<SysMem>().First();
        int block = sysmem.AllocBlock("devicemem", kind == TestMemoryCdram ? TestMemoryCdramType : TestMemoryMainType, rounded,
            kind == TestMemoryCdram ? 0x40000u : 0x1000u);
        GuestMemory.W32(handle, (uint)block);
        if (block < 0)
        {
            return block;
        }

        uint baseAddress = K.Objects.Get<MemBlock>(block)!.Address;
        GuestMemory.W32(handle + 4, baseAddress);
        GuestMemory.W32(handle + 0x10, kind);
        int result = kind switch
        {
            TestMemoryVertexUsse => sceGxmMapVertexUsseMemoryThroughEntry(baseAddress, rounded, new Ptr<uint>(handle + 8)),
            TestMemoryFragmentUsse => sceGxmMapFragmentUsseMemoryThroughEntry(baseAddress, rounded, new Ptr<uint>(handle + 8)),
            _ => sceGxmMapMemoryThroughEntry(baseAddress, rounded, attribs),
        };
        if (result != 0)
        {
            return result;
        }

        GuestMemory.W32(handleOut, handle);
        return 0;
    }

    /// <summary>NID_2B0A5A39(handle): unmaps the memory of a NID_D412F27B handle (as its kind was mapped), frees the
    /// memblock and the handle. 0, also for NULL.</summary>
    [Hle("NID_2B0A5A39", Nid = 0x2B0A5A39, Library = "SceGxmInternalForTest")]
    int GxmTestFreeDeviceMemory(uint handle)
    {
        if (handle == 0)
        {
            return 0;
        }

        uint baseAddress = GuestMemory.R32(handle + 4);
        switch (GuestMemory.R32(handle + 0x10))
        {
            case TestMemoryVertexUsse:
                sceGxmUnmapVertexUsseMemoryThroughEntry(baseAddress);
                break;
            case TestMemoryFragmentUsse:
                sceGxmUnmapFragmentUsseMemoryThroughEntry(baseAddress);
                break;
            default:
                sceGxmUnmapMemoryThroughEntry(baseAddress);
                break;
        }

        Emu.Hle.Modules.OfType<SysMem>().First().sceKernelFreeMemBlockThroughEntry((int)GuestMemory.R32(handle));
        Emu.Memory.Free(handle);
        return 0;
    }

    /// <summary>NID_7C427319(handle): the memory's base address (0 for NULL).</summary>
    [Hle("NID_7C427319", Nid = 0x7C427319, Library = "SceGxmInternalForTest", ReturnsValue = true)]
    uint GxmTestDeviceMemoryBase(uint handle)
    {
        return handle == 0 ? 0 : GuestMemory.R32(handle + 4);
    }

    /// <summary>NID_588FD7FD / NID_3D8863E2(handle): the USSE offset of the memory (0 for NULL, or host memory).</summary>
    [Hle("NID_588FD7FD", Nid = 0x588FD7FD, Library = "SceGxmInternalForTest", ReturnsValue = true)]
    [Hle("NID_3D8863E2", Nid = 0x3D8863E2, Library = "SceGxmInternalForTest", ReturnsValue = true)]
    uint GxmTestDeviceMemoryOffset(uint handle)
    {
        return handle == 0 ? 0 : GuestMemory.R32(handle + 8);
    }

    /// <summary>NID_9C7DC365, NID_9CAFAE4B, NID_631E95AA, NID_9F4A526F: empty in libgxm 3.74 (they return at once and
    /// leave r0 as it came).</summary>
    [Hle("NID_9C7DC365", Nid = 0x9C7DC365, Library = "SceGxmInternalForTest")]
    [Hle("NID_9CAFAE4B", Nid = 0x9CAFAE4B, Library = "SceGxmInternalForTest")]
    [Hle("NID_631E95AA", Nid = 0x631E95AA, Library = "SceGxmInternalForTest")]
    [Hle("NID_9F4A526F", Nid = 0x9F4A526F, Library = "SceGxmInternalForTest")]
    void GxmTestEmpty()
    {
    }
}
