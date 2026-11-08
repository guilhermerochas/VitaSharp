using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceLibstdcxx stream buffers and the two facets of libc.suprx 3.74. The module carries Dinkumware's abridged
// iostreams: no std::locale; basic_ios holds no locale and the streams use the "simple" facets _ctype<E> and
// _codecvt<E, char, _Mbstatet> directly.
//
// basic_streambuf<E> (0x78 bytes): +0 vptr, +8 _Mylock (std::_Mutex), then the six area pointers kept indirectly
// (Dinkumware): +0x30/+0x34 the get/put "first" storage, +0x38/+0x3C pointers to the get/put first pointers,
// +0x40/+0x44 next storage, +0x48/+0x4C pointers to them, +0x50/+0x54 end storage, +0x58/+0x5C pointers to them,
// +0x60/+0x68 two 64-bit counters of characters taken from the get area (+0x70/+0x74 point at them).
// basic_filebuf<E> (0x90 bytes) adds +0x78 _Mycvt (an embedded _codecvt<E, char, _Mbstatet>), +0x7C _Mychar (the
// one-element putback buffer), +0x7D (+0x7E for wchar_t) _Wrotesome, +0x80 _State (_Mbstatet, 8 bytes), +0x88
// _Closef, +0x8C _Myfile. The module points a filebuf's areas into the FILE's own buffer; the HLE FILE is unbuffered,
// so the areas stay empty and every character goes through overflow/uflow, which keeps the order of bytes that the
// streams and the C stdio functions write.
// fpos<_Mbstatet> (0x20 bytes): +0 _Myoff, +8 fpos_t (64-bit offset, then its state), +0x18 _State.
public sealed partial class Libc
{
    const uint SbLock = 8, SbGfirst = 0x30, SbPfirst = 0x34, SbIGfirst = 0x38, SbIPfirst = 0x3C, SbGnext = 0x40,
        SbPnext = 0x44, SbIGnext = 0x48, SbIPnext = 0x4C, SbGend = 0x50, SbPend = 0x54, SbIGend = 0x58, SbIPend = 0x5C,
        SbGcount = 0x60, SbPcount = 0x68, SbIGcount = 0x70, SbIPcount = 0x74, StreambufSize = 0x78;
    const uint FbCvt = 0x78, FbMychar = 0x7C, FbState = 0x80, FbClosef = 0x88, FbFile = 0x8C, FilebufSize = 0x90;

    // byte offsets of basic_streambuf's virtual functions
    const uint VsLock = 8, VsUnlock = 0xC, VsOverflow = 0x10, VsPbackfail = 0x14, VsUnderflow = 0x1C, VsUflow = 0x20,
        VsSync = 0x38;
    // and of _codecvt's
    const uint VcNoconv = 8, VcIn = 0x14, VcOut = 0x18, VcUnshift = 0x1C;

    // vtable NIDs (Libc.Rtti.g.cs)
    const uint VtStreambufC = 0x05548FF7, VtStreambufW = 0xE8A9F32E, VtFilebufC = 0xC3233C50, VtFilebufW = 0xF269A8EB,
        VtCtypeC = 0x214E1395, VtCtypeW = 0x798E7C29, VtCodecvtBase = 0x75D16BD0, VtCodecvtC = 0x9B3E5D29,
        VtCodecvtW = 0x95405266;

    const uint EofE = 0xFFFFFFFF; // char_traits<char>::eof() and char_traits<wchar_t>::eof() of the module

    static uint ESize(bool w) => w ? 2u : 1u;
    static uint RdE(uint a, bool w) => w ? W(a) : B(a);
    static void WrE(uint a, uint v, bool w) { if (w) GuestMemory.Write(a, (ushort)v); else GuestMemory.Write(a, (byte)v); }
    static uint FbWrotesome(bool w) => w ? 0x7Eu : 0x7Du;

    /// <summary>A virtual call on a guest object (this in r0, then <paramref name="args"/>; past r3 on the stack).</summary>
    static uint VCall(uint obj, uint offset, params ReadOnlySpan<uint> args)
    {
        var t = GuestThread.Current ?? throw new InvalidOperationException("SceLibstdcxx: a virtual call needs a guest thread");
        uint fn = GuestMemory.R32(GuestMemory.R32(obj) + offset);
        Span<uint> a = stackalloc uint[args.Length + 1];
        a[0] = obj;
        args.CopyTo(a[1..]);
        return t.CallGuest(fn, a);
    }

    static uint Deref(uint obj, uint field) => GuestMemory.R32(GuestMemory.R32(obj + field));
    static void SetDeref(uint obj, uint field, uint v) => GuestMemory.W32(GuestMemory.R32(obj + field), v);

    /// <summary>Both get-area counters move by <paramref name="n"/> characters.</summary>
    static void SbCount(uint sb, long n)
    {
        foreach (uint f in (ReadOnlySpan<uint>)[SbIGcount, SbIPcount])
        {
            uint p = GuestMemory.R32(sb + f);
            GuestMemory.Write(p, GuestMemory.Read<long>(p) + n);
        }
    }

    /// <summary>basic_streambuf::_Init(): empty areas, the indirect pointers on the object's own storage.</summary>
    static void SbInitAreas(uint sb)
    {
        foreach (uint f in (ReadOnlySpan<uint>)[SbGfirst, SbPfirst, SbGnext, SbPnext, SbGend, SbPend]) GuestMemory.W32(sb + f, 0);
        GuestMemory.W32(sb + SbIGfirst, sb + SbGfirst);
        GuestMemory.W32(sb + SbIPfirst, sb + SbPfirst);
        GuestMemory.W32(sb + SbIGnext, sb + SbGnext);
        GuestMemory.W32(sb + SbIPnext, sb + SbPnext);
        GuestMemory.W32(sb + SbIGend, sb + SbGend);
        GuestMemory.W32(sb + SbIPend, sb + SbPend);
        GuestMemory.W32(sb + SbIGcount, sb + SbGcount);
        GuestMemory.W32(sb + SbIPcount, sb + SbPcount);
    }

    /// <summary>The inline basic_streambuf constructor: vptr, the lock, empty areas.</summary>
    void StreambufCtor(uint sb, bool w)
    {
        GuestMemory.W32(sb, Rtti.Vptr(w ? VtStreambufW : VtStreambufC));
        _ZNSt6_MutexC1Ev(sb + SbLock);
        SbInitAreas(sb);
    }

    // ------------------------------------------------------------------ basic_streambuf<char / wchar_t>

    uint StreambufDtor(uint sb, bool w)
    {
        GuestMemory.W32(sb, Rtti.Vptr(w ? VtStreambufW : VtStreambufC));
        _ZNSt6_MutexD1Ev(sb + SbLock);
        return sb;
    }

    [Hle("_ZNSt15basic_streambufIcSt11char_traitsIcEED1Ev")] uint SbDtorC(uint sb) => StreambufDtor(sb, false);
    [Hle("_ZNSt15basic_streambufIwSt11char_traitsIwEED1Ev")] uint SbDtorW(uint sb) => StreambufDtor(sb, true);
    [Hle("_ZNSt15basic_streambufIcSt11char_traitsIcEED0Ev")] uint SbDelC(uint sb) { StreambufDtor(sb, false); free(sb); return sb; }
    [Hle("_ZNSt15basic_streambufIwSt11char_traitsIwEED0Ev")] uint SbDelW(uint sb) { StreambufDtor(sb, true); free(sb); return sb; }

    [Hle("_ZNSt15basic_streambufIcSt11char_traitsIcEE5_LockEv")]
    [Hle("_ZNSt15basic_streambufIwSt11char_traitsIwEE5_LockEv")]
    void SbLockFn(uint sb) => _ZNSt6_Mutex5_LockEv(sb + SbLock);

    [Hle("_ZNSt15basic_streambufIcSt11char_traitsIcEE7_UnlockEv")]
    [Hle("_ZNSt15basic_streambufIwSt11char_traitsIwEE7_UnlockEv")]
    void SbUnlockFn(uint sb) => _ZNSt6_Mutex7_UnlockEv(sb + SbLock);

    /// <summary>uflow: underflow(), then the character at gptr() is taken (both counters advance).</summary>
    uint StreambufUflow(uint sb, bool w)
    {
        if (VCall(sb, VsUnderflow) == EofE) return EofE;
        SbCount(sb, 1);
        uint g = Deref(sb, SbIGnext);
        SetDeref(sb, SbIGnext, g + ESize(w));
        return RdE(g, w);
    }

    [Hle("_ZNSt15basic_streambufIcSt11char_traitsIcEE5uflowEv")] uint SbUflowC(uint sb) => StreambufUflow(sb, false);
    [Hle("_ZNSt15basic_streambufIwSt11char_traitsIwEE5uflowEv")] uint SbUflowW(uint sb) => StreambufUflow(sb, true);

    /// <summary>xsgetn: copies from the get area while it has characters, else one character per uflow().</summary>
    int StreambufXsgetn(uint sb, uint ptr, int count, bool w)
    {
        uint e = ESize(w);
        int copied = 0;
        while (count > 0)
        {
            uint g = Deref(sb, SbIGnext);
            int avail = g == 0 ? 0 : (int)(Deref(sb, SbIGend) - g) / (int)e;
            if (avail > 0)
            {
                int n = Math.Min(avail, count);
                if (n != 0) GuestMemory.Copy(ptr, g, (uint)n * e);
                SetDeref(sb, SbIGnext, g + (uint)n * e);
                SbCount(sb, n);
                ptr += (uint)n * e;
                copied += n;
                count -= n;
            }
            else
            {
                uint c = VCall(sb, VsUflow);
                if (c == EofE) break;
                WrE(ptr, c, w);
                ptr += e;
                copied++;
                count--;
            }
        }
        return copied;
    }

    [Hle("_ZNSt15basic_streambufIcSt11char_traitsIcEE6xsgetnEPci")] int SbXsgetnC(uint sb, uint p, int n) => StreambufXsgetn(sb, p, n, false);
    [Hle("_ZNSt15basic_streambufIwSt11char_traitsIwEE6xsgetnEPwi")] int SbXsgetnW(uint sb, uint p, int n) => StreambufXsgetn(sb, p, n, true);

    /// <summary>xsputn: copies into the put area while it has room, else one character per overflow().</summary>
    int StreambufXsputn(uint sb, uint ptr, int count, bool w)
    {
        uint e = ESize(w);
        int copied = 0;
        while (count > 0)
        {
            uint p = Deref(sb, SbIPnext);
            int avail = p == 0 ? 0 : (int)(Deref(sb, SbIPend) - p) / (int)e;
            if (avail > 0)
            {
                int n = Math.Min(avail, count);
                if (n != 0) GuestMemory.Copy(p, ptr, (uint)n * e);
                ptr += (uint)n * e;
                SetDeref(sb, SbIPnext, p + (uint)n * e);
                copied += n;
                count -= n;
            }
            else
            {
                if (VCall(sb, VsOverflow, RdE(ptr, w)) == EofE) break;
                ptr += e;
                copied++;
                count--;
            }
        }
        return copied;
    }

    [Hle("_ZNSt15basic_streambufIcSt11char_traitsIcEE6xsputnEPKci")] int SbXsputnC(uint sb, uint p, int n) => StreambufXsputn(sb, p, n, false);
    [Hle("_ZNSt15basic_streambufIwSt11char_traitsIwEE6xsputnEPKwi")] int SbXsputnW(uint sb, uint p, int n) => StreambufXsputn(sb, p, n, true);

    /// <summary>An invalid stream position: _Myoff _BADOFF (-1), a zero fpos_t and state.</summary>
    static void BadPos(uint ret)
    {
        GuestMemory.W32(ret, 0xFFFFFFFF);
        GuestMemory.Fill(ret + 8, 0, 0x18);
    }

    [Hle("_ZNSt15basic_streambufIcSt11char_traitsIcEE7seekoffElNSt5_IosbIiE8_SeekdirENS4_9_OpenmodeE")]
    [Hle("_ZNSt15basic_streambufIwSt11char_traitsIwEE7seekoffElNSt5_IosbIiE8_SeekdirENS4_9_OpenmodeE")]
    void SbSeekoff(uint ret, uint sb, int off, int way, int mode) => BadPos(ret);

    [Hle("_ZNSt15basic_streambufIcSt11char_traitsIcEE7seekposESt4fposISt9_MbstatetENSt5_IosbIiE9_OpenmodeE")]
    [Hle("_ZNSt15basic_streambufIwSt11char_traitsIwEE7seekposESt4fposISt9_MbstatetENSt5_IosbIiE9_OpenmodeE")]
    void SbSeekpos(uint ret, uint sb, uint pos, int mode) => BadPos(ret);

    // ------------------------------------------------------------------ basic_filebuf<char / wchar_t>

    /// <summary>basic_filebuf::_Init(file, which): empty areas, _Closef for _Openfl, the file, the initial state.</summary>
    static void FilebufInit(uint fb, uint file, bool openfl, bool w)
    {
        GuestMemory.Write(fb + FbWrotesome(w), (byte)0);
        GuestMemory.Write(fb + FbClosef, (byte)(openfl ? 1 : 0));
        SbInitAreas(fb);
        GuestMemory.W32(fb + FbFile, file);
        GuestMemory.Write(fb + FbState, 0UL);
    }

    /// <summary>The inline basic_filebuf(FILE*) constructor of ios_base::Init / _Winit (_Init(file, _Newfl)).</summary>
    void FilebufCtor(uint fb, uint file, bool w)
    {
        StreambufCtor(fb, w);
        GuestMemory.W32(fb, Rtti.Vptr(w ? VtFilebufW : VtFilebufC));
        GuestMemory.W32(fb + FbCvt, Rtti.Vptr(w ? VtCodecvtW : VtCodecvtC));
        GuestMemory.Write(fb + FbState, 0UL);
        FilebufInit(fb, file, false, w);
    }

    /// <summary>_Reset_back: a get area left on the putback character becomes the empty one after it.</summary>
    static void FilebufResetBack(uint fb, bool w)
    {
        uint mc = fb + FbMychar;
        if (Deref(fb, SbIGfirst) != mc) return;
        SetDeref(fb, SbIGfirst, mc);
        SetDeref(fb, SbIGnext, mc + ESize(w));
        SetDeref(fb, SbIGend, mc + ESize(w));
    }

    static bool CvtNoconv(uint fb) => (VCall(fb + FbCvt, VcNoconv) & 0xFF) != 0;

    uint FilebufDtor(uint fb, bool w)
    {
        GuestMemory.W32(fb, Rtti.Vptr(w ? VtFilebufW : VtFilebufC));
        uint file = GuestMemory.R32(fb + FbFile);
        if (file != 0) FilebufResetBack(fb, w);
        if (B(fb + FbClosef) != 0)
        {
            if (file != 0)
            {
                FilebufEndwrite(fb, w);
                fclose(GuestMemory.R32(fb + FbFile));
            }
            FilebufInit(fb, 0, false, w);
        }
        GuestMemory.W32(fb + FbCvt, Rtti.Vptr(VtCodecvtBase));
        return StreambufDtor(fb, w);
    }

    [Hle("_ZNSt13basic_filebufIcSt11char_traitsIcEED1Ev")] uint FbDtorC(uint fb) => FilebufDtor(fb, false);
    [Hle("_ZNSt13basic_filebufIwSt11char_traitsIwEED1Ev")] uint FbDtorW(uint fb) => FilebufDtor(fb, true);
    [Hle("_ZNSt13basic_filebufIcSt11char_traitsIcEED0Ev")] uint FbDelC(uint fb) { FilebufDtor(fb, false); free(fb); return fb; }
    [Hle("_ZNSt13basic_filebufIwSt11char_traitsIwEED0Ev")] uint FbDelW(uint fb) { FilebufDtor(fb, true); free(fb); return fb; }

    [Hle("_ZNSt13basic_filebufIcSt11char_traitsIcEE5_LockEv")]
    [Hle("_ZNSt13basic_filebufIwSt11char_traitsIwEE5_LockEv")]
    void FbLockFn(uint fb) => _Lockfilelock(GuestMemory.R32(fb + FbFile));

    [Hle("_ZNSt13basic_filebufIcSt11char_traitsIcEE7_UnlockEv")]
    [Hle("_ZNSt13basic_filebufIwSt11char_traitsIwEE7_UnlockEv")]
    void FbUnlockFn(uint fb) => _Unlockfilelock(GuestMemory.R32(fb + FbFile));

    /// <summary>Guest scratch for a conversion: +0 the element, +4/+8 the two "next" pointers, +0x10 the byte buffer.</summary>
    uint CvtScratch(uint bufSize)
    {
        uint s = HeapAlloc(0x10 + bufSize, HeapAlign);
        if (s == 0) throw new InvalidOperationException("SceLibstdcxx: no memory for a conversion buffer");
        return s;
    }

    /// <summary>_Endwrite: after converted output, overflow() then the unshift sequence is written.</summary>
    bool FilebufEndwrite(uint fb, bool w)
    {
        if (CvtNoconv(fb) || B(fb + FbWrotesome(w)) == 0) return true;
        if (VCall(fb, VsOverflow, EofE) == EofE) return false;
        uint size = 8;
        uint s = CvtScratch(0x400);
        try
        {
            while (true)
            {
                uint buf = s + 0x10;
                int r = (int)VCall(fb + FbCvt, VcUnshift, fb + FbState, buf, buf + size, s + 8);
                if (r == 3) return true;
                if (r is not (0 or 1)) return false;
                if (r == 0) GuestMemory.Write(fb + FbWrotesome(w), (byte)0);
                uint n = GuestMemory.R32(s + 8) - buf;
                if (n != 0 && fwrite(buf, 1, n, GuestMemory.R32(fb + FbFile)) != n) return false;
                if (B(fb + FbWrotesome(w)) == 0) return true;
                if (n == 0)
                {
                    if (size + 8 > 0x400) return false;
                    size += 8;
                }
            }
        }
        finally { free(s); }
    }

    [Hle("_ZNSt13basic_filebufIcSt11char_traitsIcEE9_EndwriteEv")] bool FbEndwriteC(uint fb) => FilebufEndwrite(fb, false);
    [Hle("_ZNSt13basic_filebufIwSt11char_traitsIwEE9_EndwriteEv")] bool FbEndwriteW(uint fb) => FilebufEndwrite(fb, true);

    /// <summary>overflow(c): eof gives not_eof; a character goes into the put area if it has room, else to the file
    /// (directly when the codecvt does not convert, else through do_out in 8-byte steps, up to 32 bytes).</summary>
    uint FilebufOverflow(uint fb, uint c, bool w)
    {
        if (c == EofE) return 0;
        uint p = Deref(fb, SbIPnext);
        if (p != 0 && p < Deref(fb, SbIPend))
        {
            SetDeref(fb, SbIPnext, p + ESize(w));
            WrE(p, c, w);
            return c;
        }
        uint file = GuestMemory.R32(fb + FbFile);
        if (file == 0) return EofE;
        FilebufResetBack(fb, w);
        if (CvtNoconv(fb))
            return (w ? fputwc((int)c, file) != WEOF : fputc((sbyte)c, file) != EOF) ? c : EofE;
        uint size = 8;
        uint s = CvtScratch(0x20);
        try
        {
            WrE(s, c, w);
            while (true)
            {
                uint buf = s + 0x10;
                int r = (int)VCall(fb + FbCvt, VcOut, fb + FbState, s, s + ESize(w), s + 4, buf, buf + size, s + 8);
                if (r == 3)
                    return (w ? fputwc((int)RdE(s, true), file) != WEOF : fputc((sbyte)B(s), file) != EOF) ? c : EofE;
                if (r is not (0 or 1)) return EofE;
                uint n = GuestMemory.R32(s + 8) - buf;
                if (n != 0 && fwrite(buf, 1, n, file) != n) return EofE;
                GuestMemory.Write(fb + FbWrotesome(w), (byte)1);
                if (GuestMemory.R32(s + 4) != s) return c;
                if (n == 0)
                {
                    if (size >= 0x20) return EofE;
                    size += 8;
                }
            }
        }
        finally { free(s); }
    }

    [Hle("_ZNSt13basic_filebufIcSt11char_traitsIcEE8overflowEi")] uint FbOverflowC(uint fb, uint c) => FilebufOverflow(fb, c, false);
    [Hle("_ZNSt13basic_filebufIwSt11char_traitsIwEE8overflowEj")] uint FbOverflowW(uint fb, uint c) => FilebufOverflow(fb, c, true);

    /// <summary>pbackfail(c): steps back in the get area when the character matches (or c is eof), else pushes back
    /// into the file (no conversion) or into the one-element putback buffer.</summary>
    uint FilebufPbackfail(uint fb, uint c, bool w)
    {
        uint e = ESize(w);
        uint g = Deref(fb, SbIGnext);
        if (g != 0 && Deref(fb, SbIGfirst) < g && (c == EofE || RdE(g - e, w) == c))
        {
            SbCount(fb, -1);
            SetDeref(fb, SbIGnext, g - e);
            return c == EofE ? 0 : c;
        }
        uint file = GuestMemory.R32(fb + FbFile);
        if (file == 0 || c == EofE) return EofE;
        if (CvtNoconv(fb) && (w ? ungetwc((ushort)c, file) != WEOF : ungetc((byte)c, file) != EOF)) return c;
        uint mc = fb + FbMychar;
        if (g == mc) return EofE;
        WrE(mc, c, w);
        SetDeref(fb, SbIGfirst, mc);
        SetDeref(fb, SbIGnext, mc);
        SetDeref(fb, SbIGend, mc + e);
        return c;
    }

    [Hle("_ZNSt13basic_filebufIcSt11char_traitsIcEE9pbackfailEi")] uint FbPbackfailC(uint fb, uint c) => FilebufPbackfail(fb, c, false);
    [Hle("_ZNSt13basic_filebufIwSt11char_traitsIwEE9pbackfailEj")] uint FbPbackfailW(uint fb, uint c) => FilebufPbackfail(fb, c, true);

    /// <summary>underflow: the character at gptr(), else uflow() then pbackfail() of what it read.</summary>
    uint FilebufUnderflow(uint fb, bool w)
    {
        uint g = Deref(fb, SbIGnext);
        if (g != 0 && g < Deref(fb, SbIGend)) return RdE(g, w);
        uint c = VCall(fb, VsUflow);
        if (c == EofE) return c;
        VCall(fb, VsPbackfail, c);
        return c;
    }

    [Hle("_ZNSt13basic_filebufIcSt11char_traitsIcEE9underflowEv")] uint FbUnderflowC(uint fb) => FilebufUnderflow(fb, false);
    [Hle("_ZNSt13basic_filebufIwSt11char_traitsIwEE9underflowEv")] uint FbUnderflowW(uint fb) => FilebufUnderflow(fb, true);

    /// <summary>uflow: the character at gptr() (counted), else one read from the file: directly when the codecvt does
    /// not convert, else bytes until do_in yields a character (the bytes it left are pushed back).</summary>
    uint FilebufUflow(uint fb, bool w)
    {
        uint e = ESize(w);
        uint g = Deref(fb, SbIGnext);
        if (g != 0 && g < Deref(fb, SbIGend))
        {
            SbCount(fb, 1);
            SetDeref(fb, SbIGnext, g + e);
            return RdE(g, w);
        }
        uint file = GuestMemory.R32(fb + FbFile);
        if (file == 0) return EofE;
        FilebufResetBack(fb, w);
        if (CvtNoconv(fb))
        {
            if (w) { int wc = fgetwc(file); return wc == WEOF ? EofE : (uint)(wc & 0xFFFF); }
            int ch = fgetc(file);
            return ch == EOF ? EofE : (uint)(ch & 0xFF);
        }
        var str = new List<byte>();
        uint s = CvtScratch(0x40);
        try
        {
            while (true)
            {
                int ch = fgetc(file);
                if (ch == EOF) return EofE;
                str.Add((byte)ch);
                if (str.Count > 0x30) return EofE;
                uint buf = s + 0x10;
                for (int i = 0; i < str.Count; i++) GuestMemory.Write(buf + (uint)i, str[i]);
                int r = (int)VCall(fb + FbCvt, VcIn, fb + FbState, buf, buf + (uint)str.Count, s + 4, s, s + e, s + 8);
                if (r is 0 or 1)
                {
                    int used = (int)(GuestMemory.R32(s + 4) - buf);
                    if (GuestMemory.R32(s + 8) != s)
                    {
                        for (int i = str.Count - 1; i >= used; i--) ungetc(str[i], file);
                        return RdE(s, w);
                    }
                    if (used >= str.Count) str.Clear();
                    else if (used > 0) str.RemoveRange(0, used);
                }
                else if (r == 3)
                {
                    if (str.Count >= (int)e) return w ? (uint)(str[0] | str[1] << 8) : str[0];
                }
                else return EofE;
            }
        }
        finally { free(s); }
    }

    [Hle("_ZNSt13basic_filebufIcSt11char_traitsIcEE5uflowEv")] uint FbUflowC(uint fb) => FilebufUflow(fb, false);
    [Hle("_ZNSt13basic_filebufIwSt11char_traitsIwEE5uflowEv")] uint FbUflowW(uint fb) => FilebufUflow(fb, true);

    /// <summary>The file position as fpos_t (64-bit offset, then a zero state), false when the file has none.</summary>
    bool FilePos(uint file, out long pos)
    {
        pos = -1;
        if (Get(file) is not { } s) return false;
        pos = Tell(s);
        return pos >= 0;
    }

    void GoodPos(uint ret, long pos, uint fb)
    {
        GuestMemory.W32(ret, 0);
        GuestMemory.Write(ret + 8, pos);
        GuestMemory.Write(ret + 0x10, 0UL);
        GuestMemory.Write(ret + 0x18, GuestMemory.Read<ulong>(fb + FbState));
    }

    /// <summary>seekoff(off, way, mode): pending output ends first (_Endwrite); a seek relative to the current position
    /// counts a character waiting in the putback buffer.</summary>
    void FilebufSeekoff(uint ret, uint fb, int off, int way, bool w)
    {
        if (Deref(fb, SbIGnext) == fb + FbMychar && way == 1 && CvtNoconv(fb)) off--;
        uint file = GuestMemory.R32(fb + FbFile);
        if (file != 0 && FilebufEndwrite(fb, w) && ((off == 0 && way == 1) || fseek(GuestMemory.R32(fb + FbFile), off, way) == 0)
            && FilePos(GuestMemory.R32(fb + FbFile), out long pos))
        {
            FilebufResetBack(fb, w);
            GoodPos(ret, pos, fb);
            return;
        }
        BadPos(ret);
    }

    [Hle("_ZNSt13basic_filebufIcSt11char_traitsIcEE7seekoffElNSt5_IosbIiE8_SeekdirENS4_9_OpenmodeE")]
    void FbSeekoffC(uint ret, uint fb, int off, int way, int mode) => FilebufSeekoff(ret, fb, off, way, false);
    [Hle("_ZNSt13basic_filebufIwSt11char_traitsIwEE7seekoffElNSt5_IosbIiE8_SeekdirENS4_9_OpenmodeE")]
    void FbSeekoffW(uint ret, uint fb, int off, int way, int mode) => FilebufSeekoff(ret, fb, off, way, true);

    /// <summary>seekpos(pos, mode): fsetpos to the fpos_t, then the streamoff relative to it; the state comes back.</summary>
    void FilebufSeekpos(uint ret, uint fb, uint pos, bool w)
    {
        int off = (int)GuestMemory.R32(pos);
        uint file = GuestMemory.R32(fb + FbFile);
        if (file != 0 && FilebufEndwrite(fb, w) && fsetpos(GuestMemory.R32(fb + FbFile), pos + 8) == 0
            && (off == 0 || fseek(GuestMemory.R32(fb + FbFile), off, 1) == 0) && FilePos(GuestMemory.R32(fb + FbFile), out long p))
        {
            GuestMemory.Write(fb + FbState, GuestMemory.Read<ulong>(pos + 0x18));
            FilebufResetBack(fb, w);
            GoodPos(ret, p, fb);
            return;
        }
        BadPos(ret);
    }

    [Hle("_ZNSt13basic_filebufIcSt11char_traitsIcEE7seekposESt4fposISt9_MbstatetENSt5_IosbIiE9_OpenmodeE")]
    void FbSeekposC(uint ret, uint fb, uint pos, int mode) => FilebufSeekpos(ret, fb, pos, false);
    [Hle("_ZNSt13basic_filebufIwSt11char_traitsIwEE7seekposESt4fposISt9_MbstatetENSt5_IosbIiE9_OpenmodeE")]
    void FbSeekposW(uint ret, uint fb, uint pos, int mode) => FilebufSeekpos(ret, fb, pos, true);

    /// <summary>setbuf(buf, n): setvbuf on the file (unbuffered for a null buffer of size 0), then _Init(file,
    /// _Openfl); 0 when the file refuses or there is none.</summary>
    uint FilebufSetbuf(uint fb, uint buf, int n, bool w)
    {
        uint file = GuestMemory.R32(fb + FbFile);
        if (file == 0 || setvbuf(file, buf, buf == 0 && n == 0 ? 2 : 0, (uint)n * ESize(w)) != 0) return 0;
        FilebufInit(fb, file, true, w);
        return fb;
    }

    [Hle("_ZNSt13basic_filebufIcSt11char_traitsIcEE6setbufEPci")] uint FbSetbufC(uint fb, uint buf, int n) => FilebufSetbuf(fb, buf, n, false);
    [Hle("_ZNSt13basic_filebufIwSt11char_traitsIwEE6setbufEPwi")] uint FbSetbufW(uint fb, uint buf, int n) => FilebufSetbuf(fb, buf, n, true);

    /// <summary>sync: 0 with no file, when overflow() fails, or when fflush succeeds; else -1.</summary>
    [Hle("_ZNSt13basic_filebufIcSt11char_traitsIcEE4syncEv")]
    [Hle("_ZNSt13basic_filebufIwSt11char_traitsIwEE4syncEv")]
    int FbSync(uint fb)
    {
        if (GuestMemory.R32(fb + FbFile) == 0 || VCall(fb, VsOverflow, EofE) == EofE) return 0;
        return fflush(GuestMemory.R32(fb + FbFile)) >= 0 ? 0 : -1;
    }

    // ------------------------------------------------------------------ _ctype<char>

    /// <summary>The C locale classification table of the module (_Ctype): 0x01 hex digit, 0x02 upper, 0x04 space
    /// character, 0x08 punctuation, 0x10 lower, 0x20 digit, 0x40 other white space, 0x80 control, 0x400 blank (tab).</summary>
    internal static short CtypeMask(int c) => c switch
    {
        9 => 0x4C0,
        >= 10 and <= 13 => 0xC0,
        >= 0 and < 32 or 127 => 0x80,
        32 => 0x04,
        >= '0' and <= '9' => 0x21,
        >= 'A' and <= 'F' => 0x03,
        >= 'G' and <= 'Z' => 0x02,
        >= 'a' and <= 'f' => 0x11,
        >= 'g' and <= 'z' => 0x10,
        > 32 and < 127 => 0x08,
        _ => 0,
    };

    [Hle("_ZNSt6_ctypeIcED1Ev")] uint CtypeDtorC(uint obj) { GuestMemory.W32(obj, Rtti.Vptr(VtCtypeC)); return obj; }
    [Hle("_ZNSt6_ctypeIcED0Ev")] uint CtypeDelC(uint obj) { CtypeDtorC(obj); free(obj); return obj; }

    [Hle("_ZNKSt6_ctypeIcE10do_tolowerEc")] int CtypeTolowerC(uint obj, int c) => (sbyte)tolower((byte)c);
    [Hle("_ZNKSt6_ctypeIcE10do_toupperEc")] int CtypeToupperC(uint obj, int c) => (sbyte)toupper((byte)c);

    [Hle("_ZNKSt6_ctypeIcE10do_tolowerEPcPKc")]
    uint CtypeTolowerRangeC(uint obj, uint f, uint l) { for (; f != l; f++) GuestMemory.Write(f, (byte)tolower(B(f))); return l; }

    [Hle("_ZNKSt6_ctypeIcE10do_toupperEPcPKc")]
    uint CtypeToupperRangeC(uint obj, uint f, uint l) { for (; f != l; f++) GuestMemory.Write(f, (byte)toupper(B(f))); return l; }

    [Hle("_ZNKSt6_ctypeIcE8do_widenEc")] int CtypeWidenC(uint obj, int c) => (sbyte)c;
    [Hle("_ZNKSt6_ctypeIcE9do_narrowEcc")] int CtypeNarrowC(uint obj, int c, int dflt) => (sbyte)c;

    [Hle("_ZNKSt6_ctypeIcE8do_widenEPKcS2_Pc")]
    uint CtypeWidenRangeC(uint obj, uint f, uint l, uint dst) { if (l != f) GuestMemory.Copy(dst, f, l - f); return l; }

    [Hle("_ZNKSt6_ctypeIcE9do_narrowEPKcS2_cPc")]
    uint CtypeNarrowRangeC(uint obj, uint f, uint l, int dflt, uint dst) { if (l != f) GuestMemory.Copy(dst, f, l - f); return l; }

    // ------------------------------------------------------------------ _ctype<wchar_t> (through the C conversions)

    [Hle("_ZNSt6_ctypeIwED1Ev")] uint CtypeDtorW(uint obj) { GuestMemory.W32(obj, Rtti.Vptr(VtCtypeW)); return obj; }
    [Hle("_ZNSt6_ctypeIwED0Ev")] uint CtypeDelW(uint obj) { CtypeDtorW(obj); free(obj); return obj; }

    const uint VwIs = 8, VwWiden = 0x28, VwNarrow = 0x30;

    /// <summary>do_is(mask, c): the class of narrow(c, 0) in the table.</summary>
    [Hle("_ZNKSt6_ctypeIwE5do_isEsw")]
    int CtypeIsW(uint obj, int mask, int c) => (CtypeMask((byte)VCall(obj, VwNarrow, (ushort)c, 0)) & (short)mask) != 0 ? 1 : 0;

    [Hle("_ZNKSt6_ctypeIwE5do_isEPKwS2_Ps")]
    uint CtypeIsRangeW(uint obj, uint f, uint l, uint dst)
    {
        for (; f != l; f += 2, dst += 2) GuestMemory.Write(dst, CtypeMask((byte)VCall(obj, VwNarrow, W(f), 0)));
        return l;
    }

    [Hle("_ZNKSt6_ctypeIwE10do_scan_isEsPKwS2_")]
    uint CtypeScanIsW(uint obj, int mask, uint f, uint l) { for (; f != l && VCall(obj, VwIs, (uint)(short)mask, W(f)) == 0; f += 2) { } return f; }

    [Hle("_ZNKSt6_ctypeIwE11do_scan_notEsPKwS2_")]
    uint CtypeScanNotW(uint obj, int mask, uint f, uint l) { for (; f != l && VCall(obj, VwIs, (uint)(short)mask, W(f)) != 0; f += 2) { } return f; }

    /// <summary>do_tolower/do_toupper(c): through narrow(c, 0) and widen of the converted byte; c itself when it does
    /// not narrow.</summary>
    uint CtypeCaseW(uint obj, uint c, bool upper)
    {
        uint n = VCall(obj, VwNarrow, (ushort)c, 0) & 0xFF;
        if (n == 0) return (ushort)c;
        return VCall(obj, VwWiden, (uint)(sbyte)(upper ? toupper((int)n) : tolower((int)n)));
    }

    [Hle("_ZNKSt6_ctypeIwE10do_tolowerEw")] uint CtypeTolowerW(uint obj, uint c) => CtypeCaseW(obj, c, false);
    [Hle("_ZNKSt6_ctypeIwE10do_toupperEw")] uint CtypeToupperW(uint obj, uint c) => CtypeCaseW(obj, c, true);

    [Hle("_ZNKSt6_ctypeIwE10do_tolowerEPwPKw")]
    uint CtypeTolowerRangeW(uint obj, uint f, uint l) { for (; f != l; f += 2) CaseInPlaceW(obj, f, false); return l; }

    [Hle("_ZNKSt6_ctypeIwE10do_toupperEPwPKw")]
    uint CtypeToupperRangeW(uint obj, uint f, uint l) { for (; f != l; f += 2) CaseInPlaceW(obj, f, true); return l; }

    void CaseInPlaceW(uint obj, uint at, bool upper)
    {
        uint n = VCall(obj, VwNarrow, W(at), 0) & 0xFF;
        if (n != 0) GuestMemory.Write(at, (ushort)VCall(obj, VwWiden, (uint)(sbyte)(upper ? toupper((int)n) : tolower((int)n))));
    }

    /// <summary>widen(c): the byte converted from a fresh state (WEOF when it is not a whole character).</summary>
    static ushort WidenByte(byte b) => b < 0x80 ? b : (ushort)0xFFFF;

    /// <summary>narrow(c, dflt): the character when it converts to exactly one byte, else dflt.</summary>
    static int NarrowWide(ushort c, int dflt) => c < 0x80 ? c : (sbyte)dflt;

    [Hle("_ZNKSt6_ctypeIwE8do_widenEc")] uint CtypeWidenW(uint obj, int c) => WidenByte((byte)c);
    [Hle("_ZNKSt6_ctypeIwE9do_narrowEwc")] int CtypeNarrowW(uint obj, uint c, int dflt) => NarrowWide((ushort)c, dflt);

    [Hle("_ZNKSt6_ctypeIwE8do_widenEPKcS2_Pw")]
    uint CtypeWidenRangeW(uint obj, uint f, uint l, uint dst) { for (; f != l; f++, dst += 2) GuestMemory.Write(dst, WidenByte(B(f))); return l; }

    [Hle("_ZNKSt6_ctypeIwE9do_narrowEPKwS2_cPc")]
    uint CtypeNarrowRangeW(uint obj, uint f, uint l, int dflt, uint dst)
    {
        for (; f != l; f += 2, dst++) GuestMemory.Write(dst, (byte)NarrowWide(W(f), dflt));
        return l;
    }

    // ------------------------------------------------------------------ _codecvt_base, _codecvt<char, char>, _codecvt<wchar_t, char>

    [Hle("_ZNSt13_codecvt_baseD1Ev")]
    [Hle("_ZNSt8_codecvtIccSt9_MbstatetED1Ev")]
    [Hle("_ZNSt8_codecvtIwcSt9_MbstatetED1Ev")]
    uint CodecvtDtor(uint obj) { GuestMemory.W32(obj, Rtti.Vptr(VtCodecvtBase)); return obj; }

    [Hle("_ZNSt13_codecvt_baseD0Ev")]
    [Hle("_ZNSt8_codecvtIccSt9_MbstatetED0Ev")]
    [Hle("_ZNSt8_codecvtIwcSt9_MbstatetED0Ev")]
    uint CodecvtDel(uint obj) { CodecvtDtor(obj); free(obj); return obj; }

    /// <summary>do_in / do_out of _codecvt&lt;char, char&gt;: noconv, next pointers at the starts.</summary>
    [Hle("_ZNKSt8_codecvtIccSt9_MbstatetE5do_inERS0_PKcS4_RS4_PcS6_RS6_")]
    [Hle("_ZNKSt8_codecvtIccSt9_MbstatetE6do_outERS0_PKcS4_RS4_PcS6_RS6_")]
    int CodecvtNoconvC(uint obj, uint state, uint from, uint fromEnd, uint fromNext, uint to, uint toEnd, uint toNext)
    {
        GuestMemory.W32(fromNext, from);
        GuestMemory.W32(toNext, to);
        return 3;
    }

    [Hle("_ZNKSt8_codecvtIccSt9_MbstatetE10do_unshiftERS0_PcS3_RS3_")]
    int CodecvtUnshiftC(uint obj, uint state, uint to, uint toEnd, uint toNext) { GuestMemory.W32(toNext, to); return 3; }

    [Hle("_ZNKSt8_codecvtIccSt9_MbstatetE9do_lengthERKS0_PKcS5_j")]
    uint CodecvtLengthC(uint obj, uint state, uint from, uint end, uint max) => Math.Min(max, end - from);

    /// <summary>do_in: multibyte to wchar_t one character at a time (partial when nothing was converted, an
    /// incomplete sequence consumes the rest of the input into the state).</summary>
    [Hle("_ZNKSt8_codecvtIwcSt9_MbstatetE5do_inERS0_PKcS4_RS4_PwS6_RS6_")]
    int CodecvtInW(uint obj, uint state, uint from, uint fromEnd, uint fromNext, uint to, uint toEnd, uint toNext)
    {
        GuestMemory.W32(fromNext, from);
        GuestMemory.W32(toNext, to);
        int result = from != fromEnd ? 1 : 0;
        while (GuestMemory.R32(fromNext) != fromEnd && GuestMemory.R32(toNext) != toEnd)
        {
            uint fn = GuestMemory.R32(fromNext), tn = GuestMemory.R32(toNext);
            uint n = Mbr(tn, fn, fromEnd - fn, state);
            if (n == MbInvalid) return 2;
            if (n == MbIncomplete) { GuestMemory.W32(fromNext, fromEnd); return result; }
            if (n == 0) n = 1; // the NUL character
            GuestMemory.W32(fromNext, fn + n);
            result = 0;
            GuestMemory.W32(toNext, tn + 2);
        }
        return result;
    }

    /// <summary>do_out: wchar_t to multibyte; a character that does not fit in the rest of the output stops it
    /// (state restored).</summary>
    [Hle("_ZNKSt8_codecvtIwcSt9_MbstatetE6do_outERS0_PKwS4_RS4_PcS6_RS6_")]
    int CodecvtOutW(uint obj, uint state, uint from, uint fromEnd, uint fromNext, uint to, uint toEnd, uint toNext)
    {
        GuestMemory.W32(fromNext, from);
        GuestMemory.W32(toNext, to);
        int result = from != fromEnd ? 1 : 0;
        while (GuestMemory.R32(fromNext) != fromEnd && GuestMemory.R32(toNext) != toEnd)
        {
            uint fn = GuestMemory.R32(fromNext), tn = GuestMemory.R32(toNext);
            int wc = W(fn);
            if (!ValidWide(wc)) { SetErrno(EILSEQ); return 2; }
            var bytes = Utf8Of(wc);
            if (toEnd - tn < bytes.Length) return result;
            bytes.CopyTo(GuestMemory.Span(tn, bytes.Length));
            GuestMemory.Write(state, 0UL);
            GuestMemory.W32(fromNext, fn + 2);
            result = 0;
            GuestMemory.W32(toNext, tn + (uint)bytes.Length);
        }
        return result;
    }

    /// <summary>do_unshift: the bytes before the terminating NUL of wcrtomb(0) (none for UTF-8).</summary>
    [Hle("_ZNKSt8_codecvtIwcSt9_MbstatetE10do_unshiftERS0_PcS3_RS3_")]
    int CodecvtUnshiftW(uint obj, uint state, uint to, uint toEnd, uint toNext)
    {
        GuestMemory.W32(toNext, to);
        GuestMemory.Write(state, 0UL);
        return 0;
    }

    /// <summary>do_length: how many wchar_t the bytes make, at most max (an incomplete or bad sequence stops).</summary>
    [Hle("_ZNKSt8_codecvtIwcSt9_MbstatetE9do_lengthERKS0_PKcS5_j")]
    uint CodecvtLengthW(uint obj, uint state, uint from, uint end, uint max)
    {
        uint st = HeapAlloc(8, HeapAlign);
        if (st == 0) return 0;
        try
        {
            GuestMemory.Write(st, GuestMemory.Read<ulong>(state));
            uint count = 0;
            while (count < max && from != end)
            {
                uint n = Mbr(0, from, end - from, st);
                if (n is MbInvalid or MbIncomplete) break;
                if (n == 0) n = 1;
                count++;
                from += n;
            }
            return count;
        }
        finally { free(st); }
    }
}
