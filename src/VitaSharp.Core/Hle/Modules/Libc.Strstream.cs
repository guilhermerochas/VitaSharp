using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceLibstdcxx <strstream> of libc.suprx 3.74. strstreambuf (0x90 bytes): basic_streambuf<char>, +0x78 _Minsize
// (0x20), +0x7C _Pendsave (epptr while frozen), +0x80 _Seekhigh, +0x84 _Strmode (_Allocated 1, _Constant 2, _Dynamic
// 4, _Frozen 8), +0x88 _Palloc, +0x8C _Pfree. istrstream/ostrstream (0xC8): +0 vptr, +8 the strstreambuf, +0x98
// basic_ios; strstream (0xD0): the iostream vptrs at +0 and +8, +0x10 the strstreambuf, +0xA0 basic_ios. The base
// constructors and destructors (C2/D2) take the VTT their caller passes.
public sealed partial class Libc
{
    const uint SsMinsize = 0x78, SsPendsave = 0x7C, SsSeekhigh = 0x80, SsMode = 0x84, SsPalloc = 0x88, SsPfree = 0x8C;
    const uint SsAllocated = 1, SsConstant = 2, SsDynamic = 4, SsFrozen = 8;
    const uint VtStrstreambuf = 0x11B2781A;

    /// <summary>istrstream: its group then the istream-in-istrstream construction vtables; VTT: istrstream, istream
    /// part, its basic_ios, istrstream's basic_ios.</summary>
    uint[] IstrstreamVtt()
    {
        uint g = VtGroup("istrstream",
            "98 0 T67B82DEF F71D13A36 F9CF31703 FFFFFF68 FFFFFF68 T67B82DEF F7B4D8616 F6FC84131 " +
            "98 0 TE302D51A FD089A7C9 F9BA8786E FFFFFF68 FFFFFF68 TE302D51A FC46CA5C9 F7CC8951D");
        return [g + 0xC, g + 0x34, g + 0x48, g + 0x20];
    }

    uint[] OstrstreamVtt()
    {
        uint g = VtGroup("ostrstream",
            "98 0 T9F17589B F0E463FB3 F4B8BA644 FFFFFF68 FFFFFF68 T9F17589B F5D4F7F7B FD82D58BA " +
            "98 0 TF9426257 FEF21C386 F6489E51D FFFFFF68 FFFFFF68 TF9426257 F41DBA7E0 F699E7717");
        return [g + 0xC, g + 0x34, g + 0x48, g + 0x20];
    }

    /// <summary>strstream; VTT: strstream, iostream-in-strstream, its istream, that one's basic_ios, its ostream, that
    /// one's basic_ios, the iostream's basic_ios, the iostream's ostream part, strstream's basic_ios, strstream's
    /// ostream part.</summary>
    uint[] StrstreamVtt()
    {
        uint g = VtGroup("strstream",
            "A0 0 T1CC15F54 F98BD8AE1 FC4099868 98 FFFFFFF8 T1CC15F54 F77812119 F6577F8FA FFFFFF60 FFFFFF60 T1CC15F54 FECEDE119 F2FB87ED7 " +
            "A0 0 TD7A9284F F6C2D3707 F2DA9FCEC " +
            "A0 0 TE302D51A FD089A7C9 F9BA8786E FFFFFF60 FFFFFF60 TE302D51A FC46CA5C9 F7CC8951D " +
            "98 0 TF9426257 FEF21C386 F6489E51D FFFFFF68 FFFFFF68 TF9426257 F41DBA7E0 F699E7717 " +
            "FFFFFF60 FFFFFF60 TD7A9284F F1081224F FE4BD5E6A 98 FFFFFFF8 TD7A9284F FABFBB0B4 F4A804953");
        return [g + 0xC, g + 0x48, g + 0x5C, g + 0x70, g + 0x84, g + 0x98, g + 0xAC, g + 0xC0, g + 0x34, g + 0x20];
    }

    /// <summary>A VTT in guest memory (made once) for the base constructors and destructors.</summary>
    uint GuestVtt(string key, uint[] entries)
    {
        lock (_rttiLock)
        {
            if (_vtGroups.TryGetValue("VTT " + key, out uint a)) return a;
            a = StaticAlloc((uint)entries.Length * 4, 4);
            for (int i = 0; i < entries.Length; i++) GuestMemory.W32(a + (uint)i * 4, entries[i]);
            return _vtGroups["VTT " + key] = a;
        }
    }

    static uint VttAt(uint vtt, int i) => GuestMemory.R32(vtt + (uint)i * 4);

    /// <summary>Sets the vptr at <paramref name="at"/> and the one of its virtual base.</summary>
    static void SetVptrs(uint at, uint prim, uint sec)
    {
        GuestMemory.W32(at, prim);
        GuestMemory.W32(at + VbaseOffset(prim), sec);
    }

    // ------------------------------------------------------------------ strstreambuf

    /// <summary>_Init(count, gp, pp, mode): a dynamic buffer without gp (_Minsize grows to count), else the array gp of
    /// count characters (strlen for 0, unbounded below 0): pp splits it into get and put areas.</summary>
    [Hle("_ZNSt12strstreambuf5_InitEiPcS0_i")]
    void StrbufInit(uint sb, int count, uint gp, uint pp, uint mode)
    {
        SbInitAreas(sb);
        GuestMemory.W32(sb + SsMinsize, 0x20);
        GuestMemory.W32(sb + SsPendsave, 0);
        GuestMemory.W32(sb + SsSeekhigh, 0);
        GuestMemory.W32(sb + SsMode, mode);
        GuestMemory.W32(sb + SsPalloc, 0);
        GuestMemory.W32(sb + SsPfree, 0);
        if (gp == 0)
        {
            GuestMemory.W32(sb + SsMode, mode | SsDynamic);
            if (count > 0x20) GuestMemory.W32(sb + SsMinsize, (uint)count);
            return;
        }
        uint end = gp + (count < 0 ? 0x7FFFFFFFu : count == 0 ? StrLen(gp) : (uint)count);
        GuestMemory.W32(sb + SsSeekhigh, end);
        if (pp == 0)
        {
            SetDeref(sb, SbIGfirst, gp);
            SetDeref(sb, SbIGnext, gp);
            SetDeref(sb, SbIGend, end);
            return;
        }
        if (pp < gp) pp = gp;
        else if (end < pp) pp = end;
        SetDeref(sb, SbIPfirst, pp);
        SetDeref(sb, SbIPnext, pp);
        SetDeref(sb, SbIPend, end);
        SetDeref(sb, SbIGfirst, gp);
        SetDeref(sb, SbIGnext, gp);
        SetDeref(sb, SbIGend, pp);
    }

    /// <summary>Frees an allocated, unfrozen array with _Pfree (or delete[]).</summary>
    void StrbufFree(uint sb, uint p)
    {
        uint pfree = GuestMemory.R32(sb + SsPfree);
        if (pfree != 0) GuestThread.Current?.CallGuest(pfree, p);
        else free(p);
    }

    /// <summary>_Tidy: an allocated array not frozen is freed; allocated and frozen are cleared, _Seekhigh too.</summary>
    [Hle("_ZNSt12strstreambuf5_TidyEv")]
    void StrbufTidy(uint sb)
    {
        uint mode = GuestMemory.R32(sb + SsMode);
        if ((mode & (SsAllocated | SsFrozen)) == SsAllocated) StrbufFree(sb, Deref(sb, SbIGfirst));
        GuestMemory.W32(sb + SsMode, GuestMemory.R32(sb + SsMode) & ~(SsAllocated | SsFrozen));
        GuestMemory.W32(sb + SsSeekhigh, 0);
    }

    [Hle("_ZNSt12strstreambufD1Ev")]
    [Hle("_ZNSt12strstreambufD2Ev")]
    uint StrbufDtor(uint sb)
    {
        GuestMemory.W32(sb, Rtti.Vptr(VtStrstreambuf));
        StrbufTidy(sb);
        return StreambufDtor(sb, false);
    }

    [Hle("_ZNSt12strstreambufD0Ev")] uint StrbufDel(uint sb) { StrbufDtor(sb); free(sb); return sb; }

    /// <summary>The inline strstreambuf constructors: the streambuf, then _Init.</summary>
    void StrbufCtor(uint sb, int count, uint gp, uint pp, uint mode)
    {
        StreambufCtor(sb, false);
        GuestMemory.W32(sb, Rtti.Vptr(VtStrstreambuf));
        StrbufInit(sb, count, gp, pp, mode);
    }

    /// <summary>freeze(f): a frozen buffer takes no more output (epptr at eback); unfreezing restores it.</summary>
    [Hle("_ZNSt12strstreambuf6freezeEb")]
    void StrbufFreeze(uint sb, bool f)
    {
        uint mode = GuestMemory.R32(sb + SsMode);
        if (f && (mode & SsFrozen) == 0)
        {
            GuestMemory.W32(sb + SsPendsave, Deref(sb, SbIPend));
            GuestMemory.W32(sb + SsMode, mode | SsFrozen);
            SetDeref(sb, SbIPend, Deref(sb, SbIGfirst));
        }
        else if (!f && (mode & SsFrozen) != 0)
        {
            GuestMemory.W32(sb + SsMode, mode & ~SsFrozen);
            SetDeref(sb, SbIPend, GuestMemory.R32(sb + SsPendsave));
        }
    }

    /// <summary>overflow(c): into the put area if it has room, else a dynamic, writable buffer grows by half its size
    /// (at least _Minsize, which then returns to 32), moving both areas.</summary>
    [Hle("_ZNSt12strstreambuf8overflowEi")]
    uint StrbufOverflow(HleContext ctx, uint sb, uint c)
    {
        if (c == EofE) return 0;
        uint p = Deref(sb, SbIPnext);
        if (p != 0 && p < Deref(sb, SbIPend))
        {
            SetDeref(sb, SbIPnext, p + 1);
            GuestMemory.Write(p, (byte)c);
            return c & 0xFF;
        }
        uint mode = GuestMemory.R32(sb + SsMode);
        if ((mode & SsDynamic) == 0 || (mode & (SsConstant | SsFrozen)) != 0) return EofE;
        uint eback = Deref(sb, SbIGfirst);
        int old = Deref(sb, SbIGnext) == 0 ? 0 : (int)(Deref(sb, SbIPend) - eback);
        int inc = Math.Max(old / 2, (int)GuestMemory.R32(sb + SsMinsize));
        GuestMemory.W32(sb + SsMinsize, 0x20);
        while (inc > 0 && int.MaxValue - inc < old) inc /= 2;
        if (inc <= 0) return EofE;
        int size = old + inc;
        uint palloc = GuestMemory.R32(sb + SsPalloc);
        uint ptr;
        if (palloc != 0) ptr = GuestThread.Current?.CallGuest(palloc, (uint)size) ?? 0;
        else
        {
            if (size > 0x40000000) { _ZSt11_Xbad_allocv(ctx); return EofE; }
            ptr = malloc((uint)size);
        }
        if (ptr == 0) return EofE;
        if (old > 0) GuestMemory.Copy(ptr, eback, (uint)old);
        if ((GuestMemory.R32(sb + SsMode) & SsAllocated) != 0) StrbufFree(sb, eback);
        GuestMemory.W32(sb + SsMode, GuestMemory.R32(sb + SsMode) | SsAllocated);
        uint newEnd = ptr + (uint)size;
        if (old == 0)
        {
            GuestMemory.W32(sb + SsSeekhigh, ptr);
            SetDeref(sb, SbIPfirst, ptr);
            SetDeref(sb, SbIPnext, ptr);
            SetDeref(sb, SbIPend, newEnd);
            SetDeref(sb, SbIGfirst, ptr);
            SetDeref(sb, SbIGnext, ptr);
            SetDeref(sb, SbIGend, ptr);
        }
        else
        {
            GuestMemory.W32(sb + SsSeekhigh, ptr + (GuestMemory.R32(sb + SsSeekhigh) - eback));
            SetDeref(sb, SbIPfirst, ptr + (Deref(sb, SbIPfirst) - eback));
            uint pnext = ptr + (Deref(sb, SbIPnext) - eback);
            SetDeref(sb, SbIPnext, pnext);
            SetDeref(sb, SbIPend, newEnd);
            uint gnext = ptr + (Deref(sb, SbIGnext) - eback);
            SetDeref(sb, SbIGfirst, ptr);
            SetDeref(sb, SbIGnext, gnext);
            SetDeref(sb, SbIGend, pnext + 1);
        }
        p = Deref(sb, SbIPnext);
        SetDeref(sb, SbIPnext, p + 1);
        GuestMemory.Write(p, (byte)c);
        return c & 0xFF;
    }

    /// <summary>pbackfail(c): one step back in the get area; a different character replaces the one there unless the
    /// buffer is constant.</summary>
    [Hle("_ZNSt12strstreambuf9pbackfailEi")]
    uint StrbufPbackfail(uint sb, uint c)
    {
        uint g = Deref(sb, SbIGnext);
        if (g == 0 || g <= Deref(sb, SbIGfirst)) return EofE;
        if (c != EofE && (byte)c != B(g - 1) && (GuestMemory.R32(sb + SsMode) & SsConstant) != 0) return EofE;
        SetDeref(sb, SbIGnext, g - 1);
        SbCount(sb, -1);
        if (c == EofE) return 0;
        GuestMemory.Write(g - 1, (byte)c);
        return c & 0xFF;
    }

    /// <summary>underflow: the next character, the get area first extended to what was written (_Seekhigh).</summary>
    [Hle("_ZNSt12strstreambuf9underflowEv")]
    uint StrbufUnderflow(uint sb)
    {
        uint g = Deref(sb, SbIGnext);
        if (g == 0) return EofE;
        if (g < Deref(sb, SbIGend)) return B(g);
        uint p = Deref(sb, SbIPnext);
        if (p == 0 || (p <= g && GuestMemory.R32(sb + SsSeekhigh) <= g)) return EofE;
        if (GuestMemory.R32(sb + SsSeekhigh) < p) GuestMemory.W32(sb + SsSeekhigh, p);
        SetDeref(sb, SbIGend, GuestMemory.R32(sb + SsSeekhigh));
        return B(Deref(sb, SbIGnext));
    }

    /// <summary>Moves the get (and with out also the put) position, or only the put one, to a position from the start;
    /// _BADOFF when it is outside [0, _Seekhigh - eback].</summary>
    int StrbufSeek(uint sb, int off, int way, uint which)
    {
        uint pnext = Deref(sb, SbIPnext);
        if (pnext != 0 && GuestMemory.R32(sb + SsSeekhigh) < pnext) GuestMemory.W32(sb + SsSeekhigh, pnext);
        uint eback = Deref(sb, SbIGfirst);
        int high = (int)(GuestMemory.R32(sb + SsSeekhigh) - eback);
        uint g = Deref(sb, SbIGnext);
        if ((which & 1) != 0 && g != 0)
        {
            if (way == 2) off += high;
            else if (way == 1 && (which & 2) == 0) off += (int)(g - eback);
            else if (way != 0) off = -1;
            if (off < 0 || off > high) return -1;
            int delta = (int)(eback + (uint)off - g);
            SetDeref(sb, SbIGnext, eback + (uint)off);
            SbCount(sb, delta);
            if ((which & 2) != 0 && Deref(sb, SbIPnext) != 0)
            {
                SetDeref(sb, SbIPnext, Deref(sb, SbIGnext));
                SetDeref(sb, SbIPend, Deref(sb, SbIPend));
            }
            return off;
        }
        if ((which & 2) != 0 && Deref(sb, SbIPnext) != 0)
        {
            if (way == 2) off += high;
            else if (way == 1) off += (int)(Deref(sb, SbIPnext) - eback);
            else if (way != 0) off = -1;
            if (off < 0 || off > high) return -1;
            SetDeref(sb, SbIPnext, eback + (uint)off);
            return off;
        }
        return -1;
    }

    static void StreamPos(uint ret, int off)
    {
        GuestMemory.W32(ret, (uint)off);
        GuestMemory.Fill(ret + 8, 0, 0x18);
    }

    [Hle("_ZNSt12strstreambuf7seekoffElNSt5_IosbIiE8_SeekdirENS1_9_OpenmodeE")]
    void StrbufSeekoff(uint ret, uint sb, int off, int way, uint which) => StreamPos(ret, StrbufSeek(sb, off, way, which));

    /// <summary>seekpos: the offset of the position (_Myoff plus the fpos_t's low word) from the start.</summary>
    [Hle("_ZNSt12strstreambuf7seekposESt4fposISt9_MbstatetENSt5_IosbIiE9_OpenmodeE")]
    void StrbufSeekpos(uint ret, uint sb, uint pos, uint which)
    {
        int off = (int)(GuestMemory.R32(pos) + GuestMemory.R32(pos + 8));
        StreamPos(ret, off == -1 ? -1 : StrbufSeek(sb, off, 0, which));
    }

    // ------------------------------------------------------------------ istrstream / ostrstream / strstream

    uint IstrstreamDtor2(uint obj, uint vtt)
    {
        SetVptrs(obj, VttAt(vtt, 0), VttAt(vtt, 3));
        StrbufDtor(obj + 8);
        SetVptrs(obj, VttAt(vtt, 1), VttAt(vtt, 2));
        return obj;
    }

    [Hle("_ZNSt10istrstreamD2Ev")] uint IstrstreamD2(uint obj, uint vtt) => IstrstreamDtor2(obj, vtt);

    [Hle("_ZNSt10istrstreamD1Ev")]
    uint IstrstreamD1(uint obj)
    {
        IstrstreamDtor2(obj, GuestVtt("istrstream", IstrstreamVtt()));
        GuestMemory.W32(obj + 0x98, Rtti.Vptr(VtIosC));
        IosBaseDtor(obj + 0x98);
        return obj;
    }

    [Hle("_ZNSt10istrstreamD0Ev")] uint IstrstreamD0(uint obj) { IstrstreamD1(obj); free(obj); return obj; }
    [Hle("_ZTv0_n12_NSt10istrstreamD1Ev")] uint IstrstreamTvD1(uint obj) => IstrstreamD1(VirtualThunkThis(obj));
    [Hle("_ZTv0_n12_NSt10istrstreamD0Ev")] uint IstrstreamTvD0(uint obj) => IstrstreamD0(VirtualThunkThis(obj));

    /// <summary>ostrstream(ptr, count, mode) base constructor: the ostream part over the embedded strstreambuf, which
    /// writes into ptr (from its end with app).</summary>
    [Hle("_ZNSt10ostrstreamC2EPciNSt5_IosbIiE9_OpenmodeE")]
    uint OstrstreamC2(HleContext ctx, uint obj, uint vtt, uint ptr, int count, uint mode)
    {
        SetVptrs(obj, VttAt(vtt, 1), VttAt(vtt, 2));
        BasicIosInit(ctx, obj + VbaseOffset(VttAt(vtt, 1)), obj + 8, false, false);
        SetVptrs(obj, VttAt(vtt, 0), VttAt(vtt, 3));
        uint pp = ptr != 0 && (mode & 8) != 0 ? ptr + StrLen(ptr) : ptr;
        StrbufCtor(obj + 8, count, ptr, pp, 0);
        return obj;
    }

    [Hle("_ZNSt10ostrstreamC1EPciNSt5_IosbIiE9_OpenmodeE")]
    uint OstrstreamC1(HleContext ctx, uint obj, uint ptr, int count, uint mode)
    {
        uint vtt = GuestVtt("ostrstream", OstrstreamVtt());
        GuestMemory.W32(obj, VttAt(vtt, 0));
        GuestMemory.W32(obj + 0x98, Rtti.Vptr(VtIosC));
        return OstrstreamC2(ctx, obj, vtt, ptr, count, mode);
    }

    [Hle("_ZNSt10ostrstreamD2Ev")] uint OstrstreamD2(uint obj, uint vtt) => IstrstreamDtor2(obj, vtt);

    [Hle("_ZNSt10ostrstreamD1Ev")]
    uint OstrstreamD1(uint obj)
    {
        IstrstreamDtor2(obj, GuestVtt("ostrstream", OstrstreamVtt()));
        GuestMemory.W32(obj + 0x98, Rtti.Vptr(VtIosC));
        IosBaseDtor(obj + 0x98);
        return obj;
    }

    [Hle("_ZNSt10ostrstreamD0Ev")] uint OstrstreamD0(uint obj) { OstrstreamD1(obj); free(obj); return obj; }
    [Hle("_ZTv0_n12_NSt10ostrstreamD1Ev")] uint OstrstreamTvD1(uint obj) => OstrstreamD1(VirtualThunkThis(obj));
    [Hle("_ZTv0_n12_NSt10ostrstreamD0Ev")] uint OstrstreamTvD0(uint obj) => OstrstreamD0(VirtualThunkThis(obj));

    /// <summary>strstream(ptr, count, mode) base constructor: the istream and ostream parts of the iostream, then the
    /// embedded strstreambuf over ptr (from its end with app).</summary>
    [Hle("_ZNSt9strstreamC2EPciNSt5_IosbIiE9_OpenmodeE")]
    uint StrstreamC2(HleContext ctx, uint obj, uint vtt, uint ptr, int count, uint mode)
    {
        SetVptrs(obj, VttAt(vtt, 2), VttAt(vtt, 3));
        GuestMemory.W32(obj + 4, 0);
        BasicIosInit(ctx, obj + VbaseOffset(VttAt(vtt, 2)), obj + 0x10, false, false);
        SetVptrs(obj + 8, VttAt(vtt, 4), VttAt(vtt, 5));
        SetVptrs(obj, VttAt(vtt, 1), VttAt(vtt, 6));
        GuestMemory.W32(obj + 8, VttAt(vtt, 7));
        SetVptrs(obj, VttAt(vtt, 0), VttAt(vtt, 8));
        GuestMemory.W32(obj + 8, VttAt(vtt, 9));
        uint pp = ptr != 0 && (mode & 8) != 0 ? ptr + StrLen(ptr) : ptr;
        StrbufCtor(obj + 0x10, count, ptr, pp, 0);
        return obj;
    }

    [Hle("_ZNSt9strstreamC1EPciNSt5_IosbIiE9_OpenmodeE")]
    uint StrstreamC1(HleContext ctx, uint obj, uint ptr, int count, uint mode)
    {
        uint vtt = GuestVtt("strstream", StrstreamVtt());
        GuestMemory.W32(obj, VttAt(vtt, 0));
        GuestMemory.W32(obj + 0xA0, Rtti.Vptr(VtIosC));
        return StrstreamC2(ctx, obj, vtt, ptr, count, mode);
    }

    [Hle("_ZNSt9strstreamD2Ev")]
    uint StrstreamD2(uint obj, uint vtt)
    {
        SetVptrs(obj, VttAt(vtt, 0), VttAt(vtt, 8));
        GuestMemory.W32(obj + 8, VttAt(vtt, 9));
        StrbufDtor(obj + 0x10);
        SetVptrs(obj, VttAt(vtt, 1), VttAt(vtt, 6));
        GuestMemory.W32(obj + 8, VttAt(vtt, 7));
        SetVptrs(obj + 8, VttAt(vtt, 4), VttAt(vtt, 5));
        SetVptrs(obj, VttAt(vtt, 2), VttAt(vtt, 3));
        return obj;
    }

    [Hle("_ZNSt9strstreamD1Ev")]
    uint StrstreamD1(uint obj)
    {
        StrstreamD2(obj, GuestVtt("strstream", StrstreamVtt()));
        GuestMemory.W32(obj + 0xA0, Rtti.Vptr(VtIosC));
        IosBaseDtor(obj + 0xA0);
        return obj;
    }

    [Hle("_ZNSt9strstreamD0Ev")] uint StrstreamD0(uint obj) { StrstreamD1(obj); free(obj); return obj; }
    [Hle("_ZThn8_NSt9strstreamD1Ev")] uint StrstreamThn8D1(uint obj) => StrstreamD1(obj - 8);
    [Hle("_ZThn8_NSt9strstreamD0Ev")] uint StrstreamThn8D0(uint obj) => StrstreamD0(obj - 8);
    [Hle("_ZTv0_n12_NSt9strstreamD1Ev")] uint StrstreamTvD1(uint obj) => StrstreamD1(VirtualThunkThis(obj));
    [Hle("_ZTv0_n12_NSt9strstreamD0Ev")] uint StrstreamTvD0(uint obj) => StrstreamD0(VirtualThunkThis(obj));
}
