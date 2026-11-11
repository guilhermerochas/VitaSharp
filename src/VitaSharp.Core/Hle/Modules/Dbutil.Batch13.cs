using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// dbutil.suprx 3.74 (SceDbutil), the members of its objects that need no vtable of the module, read in the module:
/// <list type="bullet">
/// <item>the connection { +8 sqlite3*, +0x1C per-slot flag bytes, +0x44 a word, +0x54/+0x58 the error callback and its
/// argument, +0x98 paf::thread::RMutex }: interrupt / changes / total changes under the lock, the flag and word setters,
/// the error mapper (a nonzero SQLite code goes to the callback and comes back as 0x80103200 | code);</item>
/// <item>the result object { +0x10 RMutex, +0x30 "done" byte } and the counters guarded by a mutex (+0x20 under +0x28,
/// +0x38 under +0x40), the "done" byte under +0x60;</item>
/// <item>row arrays { +0xC count, +0x10 or +0x14 0x18-byte entries, +0x18 count }, the value holders cleared to zero and
/// the small records the module builds from one word.</item>
/// </list>
/// Lock and unlock are libpaf's RMutex (the HLE's, the same objects the module would use).
/// </summary>
public sealed partial class Dbutil
{
    const uint NidRMutexLock = 0xF9FFA0BE, NidRMutexUnlock = 0x95F9C45F;
    const uint NidInterrupt = 0xE37D81B5, NidChanges = 0x11AD1CD1, NidTotalChanges = 0x2C34D734, NidE8098F5A = 0xE8098F5A;

    /// <summary>Runs export <paramref name="nid"/> with one argument and answers its r0 (the caller's registers are kept).</summary>
    unsafe uint Call1(HleContext c, uint nid, uint a0)
    {
        uint r0 = c.Cpu->R[0];
        c.Cpu->R[0] = a0;
        if (Emu.Hle.ImplOf(nid) is { } f) f(c);
        uint r = c.Cpu->R[0];
        c.Cpu->R[0] = r0;
        return r;
    }

    void Lock(HleContext c, uint m) => Call1(c, NidRMutexLock, m);
    void Unlock(HleContext c, uint m) => Call1(c, NidRMutexUnlock, m);

    uint Locked(HleContext c, uint mutex, Func<uint> body)
    {
        Lock(c, mutex);
        try { return body(); }
        finally { Unlock(c, mutex); }
    }

    // ---- the connection
    /// <summary>(conn, int sqliteResult): 0 for 0; otherwise the connection's error callback (+0x54, with +0x58) gets the
    /// code and the answer is 0x80103200 | code.</summary>
    [Hle("NID_B0BF2396", Nid = 0xB0BF2396, Library = Lib)]
    uint ConnMapError(uint conn, uint code)
    {
        if (code == 0) return 0;
        if (U32(conn + 0x54) is var cb and not 0) CallGuestFn(cb, U32(conn + 0x58), code);
        return code | 0x80103200;
    }

    [Hle("NID_987DE0C9", Nid = 0x987DE0C9, Library = Lib)]
    void ConnE8098F5A(HleContext c) { uint conn = c.Gpr(0); c.Return(U32(conn + 8) != 0 ? Call1(c, NidE8098F5A, U32(conn + 8)) : 0); }

    [Hle("NID_37AD6852", Nid = 0x37AD6852, Library = Lib)]
    void ConnInterrupt(HleContext c)
    {
        uint conn = c.Gpr(0);
        Locked(c, conn + 0x98, () => { if (U32(conn + 8) != 0) Call1(c, NidInterrupt, U32(conn + 8)); return 0; });
    }

    [Hle("NID_D1D0D3CA", Nid = 0xD1D0D3CA, Library = Lib)]
    void ConnChanges(HleContext c) { uint conn = c.Gpr(0); c.Return(Locked(c, conn + 0x98, () => U32(conn + 8) != 0 ? Call1(c, NidChanges, U32(conn + 8)) : 0)); }

    [Hle("NID_1FD46F4E", Nid = 0x1FD46F4E, Library = Lib)]
    void ConnTotalChanges(HleContext c) { uint conn = c.Gpr(0); c.Return(Locked(c, conn + 0x98, () => U32(conn + 8) != 0 ? Call1(c, NidTotalChanges, U32(conn + 8)) : 0)); }

    /// <summary>(conn, int slot, bool value): the slot's flag byte (+0x1C + slot) under the lock, 0.</summary>
    [Hle("NID_D48BBE90", Nid = 0xD48BBE90, Library = Lib)]
    void ConnSetFlag(HleContext c)
    {
        uint conn = c.Gpr(0), slot = c.Gpr(1);
        byte v = (byte)c.Gpr(2);
        c.Return(Locked(c, conn + 0x98, () => { U8(conn + slot + 0x1C, v); return 0; }));
    }

    /// <summary>(conn, value): the word at +0x44 under the lock, 0.</summary>
    [Hle("NID_3CB037F2", Nid = 0x3CB037F2, Library = Lib)]
    void ConnSet44(HleContext c)
    {
        uint conn = c.Gpr(0), v = c.Gpr(1);
        c.Return(Locked(c, conn + 0x98, () => { U32(conn + 0x44, v); return 0; }));
    }

    // ---- counters and "done" flags under their mutexes
    [Hle("NID_6FF905F9", Nid = 0x6FF905F9, Library = Lib)]
    void Increment38(HleContext c) { uint o = c.Gpr(0); c.Return(Locked(c, o + 0x40, () => { uint v = U32(o + 0x38) + 1; U32(o + 0x38, v); return v; })); }

    [Hle("NID_5CD273F5", Nid = 0x5CD273F5, Library = Lib)]
    void Increment20(HleContext c) { uint o = c.Gpr(0); c.Return(Locked(c, o + 0x28, () => { uint v = U32(o + 0x20) + 1; U32(o + 0x20, v); return v; })); }

    [Hle("NID_055E4365", Nid = 0x055E4365, Library = Lib)]
    void Done30Under60(HleContext c) { uint o = c.Gpr(0); c.Return(Locked(c, o + 0x60, () => U8(o + 0x30))); }

    [Hle("NID_0A4E17D5", Nid = 0x0A4E17D5, Library = Lib)]
    [Hle("NID_C10EBA47", Nid = 0xC10EBA47, Library = Lib)]
    void ResultSetDone(HleContext c) { uint o = c.Gpr(0); Locked(c, o + 0x10, () => { U8(o + 0x30, 1); return 0; }); }

    // ---- row arrays and plain records
    /// <summary>(rows, int i): the word at +8 of entry i (0x18 bytes each, from +0x14). The module's bound check is
    /// "i &gt;= 0 or i &lt; count", so any non-negative index is read.</summary>
    [Hle("NID_5224B63E", Nid = 0x5224B63E, Library = Lib)]
    uint RowWord8(uint rows, int i) => i >= 0 || i < (int)U32(rows + 0x18) ? U32(U32(rows + 0x14) + (uint)(i * 0x18) + 8) : 0;

    /// <summary>(rows, int i, void *out): copies entry i (0x18 bytes, from +0x10) when 0 &lt;= i &lt; count (+0xC) and out is
    /// set; 0x80103101 otherwise.</summary>
    [Hle("NID_F9F1F23D", Nid = 0xF9F1F23D, Library = Lib)]
    int RowCopy(uint rows, int i, uint outp)
    {
        if (i < 0 || i > (int)U32(rows + 0xC) - 1 || outp == 0) return unchecked((int)0x80103101);
        GuestMemory.Copy(outp, U32(rows + 0x10) + (uint)(i * 0x18), 0x18);
        return 0;
    }

    /// <summary>A 0x30-byte value holder cleared to zero; answers it.</summary>
    [Hle("NID_D0B4D8F2", Nid = 0xD0B4D8F2, Library = Lib)]
    [Hle("NID_1BFD9CB7", Nid = 0x1BFD9CB7, Library = Lib)]
    uint Clear30(uint p) { GuestMemory.Fill(p, 0, 0x30); return p; }

    /// <summary>TRUE when +4, +0x10 and +0x1C are all zero (an empty value).</summary>
    [Hle("NID_A0DA0E0E", Nid = 0xA0DA0E0E, Library = Lib)]
    bool IsEmptyValue(uint p) => U32(p + 4) == 0 && U32(p + 0x10) == 0 && U32(p + 0x1C) == 0;

    /// <summary>Resets a record: words 0-3, 5 and 10 zero, bit 0 of byte +0x10 cleared.</summary>
    [Hle("NID_59DCDE85", Nid = 0x59DCDE85, Library = Lib)]
    [Hle("NID_4BCEC20A", Nid = 0x4BCEC20A, Library = Lib)]
    void ResetRecord(uint p)
    {
        U32(p, 0); U32(p + 4, 0); U32(p + 8, 0); U32(p + 0xC, 0);
        U8(p + 0x10, (byte)(U8(p + 0x10) & 0xFE));
        U32(p + 0x28, 0); U32(p + 0x14, 0);
    }

    /// <summary>(rec, const uint32 *src): words 0-2 zero, word 3 = *src.</summary>
    [Hle("NID_E6B40BC1", Nid = 0xE6B40BC1, Library = Lib)]
    [Hle("NID_1A6CF24B", Nid = 0x1A6CF24B, Library = Lib)]
    [Hle("NID_3236A69B", Nid = 0x3236A69B, Library = Lib)]
    [Hle("NID_CFB4B23B", Nid = 0xCFB4B23B, Library = Lib)]
    void RecordFromWord(uint p, uint src) { uint v = U32(src); U32(p, 0); U32(p + 4, 0); U32(p + 8, 0); U32(p + 0xC, v); }

    /// <summary>(stmt): 0x80103103 without a statement (+0xC); otherwise the "row ready" byte (+0x48) is cleared, 0.</summary>
    [Hle("NID_BC17F930", Nid = 0xBC17F930, Library = Lib)]
    int StmtClearRow(uint s)
    {
        if (U32(s + 0xC) == 0) return unchecked((int)0x80103103);
        U8(s + 0x48, 0);
        return 0;
    }
}
