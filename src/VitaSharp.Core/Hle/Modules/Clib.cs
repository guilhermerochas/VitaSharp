using System.Numerics;
using System.Text;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceClib (SceLibKernel): mspace heaps, string/ctype helpers and the fortified (_chk) variants. The basic
/// printf/mem/str functions live in <see cref="ProcessMgr"/>.
/// </summary>
public sealed class Clib : HleModule
{
    // ------------------------------------------------------------------ mspace heaps
    // An mspace manages a block the game hands over. Bookkeeping is host-side; the first bytes of the block are
    // reserved (as the real dlmalloc header would be) so the handle is the block address and allocations never
    // start at it.

    const uint HeaderBytes = 64, MinAlign = 16;

    sealed class Heap
    {
        public uint Base, Size;
        public readonly SortedList<uint, uint> Free = new(); // start -> size
        public readonly Dictionary<uint, (uint Start, uint Size)> Used = new(); // returned ptr -> (block start, block size)
        public uint InUse, Peak;
    }

    readonly Dictionary<uint, Heap> _heaps = new();
    readonly Dictionary<uint, Heap> _owner = new(); // allocation -> heap (for MallocUsableSize)

    [Hle]
    internal uint sceClibMspaceCreate(uint block, uint size)
    {
        if (block == 0 || size <= HeaderBytes + MinAlign) return 0;
        var h = new Heap { Base = block, Size = size };
        uint start = GuestMemory.AlignUp(block + HeaderBytes, MinAlign);
        h.Free.Add(start, block + size - start);
        GuestMemory.Fill(block, 0, HeaderBytes);
        lock (_heaps) _heaps[block] = h;
        return block;
    }

    [Hle]
    internal void sceClibMspaceDestroy(uint mspace)
    {
        lock (_heaps)
        {
            if (!_heaps.Remove(mspace, out var h)) return;
            foreach (var p in h.Used.Keys) _owner.Remove(p);
        }
    }

    uint Alloc(Heap h, uint size, uint align)
    {
        align = Math.Max(MinAlign, align == 0 ? MinAlign : (uint)BitOperations.RoundUpToPowerOf2(align));
        size = GuestMemory.AlignUp(Math.Max(size, 1), MinAlign);
        for (int i = 0; i < h.Free.Count; i++)
        {
            uint start = h.Free.Keys[i], len = h.Free.Values[i];
            uint p = GuestMemory.AlignUp(start, align);
            if ((ulong)p - start + size > len) continue;
            h.Free.RemoveAt(i);
            if (p > start) h.Free.Add(start, p - start);
            uint end = p + size, freeEnd = start + len;
            if (freeEnd > end) h.Free.Add(end, freeEnd - end);
            h.Used[p] = (p, size);
            _owner[p] = h;
            h.InUse += size;
            h.Peak = Math.Max(h.Peak, h.InUse);
            return p;
        }
        return 0;
    }

    void Release(Heap h, uint p)
    {
        if (!h.Used.Remove(p, out var blk)) return;
        _owner.Remove(p);
        h.InUse -= blk.Size;
        uint start = blk.Start, size = blk.Size;
        // coalesce with neighbours
        int idx = h.Free.IndexOfKey(start + size);
        if (idx >= 0) { size += h.Free.Values[idx]; h.Free.RemoveAt(idx); }
        for (int i = 0; i < h.Free.Count; i++)
        {
            if (h.Free.Keys[i] + h.Free.Values[i] != start) continue;
            start = h.Free.Keys[i];
            size += h.Free.Values[i];
            h.Free.RemoveAt(i);
            break;
        }
        h.Free.Add(start, size);
    }

    uint Malloc(uint mspace, uint size, uint align)
    {
        lock (_heaps) return _heaps.TryGetValue(mspace, out var h) ? Alloc(h, size, align) : 0;
    }

    [Hle] internal uint sceClibMspaceMalloc(uint mspace, uint size) => Malloc(mspace, size, MinAlign);
    [Hle] uint sceClibMspaceMemalign(uint mspace, uint alignment, uint size) => Malloc(mspace, size, alignment);

    [Hle]
    uint sceClibMspaceCalloc(uint mspace, uint num, uint size)
    {
        ulong total = (ulong)num * size;
        if (total > uint.MaxValue) return 0;
        uint p = Malloc(mspace, (uint)total, MinAlign);
        if (p != 0) GuestMemory.Fill(p, 0, (uint)total);
        return p;
    }

    [Hle] internal uint sceClibMspaceRealloc(uint mspace, uint ptr, uint size) => Realloc(mspace, ptr, size, MinAlign);
    [Hle] uint sceClibMspaceReallocalign(uint mspace, uint ptr, uint size, uint alignment) => Realloc(mspace, ptr, size, alignment);

    uint Realloc(uint mspace, uint ptr, uint size, uint align)
    {
        if (ptr == 0) return Malloc(mspace, size, align);
        lock (_heaps)
        {
            if (!_heaps.TryGetValue(mspace, out var h) || !h.Used.TryGetValue(ptr, out var blk)) return 0;
            if (size == 0) { Release(h, ptr); return 0; }
            uint q = Alloc(h, size, align);
            if (q == 0) return 0;
            GuestMemory.Copy(q, ptr, Math.Min(blk.Size, size));
            Release(h, ptr);
            return q;
        }
    }

    [Hle]
    internal void sceClibMspaceFree(uint mspace, uint ptr)
    {
        if (ptr == 0) return;
        lock (_heaps)
        {
            if (_heaps.TryGetValue(mspace, out var h)) Release(h, ptr);
            else if (_owner.TryGetValue(ptr, out var owner)) Release(owner, ptr);
        }
    }

    [Hle]
    uint sceClibMspaceMallocUsableSize(uint ptr)
    {
        lock (_heaps) return _owner.TryGetValue(ptr, out var h) && h.Used.TryGetValue(ptr, out var blk) ? blk.Size : 0;
    }

    [Hle]
    int sceClibMspaceIsHeapEmpty(uint mspace)
    {
        lock (_heaps) return _heaps.TryGetValue(mspace, out var h) && h.Used.Count == 0 ? 1 : 0;
    }

    [Hle("sceClibMspaceMallocStats")]
    [Hle("sceClibMspaceMallocStatsFast")]
    void sceClibMspaceMallocStats(uint mspace, uint stats)
    {
        if (stats == 0) return;
        lock (_heaps)
        {
            _heaps.TryGetValue(mspace, out var h);
            GuestMemory.W32(stats, h?.Size ?? 0);
            GuestMemory.W32(stats + 4, h?.Size ?? 0);
            GuestMemory.W32(stats + 8, h?.Peak ?? 0);
            GuestMemory.W32(stats + 12, h?.InUse ?? 0);
        }
    }

    // ------------------------------------------------------------------ strings

    static uint StrLen(uint s) { uint n = 0; while (GuestMemory.Read<byte>(s + n) != 0) n++; return n; }

    [Hle]
    uint sceClibStrchr(uint s, int ch)
    {
        byte c = (byte)ch;
        for (uint p = s; ; p++)
        {
            byte b = GuestMemory.Read<byte>(p);
            if (b == c) return p;
            if (b == 0) return 0;
        }
    }

    [Hle]
    uint sceClibStrrchr(uint s, int ch)
    {
        byte c = (byte)ch;
        uint last = 0;
        for (uint p = s; ; p++)
        {
            byte b = GuestMemory.Read<byte>(p);
            if (b == c) last = p;
            if (b == 0) return last;
        }
    }

    [Hle]
    uint sceClibStrstr(uint s1, uint s2)
    {
        uint n2 = StrLen(s2);
        if (n2 == 0) return s1;
        var needle = GuestMemory.Span(s2, (int)n2);
        uint n1 = StrLen(s1);
        int idx = GuestMemory.Span(s1, (int)n1).IndexOf(needle);
        return idx < 0 ? 0 : s1 + (uint)idx;
    }

    [Hle]
    uint sceClibMemchr(uint s, int ch, uint len)
    {
        int idx = GuestMemory.Span(s, (int)len).IndexOf((byte)ch);
        return idx < 0 ? 0 : s + (uint)idx;
    }

    [Hle]
    int sceClibMemcmpConstTime(uint a, uint b, uint len)
    {
        int diff = 0;
        for (uint i = 0; i < len; i++) diff |= GuestMemory.Read<byte>(a + i) ^ GuestMemory.Read<byte>(b + i);
        return diff;
    }

    static int Lower(int c) => c is >= 'A' and <= 'Z' ? c + 32 : c;

    [Hle]
    int sceClibStrncasecmp(uint a, uint b, uint n)
    {
        for (uint i = 0; i < n; i++)
        {
            int ca = Lower(GuestMemory.Read<byte>(a + i)), cb = Lower(GuestMemory.Read<byte>(b + i));
            if (ca != cb) return ca - cb;
            if (ca == 0) return 0;
        }
        return 0;
    }

    [Hle]
    uint sceClibStrncat(uint dst, uint src, uint n)
    {
        uint d = dst + StrLen(dst), i = 0;
        for (; i < n; i++)
        {
            byte c = GuestMemory.Read<byte>(src + i);
            if (c == 0) break;
            GuestMemory.Write(d + i, c);
        }
        GuestMemory.Write(d + i, (byte)0);
        return dst;
    }

    [Hle]
    uint sceClibStrlcpy(uint dst, uint src, uint size)
    {
        uint len = StrLen(src);
        if (size > 0)
        {
            uint n = Math.Min(len, size - 1);
            GuestMemory.Copy(dst, src, n);
            GuestMemory.Write(dst + n, (byte)0);
        }
        return len;
    }

    [Hle]
    uint sceClibStrlcat(uint dst, uint src, uint size)
    {
        uint dlen = 0;
        while (dlen < size && GuestMemory.Read<byte>(dst + dlen) != 0) dlen++;
        uint slen = StrLen(src);
        if (dlen == size) return size + slen;
        uint n = Math.Min(slen, size - dlen - 1);
        GuestMemory.Copy(dst + dlen, src, n);
        GuestMemory.Write(dst + dlen + n, (byte)0);
        return dlen + slen;
    }

    [Hle] int sceClibTolower(int ch) => Lower(ch & 0xFF);
    [Hle] int sceClibToupper(int ch) => (ch & 0xFF) is >= 'a' and <= 'z' ? (ch & 0xFF) - 32 : ch & 0xFF;

    /// <summary>The C locale ctype class bits (_U 0x01, _L 0x02, _N 0x04, _S 0x08, _P 0x10, _C 0x20, _X 0x40, _B 0x80).</summary>
    [Hle]
    int sceClibLookCtypeTable(int ch)
    {
        int c = ch & 0xFF, r = 0;
        if (c is >= 'A' and <= 'Z') r |= 0x01;
        if (c is >= 'a' and <= 'z') r |= 0x02;
        if (c is >= '0' and <= '9') r |= 0x04;
        if (c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r') r |= 0x08;
        if (c is >= 0x21 and <= 0x7E && (r & 0x07) == 0) r |= 0x10;
        if (c < 0x20 || c == 0x7F) r |= 0x20;
        if (c is >= 'A' and <= 'F' or >= 'a' and <= 'f' or >= '0' and <= '9') r |= 0x40;
        if (c == ' ') r |= 0x80;
        return r;
    }

    [Hle]
    long sceClibStrtoll(uint str, uint endPtr, int radix)
    {
        uint p = str;
        while (GuestMemory.Read<byte>(p) is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\v' or (byte)'\f') p++;
        bool neg = false;
        byte c = GuestMemory.Read<byte>(p);
        if (c is (byte)'+' or (byte)'-') { neg = c == '-'; p++; }
        if ((radix == 0 || radix == 16) && GuestMemory.Read<byte>(p) == '0' && (GuestMemory.Read<byte>(p + 1) | 0x20) == 'x') { p += 2; radix = 16; }
        else if (radix == 0) radix = GuestMemory.Read<byte>(p) == '0' ? 8 : 10;
        ulong v = 0;
        uint digits = 0;
        for (; ; p++, digits++)
        {
            int ch = GuestMemory.Read<byte>(p), d = ch is >= '0' and <= '9' ? ch - '0' : (ch | 0x20) is >= 'a' and <= 'z' ? (ch | 0x20) - 'a' + 10 : 99;
            if (d >= radix) break;
            v = v * (ulong)radix + (ulong)d;
        }
        if (endPtr != 0) GuestMemory.W32(endPtr, digits == 0 ? str : p);
        return neg ? -(long)v : (long)v;
    }

    // ------------------------------------------------------------------ fortified variants (__*_chk semantics)

    static void Overflow(string fn) => Log.WarnOnce("chk-" + fn, $"{fn}: destination buffer too small (guest bug), truncating");

    [Hle]
    uint sceClibMemcpyChk(uint dst, uint src, uint len, uint dstSize)
    {
        if (len > dstSize) { Overflow("sceClibMemcpyChk"); len = dstSize; }
        GuestMemory.Copy(dst, src, len);
        return dst;
    }

    [Hle]
    uint sceClibMemmoveChk(uint dst, uint src, uint len, uint dstSize)
    {
        if (len > dstSize) { Overflow("sceClibMemmoveChk"); len = dstSize; }
        GuestMemory.Copy(dst, src, len);
        return dst;
    }

    [Hle]
    uint sceClibMemsetChk(uint dst, int ch, uint len, uint dstSize)
    {
        if (len > dstSize) { Overflow("sceClibMemsetChk"); len = dstSize; }
        GuestMemory.Fill(dst, (byte)ch, len);
        return dst;
    }

    [Hle]
    uint sceClibStrcpyChk(uint dst, uint src, uint dstSize)
    {
        uint len = StrLen(src);
        if (len + 1 > dstSize) { Overflow("sceClibStrcpyChk"); len = dstSize == 0 ? 0 : dstSize - 1; }
        GuestMemory.Copy(dst, src, len);
        if (dstSize > 0) GuestMemory.Write(dst + len, (byte)0);
        return dst;
    }

    [Hle]
    uint sceClibStrncpyChk(uint dst, uint src, uint n, uint dstSize)
    {
        if (n > dstSize) { Overflow("sceClibStrncpyChk"); n = dstSize; }
        uint i = 0;
        for (; i < n; i++) { byte c = GuestMemory.Read<byte>(src + i); GuestMemory.Write(dst + i, c); if (c == 0) break; }
        for (; i < n; i++) GuestMemory.Write(dst + i, (byte)0);
        return dst;
    }

    [Hle]
    uint sceClibStrcatChk(uint dst, uint src, uint dstSize)
    {
        uint dlen = StrLen(dst), slen = StrLen(src);
        if (dlen + slen + 1 > dstSize) { Overflow("sceClibStrcatChk"); slen = dstSize > dlen ? dstSize - dlen - 1 : 0; }
        GuestMemory.Copy(dst + dlen, src, slen);
        GuestMemory.Write(dst + dlen + slen, (byte)0);
        return dst;
    }

    [Hle]
    uint sceClibStrncatChk(uint dst, uint src, uint n, uint dstSize)
    {
        uint dlen = StrLen(dst), slen = Math.Min(StrLen(src), n);
        if (dlen + slen + 1 > dstSize) { Overflow("sceClibStrncatChk"); slen = dstSize > dlen ? dstSize - dlen - 1 : 0; }
        GuestMemory.Copy(dst + dlen, src, slen);
        GuestMemory.Write(dst + dlen + slen, (byte)0);
        return dst;
    }

    [Hle] uint sceClibStrlcpyChk(uint dst, uint src, uint size, uint dstSize) => sceClibStrlcpy(dst, src, Math.Min(size, dstSize));
    [Hle] uint sceClibStrlcatChk(uint dst, uint src, uint size, uint dstSize) => sceClibStrlcat(dst, src, Math.Min(size, dstSize));

    [Hle]
    int sceClibSnprintfChk(uint buf, uint size, int flag, uint dstSize, string fmt, VarArgs va)
    {
        string s = CFormat.Format(fmt, va);
        uint n = Math.Min(size, dstSize);
        if (n > 0) GuestMemory.WriteCString(buf, s, (int)n);
        return Encoding.UTF8.GetByteCount(s);
    }

    [Hle]
    int sceClibVsnprintfChk(uint buf, uint size, int flag, uint dstSize, string fmt, uint vaList)
    {
        string s = CFormat.Format(fmt, new VarArgs(new VaListSource(vaList), 0));
        uint n = Math.Min(size, dstSize);
        if (n > 0) GuestMemory.WriteCString(buf, s, (int)n);
        return Encoding.UTF8.GetByteCount(s);
    }

    // ------------------------------------------------------------------ printf family / abort

    [Hle]
    int sceClibVprintf(string fmt, uint vaList)
    {
        string s = CFormat.Format(fmt, new VarArgs(new VaListSource(vaList), 0));
        Log.Info("[guest] " + s.TrimEnd('\n'));
        return s.Length;
    }

    [Hle]
    int sceClibDprintf(int fd, string fmt, VarArgs va)
    {
        string s = CFormat.Format(fmt, va);
        Log.Info($"[guest fd{fd}] " + s.TrimEnd('\n'));
        return s.Length;
    }

    [Hle]
    int sceClibVdprintf(int fd, string fmt, uint vaList)
    {
        string s = CFormat.Format(fmt, new VarArgs(new VaListSource(vaList), 0));
        Log.Info($"[guest fd{fd}] " + s.TrimEnd('\n'));
        return s.Length;
    }

    [Hle]
    void sceClibAbort()
    {
        Log.Error("[guest] sceClibAbort called");
        throw new InvalidOperationException("guest called sceClibAbort");
    }

    /// <summary>va_list on ARM: a pointer to the stacked arguments.</summary>
    sealed class VaListSource(uint p) : IVaSource
    {
        uint _p = p;
        public uint Next() { uint v = GuestMemory.R32(_p); _p += 4; return v; }
    }
}
