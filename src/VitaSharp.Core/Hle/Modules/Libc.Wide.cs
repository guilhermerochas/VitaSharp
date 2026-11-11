using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceLibc wide characters: wchar_t is 16 bits (UTF-16 code units), the multibyte form is UTF-8 (MB_CUR_MAX 4: libc
// clamps mbtowc's count to 4), classification is the C locale's (ASCII). mbstate_t is 8 bytes: +0 pending code point,
// +4 bytes still expected.
public sealed partial class Libc
{
    // ------------------------------------------------------------------ wcs* / wmem*

    [Hle] uint wcslen(uint s) => WcsLen(s);

    [Hle]
    uint wcsnlen_s(uint s, uint max)
    {
        if (s == 0) return 0;
        uint n = 0;
        while (n < max && W(s + n * 2) != 0) n++;
        return n;
    }

    [Hle] uint wcscpy(uint dst, uint src) { GuestMemory.Copy(dst, src, (WcsLen(src) + 1) * 2); return dst; }

    [Hle]
    uint wcsncpy(uint dst, uint src, uint n)
    {
        uint i = 0;
        for (; i < n; i++) { ushort c = W(src + i * 2); GuestMemory.Write(dst + i * 2, c); if (c == 0) break; }
        for (; i < n; i++) GuestMemory.Write(dst + i * 2, (ushort)0);
        return dst;
    }

    [Hle] uint wcscat(uint dst, uint src) { GuestMemory.Copy(dst + WcsLen(dst) * 2, src, (WcsLen(src) + 1) * 2); return dst; }

    [Hle]
    uint wcsncat(uint dst, uint src, uint n)
    {
        uint d = dst + WcsLen(dst) * 2, i = 0;
        for (; i < n; i++) { ushort c = W(src + i * 2); if (c == 0) break; GuestMemory.Write(d + i * 2, c); }
        GuestMemory.Write(d + i * 2, (ushort)0);
        return dst;
    }

    [Hle]
    int wcscmp(uint a, uint b)
    {
        for (uint i = 0; ; i++)
        {
            int ca = W(a + i * 2), cb = W(b + i * 2);
            if (ca != cb) return ca < cb ? -1 : 1;
            if (ca == 0) return 0;
        }
    }

    [Hle]
    int wcsncmp(uint a, uint b, uint n)
    {
        for (uint i = 0; i < n; i++)
        {
            int ca = W(a + i * 2), cb = W(b + i * 2);
            if (ca != cb) return ca < cb ? -1 : 1;
            if (ca == 0) return 0;
        }
        return 0;
    }

    [Hle] int wcscoll(uint a, uint b) => wcscmp(a, b);

    [Hle]
    uint wcsxfrm(uint dst, uint src, uint n)
    {
        uint len = WcsLen(src);
        if (len < n) GuestMemory.Copy(dst, src, (len + 1) * 2);
        return len;
    }

    [Hle]
    uint wcschr(uint s, int ch)
    {
        ushort c = (ushort)ch;
        for (uint p = s; ; p += 2) { ushort w = W(p); if (w == c) return p; if (w == 0) return 0; }
    }

    [Hle]
    uint wcsrchr(uint s, int ch)
    {
        ushort c = (ushort)ch;
        uint last = 0;
        for (uint p = s; ; p += 2) { ushort w = W(p); if (w == c) last = p; if (w == 0) return last; }
    }

    [Hle]
    uint wcsstr(uint s1, uint s2)
    {
        string hay = ReadWide(s1), needle = ReadWide(s2);
        int i = hay.IndexOf(needle, StringComparison.Ordinal);
        return i < 0 ? 0 : s1 + (uint)i * 2;
    }

    static bool InWideSet(uint set, ushort c) { for (uint p = set; ; p += 2) { ushort w = W(p); if (w == 0) return false; if (w == c) return true; } }

    [Hle] uint wcsspn(uint s, uint set) { uint n = 0; for (ushort c; (c = W(s + n * 2)) != 0 && InWideSet(set, c); n++) { } return n; }
    [Hle] uint wcscspn(uint s, uint set) { uint n = 0; for (ushort c; (c = W(s + n * 2)) != 0 && !InWideSet(set, c); n++) { } return n; }

    [Hle]
    uint wcspbrk(uint s, uint set)
    {
        for (uint p = s; ; p += 2) { ushort c = W(p); if (c == 0) return 0; if (InWideSet(set, c)) return p; }
    }

    /// <summary>wcstok(s, delim, &amp;ptr) (C95 three-argument form).</summary>
    [Hle]
    uint wcstok(uint s, uint delim, uint savePtr)
    {
        uint p = s != 0 ? s : GuestMemory.R32(savePtr);
        if (p == 0) return 0;
        p += wcsspn(p, delim) * 2;
        if (W(p) == 0) { GuestMemory.W32(savePtr, p); return 0; }
        uint end = p + wcscspn(p, delim) * 2;
        if (W(end) != 0) { GuestMemory.Write(end, (ushort)0); GuestMemory.W32(savePtr, end + 2); }
        else GuestMemory.W32(savePtr, end);
        return p;
    }

    [Hle]
    uint wcstok_s(uint s, uint smaxPtr, uint delim, uint savePtr)
    {
        if (smaxPtr == 0 || delim == 0 || savePtr == 0 || (s == 0 && GuestMemory.R32(savePtr) == 0)) { Constraint("wcstok_s: bad argument"); return 0; }
        uint start = s != 0 ? s : GuestMemory.R32(savePtr);
        uint r = wcstok(s, delim, savePtr);
        uint used = (GuestMemory.R32(savePtr) - start) / 2, max = GuestMemory.R32(smaxPtr);
        GuestMemory.W32(smaxPtr, used > max ? 0 : max - used);
        return r;
    }

    [Hle] uint wmemcpy(uint dst, uint src, uint n) { if (n != 0) GuestMemory.Copy(dst, src, n * 2); return dst; }
    [Hle] uint wmemmove(uint dst, uint src, uint n) { if (n != 0) GuestMemory.Copy(dst, src, n * 2); return dst; }

    [Hle]
    uint wmemset(uint dst, int c, uint n)
    {
        for (uint i = 0; i < n; i++) GuestMemory.Write(dst + i * 2, (ushort)c);
        return dst;
    }

    [Hle]
    int wmemcmp(uint a, uint b, uint n)
    {
        for (uint i = 0; i < n; i++)
        {
            int ca = W(a + i * 2), cb = W(b + i * 2);
            if (ca != cb) return ca < cb ? -1 : 1;
        }
        return 0;
    }

    [Hle]
    uint wmemchr(uint s, int c, uint n)
    {
        for (uint i = 0; i < n; i++) if (W(s + i * 2) == (ushort)c) return s + i * 2;
        return 0;
    }

    [Hle]
    int wcscpy_s(uint dst, uint size, uint src)
    {
        if (dst == 0 || BadSize(size)) return Constraint("wcscpy_s: bad destination argument");
        uint len = src == 0 ? uint.MaxValue : WcsLen(src);
        if (src == 0 || size <= len) { GuestMemory.Write(dst, (ushort)0); return Constraint("wcscpy_s: bad argument"); }
        GuestMemory.Copy(dst, src, (len + 1) * 2);
        return 0;
    }

    [Hle]
    int wcsncpy_s(uint dst, uint size, uint src, uint n)
    {
        if (dst == 0 || BadSize(size)) return Constraint("wcsncpy_s: bad destination argument");
        if (src == 0 || n > int.MaxValue) { GuestMemory.Write(dst, (ushort)0); return Constraint("wcsncpy_s: bad argument"); }
        uint len = wcsnlen_s(src, n);
        if (size <= len) { GuestMemory.Write(dst, (ushort)0); return Constraint("wcsncpy_s: bad argument"); }
        GuestMemory.Copy(dst, src, len * 2);
        GuestMemory.Write(dst + len * 2, (ushort)0);
        return 0;
    }

    [Hle]
    int wcscat_s(uint dst, uint size, uint src)
    {
        if (dst == 0 || BadSize(size)) return Constraint("wcscat_s: bad destination argument");
        uint dlen = wcsnlen_s(dst, size), slen = src == 0 ? uint.MaxValue : WcsLen(src);
        if (src == 0 || dlen == size || size - dlen <= slen) { GuestMemory.Write(dst, (ushort)0); return Constraint("wcscat_s: bad argument"); }
        GuestMemory.Copy(dst + dlen * 2, src, (slen + 1) * 2);
        return 0;
    }

    [Hle]
    int wcsncat_s(uint dst, uint size, uint src, uint n)
    {
        if (dst == 0 || BadSize(size)) return Constraint("wcsncat_s: bad destination argument");
        uint dlen = wcsnlen_s(dst, size);
        if (src == 0 || n > int.MaxValue || dlen == size) { GuestMemory.Write(dst, (ushort)0); return Constraint("wcsncat_s: bad argument"); }
        uint slen = wcsnlen_s(src, n);
        if (size - dlen <= slen) { GuestMemory.Write(dst, (ushort)0); return Constraint("wcsncat_s: bad argument"); }
        GuestMemory.Copy(dst + dlen * 2, src, slen * 2);
        GuestMemory.Write(dst + (dlen + slen) * 2, (ushort)0);
        return 0;
    }

    [Hle]
    int wmemcpy_s(uint dst, uint size, uint src, uint n)
    {
        if (dst == 0 || size > int.MaxValue / 2) return Constraint("wmemcpy_s: bad destination argument");
        if (src == 0 || n > size || Overlap(dst, n * 2, src, n * 2)) { if (size != 0) GuestMemory.Fill(dst, 0, size * 2); return Constraint("wmemcpy_s: bad argument"); }
        if (n != 0) GuestMemory.Copy(dst, src, n * 2);
        return 0;
    }

    [Hle]
    int wmemmove_s(uint dst, uint size, uint src, uint n)
    {
        if (dst == 0 || size > int.MaxValue / 2) return Constraint("wmemmove_s: bad destination argument");
        if (src == 0 || n > size) { if (size != 0) GuestMemory.Fill(dst, 0, size * 2); return Constraint("wmemmove_s: bad argument"); }
        if (n != 0) GuestMemory.Copy(dst, src, n * 2);
        return 0;
    }

    // ------------------------------------------------------------------ wide classification (C locale)

    [Hle] int iswalnum(int c) => c is >= 0 and < 0x80 ? isalnum(c) : 0;
    [Hle] int iswalpha(int c) => c is >= 0 and < 0x80 ? isalpha(c) : 0;
    [Hle] int iswblank(int c) => c is >= 0 and < 0x80 ? isblank(c) : 0;
    [Hle] int iswcntrl(int c) => c is >= 0 and < 0x80 ? iscntrl(c) : 0;
    [Hle] int iswdigit(int c) => c is >= 0 and < 0x80 ? isdigit(c) : 0;
    [Hle] int iswgraph(int c) => c is >= 0 and < 0x80 ? isgraph(c) : 0;
    [Hle] int iswlower(int c) => c is >= 0 and < 0x80 ? islower(c) : 0;
    [Hle] int iswprint(int c) => c is >= 0 and < 0x80 ? isprint(c) : 0;
    [Hle] int iswpunct(int c) => c is >= 0 and < 0x80 ? ispunct(c) : 0;
    [Hle] int iswspace(int c) => c is >= 0 and < 0x80 ? isspace(c) : 0;
    [Hle] int iswupper(int c) => c is >= 0 and < 0x80 ? isupper(c) : 0;
    [Hle] int iswxdigit(int c) => c is >= 0 and < 0x80 ? isxdigit(c) : 0;
    [Hle] int towlower(int c) => c is >= 'A' and <= 'Z' ? c + 32 : c & 0xFFFF;
    [Hle] int towupper(int c) => c is >= 'a' and <= 'z' ? c - 32 : c & 0xFFFF;

    static readonly string[] WctypeNames = ["alnum", "alpha", "blank", "cntrl", "digit", "graph", "lower", "print", "punct", "space", "upper", "xdigit"];

    /// <summary>wctype(name): 1..12 for the standard classes, 0 for an unknown name.</summary>
    [Hle] uint wctype(string name) => (uint)(Array.IndexOf(WctypeNames, name) + 1);

    [Hle("iswctype")]
    [Hle("_Iswctype")]
    int iswctype(int c, uint desc) => desc switch
    {
        1 => iswalnum(c), 2 => iswalpha(c), 3 => iswblank(c), 4 => iswcntrl(c), 5 => iswdigit(c), 6 => iswgraph(c),
        7 => iswlower(c), 8 => iswprint(c), 9 => iswpunct(c), 10 => iswspace(c), 11 => iswupper(c), 12 => iswxdigit(c), _ => 0,
    };

    /// <summary>wctrans(name): 1 "tolower", 2 "toupper", 0 otherwise.</summary>
    [Hle] uint wctrans(string name) => name switch { "tolower" => 1u, "toupper" => 2u, _ => 0u };

    [Hle("towctrans")]
    [Hle("_Towctrans")]
    int towctrans(int c, uint desc) => desc switch { 1 => towlower(c), 2 => towupper(c), _ => c & 0xFFFF };

    // ------------------------------------------------------------------ multibyte ⇄ wide (UTF-8 ⇄ UTF-16)

    const uint MbIncomplete = unchecked((uint)-2), MbInvalid = unchecked((uint)-1);

    readonly Dictionary<string, uint> _internalStates = new();

    /// <summary>The module's own mbstate_t for a function called with a NULL state.</summary>
    uint InternalState(string fn)
    {
        lock (_internalStates)
        {
            if (!_internalStates.TryGetValue(fn, out var p)) _internalStates[fn] = p = StaticAlloc(8);
            return p;
        }
    }

    /// <summary>mbrtowc core: decodes up to n bytes continuing <paramref name="state"/>. Returns the bytes used,
    /// 0 for the NUL character, -2 for an incomplete sequence (state keeps it), -1 (EILSEQ) for a bad one.</summary>
    static uint Mbr(uint pwc, uint s, uint n, uint state)
    {
        uint cp = GuestMemory.R32(state), need = GuestMemory.R32(state + 4);
        uint used = 0;
        while (used < n)
        {
            byte b = B(s + used++);
            if (need == 0)
            {
                if (b < 0x80) { cp = b; }
                else if (b is >= 0xC2 and < 0xE0) { cp = b & 0x1Fu; need = 1; }
                else if (b is >= 0xE0 and < 0xF0) { cp = b & 0x0Fu; need = 2; }
                else if (b is >= 0xF0 and < 0xF5) { cp = b & 0x07u; need = 3; }
                else { GuestMemory.Write(state, 0UL); SetErrno(EILSEQ); return MbInvalid; }
            }
            else
            {
                if ((b & 0xC0) != 0x80) { GuestMemory.Write(state, 0UL); SetErrno(EILSEQ); return MbInvalid; }
                cp = (cp << 6) | (b & 0x3Fu);
                need--;
            }
            if (need == 0)
            {
                GuestMemory.Write(state, 0UL);
                if (cp > 0xFFFF || cp is >= 0xD800 and < 0xE000) { SetErrno(EILSEQ); return MbInvalid; }
                if (pwc != 0) GuestMemory.Write(pwc, (ushort)cp);
                return cp == 0 ? 0 : used;
            }
        }
        GuestMemory.W32(state, cp);
        GuestMemory.W32(state + 4, need);
        return MbIncomplete;
    }

    static byte[] Utf8Of(int wc) => wc < 0x80 ? [(byte)wc] : wc < 0x800 ? [(byte)(0xC0 | (wc >> 6)), (byte)(0x80 | (wc & 0x3F))]
        : [(byte)(0xE0 | (wc >> 12)), (byte)(0x80 | ((wc >> 6) & 0x3F)), (byte)(0x80 | (wc & 0x3F))];

    static bool ValidWide(int wc) => wc is >= 0 and <= 0xFFFF and not (>= 0xD800 and < 0xE000);

    [Hle]
    uint mbrtowc(uint pwc, uint s, uint n, uint state)
    {
        if (state == 0) state = InternalState("mbrtowc");
        if (s == 0) { GuestMemory.Write(state, 0UL); return 0; }
        return n == 0 ? MbIncomplete : Mbr(pwc, s, n, state);
    }

    [Hle] uint mbrlen(uint s, uint n, uint state) => mbrtowc(0, s, n, state != 0 ? state : InternalState("mbrlen"));

    [Hle("mbtowc")]
    [Hle("_Mbtowc")]
    int mbtowc(uint pwc, uint s, uint n)
    {
        uint st = InternalState("mbtowc");
        if (s == 0) { GuestMemory.Write(st, 0UL); return 0; } // stateless encoding
        GuestMemory.Write(st, 0UL);
        uint r = Mbr(pwc, s, Math.Min(n, 4), st);
        if (r == MbIncomplete) { GuestMemory.Write(st, 0UL); SetErrno(EILSEQ); return -1; }
        return (int)r;
    }

    [Hle]
    int mblen(uint s, uint n)
    {
        if (s == 0) return 0;
        uint st = InternalState("mblen");
        GuestMemory.Write(st, 0UL);
        uint r = Mbr(0, s, Math.Min(n, 4), st);
        return r == MbIncomplete ? -1 : (int)r;
    }

    [Hle] int mbsinit(uint state) => state == 0 || GuestMemory.R32(state + 4) == 0 ? 1 : 0;

    [Hle]
    uint wcrtomb(uint s, int wc, uint state)
    {
        if (state == 0) state = InternalState("wcrtomb");
        if (s == 0) { GuestMemory.Write(state, 0UL); return 1; }
        wc &= 0xFFFF;
        if (!ValidWide(wc)) { SetErrno(EILSEQ); return MbInvalid; }
        var b = Utf8Of(wc);
        b.CopyTo(GuestMemory.Span(s, b.Length));
        GuestMemory.Write(state, 0UL);
        return (uint)b.Length;
    }

    [Hle]
    int wcrtomb_s(uint retval, uint s, uint smax, int wc, uint state)
    {
        if (retval == 0 || (s != 0 && BadSize(smax))) return Constraint("wcrtomb_s: bad argument");
        if (s != 0 && smax < Utf8Of(wc & 0xFFFF).Length) { GuestMemory.Write(s, (byte)0); GuestMemory.W32(retval, MbInvalid); return Constraint("wcrtomb_s: buffer too small"); }
        uint r = wcrtomb(s, wc, state);
        GuestMemory.W32(retval, r);
        return r == MbInvalid ? EILSEQ : 0;
    }

    [Hle("wctomb")]
    [Hle("_Wctomb")]
    int wctomb(uint s, int wc)
    {
        if (s == 0) return 0;
        wc &= 0xFFFF;
        if (!ValidWide(wc)) { SetErrno(EILSEQ); return -1; }
        var b = Utf8Of(wc);
        b.CopyTo(GuestMemory.Span(s, b.Length));
        return b.Length;
    }

    [Hle]
    int wctomb_s(uint status, uint s, uint smax, int wc)
    {
        if (status == 0 || (s != 0 && BadSize(smax))) return Constraint("wctomb_s: bad argument");
        if (s == 0) { GuestMemory.W32(status, 0); return 0; }
        int len = Utf8Of(wc & 0xFFFF).Length;
        if (smax < len) { GuestMemory.W32(status, unchecked((uint)-1)); return Constraint("wctomb_s: buffer too small"); }
        int r = wctomb(s, wc);
        GuestMemory.W32(status, (uint)r);
        return r < 0 ? EILSEQ : 0;
    }

    [Hle("btowc")]
    [Hle("_Btowc")]
    int btowc(int c) => c is >= 0 and < 0x80 ? c : WEOF;

    [Hle("wctob")]
    [Hle("_Wctob")]
    int wctob(int wc) => (wc & 0xFFFF) < 0x80 ? wc & 0xFFFF : EOF;

    /// <summary>mbsrtowcs core: converts *src until NUL or n wide characters (dst NULL = count only); *src advances
    /// (NULL after the terminator when dst is given).</summary>
    uint MbsToWcs(uint dst, uint srcPtr, uint n, uint state)
    {
        uint src = GuestMemory.R32(srcPtr), count = 0;
        while (dst == 0 || count < n)
        {
            uint tmp = dst != 0 ? dst + count * 2 : 0;
            uint r = Mbr(tmp, src, 4, state);
            if (r == MbInvalid || r == MbIncomplete) { if (dst != 0) GuestMemory.W32(srcPtr, src); SetErrno(EILSEQ); return MbInvalid; }
            if (r == 0) { if (dst != 0) GuestMemory.W32(srcPtr, 0); return count; }
            src += r;
            count++;
        }
        GuestMemory.W32(srcPtr, src);
        return count;
    }

    uint WcsToMbs(uint dst, uint srcPtr, uint n)
    {
        uint src = GuestMemory.R32(srcPtr), count = 0;
        while (true)
        {
            int wc = W(src);
            if (wc == 0)
            {
                if (dst != 0) { if (count < n) { GuestMemory.Write(dst + count, (byte)0); GuestMemory.W32(srcPtr, 0); } else GuestMemory.W32(srcPtr, src); }
                return count;
            }
            if (!ValidWide(wc)) { if (dst != 0) GuestMemory.W32(srcPtr, src); SetErrno(EILSEQ); return MbInvalid; }
            var b = Utf8Of(wc);
            if (dst != 0)
            {
                if (count + b.Length > n) { GuestMemory.W32(srcPtr, src); return count; }
                b.CopyTo(GuestMemory.Span(dst + count, b.Length));
            }
            count += (uint)b.Length;
            src += 2;
        }
    }

    [Hle] uint mbsrtowcs(uint dst, uint srcPtr, uint n, uint state) => MbsToWcs(dst, srcPtr, n, state != 0 ? state : InternalState("mbsrtowcs"));
    [Hle] uint wcsrtombs(uint dst, uint srcPtr, uint n, uint state) => WcsToMbs(dst, srcPtr, n);

    uint _convPtr;
    uint ConvPtr() { lock (_internalStates) return _convPtr != 0 ? _convPtr : _convPtr = StaticAlloc(4); }

    [Hle]
    uint mbstowcs(uint dst, uint src, uint n)
    {
        uint st = InternalState("mbstowcs");
        GuestMemory.Write(st, 0UL);
        lock (_internalStates)
        {
            GuestMemory.W32(ConvPtr(), src);
            return MbsToWcs(dst, ConvPtr(), n, st);
        }
    }

    [Hle]
    uint wcstombs(uint dst, uint src, uint n)
    {
        lock (_internalStates)
        {
            GuestMemory.W32(ConvPtr(), src);
            return WcsToMbs(dst, ConvPtr(), n);
        }
    }

    [Hle]
    int mbstowcs_s(uint retval, uint dst, uint dmax, uint src, uint len)
    {
        if (retval == 0 || src == 0 || (dst != 0 && BadSize(dmax))) return Constraint("mbstowcs_s: bad argument");
        uint r = mbstowcs(dst, src, dst != 0 ? Math.Min(len, dmax - 1) : len);
        if (r == MbInvalid) { if (dst != 0) GuestMemory.Write(dst, (ushort)0); GuestMemory.W32(retval, r); return EILSEQ; }
        if (dst != 0) GuestMemory.Write(dst + r * 2, (ushort)0);
        GuestMemory.W32(retval, r);
        return 0;
    }

    [Hle]
    int mbsrtowcs_s(uint retval, uint dst, uint dmax, uint srcPtr, uint len, uint state)
    {
        if (retval == 0 || srcPtr == 0 || GuestMemory.R32(srcPtr) == 0 || (dst != 0 && BadSize(dmax))) return Constraint("mbsrtowcs_s: bad argument");
        uint r = mbsrtowcs(dst, srcPtr, dst != 0 ? Math.Min(len, dmax - 1) : len, state);
        if (r == MbInvalid) { if (dst != 0) GuestMemory.Write(dst, (ushort)0); GuestMemory.W32(retval, r); return EILSEQ; }
        if (dst != 0) GuestMemory.Write(dst + r * 2, (ushort)0);
        GuestMemory.W32(retval, r);
        return 0;
    }

    [Hle]
    int wcstombs_s(uint retval, uint dst, uint dmax, uint src, uint len)
    {
        // as libc.suprx 3.74 does it: len counts wide characters; each one is converted only when its bytes fit in
        // what is left of dmax, otherwise the conversion is a constraint violation (dst emptied, *retval -1), not a
        // silent truncation; the terminator is added when there is room, and *retval set only then (or at the NUL)
        if (retval == 0 || src == 0 || (dst == 0 && dmax != 0) || (dst != 0 && (BadSize(dmax) || len > int.MaxValue)))
        {
            if (retval != 0) GuestMemory.W32(retval, MbInvalid);
            if (dst != 0 && !BadSize(dmax)) GuestMemory.Write(dst, (byte)0);
            return Constraint("wcstombs_s: bad argument");
        }
        uint written = 0;
        for (uint i = 0; i < len; i++)
        {
            int wc = W(src + i * 2);
            if (!ValidWide(wc)) { GuestMemory.W32(retval, MbInvalid); return EILSEQ; }
            byte[] bytes = wc == 0 ? [0] : Utf8Of(wc);
            if (dst != 0)
            {
                if (dmax - written < (uint)bytes.Length)
                {
                    GuestMemory.Write(dst, (byte)0);
                    GuestMemory.W32(retval, MbInvalid);
                    return Constraint("wcstombs_s: buffer overflow");
                }
                bytes.CopyTo(GuestMemory.Span(dst + written, bytes.Length));
            }
            written += (uint)bytes.Length;
            if (wc == 0) { GuestMemory.W32(retval, written - 1); return 0; }
        }
        if (written < dmax)
        {
            GuestMemory.W32(retval, written);
            if (dst != 0) GuestMemory.Write(dst + written, (byte)0);
        }
        return 0;
    }

    [Hle]
    int wcsrtombs_s(uint retval, uint dst, uint dmax, uint srcPtr, uint len, uint state)
    {
        // as libc.suprx 3.74 does it: here len counts bytes; a character whose bytes do not fit in len or in what is
        // left of dmax is a constraint violation ("buffer too small": *retval -1, a NUL where the conversion stopped,
        // *src at that character); a converted NUL ends it (*src NULL, *retval the bytes before it); without a
        // destination it only counts up to the NUL
        if (retval == 0 || srcPtr == 0 || GuestMemory.R32(srcPtr) == 0 || state == 0 || (dst == 0 && dmax != 0)
            || (dst != 0 && (BadSize(dmax) || len > int.MaxValue)))
        {
            if (dst != 0 && !BadSize(dmax)) GuestMemory.Write(dst, (byte)0);
            if (retval != 0) GuestMemory.W32(retval, MbInvalid);
            return Constraint("wcsrtombs_s: bad argument");
        }
        uint p = GuestMemory.R32(srcPtr), written = 0;
        while (true)
        {
            if (dst != 0 && (len == 0 || dmax - written == 0))
            {
                GuestMemory.W32(srcPtr, p);
                if (dmax - written != 0) { GuestMemory.Write(dst + written, (byte)0); GuestMemory.W32(retval, written); return 0; }
                // as SceLibc_8316C7E0 (FW 3.74): this NUL lands one byte past the buffer, at dst + dmax
                GuestMemory.Write(dst + dmax, (byte)0);
                GuestMemory.W32(retval, MbInvalid);
                return Constraint("wcstombs_s: buffer too small");
            }
            int wc = W(p);
            if (!ValidWide(wc)) { GuestMemory.W32(retval, MbInvalid); return EILSEQ; }
            byte[] bytes = wc == 0 ? [0] : Utf8Of(wc);
            if (dst != 0)
            {
                if ((uint)bytes.Length > len || (uint)bytes.Length > dmax - written)
                {
                    GuestMemory.W32(srcPtr, p);
                    GuestMemory.Write(dst + written, (byte)0);
                    GuestMemory.W32(retval, MbInvalid);
                    return Constraint("wcstombs_s: buffer too small");
                }
                bytes.CopyTo(GuestMemory.Span(dst + written, bytes.Length));
                len -= (uint)bytes.Length;
            }
            written += (uint)bytes.Length;
            if (wc == 0)
            {
                if (dst != 0) GuestMemory.W32(srcPtr, 0);
                GuestMemory.W32(retval, written - 1);
                return 0;
            }
            p += 2;
        }
    }

    // char16_t / char32_t (C11 uchar.h)
    [Hle] uint mbrtoc16(uint pc16, uint s, uint n, uint state) => mbrtowc(pc16, s, n, state != 0 ? state : InternalState("mbrtoc16"));

    [Hle]
    uint mbrtoc32(uint pc32, uint s, uint n, uint state)
    {
        if (state == 0) state = InternalState("mbrtoc32");
        if (s == 0) { GuestMemory.Write(state, 0UL); return 0; }
        uint tmp = InternalState("mbrtoc32.tmp");
        uint r = n == 0 ? MbIncomplete : Mbr(tmp, s, n, state);
        if (r != MbInvalid && r != MbIncomplete && pc32 != 0) GuestMemory.W32(pc32, W(tmp));
        return r;
    }

    [Hle] uint c16rtomb(uint s, int c16, uint state) => wcrtomb(s, c16, state != 0 ? state : InternalState("c16rtomb"));
    [Hle] uint c32rtomb(uint s, uint c32, uint state) => c32 > 0xFFFF ? Fail32() : wcrtomb(s, (int)c32, state != 0 ? state : InternalState("c32rtomb"));

    static uint Fail32() { SetErrno(EILSEQ); return MbInvalid; }
}
