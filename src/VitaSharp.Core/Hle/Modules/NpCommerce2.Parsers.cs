using System.Buffers.Binary;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// np_commerce2.suprx's result parsers (FW 3.74, the system-side NIDs the Store / Sign Up / PS4 Link use). A store
/// server reply is a 100-byte native header (word 0 = reply id, word 2 = 4 + body length, word 3 = a "lenient"
/// flag) followed at +0x68 by a body of TLV items: a big-endian 16-bit tag (low 12 bits) and a big-endian 16-bit
/// length, then the value. Items with tag 1 are typed sections (value = 12-bit type + 16-bit count, then the
/// section's items); items with tag 2 are typed sections with an 8-byte header (type, count, two more words).
/// Each parser fills a caller record whose first 0x5C bytes are common: [0] reply id, [1] reply buffer, [2] header +
/// body length, [3] this item's data, [4] its length, [5] the cursor after the item's fixed part, [6] the lenient
/// flag; the item's own fields follow from word 0x17 on. Strings are pointers into the reply; a missing or empty
/// string points at reply+4 (a NUL inside the header). Times are server milliseconds since 1970 turned into
/// SceRtcTick microseconds since year 1 (0 stays 0). Errors are the library's: 0x80550F01 not initialised,
/// 0x80550F03 NULL argument, 0x80550F06 index out of range, 0x80550F14 buffer too small, 0x80550F87 tag not found,
/// 0x80550F88 / 0x80550F89 item shorter than its fixed part, 0x80550F8A item runs past its parent.
/// </summary>
public sealed partial class NpCommerce2
{
    internal const int C2_NOT_INIT = unchecked((int)0x80550F01), C2_NULL = unchecked((int)0x80550F03),
        C2_NO_INDEX = unchecked((int)0x80550F06), C2_BAD_BUFFER = unchecked((int)0x80550F14),
        C2_NOT_FOUND = unchecked((int)0x80550F87), C2_NO_ROOM = unchecked((int)0x80550F88),
        C2_TOO_SHORT = unchecked((int)0x80550F89), C2_OVERRUN = unchecked((int)0x80550F8A);

    // ------------------------------------------------------------------ TLV reader

    static ushort Be16(uint a) => BinaryPrimitives.ReverseEndianness(GuestMemory.Read<ushort>(a));
    static uint Be32(uint a) => BinaryPrimitives.ReverseEndianness(GuestMemory.Read<uint>(a));
    static ulong Be64(uint a) => BinaryPrimitives.ReverseEndianness(GuestMemory.Read<ulong>(a));
    static uint Rd(uint rec, int word) => GuestMemory.R32(rec + (uint)word * 4);
    static void Wr(uint rec, int word, uint v) => GuestMemory.W32(rec + (uint)word * 4, v);
    static uint At(uint rec, int word) => rec + (uint)word * 4;
    static bool Opt(int r) => r >= 0 || r == C2_NOT_FOUND;

    /// <summary>First item with <paramref name="tag"/> in [p, p+len): offset of its value, or an error. A value
    /// shorter than <paramref name="minLen"/> fails; one running past len fails unless lenient.</summary>
    internal static int TlvFind(uint p, uint len, int tag, int minLen, out ushort valueLen, bool lenient = false)
    {
        valueLen = 0;
        if (len < 4) return C2_NOT_FOUND;
        uint cur = p;
        do
        {
            int t = Be16(cur) & 0xFFF, l = Be16(cur + 2);
            if (t == tag)
            {
                uint off = cur + 4 - p;
                if (len < off + (uint)minLen) return C2_NO_ROOM;
                if (l < minLen) return C2_TOO_SHORT;
                if (len < off + (uint)l && !lenient) return C2_OVERRUN;
                valueLen = (ushort)l;
                return (int)off;
            }
            cur += 4 + (uint)l;
        } while (cur - p + 4 <= len);
        return C2_NOT_FOUND;
    }

    /// <summary>The tag-1 section of <paramref name="type"/>: offset of its items, their length and its count.</summary>
    internal static int TlvSection(uint p, uint len, int type, out ushort itemsLen, out uint count, bool lenient = false)
    {
        itemsLen = 0; count = 0;
        uint cur = p, used = 0;
        do
        {
            int r = TlvFind(cur, len - used, 1, 4, out ushort sl, lenient);
            if (r < 0) return r;
            cur += (uint)r;
            if ((Be16(cur) & 0xFFF) == type)
            {
                itemsLen = (ushort)(sl - 4);
                count = Be16(cur + 2);
                return (int)(cur - p + 4);
            }
            cur += sl;
            used = cur - p;
        } while (used <= len);
        return C2_NOT_FOUND;
    }

    /// <summary>The tag-2 section of <paramref name="type"/> (8-byte header). <paramref name="info"/> (0x2C bytes,
    /// optional) gets [0] the word at +4, [1] the count at +2, [2] the word at +6.</summary>
    internal static int TlvSection8(uint p, uint len, int type, out ushort itemsLen, uint info, bool lenient)
    {
        itemsLen = 0;
        uint cur = p, used = 0;
        do
        {
            int r = TlvFind(cur, len - used, 2, 8, out ushort sl, lenient);
            if (r < 0) return r;
            cur += (uint)r;
            if ((Be16(cur) & 0xFFF) == type)
            {
                itemsLen = (ushort)(sl - 8);
                if (info != 0)
                {
                    GuestMemory.Fill(info, 0, 0x2C);
                    Wr(info, 0, Be16(cur + 4));
                    Wr(info, 1, Be16(cur + 2));
                    Wr(info, 2, Be16(cur + 6));
                }
                return (int)(cur - p + 8);
            }
            cur += sl;
            used = cur - p;
        } while (used <= len);
        return C2_NOT_FOUND;
    }

    /// <summary>Item <paramref name="index"/> of the section <paramref name="tag"/> (whose items carry the same tag).</summary>
    internal static int TlvElement(uint p, uint len, int tag, int minLen, uint index, out ushort elemLen)
    {
        elemLen = 0;
        int r = TlvSection(p, len, tag, out ushort itemsLen, out uint count);
        if (r < 0) return r;
        if (index >= count) return C2_NO_INDEX;
        uint start = p + (uint)r, cur = start;
        ushort last = 0;
        for (uint i = 0; i <= index; i++)
        {
            if (i != 0) cur += last;
            r = TlvFind(cur, itemsLen - (cur - start), tag, minLen, out last);
            if (r < 0) return r;
            cur += (uint)r;
        }
        elemLen = last;
        return (int)(cur - p);
    }

    /// <summary>Item <paramref name="index"/> of the tag-2 section <paramref name="tag"/>.</summary>
    internal static int TlvElement8(uint p, uint len, int tag, int minLen, uint index, out ushort elemLen, uint lenient)
    {
        elemLen = 0;
        int r = TlvSection8Count(p, len, tag, lenient != 0, out uint count);
        if (r < 0) return r;
        if (index >= count) return C2_NO_INDEX;
        uint cur = p + (uint)r;
        ushort last = 0;
        for (uint i = 0; i <= index; i++)
        {
            if (i != 0) cur += last;
            r = TlvFind(cur, len - (cur - p), tag, minLen, out last);
            if (r < 0) return r;
            cur += (uint)r;
        }
        elemLen = last;
        return (int)(cur - p);
    }

    static int TlvSection8Count(uint p, uint len, int type, bool lenient, out uint count)
    {
        count = 0;
        uint cur = p, used = 0;
        do
        {
            int r = TlvFind(cur, len - used, 2, 8, out ushort sl, lenient);
            if (r < 0) return r;
            cur += (uint)r;
            if ((Be16(cur) & 0xFFF) == type) { count = Be16(cur + 2); return (int)(cur - p + 8); }
            cur += sl;
            used = cur - p;
        } while (used <= len);
        return C2_NOT_FOUND;
    }

    /// <summary>The reply header: 0x68 (the body offset) or 0x80550F14.</summary>
    internal static int ReplyHeader(uint buf, uint size, out uint id, out uint bodyLen, out uint lenient)
    {
        id = bodyLen = lenient = 0;
        if (size < 0x68) return C2_BAD_BUFFER;
        uint w2 = GuestMemory.R32(buf + 8);
        if (w2 < 4 || size < w2 + 100) return C2_BAD_BUFFER;
        id = GuestMemory.R32(buf);
        lenient = GuestMemory.R32(buf + 12);
        bodyLen = w2 - 4;
        return 0x68;
    }

    // ------------------------------------------------------------------ record helpers

    /// <summary>A top-level record over a reply buffer (zeroes <paramref name="size"/> bytes on success).</summary>
    static int Root(uint rec, uint size, uint buf, uint bufSize, out uint body, out uint bodyLen, bool keepLenient = false)
    {
        body = 0;
        int r = ReplyHeader(buf, bufSize, out uint id, out bodyLen, out uint lenient);
        if (r < 0) return r;
        GuestMemory.Fill(rec, 0, size);
        body = buf + (uint)r;
        Wr(rec, 0, id); Wr(rec, 1, buf); Wr(rec, 2, (uint)r + bodyLen); Wr(rec, 3, body); Wr(rec, 4, bodyLen);
        if (keepLenient) Wr(rec, 6, lenient);
        return r;
    }

    /// <summary>A child record over one item of <paramref name="parent"/>'s reply.</summary>
    static void Child(uint parent, uint rec, uint size, uint data, uint len) => ChildOf(Rd(parent, 0), Rd(parent, 1), rec, size, data, len);

    static void ChildOf(uint id, uint buf, uint rec, uint size, uint data, uint len)
    {
        GuestMemory.Fill(rec, 0, size);
        Wr(rec, 0, id); Wr(rec, 1, buf); Wr(rec, 3, data); Wr(rec, 4, len);
    }

    /// <summary>The part of a record after its cursor: [5] .. [3]+[4].</summary>
    static void Rest(uint rec, out uint p, out uint len) { p = Rd(rec, 5); len = Rd(rec, 4) - (p - Rd(rec, 3)); }

    /// <summary>A string field: points at the item's value, or at reply+4 when it is missing or empty.</summary>
    static int Str(uint rec, uint dst, uint p, uint len, int tag)
    {
        GuestMemory.W32(dst, rec == 0 ? 0 : Rd(rec, 1) + 4);
        int r = TlvFind(p, len, tag, 0, out ushort l);
        if (r >= 0 && l != 0) GuestMemory.W32(dst, p + (uint)r);
        return r;
    }

    /// <summary>A section count field (written only when the section is there).</summary>
    static int Count(uint p, uint len, int type, uint dst)
    {
        int r = TlvSection(p, len, type, out _, out uint n);
        if (r >= 0) GuestMemory.W32(dst, n);
        return r;
    }

    static void Ticks(uint dst, uint src)
    {
        ulong ms = Be64(src);
        GuestMemory.Write(dst, ms == 0 ? 0UL : ms * 1000 + 62135596800000000UL);
    }

    static void Be32To(uint dst, uint src) => GuestMemory.W32(dst, Be32(src));
    static void Be64To(uint dst, uint src) => GuestMemory.Write(dst, Be64(src));

    int? Need(uint a) => !_inited ? C2_NOT_INIT : a == 0 ? C2_NULL : null;
    int? Need(uint a, uint b) => !_inited ? C2_NOT_INIT : a == 0 || b == 0 ? C2_NULL : null;
    int? Need(uint a, uint b, uint c) => !_inited ? C2_NOT_INIT : a == 0 || b == 0 || c == 0 ? C2_NULL : null;
}
