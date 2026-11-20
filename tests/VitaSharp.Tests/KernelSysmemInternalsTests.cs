using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The rest of SceSysmemForKernel (FW 3.74): the class of the HLE's memory blocks and heaps, the UID vectors and entry
/// information walks, the module's hash, the physical memory partitions and pools, the address spaces processmgr
/// creates, the name heap, the fixed heaps, the heap information and the UID entry heaps.
/// </summary>
[Collection("guest")]
public sealed class KernelSysmemInternalsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint InvalidArgument = 0x80020005, BadSize = 0x80020009, PhyMemPartInvalid = 0x8002430B;
    private const uint AllocMemBlock = 0xC94850C9, MemBlockClass = 0x86681B64, KernelRw = 0x1020D006;

    private readonly HleHarness _harness = new(fixture, "ksysmemi");

    public void Dispose() => _harness.Dispose();

    private unsafe uint R0(GuestThread thread, uint nid, params uint[] args)
    {
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not implemented");
        for (int i = 0; i < 4; i++)
        {
            thread.Core.State->R[i] = i < args.Length ? args[i] : 0xA5A5A5A5;
        }

        uint stack = thread.Core.State->R[13];
        thread.Core.State->R[13] = stack - 0x40;
        for (int i = 4; i < args.Length; i++)
        {
            GuestMemory.W32(stack - 0x40 + (uint)(i - 4) * 4, args[i]);
        }

        implementation(thread.Hle);
        thread.Core.State->R[13] = stack;
        return thread.Core.State->R[0];
    }

    private uint Sized(uint size)
    {
        uint record = _harness.Alloc(size);
        GuestMemory.W32(record, size);
        return record;
    }

    [Fact]
    public void MemoryBlocksAreObjectsOfTheMemoryBlockClass()
    {
        _harness.OnGuest(t =>
        {
            uint block = R0(t, AllocMemBlock, _harness.Str("SceClassBlock"), KernelRw, 0x1000, 0);
            uint memBlockClass = R0(t, MemBlockClass);
            uint classOut = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0xC74B0152, block, classOut)); // ksceGUIDGetClass
            Assert.Equal(memBlockClass, GuestMemory.R32(classOut));
            uint objectOut = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0x0E43E113, block, memBlockClass, objectOut)); // ksceGUIDGetObjectWithClass
            Assert.Equal(R0(t, 0xFA6C098C, block), GuestMemory.R32(objectOut));
            Assert.Equal(0u, R0(t, 0x00ED6C14, block, memBlockClass, objectOut)); // ksceGUIDReferObjectWithClass
        });
    }

    [Fact]
    public void UidVectorsAndEntryInfoListTheObjectsOfAClass()
    {
        _harness.OnGuest(t =>
        {
            uint first = R0(t, AllocMemBlock, _harness.Str("SceVectorA"), KernelRw, 0x1000, 0);
            uint second = R0(t, AllocMemBlock, _harness.Str("SceVectorB"), KernelRw, 0x1000, 0);
            uint memBlockClass = R0(t, MemBlockClass);
            uint vector = _harness.Alloc(0x400);
            uint count = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0x52137FA3, memBlockClass, 0, vector, 0x100, count));
            List<uint> uids = new();
            for (uint i = 0; i < GuestMemory.R32(count); i++)
            {
                uids.Add(GuestMemory.R32(vector + i * 4));
            }

            Assert.Contains(first, uids);
            Assert.Contains(second, uids);
            Assert.Equal(0u, R0(t, 0x52137FA3, memBlockClass, 0, vector, 1, count));
            Assert.Equal(1u, GuestMemory.R32(count));
            Assert.Equal(0u, R0(t, 0x52137FA3, 0, 0, vector, 0x100, count));
            Assert.Equal(0u, GuestMemory.R32(count));
            Assert.Equal(InvalidArgument, R0(t, 0x08C05493, (uint)KernelSpace.KernelPid, memBlockClass, 0, vector, 4, count));

            Assert.Equal(InvalidArgument, R0(t, 0x71E934DB, memBlockClass, 0, 0, vector, 0xFFFFFFFF, count));
            Assert.Equal(InvalidArgument, R0(t, 0x71E934DB, memBlockClass, 0, 0, 0, 1, count));
            uint infos = _harness.Alloc(0x24 * 0x40);
            Assert.Equal(0u, R0(t, 0x71E934DB, memBlockClass, 0, 0, infos, 0x40, count));
            bool found = false;
            for (uint i = 0; i < GuestMemory.R32(count); i++)
            {
                uint info = infos + i * 0x24;
                Assert.Equal(0x24u, GuestMemory.R32(info));
                Assert.Equal(memBlockClass, GuestMemory.R32(info + 0xC));
                found |= GuestMemory.R32(info + 4) == first;
            }

            Assert.True(found);
            uint entry = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0xFF464A49, first, entry));
            Assert.Equal(first, GuestMemory.R32(GuestMemory.R32(entry) + 0x10));
            Assert.Equal(0x80024501u, R0(t, 0xFF464A49, 2, entry));
        });
    }

    [Fact]
    public void TheHashIsMurmur3WithTheFirmwaresTail()
    {
        _harness.OnGuest(t =>
        {
            Assert.Equal(0xBA6BD213u, R0(t, 0xEB48682E, _harness.Str("test"), 4, 0)); // MurmurHash3_x86_32 vectors
            Assert.Equal(0x514E28B7u, R0(t, 0xEB48682E, _harness.Str(""), 0, 1));
            Assert.Equal(0u, R0(t, 0xEB48682E, _harness.Str(""), 0, 0));
            Assert.NotEqual(R0(t, 0xEB48682E, _harness.Str("ab"), 2, 0), R0(t, 0xEB48682E, _harness.Str("ba"), 2, 0));
        });
    }

    [Fact]
    public void PhysicalPartitionsHoldChunksOfMainMemory()
    {
        _harness.OnGuest(t =>
        {
            uint kd = R0(t, 0x0164D817);
            Assert.NotEqual(0u, kd);
            Assert.Equal("ScePhyMemPartKD", GuestMemory.ReadCString(kd + 0x8C));
            Assert.Equal(0u, R0(t, 0x0B1B4ACA)); // a retail unit has no tool partition

            uint partOut = _harness.Word(0);
            Assert.Equal(InvalidArgument, R0(t, 0xA2DFA383, _harness.Str("ScePhyMemPartTest"), 0x10000, 0x10000, 0));
            Assert.Equal(0x80020001u, R0(t, 0xA2DFA383, _harness.Str("ScePhyMemPartTest"), 0x50000, 0x10000, partOut));
            Assert.Equal(0u, R0(t, 0xA2DFA383, _harness.Str("ScePhyMemPartTest"), 0x10000, 0x10000, partOut));
            uint part = GuestMemory.R32(partOut);
            uint info = _harness.Alloc(0x10);
            Assert.Equal(0u, R0(t, 0xB9B69700, part, info));
            Assert.InRange(GuestMemory.R32(info), 0x40000000u, 0x5FFFFFFFu);
            Assert.Equal(0x10000u, GuestMemory.R32(info + 4));
            Assert.Equal(0u, R0(t, 0x5B434019, part, 0x4000));
            Assert.Equal(0u, R0(t, 0xB9B69700, part, info));
            Assert.Equal(0x14000u, GuestMemory.R32(info + 4));

            uint full = Sized(0xB0);
            Assert.Equal(BadSize, R0(t, 0x73E03A38, part, _harness.Alloc(0xB0)));
            Assert.Equal(0u, R0(t, 0x73E03A38, part, full));
            Assert.Equal(2u, GuestMemory.R32(full + 0x20));
            uint uid = GuestMemory.R32(part + 0x10);
            Assert.Equal(uid, GuestMemory.R32(full + 4));
            Assert.Equal("ScePhyMemPartTest", GuestMemory.ReadCString(GuestMemory.R32(full + 8)));
            uint byUid = Sized(0xB0);
            Assert.Equal(0u, R0(t, 0x029F5989, uid, byUid));
            Assert.Equal(0x14000u, GuestMemory.R32(byUid + 0x10));

            uint vector = _harness.Alloc(0x100);
            uint count = _harness.Word(0);
            Assert.Equal(InvalidArgument, R0(t, 0x021053DD, 0, vector, 0x100, 0));
            Assert.Equal(0u, R0(t, 0x021053DD, 0, vector, 0x100, count));
            Assert.Equal(2u, GuestMemory.R32(count));

            Assert.Equal(0u, R0(t, 0x3F72B2E7, part));
            Assert.Equal(PhyMemPartInvalid, R0(t, 0xB9B69700, part, info));
            Assert.Equal(PhyMemPartInvalid, R0(t, 0x36FDA794, part, 0, 0x1000));
            Assert.Equal(0u, R0(t, 0x021053DD, 0, vector, 0x100, count));
            Assert.Equal(1u, GuestMemory.R32(count));
        });
    }

    [Fact]
    public void PhysicalPoolsHandOutAlignedRangesAndTakeThemBack()
    {
        _harness.OnGuest(t =>
        {
            uint free = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0x05822B82, 0x10001, free));
            uint before = GuestMemory.R32(free);
            uint address = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0xEA98CCE1, 0x10001, 0x3000, 0x10000, address));
            Assert.Equal(0u, GuestMemory.R32(address) & 0xFFFF);
            Assert.Equal(0u, R0(t, 0x05822B82, 0x10001, free));
            Assert.Equal(before - 0x3000, GuestMemory.R32(free));
            Assert.Equal(0u, R0(t, 0x844DF3DA, 0x10001, GuestMemory.R32(address), 0x3000));
            Assert.Equal(0u, R0(t, 0x05822B82, 0x10001, free));
            Assert.Equal(before, GuestMemory.R32(free));
            Assert.Equal(0x80024C02u, R0(t, 0xEA98CCE1, 0x5, 0x1000, 0, address));
            Assert.Equal(0x80024C02u, R0(t, 0x05822B82, 0x20001, free));
            Assert.Equal(0u, R0(t, 0xEA98CCE1, 0x20001, 0x1000, 0, address));
            Assert.InRange(GuestMemory.R32(address), 0x20000000u, 0x27FFFFFFu);
        });
    }

    [Fact]
    public void AnAddressSpaceIsAUidOverTheProcesssRecord()
    {
        _harness.OnGuest(t =>
        {
            uint option = Sized(0x10);
            uint name = _harness.Str("SceTestAddressSpace");
            Assert.Equal(InvalidArgument, R0(t, 0x8EE89D2C, (uint)t.Pid, name, 0x30000002, option));
            Assert.Equal(InvalidArgument, R0(t, 0x8EE89D2C, (uint)t.Pid, name, 0x10000001, _harness.Alloc(0x10)));
            uint uid = R0(t, 0x8EE89D2C, (uint)t.Pid, name, 0x10000001, option);
            Assert.True((int)uid > 0);
            uint record = R0(t, 0xFA6C098C, uid);
            Assert.Equal(0x4D95AEECu, GuestMemory.R32(record + 0x16C));
            Assert.Equal((uint)t.Pid, GuestMemory.R32(record + 0x14));
            Assert.Equal(R0(t, 0x99D3BF76), GuestMemory.R32(record + 4));

            uint context = _harness.Word(0);
            Assert.Equal(InvalidArgument, R0(t, 0xA7C91D62, uid, 0));
            Assert.Equal(0u, R0(t, 0xA7C91D62, uid, context));
            Assert.NotEqual(0u, GuestMemory.R32(context));

            uint kd = R0(t, 0x0164D817);
            Assert.Equal(0u, R0(t, 0x7DF07D60, uid, 6, kd));
            uint info = Sized(0x654);
            Assert.Equal(BadSize, R0(t, 0xC3EF4055, uid, _harness.Alloc(0x654)));
            Assert.Equal(0u, R0(t, 0xC3EF4055, uid, info));
            Assert.Equal(uid, GuestMemory.R32(info + 4));
            Assert.Equal(1u, GuestMemory.R32(info + 0xC));
            Assert.Equal(1u, GuestMemory.R32(info + 0x610));
            Assert.Equal(kd, GuestMemory.R32(info + 0x614 + 6 * 4));

            uint kernelSpace = R0(t, 0x2E5CC243);
            Assert.Equal(0x30000002u, GuestMemory.R32(kernelSpace + 0x10));
            Assert.Equal(GuestMemory.R32(kernelSpace + 0x1C), R0(t, 0x0B5A3E93, GuestMemory.KernelStart + 0x1000));

            Assert.Equal(0u, R0(t, 0x4A2E421D, uid));
            Assert.Equal(0x80024501u, R0(t, 0xA7C91D62, uid, context));
        });
    }

    [Fact]
    public void TheNameHeapKeepsOneReferencedCopyOfEachName()
    {
        _harness.OnGuest(t =>
        {
            uint name = _harness.Str("SceSharedName");
            uint copyOut = _harness.Word(0);
            Assert.Equal(0x80024000u, R0(t, 0x40965FD1, name, 0, copyOut));
            Assert.Equal(0u, GuestMemory.R32(copyOut));
            Assert.Equal(0u, R0(t, 0xB628B46B, name, 0, copyOut));
            uint copy = GuestMemory.R32(copyOut);
            Assert.Equal("SceSharedName", GuestMemory.ReadCString(copy));
            Assert.Equal(0u, R0(t, 0xB628B46B, name, 0, copyOut));
            Assert.Equal(copy, GuestMemory.R32(copyOut));
            Assert.Equal(0u, R0(t, 0x40965FD1, name, 0, copyOut));
            Assert.Equal(copy, GuestMemory.R32(copyOut));
            Assert.Equal(InvalidArgument, R0(t, 0x40965FD1, 0, 0, copyOut));

            uint info = Sized(0x14);
            Assert.Equal(0u, R0(t, 0x01194C2E, info));
            Assert.True(GuestMemory.R32(info + 4) >= 1);
            Assert.Equal(InvalidArgument, R0(t, 0x01194C2E, _harness.Alloc(0x14)));

            Assert.Equal(0u, R0(t, 0x634D0D0C, copy, 0));
            Assert.Equal(0u, R0(t, 0x40965FD1, name, 0, copyOut));
            Assert.Equal(0u, R0(t, 0x634D0D0C, copy, 0));
            Assert.Equal(0x80024000u, R0(t, 0x40965FD1, name, 0, copyOut));
            Assert.Equal(0x80024000u, R0(t, 0x634D0D0C, copy, 0));
        });
    }

    [Fact]
    public void FixedHeapsHandOutItemsOfTheirSize()
    {
        _harness.OnGuest(t =>
        {
            uint heap = R0(t, 0x3717C322, 0x40);
            Assert.NotEqual(0u, heap);
            Assert.Equal(heap, R0(t, 0x3717C322, 0x40));
            Assert.Equal(0u, R0(t, 0x3717C322, 0x50));
            uint item = R0(t, 0xABA5313C, heap);
            Assert.NotEqual(0u, item);
            uint info = Sized(0x90);
            Assert.Equal(BadSize, R0(t, 0xA24D2C8D, heap, _harness.Alloc(0x90)));
            Assert.Equal(InvalidArgument, R0(t, 0xA24D2C8D, 0, info));
            Assert.Equal(0u, R0(t, 0xA24D2C8D, heap, info));
            Assert.Equal(0x40u, GuestMemory.R32(info + 4));
            Assert.Equal(1u, GuestMemory.R32(info + 0x10));
            Assert.Equal(0u, R0(t, 0x93344755, heap, item));
            Assert.Equal(0u, R0(t, 0xA24D2C8D, heap, info));
            Assert.Equal(0u, GuestMemory.R32(info + 0x10));
        });
    }

    [Fact]
    public void HeapInformationCountsTheHeapsAllocations()
    {
        _harness.OnGuest(t =>
        {
            uint heap = R0(t, 0x9328E0E8, _harness.Str("SceInfoHeap"), 0x10000, 0);
            uint first = R0(t, 0x7B4CB60A, heap, 0x40);
            R0(t, 0x7B4CB60A, heap, 0x20);
            uint info = Sized(0x70);
            Assert.Equal(BadSize, R0(t, 0x17DB1A0A, heap, 0, _harness.Alloc(0x70)));
            Assert.Equal(0u, R0(t, 0x17DB1A0A, heap, 0, info));
            Assert.Equal(heap, GuestMemory.R32(info + 8));
            Assert.Equal("SceInfoHeap", GuestMemory.ReadCString(GuestMemory.R32(info + 0xC)));
            Assert.Equal(0x10000u, GuestMemory.R32(info + 0x14));
            Assert.Equal(0x60u, GuestMemory.R32(info + 0x1C));
            uint pairs = _harness.Alloc(0x20);
            Assert.Equal(2u, R0(t, 0xBF70207B, heap, 0, 4, pairs));
            Assert.Equal(first, GuestMemory.R32(pairs));
            Assert.Equal(0x40u, GuestMemory.R32(pairs + 4));
            Assert.Equal(InvalidArgument, R0(t, 0xBF70207B, heap, 0, 4, 0));
            Assert.Equal(0x40u, R0(t, 0xD124075A, first));
            Assert.Equal(0u, R0(t, 0x82059651, heap));
            Assert.Equal(0x80024901u, R0(t, 0x82059651, 3));
        });
    }

    [Fact]
    public void AProcesssUidEntryHeapCountsItsHandles()
    {
        _harness.OnGuest(t =>
        {
            uint heap = R0(t, 0x9328E0E8, _harness.Str("SceEntryHeapHeap"), 0x10000, 0);
            uint entryHeap = R0(t, 0x89BEF058, (uint)t.Pid, heap);
            Assert.True((int)entryHeap > 0);
            Assert.Equal(0x80029001u, R0(t, 0x89BEF058, 0x7FFF0001, heap));
            uint info = Sized(0x18);
            Assert.Equal(InvalidArgument, R0(t, 0xCCD47B97, entryHeap, _harness.Alloc(0x18)));
            Assert.Equal(0u, R0(t, 0xCCD47B97, entryHeap, info));
            Assert.Equal(0x20u, GuestMemory.R32(info + 4));
            Assert.Equal(0u, R0(t, 0xCCD47B97, 0x10003, info));
            Assert.True(GuestMemory.R32(info + 0x10) > 0);
            Assert.Equal(0u, R0(t, 0xF6D09BD9, entryHeap));
            Assert.Equal(0x80024501u, R0(t, 0xCCD47B97, entryHeap, info));
        });
    }
}
