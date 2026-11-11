using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePafStdc: the C library functions libpaf.suprx (FW 3.74) exports to the system applications as
/// sce_paf_private_*. They follow libpaf's own code: NULL arguments are answered instead of faulting (copies return
/// NULL, comparisons put NULL first, lengths are 0), lengths and counts are signed (0 or less does nothing), characters
/// compare as unsigned bytes, the wide functions work on 16-bit characters, the classification table is paf's 7-bit
/// one, and rand is paf's own 32-bit LCG. Memory comes from the HLE heap, formatting, conversions, sorting and files
/// from the HLE C library. Part of the fallback C library: a program that loads libpaf uses libpaf's.
/// </summary>
public sealed partial class Libc
{
    static ushort H(uint p) => GuestMemory.Read<ushort>(p);

    /// <summary>libpaf's classification table for 0..0x7F (1 upper, 2 lower, 4 digit, 8 space, 0x10 punctuation,
    /// 0x20 control, 0x40 hex letter), as its look_ctype_table reads it.</summary>
    static ReadOnlySpan<byte> PafCtype =>
    [
        0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x08, 0x08, 0x08, 0x08, 0x08, 0x20, 0x20,
        0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20,
        0x08, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10,
        0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10,
        0x10, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
        0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x10, 0x10, 0x10, 0x10, 0x10,
        0x10, 0x42, 0x42, 0x42, 0x42, 0x42, 0x42, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02,
        0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x10, 0x10, 0x10, 0x10, 0x20,
    ];

    /// <summary>The NULL rule of libpaf's comparisons: both NULL (or the same pointer) 0, a NULL first -1, else 1.</summary>
    static int? PafNullCmp(uint a, uint b) => a != 0 && b != 0 ? null : a == b ? 0 : a == 0 ? -1 : 1;

    // ---- memory
    [Hle("sce_paf_private_memcpy")]
    [Hle("sce_paf_private_memcpy2")]
    uint PafMemcpy(uint dst, uint src, int n) { if (dst != src && n > 0) GuestMemory.Copy(dst, src, (uint)n); return dst; }

    [Hle] uint sce_paf_private_memmove(uint dst, uint src, int n) { if (dst != src && n > 0) GuestMemory.Copy(dst, src, (uint)n); return dst; }

    [Hle("sce_paf_private_memset")]
    [Hle("sce_paf_private_memset2")]
    uint PafMemset(uint dst, int c, int n) { if (n > 0) GuestMemory.Fill(dst, (byte)c, (uint)n); return dst; }

    [Hle]
    int sce_paf_private_memcmp(uint a, uint b, int n)
    {
        for (uint i = 0; (int)i < n; i++)
            if (B(a + i) != B(b + i)) return B(a + i) - B(b + i);
        return 0;
    }

    [Hle]
    uint sce_paf_private_memchr(uint s, int c, int n)
    {
        for (uint i = 0; (int)i < n; i++) if (B(s + i) == (byte)c) return s + i;
        return 0;
    }

    /// <summary>(dst, src, n): n 16-bit characters (memcpy of n*2 bytes).</summary>
    [Hle] uint sce_paf_private_wmemcpy(uint dst, uint src, int n) => PafMemcpy(dst, src, n * 2);

    [Hle("sce_paf_private_malloc")]
    [Hle("sce_paf_private_malloc2")]
    uint PafMalloc(uint size) => malloc(size);

    [Hle("sce_paf_private_free")]
    [Hle("sce_paf_private_free2")]
    void PafFree(uint p) { if (p != 0) free(p); }

    [Hle] uint sce_paf_memalign(uint align, uint size) => memalign(align, size);

    // ---- strings
    [Hle] uint sce_paf_private_strlen(uint s) => s == 0 ? 0 : StrLen(s);

    [Hle]
    int sce_paf_private_strcmp(uint a, uint b)
    {
        if (PafNullCmp(a, b) is { } r) return r;
        for (; ; a++, b++)
        {
            byte x = B(a), y = B(b);
            if (x != y) return x - y;
            if (x == 0) return 0;
        }
    }

    [Hle]
    int sce_paf_private_strncmp(uint a, uint b, int n)
    {
        if (PafNullCmp(a, b) is { } r) return r;
        for (; n > 0; n--, a++, b++)
        {
            byte x = B(a), y = B(b);
            if (x != y) return x - y;
            if (x == 0) return 0;
        }
        return 0;
    }

    static int PafLower(byte c) => c <= 0x7F && (PafCtype[c] & 1) != 0 ? c + 32 : c;

    [Hle]
    int sce_paf_private_strcasecmp(uint a, uint b)
    {
        if (PafNullCmp(a, b) is { } r) return r;
        for (; ; a++, b++)
        {
            int x = PafLower(B(a)), y = PafLower(B(b));
            if (x != y) return x - y;
            if (x == 0) return 0;
        }
    }

    [Hle]
    int sce_paf_private_strncasecmp(uint a, uint b, int n)
    {
        if (PafNullCmp(a, b) is { } r) return r;
        for (; n > 0; n--, a++, b++)
        {
            int x = PafLower(B(a)), y = PafLower(B(b));
            if (x != y) return x - y;
            if (x == 0) return 0;
        }
        return 0;
    }

    [Hle]
    uint sce_paf_private_strcpy(uint dst, uint src)
    {
        if (dst == 0 || src == 0) return 0;
        uint d = dst;
        byte c;
        do { c = B(src++); GuestMemory.Write(d++, c); } while (c != 0);
        return dst;
    }

    [Hle]
    uint sce_paf_private_strncpy(uint dst, uint src, int n)
    {
        if (dst == 0 || src == 0) return 0;
        uint i = 0;
        for (; (int)i < n && B(src + i) != 0; i++) GuestMemory.Write(dst + i, B(src + i));
        for (; (int)i < n; i++) GuestMemory.Write(dst + i, (byte)0);
        return dst;
    }

    [Hle]
    uint sce_paf_private_strncat(uint dst, uint src, int n)
    {
        if (dst == 0 || src == 0) return 0;
        uint d = dst + StrLen(dst);
        for (; n > 0 && B(src) != 0; n--) GuestMemory.Write(d++, B(src++));
        GuestMemory.Write(d, (byte)0);
        return dst;
    }

    /// <summary>BSD strlcpy: copies at most size-1 characters, always terminated; returns strlen(src) (0 for NULL).</summary>
    [Hle]
    uint sce_paf_private_strlcpy(uint dst, uint src, uint size)
    {
        if (dst == 0 || src == 0) return 0;
        uint len = StrLen(src);
        if (size != 0)
        {
            uint n = Math.Min(len, size - 1);
            if (n != 0) GuestMemory.Copy(dst, src, n);
            GuestMemory.Write(dst + n, (byte)0);
        }
        return len;
    }

    /// <summary>BSD strlcat: appends while the result fits in size; returns the length it tried to create.</summary>
    [Hle]
    uint sce_paf_private_strlcat(uint dst, uint src, uint size)
    {
        if (dst == 0 || src == 0) return 0;
        uint dlen = 0;
        while (dlen < size && B(dst + dlen) != 0) dlen++;
        uint slen = StrLen(src);
        if (dlen == size) return size + slen;
        uint n = Math.Min(slen, size - dlen - 1);
        if (n != 0) GuestMemory.Copy(dst + dlen, src, n);
        GuestMemory.Write(dst + dlen + n, (byte)0);
        return dlen + slen;
    }

    /// <summary>(s, ch): ch is compared with the signed characters unconverted (libpaf's ldrsb compare).</summary>
    [Hle]
    uint sce_paf_private_strchr(uint s, int ch)
    {
        for (; ; s++)
        {
            int c = (sbyte)B(s);
            if (c == ch) return s;
            if (c == 0) return 0;
        }
    }

    [Hle]
    uint sce_paf_private_strrchr(uint s, int ch)
    {
        if (s == 0) return 0;
        int want = (sbyte)ch;
        uint found = 0;
        for (; ; s++)
        {
            int c = (sbyte)B(s);
            if (c == want) found = s;
            if (c == 0) return found;
        }
    }

    [Hle] uint sce_paf_private_strpbrk(uint s, uint set) => strpbrk(s, set);
    [Hle] uint sce_paf_private_strtok(uint s, uint delim) => strtok(s, delim);
    [Hle] uint sce_paf_private_strtok_r(uint s, uint delim, uint save) => s == 0 && (save == 0 || GuestMemory.R32(save) == 0) ? 0 : strtok_r(s, delim, save);

    // ---- 16-bit wide strings
    [Hle]
    uint sce_paf_private_wcslen(uint s)
    {
        if (s == 0) return 0;
        uint n = 0;
        while (H(s + n * 2) != 0) n++;
        return n;
    }

    [Hle]
    uint sce_paf_private_wcsnlen(uint s, uint max)
    {
        if (s == 0) return 0;
        uint n = 0;
        while (n < max && H(s + n * 2) != 0) n++;
        return n;
    }

    [Hle]
    uint sce_paf_private_wcschr(uint s, int ch)
    {
        if (s == 0) return 0;
        ushort want = (ushort)ch;
        for (; ; s += 2)
        {
            ushort c = H(s);
            if (c == want) return s;
            if (c == 0) return 0;
        }
    }

    [Hle]
    int sce_paf_private_wcscmp(uint a, uint b)
    {
        if (PafNullCmp(a, b) is { } r) return r;
        for (; ; a += 2, b += 2)
        {
            ushort x = H(a), y = H(b);
            if (x != y) return x - y;
            if (x == 0) return 0;
        }
    }

    [Hle]
    int sce_paf_private_wcsncmp(uint a, uint b, int n)
    {
        if (PafNullCmp(a, b) is { } r) return r;
        for (; n > 0; n--, a += 2, b += 2)
        {
            ushort x = H(a), y = H(b);
            if (x != y) return x - y;
            if (x == 0) return 0;
        }
        return 0;
    }

    [Hle]
    uint sce_paf_private_wcsncpy(uint dst, uint src, int n)
    {
        if (dst == 0 || src == 0) return 0;
        uint i = 0;
        for (; (int)i < n && H(src + i * 2) != 0; i++) GuestMemory.Write(dst + i * 2, H(src + i * 2));
        for (; (int)i < n; i++) GuestMemory.Write(dst + i * 2, (ushort)0);
        return dst;
    }

    [Hle]
    uint sce_paf_private_wcsncat(uint dst, uint src, int n)
    {
        if (dst == 0 || src == 0) return 0;
        uint d = dst + sce_paf_private_wcslen(dst) * 2;
        for (; n > 0 && H(src) != 0; n--, src += 2, d += 2) GuestMemory.Write(d, H(src));
        GuestMemory.Write(d, (ushort)0);
        return dst;
    }

    // ---- characters
    [Hle] int sce_paf_private_look_ctype_table(int c) => (uint)c > 0x7F ? 0 : PafCtype[c];
    [Hle] int sce_paf_private_toupper(int c) => (uint)c <= 0x7F && (PafCtype[c] & 2) != 0 ? c - 32 : c;
    [Hle] int sce_paf_private_tolower(int c) => (uint)c <= 0x7F && (PafCtype[c] & 1) != 0 ? c + 32 : c;
    [Hle] int sce_paf_private_abs(int v) => v < 0 ? -v : v;

    // ---- conversions and formatting (the C library's)
    [Hle] int sce_paf_private_atoi(uint s) => atoi(s);
    [Hle] int sce_paf_private_atol(uint s) => atol(s);
    [Hle] long sce_paf_private_atoll(uint s) => atoll(s);
    [Hle] double sce_paf_private_atof(uint s) => atof(s);
    [Hle] int sce_paf_private_strtol(uint s, uint end, int radix) => strtol(s, end, radix);
    [Hle] uint sce_paf_private_strtoul(uint s, uint end, int radix) => strtoul(s, end, radix);
    [Hle] long sce_paf_private_strtoll(uint s, uint end, int radix) => strtoll(s, end, radix);
    [Hle] ulong sce_paf_private_strtoull(uint s, uint end, int radix) => strtoull(s, end, radix);
    [Hle] float sce_paf_private_strtof(uint s, uint end) => strtof(s, end);
    [Hle] int sce_paf_private_snprintf(uint buf, uint n, string fmt, VarArgs va) => snprintf(buf, n, fmt, va);
    [Hle] int sce_paf_private_sprintf(uint buf, string fmt, VarArgs va) => sprintf(buf, fmt, va);
    [Hle] int sce_paf_private_vsnprintf(uint buf, uint n, string fmt, uint va) => vsnprintf(buf, n, fmt, va);
    [Hle] int sce_paf_private_swprintf(uint buf, uint n, uint fmt, VarArgs va) => swprintf(buf, n, fmt, va);

    // ---- random numbers: libpaf's LCG (state = state * 0x41C64E6D + 12345, 15 bits out)
    uint _pafRand = 1;
    [Hle] void sce_paf_private_srand(uint seed) => _pafRand = seed;
    [Hle] int sce_paf_private_rand() { _pafRand = _pafRand * 0x41C64E6D + 12345; return (int)((_pafRand >> 16) & 0x7FFF); }

    // ---- sorting (NULL base, compare function or counts: nothing to do)
    [Hle]
    void sce_paf_private_qsort(uint base_, uint n, uint size, uint cmp)
    {
        if (base_ == 0 || n == 0 || size == 0 || cmp == 0) return;
        qsort(base_, n, size, cmp);
    }

    [Hle]
    uint sce_paf_private_bsearch(uint key, uint base_, uint n, uint size, uint cmp) =>
        base_ == 0 || n == 0 || size == 0 || cmp == 0 ? 0 : bsearch(key, base_, n, size, cmp);

    // ---- exit handlers
    uint _pafAtexit;
    /// <summary>(void (*fn)(void)): libpaf keeps one exit function (a single word it overwrites).</summary>
    [Hle] void sce_paf_private_atexit(uint fn) => _pafAtexit = fn;
    /// <summary>(void *obj, void (*dtor)(void *), void *dso): the ARM EABI form of __cxa_atexit.</summary>
    [Hle] int sce_paf_aeabi_atexit(uint obj, uint dtor, uint dso) => __cxa_atexit(dtor, obj, dso);
    [Hle] int sce_paf_cxa_atexit(uint dtor, uint obj, uint dso) => __cxa_atexit(dtor, obj, dso);

    // ---- files
    [Hle] uint sce_paf_private_fopen(string path, string mode) => fopen(path, mode);
    [Hle] int sce_paf_private_fclose(uint file) => fclose(file);
    [Hle] uint sce_paf_private_fread(uint buf, uint size, uint count, uint file) => fread(buf, size, count, file);
    [Hle] uint sce_paf_private_fwrite(uint buf, uint size, uint count, uint file) => fwrite(buf, size, count, file);
    [Hle] int sce_paf_private_fseek(uint file, int off, int whence) => fseek(file, off, whence);
    [Hle] int sce_paf_private_ftell(uint file) => ftell(file);
    [Hle] int sce_paf_private_fgetc(uint file) => fgetc(file);

    // ---- the plain names libpaf exports beside the sce_paf_private_* ones
    [Hle] uint sce_paf_malloc(uint size) => malloc(size);
    [Hle] void sce_paf_free(uint p) { if (p != 0) free(p); }
    [Hle] uint sce_paf_realloc(uint p, uint size) => realloc(p, size);
    [Hle] uint sce_paf_calloc(uint n, uint size) => calloc(n, size);
    [Hle] uint sce_paf_musable_size(uint p) => p == 0 ? 0 : malloc_usable_size(p);
    /// <summary>The heap reports of libpaf's global heap allocator: the HLE heap has nothing to give back or check.</summary>
    [Hle] int sce_paf_mtrim() => 0;
    [Hle] int sce_paf_check_memorybounds() => 0;
    [Hle] void sce_paf_dump_heap_info(uint how) => Util.Log.Info("sce_paf_dump_heap_info: HLE heap");
    [Hle] void sce_paf_dump_heap_status() => Util.Log.Info("sce_paf_dump_heap_status: HLE heap");
    [Hle] void sce_paf_report_memoryblocks(uint a, uint b) => Util.Log.Info("sce_paf_report_memoryblocks: HLE heap");

    /// <summary>(dst, value, n): n 32-bit words.</summary>
    [Hle]
    uint sce_paf_memset32(uint dst, uint v, int n)
    {
        for (uint i = 0; (int)i < n; i++) GuestMemory.W32(dst + i * 4, v);
        return dst;
    }

    /// <summary>bcmp as libpaf has it: the sign of the first differing byte (unsigned), n signed.</summary>
    [Hle("sce_paf_bcmp", Nid = 0x1076722F)] int sce_paf_bcmp(uint a, uint b, int n) => Math.Sign(sce_paf_private_memcmp(a, b, n));
    [Hle("sce_paf_bcopy", Nid = 0x44C0825D)] void sce_paf_bcopy(uint src, uint dst, int n) { if (src != dst && n > 0) GuestMemory.Copy(dst, src, (uint)n); }
    [Hle("sce_paf_bzero", Nid = 0xD21442B4)] void sce_paf_bzero(uint dst, int n) { if (n > 0) GuestMemory.Fill(dst, 0, (uint)n); }
    [Hle("sce_paf_strcat", Nid = 0x2344E6F2)] uint sce_paf_strcat(uint dst, uint src) => strcat(dst, src);
    [Hle("sce_paf_strspn", Nid = 0xE3F8F3D6)] uint sce_paf_strspn(uint s, uint set) => strspn(s, set);
    [Hle("sce_paf_strcspn", Nid = 0xB429DD2A)] uint sce_paf_strcspn(uint s, uint set) => strcspn(s, set);
    [Hle] uint sce_paf_strtok(uint s, uint delim) => strtok(s, delim);
    [Hle("sce_paf_vsprintf", Nid = 0x3079B56B)] int sce_paf_vsprintf(uint buf, string fmt, uint va) => vsprintf(buf, fmt, va);
    [Hle] int sce_paf_vswprintf(uint buf, uint n, uint fmt, uint va) => vswprintf(buf, n, fmt, va);

    /// <summary>(s, int *out): the decimal number at s into *out; returns where it stopped.</summary>
    [Hle]
    uint sce_paf_atob(uint s, uint outValue)
    {
        uint end = _pafScratch != 0 ? _pafScratch : _pafScratch = StaticAlloc(4, 4);
        int v = strtol(s, end, 10);
        GuestMemory.W32(outValue, (uint)v);
        return GuestMemory.R32(end);
    }

    uint _pafScratch;

    // ---- wide strings (16-bit), with libpaf's NULL rules
    [Hle("sce_paf_wcscpy", Nid = 0xE9B40477)] uint sce_paf_wcscpy(uint dst, uint src) => dst == 0 || src == 0 ? 0 : wcscpy(dst, src);
    [Hle("sce_paf_wcscat", Nid = 0xDDDDD6E0)] uint sce_paf_wcscat(uint dst, uint src) => dst == 0 || src == 0 ? 0 : wcscat(dst, src);
    [Hle("sce_paf_wcsrchr", Nid = 0x439EB9D4)] uint sce_paf_wcsrchr(uint s, int c) => s == 0 ? 0 : wcsrchr(s, (ushort)c);
    [Hle] uint sce_paf_wcsstr(uint s, uint find) => wcsstr(s, find);
    [Hle("sce_paf_wcspbrk", Nid = 0xD7A2DF29)] uint sce_paf_wcspbrk(uint s, uint set) => wcspbrk(s, set);
    [Hle("sce_paf_wcsspn", Nid = 0x85DB686E)] uint sce_paf_wcsspn(uint s, uint set) => wcsspn(s, set);
    [Hle("sce_paf_wcscspn", Nid = 0xDE8B2091)] uint sce_paf_wcscspn(uint s, uint set) => wcscspn(s, set);
    [Hle] uint sce_paf_wcstok(uint s, uint delim, uint save) => s == 0 && GuestMemory.R32(save) == 0 ? 0 : wcstok(s, delim, save);
    [Hle("sce_paf_wmemmove", Nid = 0x468A09B1)] uint sce_paf_wmemmove(uint dst, uint src, int n) { if (dst != src && n > 0) GuestMemory.Copy(dst, src, (uint)n * 2); return dst; }

    [Hle("sce_paf_wmemset", Nid = 0x36890967)]
    uint sce_paf_wmemset(uint dst, int c, int n)
    {
        for (uint i = 0; (int)i < n; i++) GuestMemory.Write(dst + i * 2, (ushort)c);
        return dst;
    }

    [Hle("sce_paf_wmemcmp", Nid = 0x381B6216)]
    int sce_paf_wmemcmp(uint a, uint b, int n)
    {
        for (uint i = 0; (int)i < n; i++)
        {
            ushort x = H(a + i * 2), y = H(b + i * 2);
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    [Hle("sce_paf_wmemchr", Nid = 0x8236A020)]
    uint sce_paf_wmemchr(uint s, int c, int n)
    {
        for (uint i = 0; (int)i < n; i++) if (H(s + i * 2) == (ushort)c) return s + i * 2;
        return 0;
    }

    static int PafLowerW(ushort c) => c < 0x80 ? PafLower((byte)c) : c;

    [Hle("sce_paf_wcscasecmp", Nid = 0x92A125F6)]
    int sce_paf_wcscasecmp(uint a, uint b)
    {
        if (PafNullCmp(a, b) is { } r) return r;
        for (; ; a += 2, b += 2)
        {
            int x = PafLowerW(H(a)), y = PafLowerW(H(b));
            if (x != y) return x - y;
            if (x == 0) return 0;
        }
    }

    [Hle("sce_paf_wcsncasecmp", Nid = 0x8D65035E)]
    int sce_paf_wcsncasecmp(uint a, uint b, int n)
    {
        if (PafNullCmp(a, b) is { } r) return r;
        for (; n > 0; n--, a += 2, b += 2)
        {
            int x = PafLowerW(H(a)), y = PafLowerW(H(b));
            if (x != y) return x - y;
            if (x == 0) return 0;
        }
        return 0;
    }

    // ---- libpaf's single-precision math (its own short series; the HLE gives the exact value)
    [Hle] float sce_paf_sinf(float x) => MathF.Sin(x);
    [Hle] float sce_paf_cosf(float x) => MathF.Cos(x);
    [Hle] float sce_paf_tanf(float x) => MathF.Sin(x) / MathF.Cos(x);
    [Hle] float sce_paf_asinf(float x) => MathF.Asin(x);
    [Hle] float sce_paf_acosf(float x) => MathF.Acos(x);
    [Hle] float sce_paf_atanf(float x) => MathF.Atan(x);
    [Hle] float sce_paf_atan2f(float y, float x) => MathF.Atan2(y, x);
    [Hle] float sce_paf_expf(float x) => MathF.Exp(x);
    /// <summary>0 gives -infinity, a negative number NaN.</summary>
    [Hle] float sce_paf_logf(float x) => MathF.Log(x);
    [Hle] float sce_paf_powf(float x, float y) => MathF.Pow(x, y);
    [Hle] float sce_paf_floorf(float x) => MathF.Floor(x);
    [Hle] float sce_paf_ceilf(float x) => MathF.Ceiling(x);
    [Hle] float sce_paf_roundf(float x) => MathF.Round(x, MidpointRounding.AwayFromZero);
    /// <summary>The double floor goes through a float, as libpaf's does.</summary>
    [Hle] double sce_paf_floor(double x) => MathF.Floor((float)x);
    /// <summary>A zero divisor gives 0.</summary>
    [Hle] double sce_paf_fmod(double x, double y) => y == 0 ? 0 : x % y;

    // ---- files (the C library's streams)
    [Hle] int sce_paf_feof(uint file) => file == 0 ? 0 : feof(file);
    /// <summary>A NULL or closed stream counts as an error.</summary>
    [Hle] int sce_paf_ferror(uint file) => file == 0 || Get(file) == null ? 1 : ferror(file);
    [Hle] int sce_paf_fileno(uint file) => file == 0 || Get(file) == null ? -1 : fileno(file);
    [Hle] void sce_paf_clearerr(uint file) { if (file != 0) clearerr(file); }
    [Hle] int sce_paf_ungetc(int c, uint file) => ungetc(c, file);
    [Hle("sce_paf_fputc", Nid = 0xAF1831DC)] int sce_paf_fputc(int c, uint file) => fputc(c, file);
    [Hle] uint sce_paf_fdopen(int fd, uint mode) => mode == 0 ? 0 : fdopen(fd, GuestMemory.ReadCString(mode));

    // ---- process exit and the C++ runtime (the C library's)
    [Hle("exit", Nid = 0x92BF63C9)] void PafExit(int status) => exit(status);
    [Hle("abort", Library = "ScePafStdc")] void PafAbort() => abort();
    [Hle("sce_paf_private_towlower")] int PafPrivateTowlower(int c) => towlower(c);
    [Hle("sce_paf_private_fflush")] int PafPrivateFflush(uint file) => fflush(file);
    /// <summary>sce_paf_exit (FW 3.74): libpaf's own exit is a fatal stop, the (empty) debug print then a breakpoint,
    /// never a return: the process ends as on abort.</summary>
    [Hle("sce_paf_exit", Nid = 0xD608246A, Library = "ScePafStdc")] void PafExitFatal(int status) => abort();
    [Hle("__cxa_finalize", Library = "ScePafStdc")] void PafCxaFinalize(uint dso) => __cxa_finalize(dso);
    [Hle("__cxa_guard_acquire", Library = "ScePafStdc")] int PafGuardAcquire(uint g) => __cxa_guard_acquire(g);
    [Hle("__cxa_guard_release", Library = "ScePafStdc")] void PafGuardRelease(uint g) => __cxa_guard_release(g);
    [Hle("__cxa_guard_abort", Library = "ScePafStdc")] void PafGuardAbort(uint g) => __cxa_guard_abort(g);
    /// <summary>A call through a deleted virtual function: libpaf prints and stops.</summary>
    [Hle("__cxa_deleted_virtual", Library = "ScePafStdc")]
    void PafDeletedVirtual() { Util.Log.Warn("libpaf: deleted virtual function called"); abort(); }

    // ---- for the libpaf classes (Paf): their objects and strings come from the same heap as sce_paf_malloc
    internal uint PafHeapAlloc(uint size) => malloc(size);
    internal void PafHeapFree(uint p) => free(p);
    internal uint PafStatic(uint size, uint align) => StaticAlloc(size, align);

    // ---- for other HLE libraries whose C allocator is this heap (SceLibMonoBridge's eglib)
    internal uint HeapMalloc(uint size) => malloc(size);
    internal uint HeapCalloc(uint n, uint size) => calloc(n, size);
    internal uint HeapRealloc(uint p, uint size) => realloc(p, size);
    internal void HeapFree(uint p) => free(p);
    internal uint HeapUsableSize(uint p) => malloc_usable_size(p);
}
