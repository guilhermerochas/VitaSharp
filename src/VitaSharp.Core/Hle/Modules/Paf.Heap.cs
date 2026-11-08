using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// paf::memory::HeapAllocator ({ vtable, heap id +4, base +8, size +0xC, name[32] +0x10, attached +0x30, flags +0x31,
/// mutex +0x34, memory block +0x54, block type +0x58 }: an SceClib mspace over the caller's memory, or over a block
/// of its own when no memory is given), the global allocator sce_paf_malloc uses, and the paf buffers ({ vtable,
/// address +4, size +8 }; a const buffer keeps its address at +0xC) handed out as common::SharedPtr.
/// </summary>
public sealed partial class Paf
{
    Clib? _clib;
    Clib Cl => _clib ??= Emu.Hle.Modules.OfType<Clib>().First();
    uint _globalHeap;

    uint HeapVtable => VTable("HeapAllocator",
        HostSlot("Heap.GetType", c => c.Return(0)), 0xD9E9BEC8, 0x68B7420B, 0xD4358B8E, 0xC32ECD73, 0x25238A3A, 0x79F13D72);

    [Hle("_ZN3paf6memory13HeapAllocatorC1EPvjPKcPKNS1_6OptionE")]
    [Hle("_ZN3paf6memory13HeapAllocatorC2EPvjPKcPKNS1_6OptionE")]
    uint HeapCtor(uint me, uint addr, uint size, uint name, uint option)
    {
        GuestMemory.Fill(me + 4, 0, 0x58);
        U(me, HeapVtable);
        bool attached = addr != 0;
        if (!attached) addr = Emu.Memory.Alloc(size, "paf heap " + CRead(name));
        U(me + 4, Cl.sceClibMspaceCreate(addr, size));
        U(me + 8, addr); U(me + 0xC, size);
        GuestMemory.WriteCString(me + 0x10, CRead(name), 31);
        B(me + 0x30, (byte)(attached ? 1 : 0));
        if (option != 0) { B(me + 0x31, B(option + 8)); B(me + 0x32, B(option + 9)); U(me + 0x58, U(option + 0xC)); }
        return me;
    }

    [Hle("_ZN3paf6memory13HeapAllocatorD1Ev")]
    [Hle("_ZN3paf6memory13HeapAllocatorD2Ev")]
    uint HeapDtor(uint me)
    {
        if (U(me + 4) != 0) Cl.sceClibMspaceDestroy(U(me + 4));
        if (B(me + 0x30) == 0 && U(me + 8) != 0) Emu.Memory.Free(U(me + 8));
        U(me + 4, 0);
        return me;
    }

    [Hle("_ZN3paf6memory13HeapAllocatorD0Ev")] void HeapDelete(uint me) { HeapDtor(me); Free(me); }

    /// <summary>The global allocator (heap id 0) is the HLE heap the C library and sce_paf_malloc share.</summary>
    bool IsGlobal(uint me) => me == 0 || U(me + 4) == 0;

    [Hle("_ZN3paf6memory13HeapAllocator14sce_paf_mallocEj")]
    [Hle("_ZN3paf6memory13HeapAllocator16sce_paf_malloc_dEjPKcii")]
    uint HeapMalloc(uint me, uint size) => IsGlobal(me) ? Alloc(size) : Cl.sceClibMspaceMalloc(U(me + 4), size);

    [Hle("_ZN3paf6memory13HeapAllocator16sce_paf_memalignEjj")]
    [Hle("_ZN3paf6memory13HeapAllocator18sce_paf_memalign_dEjjPKcii")]
    uint HeapMemalign(uint me, uint align, uint size) => IsGlobal(me) ? C.PafHeapAlloc(size) : Cl.MspaceMemalign(U(me + 4), align, size);

    [Hle("_ZN3paf6memory13HeapAllocator14sce_paf_callocEjj")]
    [Hle("_ZN3paf6memory13HeapAllocator16sce_paf_calloc_dEjjPKcii")]
    uint HeapCalloc(uint me, uint n, uint size)
    {
        if (!IsGlobal(me)) return Cl.MspaceCalloc(U(me + 4), n, size);
        ulong total = (ulong)n * size;
        if (total > uint.MaxValue) return 0;
        uint p = Alloc((uint)total);
        if (p != 0) GuestMemory.Fill(p, 0, (uint)total);
        return p;
    }

    [Hle("_ZN3paf6memory13HeapAllocator15sce_paf_reallocEPvj")]
    [Hle("_ZN3paf6memory13HeapAllocator17sce_paf_realloc_dEPvjPKcii")]
    uint HeapRealloc(uint me, uint p, uint size)
    {
        if (!IsGlobal(me)) return Cl.sceClibMspaceRealloc(U(me + 4), p, size);
        uint n = Alloc(size);
        if (n != 0 && p != 0) { GuestMemory.Copy(n, p, Math.Min(size, Cl.UsableSize(p) is var u and > 0 ? u : size)); Free(p); }
        return n;
    }

    [Hle("_ZN3paf6memory13HeapAllocator12sce_paf_freeEPv")]
    [Hle("_ZN3paf6memory13HeapAllocator14sce_paf_free_dEPvPKcii")]
    void HeapFree(uint me, uint p) { if (p == 0) return; if (IsGlobal(me)) Free(p); else Cl.sceClibMspaceFree(U(me + 4), p); }

    [Hle("_ZN3paf6memory13HeapAllocator20sce_paf_musable_sizeEPv")] uint HeapUsable(uint me, uint p) => p == 0 ? 0 : Cl.UsableSize(p);
    [Hle("_ZN3paf6memory13HeapAllocator13sce_paf_mtrimEv")] void HeapTrim(uint me) { }
    [Hle("_ZN3paf6memory13HeapAllocator24sce_paf_dump_heap_statusEv")] void HeapDumpStatus(uint me) => Util.Log.Info($"paf heap {GuestMemory.ReadCString(me + 0x10, 32)}: {HeapFreeSize(me)} bytes free of {U(me + 0xC)}");
    [Hle("_ZN3paf6memory13HeapAllocator22sce_paf_dump_heap_infoEi")] void HeapDumpInfo(uint me, int sort) => HeapDumpStatus(me);
    [Hle("_ZN3paf6memory13HeapAllocator26sce_paf_check_memoryboundsEv")] int HeapCheckBounds(uint me) => 0;
    [Hle("_ZN3paf6memory13HeapAllocator17CheckMemoryBoundsEPv")] int HeapCheckBlock(uint me, uint p) => 0;

    [Hle("_ZNK3paf6memory13HeapAllocator7GetNameEv")] uint HeapName(uint me) => me + 0x10;
    [Hle("_ZNK3paf6memory13HeapAllocator12GetBlockTypeEv")] uint HeapBlockType(uint me) => U(me + 0x58);

    [Hle("_ZNK3paf6memory13HeapAllocator8GetRangeEPPvPj")]
    void HeapRange(uint me, uint baseOut, uint sizeOut) { if (baseOut != 0) U(baseOut, U(me + 8)); if (sizeOut != 0) U(sizeOut, U(me + 0xC)); }

    uint HeapStats(uint me, int word)
    {
        uint st = Alloc(16);
        try { Cl.MallocStats(U(me + 4), st); return U(st + (uint)word * 4); }
        finally { Free(st); }
    }

    [Hle("_ZN3paf6memory13HeapAllocator11GetFreeSizeEv")]
    uint HeapFreeSize(uint me) => IsGlobal(me) ? 0 : HeapStats(me, 0) - HeapStats(me, 3);

    /// <summary>(HeapInfo*): { free blocks, free bytes } (the mspace's free space as one block).</summary>
    [Hle("_ZN3paf6memory13HeapAllocator9QueryInfoEPNS0_8HeapInfoE")]
    void HeapQueryInfo(uint me, uint info) { if (info == 0) return; uint free = HeapFreeSize(me); U(info, free != 0 ? 1u : 0u); U(info + 4, free); }

    [Hle("_ZN3paf6memory13HeapAllocator27sce_paf_report_memoryblocksEPFiPvjiS2_ES2_")] int HeapReport(uint me, uint cb, uint user) => 0;

    /// <summary>The allocator object behind sce_paf_malloc: heap id 0 routes it to the HLE heap.</summary>
    [Hle("GetGlobalHeapAllocator")]
    uint GlobalHeapAllocator()
    {
        if (_globalHeap != 0) return _globalHeap;
        uint h = Static(0x5C);
        U(h, HeapVtable);
        GuestMemory.WriteCString(h + 0x10, "PafGlobalHeap", 31);
        return _globalHeap = h;
    }

    // ---- buffers
    uint BufferVtable(bool isConst, bool owns) => VTable($"Buffer.{isConst}.{owns}",
        HostSlot($"Buffer.GetAddr.{isConst}", c => c.Return(U(c.Gpr(0) + (isConst ? 0xCu : 4u)))),
        HostSlot($"Buffer.GetAddrConst.{isConst}", c => c.Return(U(c.Gpr(0) + (isConst ? 0xCu : 4u)))),
        HostSlot("Buffer.GetSize", c => c.Return(U(c.Gpr(0) + 8))),
        HostSlot($"Buffer.D1.{owns}", c => { if (owns) Free(U(c.Gpr(0) + 4)); c.Return(c.Gpr(0)); }),
        HostSlot($"Buffer.D0.{owns}", c => { uint o = c.Gpr(0); if (owns) Free(U(o + 4)); if (U(o + 0x10) != 0) SharedPtrRelease(o + 0x10); Free(o); c.Return(0); }));

    void NewBuffer(uint ret, uint addr, uint size, bool isConst, bool owns, uint parentSp = 0)
    {
        uint b = Alloc(0x18);
        GuestMemory.Fill(b, 0, 0x18);
        U(b, BufferVtable(isConst, owns));
        if (isConst) U(b + 0xC, addr); else U(b + 4, addr);
        U(b + 8, size);
        if (parentSp != 0 && U(parentSp) != 0)
        {
            U(b + 0x10, U(parentSp)); U(b + 0x14, U(parentSp + 4));
            if (U(parentSp + 4) != 0) U(U(parentSp + 4) + 4, U(U(parentSp + 4) + 4) + 1);
        }
        SharedPtrNew(ret, b, 4);
    }

    static void NullSp(uint ret) { U(ret, 0); U(ret + 4, 0); }

    /// <summary>(SharedPtr&lt;MallocBuffer&gt; &ret, void *mem, size): takes ownership of heap memory.</summary>
    [Hle("_ZN3paf12MallocBuffer6CreateEPvj")]
    void MallocBufferCreate(uint ret, uint addr, uint size) { if (addr == 0) NullSp(ret); else NewBuffer(ret, addr, size, false, true); }

    /// <summary>Allocates the memory (64-byte aligned from 64 bytes up) and wraps it.</summary>
    [Hle("_ZN3paf12MallocBuffer8AllocateEj")]
    void MallocBufferAllocate(uint ret, uint size)
    {
        uint p = Alloc(size);
        if (p == 0) NullSp(ret); else NewBuffer(ret, p, size, false, true);
    }

    [Hle("_ZN3paf15HeapAllocBuffer8AllocateEjPNS_6memory13HeapAllocatorE")]
    void HeapAllocBufferAllocate(uint ret, uint size, uint heap)
    {
        uint p = HeapMemalign(heap, size >= 0x40 ? 0x40u : 8u, size);
        if (p == 0) NullSp(ret); else NewBuffer(ret, p, size, false, IsGlobal(heap));
    }

    [Hle("_ZN3paf15HeapAllocBuffer6CreateEPvjPNS_6memory13HeapAllocatorE")]
    void HeapAllocBufferCreate(uint ret, uint addr, uint size, uint heap) { if (addr == 0) NullSp(ret); else NewBuffer(ret, addr, size, false, IsGlobal(heap)); }

    /// <summary>(ret, const void *mem, size): a const view the buffer does not own.</summary>
    [Hle("_ZN3paf20UnmanagedConstBuffer6CreateEPKvj")]
    void UnmanagedConstCreate(uint ret, uint addr, uint size) { if (addr == 0) NullSp(ret); else NewBuffer(ret, addr, size, true, false); }

    /// <summary>(ret, addr, size, SharedPtr parent): a window into the parent's memory, which it keeps alive; a window
    /// outside the parent, or no parent, gives an empty pointer.</summary>
    [Hle("_ZN3paf9SubBuffer6CreateEPvjNS_6common9SharedPtrINS_6BufferEEE")]
    void SubBufferCreate(uint ret, uint addr, uint size, uint parentSp) => SubCreate(ret, addr, size, parentSp, false);

    [Hle("_ZN3paf14SubConstBuffer6CreateEPKvjNS_6common9SharedPtrIKNS_6BufferEEE")]
    void SubConstBufferCreate(uint ret, uint addr, uint size, uint parentSp) => SubCreate(ret, addr, size, parentSp, true);

    void SubCreate(uint ret, uint addr, uint size, uint parentSp, bool isConst)
    {
        uint parent = U(parentSp);
        if (addr == 0 || parent == 0) { NullSp(ret); return; }
        uint pAddr = U(parent + 4) != 0 ? U(parent + 4) : U(parent + 0xC), pSize = U(parent + 8);
        if (pAddr == 0 || addr < pAddr || (ulong)addr + size > (ulong)pAddr + pSize) { NullSp(ret); return; }
        NewBuffer(ret, addr, size, isConst, false, parentSp);
    }
}
