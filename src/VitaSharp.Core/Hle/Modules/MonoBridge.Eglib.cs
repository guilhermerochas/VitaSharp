using System.Globalization;
using System.Text;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// eglib (Mono's GLib subset, exported with the <c>monoeg_</c> prefix): memory, strings, GString, GArray, GPtrArray,
/// GList, GSList, GQueue, GHashTable, GError. The objects live in guest memory with eglib's layouts and growth rules.
/// </summary>
public sealed unsafe partial class MonoBridge
{
    // ------------------------------------------------------------------ memory

    /// <summary>g_malloc: NULL for 0 bytes; eglib stops the program when the heap is exhausted (logged here).</summary>
    [Hle("monoeg_malloc", Library = Lib)]
    uint GMalloc(uint n)
    {
        if (n == 0) return 0;
        uint p = Malloc(n);
        if (p == 0) Log.Error($"eglib: could not allocate {n} bytes");
        return p;
    }

    [Hle("monoeg_malloc0", Library = Lib)]
    uint GMalloc0(uint n)
    {
        if (n == 0) return 0;
        uint p = Malloc0(n);
        if (p == 0) Log.Error($"eglib: could not allocate {n} bytes");
        return p;
    }

    /// <summary>g_realloc(obj, size): size 0 frees and returns NULL.</summary>
    [Hle("monoeg_realloc", Library = Lib)]
    [Hle("monoeg_try_realloc", Library = Lib)]
    uint GRealloc(uint obj, uint size)
    {
        if (size == 0) { Free(obj); return 0; }
        return Realloc(obj, size);
    }

    [Hle("monoeg_try_malloc", Library = Lib)] uint GTryMalloc(uint n) => n == 0 ? 0 : Malloc(n);
    [Hle("monoeg_g_free", Library = Lib)] void GFree(uint p) => Free(p);

    [Hle("monoeg_g_memdup", Library = Lib)]
    uint GMemdup(uint mem, uint size)
    {
        if (mem == 0 || size == 0) return 0;
        uint p = Malloc(size);
        if (p != 0) GuestMemory.Copy(p, mem, size);
        return p;
    }

    // ------------------------------------------------------------------ strings

    /// <summary>g_strndup(str, n): n bytes (strncpy: stops at the NUL) and a terminator; NULL for NULL.</summary>
    [Hle("monoeg_g_strndup", Library = Lib)]
    uint GStrndup(uint str, uint n)
    {
        if (str == 0) return 0;
        uint len = 0;
        while (len < n && R8(str + len) != 0) len++;
        uint p = Malloc(n + 1);
        if (p == 0) return 0;
        GuestMemory.Copy(p, str, len);
        GuestMemory.Fill(p + len, 0, n + 1 - len);
        return p;
    }

    [Hle("monoeg_g_strfreev", Library = Lib)]
    void GStrfreev(uint v)
    {
        if (v == 0) return;
        for (uint p = v; R32(p) != 0; p += 4) Free(R32(p));
        Free(v);
    }

    /// <summary>A malloc'ed NULL-terminated vector of copies of <paramref name="items"/>.</summary>
    uint StrVector(IReadOnlyList<byte[]> items)
    {
        uint v = Malloc((uint)(items.Count + 1) * 4);
        for (int i = 0; i < items.Count; i++) W32(v + (uint)i * 4, DupBytes(items[i]));
        W32(v + (uint)items.Count * 4, 0);
        return v;
    }

    /// <summary>g_strconcat(first, ..., NULL).</summary>
    [Hle("monoeg_g_strconcat", Library = Lib)]
    uint GStrconcat(uint first, VarArgs va)
    {
        if (first == 0) return 0;
        var b = new List<byte>(Bytes(first));
        for (uint s = va.NextU32(); s != 0; s = va.NextU32()) b.AddRange(Bytes(s));
        return DupBytes(b.ToArray());
    }

    /// <summary>g_strjoin(separator, ..., NULL): NULL separator = "".</summary>
    [Hle("monoeg_g_strjoin", Library = Lib)]
    uint GStrjoin(uint separator, VarArgs va)
    {
        byte[] sep = Bytes(separator);
        var b = new List<byte>();
        bool firstOne = true;
        for (uint s = va.NextU32(); s != 0; s = va.NextU32())
        {
            if (!firstOne) b.AddRange(sep);
            b.AddRange(Bytes(s));
            firstOne = false;
        }
        return DupBytes(b.ToArray());
    }

    /// <summary>g_strsplit(string, delimiter, max_tokens): at most max_tokens pieces (the last one holds the rest; &lt;1 =
    /// no limit); an empty string gives an empty vector, a delimiter at either end an empty piece.</summary>
    [Hle("monoeg_g_strsplit", Library = Lib)]
    uint GStrsplit(uint str, uint delimiter, int maxTokens)
    {
        if (str == 0 || delimiter == 0 || R8(delimiter) == 0) return 0;
        byte[] s = Bytes(str), d = Bytes(delimiter);
        var parts = new List<byte[]>();
        if (s.Length > 0)
        {
            int limit = maxTokens < 1 ? int.MaxValue : maxTokens, start = 0;
            while (parts.Count < limit - 1)
            {
                int at = s.AsSpan(start).IndexOf(d);
                if (at < 0) break;
                parts.Add(s[start..(start + at)]);
                start += at + d.Length;
            }
            parts.Add(s[start..]);
        }
        return StrVector(parts);
    }

    static bool IsSpace(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f' or (byte)'\v';

    /// <summary>g_strchug: removes the leading white space in place, returns str.</summary>
    [Hle("monoeg_g_strchug", Library = Lib)]
    uint GStrchug(uint str)
    {
        if (str == 0) return 0;
        uint n = StrLen(str), i = 0;
        while (i < n && IsSpace(R8(str + i))) i++;
        if (i > 0) GuestMemory.Copy(str, str + i, n - i + 1);
        return str;
    }

    /// <summary>g_strchomp: removes the trailing white space in place, returns str.</summary>
    [Hle("monoeg_g_strchomp", Library = Lib)]
    uint GStrchomp(uint str)
    {
        if (str == 0) return 0;
        uint n = StrLen(str);
        while (n > 0 && IsSpace(R8(str + n - 1))) W8(str + --n, 0);
        return str;
    }

    [Hle("monoeg_g_strreverse", Library = Lib)]
    uint GStrreverse(uint str)
    {
        if (str == 0) return 0;
        uint n = StrLen(str);
        for (uint i = 0, j = n - 1; n > 0 && i < j; i++, j--) { byte t = R8(str + i); W8(str + i, R8(str + j)); W8(str + j, t); }
        return str;
    }

    [Hle("monoeg_g_str_has_prefix", Library = Lib)]
    bool GStrHasPrefix(uint str, uint prefix) => Bytes(str).AsSpan().StartsWith(Bytes(prefix));

    /// <summary>g_strlcpy(dest, src, size): at most size-1 bytes and a terminator; the length of src.</summary>
    [Hle("monoeg_g_strlcpy", Library = Lib)]
    uint GStrlcpy(uint dest, uint src, uint size)
    {
        if (src == 0) return 0;
        uint len = StrLen(src);
        if (dest == 0 || size == 0) return len;
        uint n = Math.Min(len, size - 1);
        GuestMemory.Copy(dest, src, n);
        W8(dest + n, 0);
        return len;
    }

    sealed class VaList(uint p) : IVaSource
    {
        uint _p = p;
        public uint Next() { uint v = R32(_p); _p += 4; return v; }
        public uint? Address => _p;
    }

    static VarArgs Va(uint list) => new(new VaList(list), 0);
    static string Fmt(uint fmt, VarArgs va) => fmt == 0 ? "" : CFormat.Format(Utf8(fmt), va);

    [Hle("monoeg_g_strdup_printf", Library = Lib)] uint GStrdupPrintf(uint fmt, VarArgs va) => fmt == 0 ? 0 : StrDup(Fmt(fmt, va));
    [Hle("monoeg_g_strdup_vprintf", Library = Lib)] uint GStrdupVprintf(uint fmt, uint va) => fmt == 0 ? 0 : StrDup(Fmt(fmt, Va(va)));

    /// <summary>g_snprintf(buf, n, fmt, ...): vsnprintf (the full length).</summary>
    [Hle("monoeg_g_snprintf", Library = Lib)]
    int GSnprintf(uint buf, uint n, uint fmt, VarArgs va)
    {
        byte[] b = Encoding.UTF8.GetBytes(Fmt(fmt, va));
        if (buf != 0 && n > 0)
        {
            int c = (int)Math.Min((uint)b.Length, n - 1);
            b.AsSpan(0, c).CopyTo(GuestMemory.Span(buf, c));
            W8(buf + (uint)c, 0);
        }
        return b.Length;
    }

    static byte AsciiLower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;

    /// <summary>g_ascii_strdown(str, len): a lower-case (ASCII) copy of len bytes (len &lt; 0: the whole string).</summary>
    [Hle("monoeg_g_ascii_strdown", Library = Lib)]
    uint GAsciiStrdown(uint str, int len)
    {
        if (str == 0) return 0;
        byte[] b = Bytes(str, len < 0 ? -1 : len);
        for (int i = 0; i < b.Length; i++) b[i] = AsciiLower(b[i]);
        return DupBytes(b);
    }

    [Hle("monoeg_g_ascii_strncasecmp", Library = Lib)]
    int GAsciiStrncasecmp(uint s1, uint s2, uint n)
    {
        for (uint i = 0; i < n; i++)
        {
            int a = AsciiLower(R8(s1 + i)), b = AsciiLower(R8(s2 + i));
            if (a != b) return a - b;
            if (a == 0) return 0;
        }
        return 0;
    }

    [Hle("g_ascii_strcasecmp", Library = Lib)]
    int GAsciiStrcasecmp(uint s1, uint s2) => GAsciiStrncasecmp(s1, s2, uint.MaxValue);

    [Hle("monoeg_g_ascii_tolower", Library = Lib)] int GAsciiTolower(int c) => c is >= 'A' and <= 'Z' ? c + 32 : c;

    [Hle("monoeg_g_ascii_xdigit_value", Library = Lib)]
    int GAsciiXdigitValue(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>g_strerror(errnum): the C library's text (a constant string).</summary>
    [Hle("monoeg_g_strerror", Library = Lib)] uint GStrerror(int e) => C.ConstString(Libc.ErrorText(e));

    [Hle("monoeg_g_str_equal", Library = Lib)] bool GStrEqual(uint a, uint b) => Bytes(a).AsSpan().SequenceEqual(Bytes(b));

    /// <summary>g_str_hash: eglib's (hash = (hash &lt;&lt; 5) - (hash + c) over the bytes after the first).</summary>
    [Hle("monoeg_g_str_hash", Library = Lib)]
    uint GStrHash(uint v)
    {
        uint hash = 0;
        for (uint p = v; R8(p) != 0;)
        {
            p++;
            hash = (hash << 5) - (hash + R8(p));
        }
        return hash;
    }

    [Hle("monoeg_g_direct_hash", Library = Lib)] uint GDirectHash(uint v) => v;
    [Hle("monoeg_g_direct_equal", Library = Lib)] bool GDirectEqual(uint a, uint b) => a == b;

    static readonly uint[] Primes =
    [
        11, 19, 37, 73, 109, 163, 251, 367, 557, 823, 1237, 1861, 2777, 4177, 6247, 9371, 14057, 21089, 31627, 47431,
        71143, 106721, 160073, 240101, 360163, 540217, 810343, 1215497, 1823231, 2734867, 4102283, 6153409, 9230113, 13845163,
    ];

    /// <summary>g_spaced_primes_closest(x): the first prime of eglib's table &gt;= x, else the next prime above x.</summary>
    [Hle("monoeg_g_spaced_primes_closest", Library = Lib)]
    uint GSpacedPrimesClosest(uint x)
    {
        foreach (uint p in Primes) if (x <= p) return p;
        for (uint n = x | 1; ; n += 2)
        {
            bool prime = true;
            for (uint d = 3; (ulong)d * d <= n; d += 2) if (n % d == 0) { prime = false; break; }
            if (prime) return n;
        }
    }

    // ------------------------------------------------------------------ GString { str, len, allocated_len }

    [Hle("monoeg_g_string_new", Library = Lib)]
    uint GStringNew(uint init)
    {
        uint len = init == 0 ? 0 : StrLen(init);
        uint s = Malloc(12), alloc = Math.Max(len + 1, 16);
        uint str = Malloc(alloc);
        if (len > 0) GuestMemory.Copy(str, init, len);
        W8(str + len, 0);
        W32(s, str); W32(s + 4, len); W32(s + 8, alloc);
        return s;
    }

    /// <summary>g_string_append_len(string, val, len): len &lt; 0 = strlen; grows to (allocated + len + 16) * 2.</summary>
    [Hle("monoeg_g_string_append_len", Library = Lib)]
    uint GStringAppendLen(uint s, uint val, int len)
    {
        if (s == 0) return 0;
        if (val == 0) return s;
        uint n = len < 0 ? StrLen(val) : (uint)len;
        AppendRaw(s, val, n);
        return s;
    }

    void AppendRaw(uint s, uint src, uint n)
    {
        uint str = R32(s), cur = R32(s + 4), alloc = R32(s + 8);
        if (cur + n >= alloc)
        {
            alloc = (alloc + n + 16) * 2;
            str = Realloc(str, alloc);
            W32(s, str); W32(s + 8, alloc);
        }
        GuestMemory.Copy(str + cur, src, n);
        W32(s + 4, cur + n);
        W8(str + cur + n, 0);
    }

    void AppendBytes(uint s, ReadOnlySpan<byte> b)
    {
        uint tmp = DupBytes(b);
        AppendRaw(s, tmp, (uint)b.Length);
        Free(tmp);
    }

    [Hle("monoeg_g_string_append", Library = Lib)] uint GStringAppend(uint s, uint val) => GStringAppendLen(s, val, -1);

    [Hle("monoeg_g_string_append_c", Library = Lib)]
    uint GStringAppendC(uint s, int c)
    {
        if (s == 0) return 0;
        AppendBytes(s, [(byte)c]);
        return s;
    }

    [Hle("monoeg_g_string_append_printf", Library = Lib)]
    void GStringAppendPrintf(uint s, uint fmt, VarArgs va)
    {
        if (s == 0 || fmt == 0) return;
        AppendBytes(s, Encoding.UTF8.GetBytes(Fmt(fmt, va)));
    }

    /// <summary>g_string_printf(string, fmt, ...): replaces the contents (allocated = len + 1).</summary>
    [Hle("monoeg_g_string_printf", Library = Lib)]
    void GStringPrintf(uint s, uint fmt, VarArgs va)
    {
        if (s == 0 || fmt == 0) return;
        Free(R32(s));
        uint str = StrDup(Fmt(fmt, va)), len = StrLen(str);
        W32(s, str); W32(s + 4, len); W32(s + 8, len + 1);
    }

    /// <summary>g_string_free(string, free_segment): the characters when kept, else NULL.</summary>
    [Hle("monoeg_g_string_free", Library = Lib)]
    uint GStringFree(uint s, bool freeSegment)
    {
        if (s == 0) return 0;
        uint data = R32(s);
        Free(s);
        if (!freeSegment) return data;
        Free(data);
        return 0;
    }

    // ------------------------------------------------------------------ GArray { data, len, clear_, element_size, zero_terminated, capacity }

    void ArrayEnsure(uint a, uint capacity)
    {
        uint cap = R32(a + 20), es = R32(a + 12);
        if (capacity <= cap) return;
        uint ncap = (capacity + 63) & ~63u;
        uint data = Realloc(R32(a), es * ncap);
        W32(a, data);
        if (R32(a + 8) != 0) GuestMemory.Fill(data + es * cap, 0, es * (ncap - cap));
        W32(a + 20, ncap);
    }

    [Hle("monoeg_g_array_new", Library = Lib)]
    uint GArrayNew(bool zeroTerminated, bool clear, uint elementSize)
    {
        uint a = Malloc0(24);
        W32(a + 8, clear ? 1u : 0); W32(a + 12, elementSize); W32(a + 16, zeroTerminated ? 1u : 0);
        ArrayEnsure(a, 16);
        return a;
    }

    [Hle("monoeg_g_array_free", Library = Lib)]
    uint GArrayFree(uint a, bool freeSegment)
    {
        if (a == 0) return 0;
        uint data = R32(a), rv = 0;
        if (freeSegment) Free(data); else rv = data;
        Free(a);
        return rv;
    }

    [Hle("monoeg_g_array_append_vals", Library = Lib)]
    uint GArrayAppendVals(uint a, uint data, uint len)
    {
        uint es = R32(a + 12), n = R32(a + 4);
        bool zt = R32(a + 16) != 0;
        ArrayEnsure(a, n + len + (zt ? 1u : 0));
        GuestMemory.Copy(R32(a) + n * es, data, len * es);
        W32(a + 4, n + len);
        if (zt) GuestMemory.Fill(R32(a) + (n + len) * es, 0, es);
        return a;
    }

    [Hle("monoeg_g_array_insert_vals", Library = Lib)]
    uint GArrayInsertVals(uint a, uint index, uint data, uint len)
    {
        uint es = R32(a + 12), n = R32(a + 4);
        bool zt = R32(a + 16) != 0;
        ArrayEnsure(a, n + len + (zt ? 1u : 0));
        uint d = R32(a);
        GuestMemory.Copy(d + (index + len) * es, d + index * es, (n - index) * es);
        GuestMemory.Copy(d + index * es, data, len * es);
        W32(a + 4, n + len);
        if (zt) GuestMemory.Fill(d + (n + len) * es, 0, es);
        return a;
    }

    // ------------------------------------------------------------------ GPtrArray { pdata, len, size }

    void PtrArrayGrow(uint a, uint extra)
    {
        uint need = R32(a + 4) + extra;
        if (need <= R32(a + 8)) return;
        uint size = 1;
        while (size < need) size <<= 1;
        size = Math.Max(size, 16);
        W32(a + 8, size);
        W32(a, Realloc(R32(a), size * 4));
    }

    [Hle("monoeg_g_ptr_array_new", Library = Lib)] uint GPtrArrayNew() => GPtrArraySizedNew(0);

    [Hle("monoeg_g_ptr_array_sized_new", Library = Lib)]
    uint GPtrArraySizedNew(uint reserved)
    {
        uint a = Malloc0(12);
        if (reserved > 0) PtrArrayGrow(a, reserved);
        return a;
    }

    [Hle("monoeg_g_ptr_array_free", Library = Lib)]
    uint GPtrArrayFree(uint a, bool freeSegment)
    {
        if (a == 0) return 0;
        uint data = R32(a), rv = 0;
        if (freeSegment) Free(data); else rv = data;
        Free(a);
        return rv;
    }

    [Hle("monoeg_g_ptr_array_add", Library = Lib)]
    void GPtrArrayAdd(uint a, uint data)
    {
        if (a == 0) return;
        PtrArrayGrow(a, 1);
        uint n = R32(a + 4);
        W32(R32(a) + n * 4, data);
        W32(a + 4, n + 1);
    }

    [Hle("monoeg_g_ptr_array_remove_index", Library = Lib)]
    uint GPtrArrayRemoveIndex(uint a, uint index)
    {
        uint n = R32(a + 4), p = R32(a);
        if (index >= n) return 0;
        uint removed = R32(p + index * 4);
        if (index != n - 1) GuestMemory.Copy(p + index * 4, p + (index + 1) * 4, (n - index - 1) * 4);
        W32(a + 4, n - 1);
        W32(p + (n - 1) * 4, 0);
        return removed;
    }

    [Hle("monoeg_g_ptr_array_remove_index_fast", Library = Lib)]
    uint GPtrArrayRemoveIndexFast(uint a, uint index)
    {
        uint n = R32(a + 4), p = R32(a);
        if (index >= n) return 0;
        uint removed = R32(p + index * 4);
        if (index != n - 1) W32(p + index * 4, R32(p + (n - 1) * 4));
        W32(a + 4, n - 1);
        W32(p + (n - 1) * 4, 0);
        return removed;
    }

    int PtrIndex(uint a, uint data)
    {
        uint n = R32(a + 4), p = R32(a);
        for (uint i = 0; i < n; i++) if (R32(p + i * 4) == data) return (int)i;
        return -1;
    }

    [Hle("monoeg_g_ptr_array_remove", Library = Lib)]
    bool GPtrArrayRemove(uint a, uint data)
    {
        int i = a == 0 ? -1 : PtrIndex(a, data);
        if (i < 0) return false;
        GPtrArrayRemoveIndex(a, (uint)i);
        return true;
    }

    [Hle("monoeg_g_ptr_array_remove_fast", Library = Lib)]
    bool GPtrArrayRemoveFast(uint a, uint data)
    {
        int i = a == 0 ? -1 : PtrIndex(a, data);
        if (i < 0) return false;
        GPtrArrayRemoveIndexFast(a, (uint)i);
        return true;
    }

    // ------------------------------------------------------------------ GList { data, next, prev }

    uint NewList(uint data, uint prev, uint next)
    {
        uint l = Malloc0(12);
        W32(l, data); W32(l + 4, next); W32(l + 8, prev);
        if (prev != 0) W32(prev + 4, l);
        if (next != 0) W32(next + 8, l);
        return l;
    }

    [Hle("monoeg_g_list_alloc", Library = Lib)] uint GListAlloc() => Malloc0(12);

    [Hle("monoeg_g_list_prepend", Library = Lib)]
    uint GListPrepend(uint list, uint data) => NewList(data, list != 0 ? R32(list + 8) : 0, list);

    static uint ListLast(uint list)
    {
        if (list == 0) return 0;
        while (R32(list + 4) != 0) list = R32(list + 4);
        return list;
    }

    [Hle("monoeg_g_list_append", Library = Lib)]
    uint GListAppend(uint list, uint data)
    {
        uint node = NewList(data, ListLast(list), 0);
        return list != 0 ? list : node;
    }

    [Hle("monoeg_g_list_length", Library = Lib)]
    uint GListLength(uint list) { uint n = 0; for (; list != 0; list = R32(list + 4)) n++; return n; }

    [Hle("monoeg_g_list_nth", Library = Lib)]
    uint GListNth(uint list, uint n) { for (; list != 0 && n > 0; n--) list = R32(list + 4); return list; }

    [Hle("monoeg_g_list_nth_data", Library = Lib)]
    uint GListNthData(uint list, uint n) => GListNth(list, n) is var l and not 0 ? R32(l) : 0;

    [Hle("monoeg_g_list_find", Library = Lib)]
    uint GListFind(uint list, uint data) { for (; list != 0; list = R32(list + 4)) if (R32(list) == data) return list; return 0; }

    static uint Disconnect(uint node)
    {
        uint next = R32(node + 4), prev = R32(node + 8);
        if (next != 0) W32(next + 8, prev);
        if (prev != 0) W32(prev + 4, next);
        return node;
    }

    [Hle("monoeg_g_list_remove", Library = Lib)]
    uint GListRemove(uint list, uint data)
    {
        uint cur = GListFind(list, data);
        if (cur == 0) return list;
        if (cur == list) list = R32(list + 4);
        Free(Disconnect(cur));
        return list;
    }

    [Hle("monoeg_g_list_remove_link", Library = Lib)]
    uint GListRemoveLink(uint list, uint link)
    {
        if (link == 0) return list;
        if (list == link) list = R32(list + 4);
        Disconnect(link);
        W32(link + 4, 0); W32(link + 8, 0);
        return list;
    }

    [Hle("monoeg_g_list_delete_link", Library = Lib)]
    uint GListDeleteLink(uint list, uint link)
    {
        list = GListRemoveLink(list, link);
        Free(link);
        return list;
    }

    [Hle("monoeg_g_list_reverse", Library = Lib)]
    uint GListReverse(uint list)
    {
        uint reverse = 0;
        while (list != 0)
        {
            reverse = list;
            list = R32(reverse + 4);
            W32(reverse + 4, R32(reverse + 8));
            W32(reverse + 8, list);
        }
        return reverse;
    }

    [Hle("monoeg_g_list_copy", Library = Lib)]
    uint GListCopy(uint list)
    {
        uint copy = 0, tail = 0;
        for (; list != 0; list = R32(list + 4))
        {
            tail = NewList(R32(list), tail, 0);
            if (copy == 0) copy = tail;
        }
        return copy;
    }

    [Hle("monoeg_g_list_foreach", Library = Lib)]
    void GListForeach(uint list, uint func, uint userData)
    {
        while (list != 0)
        {
            uint next = R32(list + 4);
            Call(func, R32(list), userData);
            list = next;
        }
    }

    [Hle("monoeg_g_list_free", Library = Lib)]
    void GListFree(uint list) { while (list != 0) { uint n = R32(list + 4); Free(list); list = n; } }

    /// <summary>g_list_insert_before(list, sibling, data): NULL sibling appends.</summary>
    [Hle("monoeg_g_list_insert_before", Library = Lib)]
    uint GListInsertBefore(uint list, uint sibling, uint data)
    {
        if (sibling == 0) return GListAppend(list, data);
        uint node = NewList(data, R32(sibling + 8), sibling);
        return sibling == list ? node : list;
    }

    /// <summary>A stable merge sort of the node addresses by their data (the guest comparison).</summary>
    static uint[] SortNodes(List<uint> nodes, uint func)
    {
        var a = nodes.ToArray();
        var tmp = new uint[a.Length];
        for (int width = 1; width < a.Length; width *= 2)
        {
            for (int lo = 0; lo < a.Length; lo += 2 * width)
            {
                int mid = Math.Min(lo + width, a.Length), hi = Math.Min(lo + 2 * width, a.Length), i = lo, j = mid, k = lo;
                while (i < mid && j < hi) tmp[k++] = (int)Call(func, R32(a[i]), R32(a[j])) <= 0 ? a[i++] : a[j++];
                while (i < mid) tmp[k++] = a[i++];
                while (j < hi) tmp[k++] = a[j++];
            }
            (a, tmp) = (tmp, a);
        }
        return a;
    }

    [Hle("monoeg_g_list_sort", Library = Lib)]
    uint GListSort(uint list, uint func)
    {
        if (list == 0 || R32(list + 4) == 0) return list;
        var nodes = new List<uint>();
        for (uint l = list; l != 0; l = R32(l + 4)) nodes.Add(l);
        var s = SortNodes(nodes, func);
        for (int i = 0; i < s.Length; i++)
        {
            W32(s[i] + 8, i > 0 ? s[i - 1] : 0);
            W32(s[i] + 4, i + 1 < s.Length ? s[i + 1] : 0);
        }
        return s[0];
    }

    // ------------------------------------------------------------------ GSList { data, next }

    [Hle("monoeg_g_slist_prepend", Library = Lib)]
    uint GSlistPrepend(uint list, uint data) { uint n = Malloc0(8); W32(n, data); W32(n + 4, list); return n; }

    [Hle("monoeg_g_slist_last", Library = Lib)]
    uint GSlistLast(uint list) { if (list == 0) return 0; while (R32(list + 4) != 0) list = R32(list + 4); return list; }

    [Hle("monoeg_g_slist_append", Library = Lib)]
    uint GSlistAppend(uint list, uint data)
    {
        uint n = GSlistPrepend(0, data);
        if (list == 0) return n;
        W32(GSlistLast(list) + 4, n);
        return list;
    }

    [Hle("monoeg_g_slist_concat", Library = Lib)]
    uint GSlistConcat(uint l1, uint l2)
    {
        if (l1 == 0) return l2;
        W32(GSlistLast(l1) + 4, l2);
        return l1;
    }

    [Hle("monoeg_g_slist_copy", Library = Lib)]
    uint GSlistCopy(uint list)
    {
        uint copy = 0, tail = 0;
        for (; list != 0; list = R32(list + 4))
        {
            uint n = GSlistPrepend(0, R32(list));
            if (tail != 0) W32(tail + 4, n); else copy = n;
            tail = n;
        }
        return copy;
    }

    [Hle("monoeg_g_slist_find", Library = Lib)]
    uint GSlistFind(uint list, uint data) { for (; list != 0; list = R32(list + 4)) if (R32(list) == data) return list; return 0; }

    [Hle("monoeg_g_slist_delete_link", Library = Lib)]
    uint GSlistDeleteLink(uint list, uint link)
    {
        if (list == 0 || link == 0) return list;
        if (list == link) { uint next = R32(list + 4); Free(list); return next; }
        for (uint prev = list; R32(prev + 4) != 0; prev = R32(prev + 4))
            if (R32(prev + 4) == link) { W32(prev + 4, R32(link + 4)); Free(link); break; }
        return list;
    }

    [Hle("monoeg_g_slist_remove", Library = Lib)]
    uint GSlistRemove(uint list, uint data) => GSlistDeleteLink(list, GSlistFind(list, data));

    [Hle("monoeg_g_slist_foreach", Library = Lib)]
    void GSlistForeach(uint list, uint func, uint userData)
    {
        while (list != 0)
        {
            uint next = R32(list + 4);
            Call(func, R32(list), userData);
            list = next;
        }
    }

    [Hle("monoeg_g_slist_free", Library = Lib)]
    void GSlistFree(uint list) { while (list != 0) { uint n = R32(list + 4); Free(list); list = n; } }

    [Hle("monoeg_g_slist_free_1", Library = Lib)] void GSlistFree1(uint node) => Free(node);

    /// <summary>g_slist_insert_sorted(list, data, func): before the first node the data compares below.</summary>
    [Hle("monoeg_g_slist_insert_sorted", Library = Lib)]
    uint GSlistInsertSorted(uint list, uint data, uint func)
    {
        if (list == 0 || (int)Call(func, R32(list), data) > 0) return GSlistPrepend(list, data);
        uint prev = list;
        for (; R32(prev + 4) != 0; prev = R32(prev + 4))
            if ((int)Call(func, R32(R32(prev + 4)), data) > 0) break;
        W32(prev + 4, GSlistPrepend(R32(prev + 4), data));
        return list;
    }

    [Hle("monoeg_g_slist_length", Library = Lib)]
    uint GSlistLength(uint list) { uint n = 0; for (; list != 0; list = R32(list + 4)) n++; return n; }

    [Hle("monoeg_g_slist_nth", Library = Lib)]
    uint GSlistNth(uint list, uint n) { for (; list != 0 && n > 0; n--) list = R32(list + 4); return list; }

    [Hle("monoeg_g_slist_nth_data", Library = Lib)]
    uint GSlistNthData(uint list, uint n) => GSlistNth(list, n) is var l and not 0 ? R32(l) : 0;

    [Hle("monoeg_g_slist_reverse", Library = Lib)]
    uint GSlistReverse(uint list)
    {
        uint prev = 0;
        while (list != 0) { uint next = R32(list + 4); W32(list + 4, prev); prev = list; list = next; }
        return prev;
    }

    // ------------------------------------------------------------------ GQueue { head, tail, length }

    [Hle("monoeg_g_queue_new", Library = Lib)] uint GQueueNew() => Malloc0(12);

    [Hle("monoeg_g_queue_free", Library = Lib)]
    void GQueueFree(uint q) { if (q == 0) return; GListFree(R32(q)); Free(q); }

    [Hle("monoeg_g_queue_is_empty", Library = Lib)] bool GQueueIsEmpty(uint q) => q == 0 || R32(q + 8) == 0;

    [Hle("monoeg_g_queue_pop_head", Library = Lib)]
    uint GQueuePopHead(uint q)
    {
        if (q == 0 || R32(q) == 0) return 0;
        uint old = R32(q), result = R32(old), head = R32(old + 4);
        Free(old);
        W32(q, head);
        W32(q + 8, R32(q + 8) - 1);
        if (head != 0) W32(head + 8, 0); else W32(q + 4, 0);
        return result;
    }

    [Hle("monoeg_g_queue_push_head", Library = Lib)]
    void GQueuePushHead(uint q, uint data)
    {
        if (q == 0) return;
        W32(q, GListPrepend(R32(q), data));
        if (R32(q + 4) == 0) W32(q + 4, R32(q));
        W32(q + 8, R32(q + 8) + 1);
    }

    // ------------------------------------------------------------------ GHashTable
    // { hash_func, key_equal_func, Slot **table, table_size, in_use, threshold, last_rehash, value_destroy, key_destroy },
    // Slot { key, value, next }; the iterator { GHashTable *ht; int slot_index; Slot *slot }.

    const uint HtHash = 0, HtEqual = 4, HtTable = 8, HtSize = 12, HtInUse = 16, HtThreshold = 20, HtLastRehash = 24,
        HtValueDestroy = 28, HtKeyDestroy = 32;

    uint HashOf(uint ht, uint key)
    {
        uint fn = R32(ht + HtHash);
        return KnownFunction(fn) switch
        {
            "monoeg_g_direct_hash" => key,
            "monoeg_g_str_hash" => GStrHash(key),
            _ => Call(fn, key),
        };
    }

    bool KeysEqual(uint ht, uint a, uint b)
    {
        uint fn = R32(ht + HtEqual);
        return KnownFunction(fn) switch
        {
            "monoeg_g_direct_equal" => a == b,
            "monoeg_g_str_equal" => GStrEqual(a, b),
            _ => Call(fn, a, b) != 0,
        };
    }

    /// <summary>The eglib hash/equality function an address calls (an import stub or this module's own stub), so the
    /// common tables do not go through the guest for every probe.</summary>
    string? KnownFunction(uint fn)
    {
        uint nid = StubNid(fn);
        if (nid == 0) return null;
        var info = NidDatabase.Lookup(nid);
        return info?.Library == Lib ? info.Name : null;
    }

    [Hle("monoeg_g_hash_table_new", Library = Lib)]
    uint GHashTableNew(uint hash, uint equal) => GHashTableNewFull(hash, equal, 0, 0);

    [Hle("monoeg_g_hash_table_new_full", Library = Lib)]
    uint GHashTableNewFull(uint hash, uint equal, uint keyDestroy, uint valueDestroy)
    {
        if (hash == 0) hash = ExportStub("monoeg_g_direct_hash");
        if (equal == 0) equal = ExportStub("monoeg_g_direct_equal");
        uint ht = Malloc0(36), size = GSpacedPrimesClosest(1);
        W32(ht + HtHash, hash); W32(ht + HtEqual, equal);
        W32(ht + HtSize, size); W32(ht + HtTable, Malloc0(size * 4)); W32(ht + HtLastRehash, size);
        W32(ht + HtKeyDestroy, keyDestroy); W32(ht + HtValueDestroy, valueDestroy);
        return ht;
    }

    void Rehash(uint ht)
    {
        int diff = Math.Abs((int)R32(ht + HtLastRehash) - (int)R32(ht + HtInUse));
        if (!(diff * 0.75 > R32(ht + HtSize) * 2.0)) return;
        uint oldSize = R32(ht + HtSize), oldTable = R32(ht + HtTable);
        uint size = GSpacedPrimesClosest(R32(ht + HtInUse)), table = Malloc0(size * 4);
        W32(ht + HtSize, size); W32(ht + HtTable, table);
        for (uint i = 0; i < oldSize; i++)
            for (uint s = R32(oldTable + i * 4), next; s != 0; s = next)
            {
                uint h = HashOf(ht, R32(s)) % size;
                next = R32(s + 8);
                W32(s + 8, R32(table + h * 4));
                W32(table + h * 4, s);
            }
        Free(oldTable);
        W32(ht + HtLastRehash, size);
    }

    /// <summary>g_hash_table_insert_replace(hash, key, value, replace): an existing key keeps its key unless replace
    /// (the destroy functions run on what is replaced).</summary>
    [Hle("monoeg_g_hash_table_insert_replace", Library = Lib)]
    void GHashTableInsertReplace(uint ht, uint key, uint value, bool replace)
    {
        if (ht == 0) return;
        if (R32(ht + HtInUse) >= R32(ht + HtThreshold)) Rehash(ht);
        uint h = HashOf(ht, key) % R32(ht + HtSize), bucket = R32(ht + HtTable) + h * 4;
        for (uint s = R32(bucket); s != 0; s = R32(s + 8))
        {
            if (!KeysEqual(ht, R32(s), key)) continue;
            if (replace)
            {
                if (R32(ht + HtKeyDestroy) is var kd and not 0) Call(kd, R32(s));
                W32(s, key);
            }
            if (R32(ht + HtValueDestroy) is var vd and not 0) Call(vd, R32(s + 4));
            W32(s + 4, value);
            return;
        }
        uint n = Malloc(12);
        W32(n, key); W32(n + 4, value); W32(n + 8, R32(bucket));
        W32(bucket, n);
        W32(ht + HtInUse, R32(ht + HtInUse) + 1);
    }

    uint FindSlot(uint ht, uint key, out uint prevLink)
    {
        uint h = HashOf(ht, key) % R32(ht + HtSize);
        prevLink = R32(ht + HtTable) + h * 4;
        for (uint s = R32(prevLink); s != 0; prevLink = s + 8, s = R32(s + 8))
            if (KeysEqual(ht, R32(s), key)) return s;
        return 0;
    }

    [Hle("monoeg_g_hash_table_lookup", Library = Lib)]
    uint GHashTableLookup(uint ht, uint key) => ht != 0 && FindSlot(ht, key, out _) is var s and not 0 ? R32(s + 4) : 0;

    [Hle("monoeg_g_hash_table_lookup_extended", Library = Lib)]
    bool GHashTableLookupExtended(uint ht, uint key, uint origKey, uint value)
    {
        if (ht == 0 || FindSlot(ht, key, out _) is not (var s and not 0)) return false;
        if (origKey != 0) W32(origKey, R32(s));
        if (value != 0) W32(value, R32(s + 4));
        return true;
    }

    [Hle("monoeg_g_hash_table_size", Library = Lib)] uint GHashTableSize(uint ht) => ht == 0 ? 0 : R32(ht + HtInUse);

    [Hle("monoeg_g_hash_table_foreach", Library = Lib)]
    void GHashTableForeach(uint ht, uint func, uint userData)
    {
        if (ht == 0 || func == 0) return;
        uint table = R32(ht + HtTable), size = R32(ht + HtSize);
        for (uint i = 0; i < size; i++)
            for (uint s = R32(table + i * 4); s != 0; s = R32(s + 8))
                Call(func, R32(s), R32(s + 4), userData);
    }

    void DestroySlot(uint ht, uint s, bool notify)
    {
        if (notify)
        {
            if (R32(ht + HtKeyDestroy) is var kd and not 0) Call(kd, R32(s));
            if (R32(ht + HtValueDestroy) is var vd and not 0) Call(vd, R32(s + 4));
        }
        Free(s);
    }

    [Hle("monoeg_g_hash_table_remove", Library = Lib)]
    bool GHashTableRemove(uint ht, uint key)
    {
        if (ht == 0 || FindSlot(ht, key, out uint link) is not (var s and not 0)) return false;
        W32(link, R32(s + 8));
        DestroySlot(ht, s, true);
        W32(ht + HtInUse, R32(ht + HtInUse) - 1);
        return true;
    }

    uint ForeachRemove(uint ht, uint func, uint userData, bool notify)
    {
        if (ht == 0 || func == 0) return 0;
        uint table = R32(ht + HtTable), size = R32(ht + HtSize), count = 0;
        for (uint i = 0; i < size; i++)
        {
            uint link = table + i * 4;
            for (uint s = R32(link); s != 0;)
            {
                uint next = R32(s + 8);
                if (Call(func, R32(s), R32(s + 4), userData) != 0)
                {
                    W32(link, next);
                    DestroySlot(ht, s, notify);
                    count++;
                }
                else link = s + 8;
                s = next;
            }
        }
        if (count > 0)
        {
            W32(ht + HtInUse, R32(ht + HtInUse) - count);
            Rehash(ht);
        }
        return count;
    }

    [Hle("monoeg_g_hash_table_foreach_remove", Library = Lib)]
    uint GHashTableForeachRemove(uint ht, uint func, uint userData) => ForeachRemove(ht, func, userData, true);

    [Hle("monoeg_g_hash_table_foreach_steal", Library = Lib)]
    uint GHashTableForeachSteal(uint ht, uint func, uint userData) => ForeachRemove(ht, func, userData, false);

    [Hle("monoeg_g_hash_table_destroy", Library = Lib)]
    void GHashTableDestroy(uint ht)
    {
        if (ht == 0) return;
        uint table = R32(ht + HtTable), size = R32(ht + HtSize);
        for (uint i = 0; i < size; i++)
            for (uint s = R32(table + i * 4), next; s != 0; s = next) { next = R32(s + 8); DestroySlot(ht, s, true); }
        Free(table);
        Free(ht);
    }

    [Hle("monoeg_g_hash_table_iter_init", Library = Lib)]
    void GHashTableIterInit(uint it, uint ht) { W32(it, ht); W32(it + 4, 0xFFFFFFFF); W32(it + 8, 0); }

    /// <summary>g_hash_table_iter_next(iter, key*, value*): the next slot in table order, FALSE (index -2) at the end.</summary>
    [Hle("monoeg_g_hash_table_iter_next", Library = Lib)]
    bool GHashTableIterNext(uint it, uint key, uint value)
    {
        uint ht = R32(it);
        int index = (int)R32(it + 4);
        if (index == -2) return false;
        uint slot = R32(it + 8);
        if (slot == 0)
        {
            while (true)
            {
                index++;
                if (index >= (int)R32(ht + HtSize)) { W32(it + 4, unchecked((uint)-2)); return false; }
                if (R32(R32(ht + HtTable) + (uint)index * 4) != 0) break;
            }
            W32(it + 4, (uint)index);
            slot = R32(R32(ht + HtTable) + (uint)index * 4);
        }
        if (key != 0) W32(key, R32(slot));
        if (value != 0) W32(value, R32(slot + 4));
        W32(it + 8, R32(slot + 8));
        return true;
    }

    // ------------------------------------------------------------------ GError { domain, code, message }

    uint NewError(uint domain, int code, string message)
    {
        uint e = Malloc(12);
        W32(e, domain); W32(e + 4, (uint)code); W32(e + 8, StrDup(message));
        return e;
    }

    /// <summary>Stores a new GError in *err (when the caller asked for one).</summary>
    void SetError(uint err, uint domain, int code, string message)
    {
        if (err != 0) W32(err, NewError(domain, code, message));
    }

    [Hle("monoeg_g_error_free", Library = Lib)]
    void GErrorFree(uint e) { if (e == 0) return; Free(R32(e + 8)); Free(e); }
}
