using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// np_common.suprx's utilities without public names (FW 3.74), read in the module: URL and XML escaping, Base64
// decoding, the library's own strtol family, the NP id helpers and the configuration block np_manager hands it. The
// escaping functions share one shape: (char *dst, uint dstSize, const char *src, uint srcLen, uint *outLen); a NULL
// dst only measures (then outLen is required), the count includes the terminating NUL, 0x80550601 for a missing source,
// 0x80550602 when dst is too small, 0x80550608 for a character XML cannot carry.

public sealed partial class NpManager
{
    const int COMMON_NULLARG = unchecked((int)0x80550601), COMMON_TOO_SMALL = unchecked((int)0x80550602),
        COMMON_BAD_CHAR = unchecked((int)0x80550608), COMMON_TOO_LONG = unchecked((int)0x80550606),
        B64_SIZE = unchecked((int)0x80558003), B64_CHAR = unchecked((int)0x80558004);

    /// <summary>Writes one output piece: counts it, and when writing checks the room first.</summary>
    sealed class EscapeWriter(uint start, uint size)
    {
        public uint Count;
        public bool Writing { get; } = start != 0;
        uint _p = start;
        public bool Put(ReadOnlySpan<byte> bytes)
        {
            Count += (uint)bytes.Length;
            if (Count > size) return !Writing;
            if (Writing) { foreach (byte b in bytes) GuestMemory.Write(_p++, b); }
            return true;
        }
        public int Finish(uint outLen)
        {
            if (Count + 1 > size) { if (Writing) return COMMON_TOO_SMALL; }
            else if (Writing) GuestMemory.Write<byte>(_p, 0);
            if (outLen != 0) GuestMemory.W32(outLen, Count + 1);
            return 0;
        }
    }

    const string Hex = "0123456789ABCDEF";

    /// <summary>application/x-www-form-urlencoded: letters, digits and "*-._" stay, a space becomes '+', every other
    /// byte %XX (upper-case hex).</summary>
    [Hle("sceNpCommon_14BC2B99", Nid = 0x14BC2B99, Library = "SceNpCommon")]
    int NpCommonUrlEncode(uint dst, uint size, uint src, uint len, uint outLen)
    {
        if (src == 0 || len == 0 || (dst == 0 && outLen == 0)) return COMMON_NULLARG;
        var w = new EscapeWriter(dst, size);
        for (uint i = 0; i < len; i++)
        {
            byte c = GuestMemory.Read<byte>(src + i);
            bool keep = c is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                or (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_';
            bool ok = c == ' ' ? w.Put("+"u8) : keep ? w.Put([c]) : w.Put([(byte)'%', (byte)Hex[c >> 4], (byte)Hex[c & 0xF]]);
            if (!ok) return COMMON_TOO_SMALL;
        }
        return w.Finish(outLen);
    }

    /// <summary>The control characters XML 1.0 cannot carry (all below 0x20 but tab, LF and CR).</summary>
    static bool XmlForbidden(byte c) => c < 0x20 && c is not (9 or 10 or 13);

    /// <summary>Escapes &lt; &gt; &amp; ' " for XML and copies well-formed UTF-8 sequences; forbidden control characters
    /// and broken UTF-8 are 0x80550608.</summary>
    [Hle("sceNpCommon_C2BF5C1F", Nid = 0xC2BF5C1F, Library = "SceNpCommon")]
    int NpCommonXmlEscape(uint dst, uint size, uint src, uint len, uint outLen)
    {
        if (src == 0 || len == 0 || (dst == 0 && outLen == 0)) return COMMON_NULLARG;
        var w = new EscapeWriter(dst, size);
        uint i = 0;
        byte B(uint k) => GuestMemory.Read<byte>(src + k);
        while (i < len)
        {
            byte c = B(i);
            bool ok;
            if (c < 0x80)
            {
                if (XmlForbidden(c)) return COMMON_BAD_CHAR;
                ok = c switch
                {
                    (byte)'<' => w.Put("&lt;"u8), (byte)'>' => w.Put("&gt;"u8), (byte)'&' => w.Put("&amp;"u8),
                    (byte)'\'' => w.Put("&apos;"u8), (byte)'"' => w.Put("&quot;"u8), _ => w.Put([c]),
                };
                i++;
            }
            else
            {
                int n = (c & 0xE0) == 0xC0 && c > 0xC1 && (B(i + 1) & 0xC0) == 0x80 ? 2
                    : (c & 0xF0) == 0xE0 && (B(i + 1) & 0xC0) == 0x80 && (B(i + 2) & 0xC0) == 0x80 ? 3
                    : (c & 0xF8) == 0xF0 && (B(i + 1) & 0xC0) == 0x80 && (B(i + 2) & 0xC0) == 0x80 && (B(i + 3) & 0xC0) == 0x80 ? 4 : 0;
                if (n == 0) return COMMON_BAD_CHAR;
                ok = w.Put(GuestMemory.ReadSpan(src + i, n));
                i += (uint)n;
            }
            if (!ok) return COMMON_TOO_SMALL;
        }
        return w.Finish(outLen);
    }

    static readonly (string Entity, byte Char)[] XmlEntities = [("&lt;", (byte)'<'), ("&gt;", (byte)'>'), ("&amp;", (byte)'&'), ("&apos;", (byte)'\''), ("&quot;", (byte)'"')];

    /// <summary>The five XML entities back to characters; any other '&amp;' stays as it is.</summary>
    [Hle("sceNpCommon_389F8C14", Nid = 0x389F8C14, Library = "SceNpCommon")]
    int NpCommonXmlUnescape(uint dst, uint size, uint src, uint len, uint outLen)
    {
        if (src == 0 || len == 0 || (dst == 0 && outLen == 0)) return COMMON_NULLARG;
        uint count = 0, i = 0, p = dst;
        while (i < len)
        {
            count++;
            if (count > size && dst != 0) return COMMON_TOO_SMALL;
            byte c = GuestMemory.Read<byte>(src + i), outc = c;
            uint step = 1;
            if (c == '&')
                foreach (var (e, ch) in XmlEntities)
                    if (MatchesAt(src + i, e)) { outc = ch; step = (uint)e.Length; break; }
            if (dst != 0) GuestMemory.Write(p++, outc);
            i += step;
        }
        if (count + 1 > size) { if (dst != 0) return COMMON_TOO_SMALL; }
        else if (dst != 0) GuestMemory.Write<byte>(p, 0);
        if (outLen != 0) GuestMemory.W32(outLen, count + 1);
        return 0;
    }

    static bool MatchesAt(uint p, string s)
    {
        for (int k = 0; k < s.Length; k++) if (GuestMemory.Read<byte>(p + (uint)k) != s[k]) return false;
        return true;
    }

    /// <summary>(char *dst, uint dstSize, const char *src, uint srcLen, uint *outLen, uint *consumed): the first
    /// character of an XML text (an entity or a UTF-8 sequence): how many bytes it takes in the source and in the output.
    /// The library unescapes the source into a 7-byte scratch to produce it, so a source longer than one character's
    /// worth fails with 0x80550602 there (and then reports neither count).</summary>
    [Hle("sceNpCommon_38B54F30", Nid = 0x38B54F30, Library = "SceNpCommon")]
    int NpCommonXmlFirstChar(uint dst, uint size, uint src, uint len, uint outLen, uint consumed)
    {
        if (src == 0 || len == 0 || (dst == 0 && outLen == 0)) return COMMON_NULLARG;
        bool writing = dst != 0;
        byte B(uint k) => GuestMemory.Read<byte>(src + k);
        uint used = 0, outN = 0;
        int r = 0;
        byte c = B(0);
        bool ready = false;
        if (c < 0x80)
        {
            outN = 1;
            if (c != '&') { used = 1; ready = true; }
            else if (len < 4) { r = COMMON_TOO_SMALL; used = 4; }
            else if (MatchesAt(src, "&lt;") || MatchesAt(src, "&gt;")) { used = 4; ready = true; }
            else if (len > 4)
            {
                if (MatchesAt(src, "&amp;")) { used = 5; ready = true; }
                else if (len < 6) { r = COMMON_TOO_SMALL; used = 6; }
                else { used = MatchesAt(src, "&apos;") || MatchesAt(src, "&quot;") ? 6u : 1u; ready = true; }
            }
            else { r = COMMON_TOO_SMALL; used = 5; }
        }
        else
        {
            ready = true;
            if ((c & 0xE0) == 0xC0 && c > 0xC1 && len < 2) { r = COMMON_TOO_SMALL; used = 2; ready = false; }
            else if ((c & 0xE0) == 0xC0 && c > 0xC1 && (B(1) & 0xC0) == 0x80) used = outN = 2;
            else if ((c & 0xF0) == 0xE0 && len < 3) { r = COMMON_TOO_SMALL; used = 3; ready = false; }
            else if ((c & 0xF0) == 0xE0 && (B(1) & 0xC0) == 0x80 && (B(2) & 0xC0) == 0x80) used = outN = 3;
            else if ((c & 0xF8) == 0xF0)
            {
                if (len < 4) { r = COMMON_TOO_SMALL; used = 4; ready = false; }
                else if ((B(1) & 0xC0) == 0x80 && (B(2) & 0xC0) == 0x80 && (B(3) & 0xC0) == 0x80) used = outN = 4;
            }
            else used = outN = 1;
        }
        if (ready)
        {
            if (size < outN && writing) r = COMMON_TOO_SMALL;
            else
            {
                uint scratch = Emu.Memory.Alloc(8, "np_common scratch", MemoryRegion.User, 8);
                try
                {
                    r = NpCommonXmlUnescape(scratch, 7, src, len, 0);
                    if (r < 0) return r;
                    if (writing) GuestMemory.Copy(dst, scratch, outN);
                }
                finally { Emu.Memory.Free(scratch); }
            }
        }
        if (outLen != 0) GuestMemory.W32(outLen, outN);
        if (consumed != 0) GuestMemory.W32(consumed, used);
        return r;
    }

    // ---- Base64 (the standard alphabet, '=' padding; the length must be a multiple of 4)

    static int B64(byte c) => c switch
    {
        >= (byte)'A' and <= (byte)'Z' => c - 'A', >= (byte)'a' and <= (byte)'z' => c - 'a' + 26,
        >= (byte)'0' and <= (byte)'9' => c - '0' + 52, (byte)'+' => 62, (byte)'/' => 63, _ => -1,
    };

    /// <summary>(const char *src, uint len): the decoded size, or 0x80558003 (length) / 0x80558004 (character).</summary>
    [Hle("sceNpCommon_A0C5C10E", Nid = 0xA0C5C10E, Library = "SceNpCommon")]
    int NpCommonBase64DecodedSize(uint src, uint len) => Base64Decode(src, len, 0, 0, false);

    /// <summary>(const char *src, uint len, uint8_t *dst, uint dstSize): decodes; returns the byte count, 0x80558003 when
    /// the length is not a multiple of 4 or dst is too small (what fitted is already written), 0x80558004 for a
    /// character outside the alphabet.</summary>
    [Hle("sceNpCommon_FB1F70F3", Nid = 0xFB1F70F3, Library = "SceNpCommon")]
    int NpCommonBase64Decode(uint src, uint len, uint dst, uint dstSize) => Base64Decode(src, len, dst, dstSize, true);

    static int Base64Decode(uint src, uint len, uint dst, uint room, bool write)
    {
        if ((len & 3) != 0) return B64_SIZE;
        int n = 0;
        int V(uint k) => B64(GuestMemory.Read<byte>(src + k));
        for (; len > 4; len -= 4, src += 4)
        {
            int a = V(0), b = V(1), c = V(2), d = V(3);
            if (a < 0 || b < 0 || c < 0 || d < 0) return B64_CHAR;
            if (write)
            {
                if (room < 3) return B64_SIZE;
                uint v = (uint)(a << 18 | b << 12 | c << 6 | d);
                GuestMemory.Write(dst, (byte)(v >> 16)); GuestMemory.Write(dst + 1, (byte)(v >> 8)); GuestMemory.Write(dst + 2, (byte)v);
                dst += 3; room -= 3;
            }
            n += 3;
        }
        if (len != 4) return B64_SIZE;
        int x = V(0), y = V(1);
        if (x < 0 || y < 0) return B64_CHAR;
        uint w = (uint)(x << 18 | y << 12);
        byte c2 = GuestMemory.Read<byte>(src + 2), c3 = GuestMemory.Read<byte>(src + 3);
        if (c2 == '=')
        {
            if (c3 != '=') return B64_CHAR;
            if (write) { if (room == 0) return B64_SIZE; GuestMemory.Write(dst, (byte)(w >> 16)); }
            return n + 1;
        }
        if (V(2) < 0) return B64_CHAR;
        w |= (uint)V(2) << 6;
        if (c3 == '=')
        {
            if (write) { if (room < 2) return B64_SIZE; GuestMemory.Write(dst, (byte)(w >> 16)); GuestMemory.Write(dst + 1, (byte)(w >> 8)); }
            return n + 2;
        }
        if (V(3) < 0) return B64_CHAR;
        w |= (uint)V(3);
        if (write)
        {
            if (room < 3) return B64_SIZE;
            GuestMemory.Write(dst, (byte)(w >> 16)); GuestMemory.Write(dst + 1, (byte)(w >> 8)); GuestMemory.Write(dst + 2, (byte)w);
        }
        return n + 3;
    }

    // ---- the library's strtol family: leading white space, (signed forms) any run of '+'/'-', then "0x"/"0X" (base
    // 0 or 16), "0b"/"0B" (base 0 or 2), a leading 0 (base 0: octal) or a leading 'o'/'O' (always octal); digits and
    // letters up to the base, wrapping without overflow checks; *end = the first character not taken. NULL: 0.

    static bool IsSpace(byte c) => c is (byte)' ' or >= 9 and <= 13;

    ulong ParseNumber(uint s, uint end, int radix, bool signed, out bool negative)
    {
        negative = false;
        while (IsSpace(GuestMemory.Read<byte>(s))) s++;
        if (signed)
            while (GuestMemory.Read<byte>(s) is (byte)'-' or (byte)'+')
            {
                if (GuestMemory.Read<byte>(s) == '-') negative = !negative;
                s++;
            }
        if (radix is < 2 or > 36) radix = 0;
        byte c0 = GuestMemory.Read<byte>(s), c1 = GuestMemory.Read<byte>(s + 1);
        if (c0 == '0')
        {
            if (c1 is (byte)'x' or (byte)'X' && radix is 0 or 16) { radix = 16; s += 2; }
            else if (c1 is (byte)'b' or (byte)'B' && radix is 0 or 2) { radix = 2; s += 2; }
            else if (c1 is not ((byte)'x' or (byte)'X' or (byte)'b' or (byte)'B') && radix == 0) { radix = 8; s++; }
        }
        else if (c0 is (byte)'o' or (byte)'O') { radix = 8; s++; }
        if (radix == 0) radix = 10;
        ulong v = 0;
        while (true)
        {
            byte c = GuestMemory.Read<byte>(s);
            int d = c is >= (byte)'0' and <= (byte)'9' ? c - '0'
                : c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' ? (c | 0x20) - 0x57 : 9999999;
            if (d >= radix) break;
            v = v * (ulong)radix + (ulong)d;
            s++;
        }
        if (end != 0) GuestMemory.W32(end, s);
        return v;
    }

    /// <summary>(const char *s, char **end, int base): int, wrapping.</summary>
    [Hle("sceNpCommon_4C4AA451", Nid = 0x4C4AA451, Library = "SceNpCommon")]
    int NpCommonStrtol(uint s, uint end, int radix)
    {
        if (s == 0) return 0;
        uint v = (uint)ParseNumber(s, end, radix, true, out bool neg);
        return neg ? -(int)v : (int)v;
    }

    /// <summary>(const char *s, char **end, int base): unsigned int, no sign.</summary>
    [Hle("sceNpCommon_5B770B80", Nid = 0x5B770B80, Library = "SceNpCommon")]
    uint NpCommonStrtoul(uint s, uint end, int radix) => s == 0 ? 0 : (uint)ParseNumber(s, end, radix, false, out _);

    /// <summary>(const char *s, char **end, int base): long long.</summary>
    [Hle("sceNpCommon_37954C1F", Nid = 0x37954C1F, Library = "SceNpCommon")]
    long NpCommonStrtoll(uint s, uint end, int radix)
    {
        if (s == 0) return 0;
        ulong v = ParseNumber(s, end, radix, true, out bool neg);
        return neg ? -(long)v : (long)v;
    }

    /// <summary>(const char *s, char **end, int base): unsigned long long, no sign.</summary>
    [Hle("sceNpCommon_CF843378", Nid = 0xCF843378, Library = "SceNpCommon")]
    ulong NpCommonStrtoull(uint s, uint end, int radix) => s == 0 ? 0 : ParseNumber(s, end, radix, false, out _);

    /// <summary>atoll: NID_37954C1F with base 0.</summary>
    [Hle("sceNpCommon_E1FF4E70", Nid = 0xE1FF4E70, Library = "SceNpCommon")]
    long NpCommonAtoll(uint s) => NpCommonStrtoll(s, 0, 0);

    // ---- small helpers

    /// <summary>(const SceNpOnlineId *a, const SceNpOnlineId *b): the 16 bytes equal: 0, else 0x80550609.</summary>
    [Hle("sceNpCommon_8C21F489", Nid = 0x8C21F489, Library = "SceNpCommon")]
    int NpCommonCompareOnlineId(uint a, uint b)
    {
        if (a == 0 || b == 0) return ERR_INVALID_ARGUMENT;
        for (uint i = 0; i < 0x10; i++)
        {
            byte x = GuestMemory.Read<byte>(a + i), y = GuestMemory.Read<byte>(b + i);
            if (x != y) return UTIL_NOT_MATCH;
            if (x == 0) break;
        }
        return 0;
    }

    /// <summary>(uint64 a, uint64 b): whether the two 64-bit values differ.</summary>
    [Hle("sceNpCommon_A1CF53F3", Nid = 0xA1CF53F3, Library = "SceNpCommon")]
    int NpCommonDiffer64(uint aLo, uint aHi, uint bLo, uint bHi) => aLo != bLo || aHi != bHi ? 1 : 0;

    /// <summary>(uint64 *out): the system time in microseconds (sceKernelGetSystemTimeWide).</summary>
    [Hle("sceNpCommon_55F7FCF7", Nid = 0x55F7FCF7, Library = "SceNpCommon")]
    int NpCommonGetSystemTime(uint outp)
    {
        GuestMemory.Write(outp, Emu.Kernel.ProcessTimeUs);
        return 0;
    }

    /// <summary>(SceNpId *id, int clear): with clear the platform word (+0x18) is zeroed; an id of type 1 (+0x1C) without
    /// a platform gets "psp2"; other types are 0x80550605.</summary>
    [Hle("sceNpCommon_256670D6", Nid = 0x256670D6, Library = "SceNpCommon")]
    int NpCommonSetDefaultPlatform(uint id, int clear)
    {
        if (id == 0) return COMMON_NULLARG;
        if (clear != 0) GuestMemory.W32(id + 0x18, 0);
        if (GuestMemory.Read<byte>(id + 0x1C) != 1) return UTIL_INVALID_NP_ID;
        if (GuestMemory.Read<byte>(id + 0x18) == 0) GuestMemory.WriteCString(id + 0x18, "psp2", 5);
        return 0;
    }

    // ---- the settings np_manager gives the library at sceNpInit: an environment name (up to 16 characters) and a
    // 0x3C-byte block whose words are the defaults of the debug settings below

    readonly byte[] _commonEnv = new byte[17];
    readonly byte[] _commonConfig = new byte[0x3C];

    /// <summary>(const char *name): keeps an environment name of at most 16 characters (0x80550606 for longer).</summary>
    [Hle("sceNpCommon_652FC5E0", Nid = 0x652FC5E0, Library = "SceNpCommon")]
    int NpCommonSetEnvironment(uint name)
    {
        if (name == 0) return 0;
        string s = GuestMemory.ReadCString(name, 0x100);
        if (s.Length >= 0x11) return COMMON_TOO_LONG;
        Array.Clear(_commonEnv);
        System.Text.Encoding.Latin1.GetBytes(s, _commonEnv);
        return 0;
    }

    /// <summary>(const void *config 0x3C): keeps the block.</summary>
    [Hle("sceNpCommon_DA04BA55", Nid = 0xDA04BA55, Library = "SceNpCommon")]
    int NpCommonSetConfig(uint config)
    {
        GuestMemory.ReadSpan(config, 0x3C).CopyTo(_commonConfig);
        return 0;
    }

    uint ConfigWord(int offset) => BitConverter.ToUInt32(_commonConfig, offset);

    // The debug settings read the registry only on a test console (NP test flags, a non-retail kit or the store flag);
    // the emulated console is a retail one, so they are the configuration's defaults.

    /// <summary>(int *out): registry 0x156489 on a test console, else configuration word +0x38.</summary>
    [Hle("sceNpCommon_187C0A8D", Nid = 0x187C0A8D, Library = "SceNpCommon")]
    int NpCommonDebugSetting1(uint outp) { GuestMemory.W32(outp, ConfigWord(0x38)); return 0; }
    /// <summary>(int *out): registry 0xD18E5 on a test console, else configuration word +0x34.</summary>
    [Hle("sceNpCommon_43F5DE5F", Nid = 0x43F5DE5F, Library = "SceNpCommon")]
    int NpCommonDebugSetting2(uint outp) { GuestMemory.W32(outp, ConfigWord(0x34)); return 0; }
    /// <summary>(int *out): registry 0xB6ECD on a test console, else configuration word +0x30.</summary>
    [Hle("sceNpCommon_B88B3F92", Nid = 0xB88B3F92, Library = "SceNpCommon")]
    int NpCommonDebugSetting3(uint outp) { GuestMemory.W32(outp, ConfigWord(0x30)); return 0; }
    /// <summary>(void): a flag (registry 0xB73CD on a test console), else configuration word +0.</summary>
    [Hle("sceNpCommon_BBC13621", Nid = 0xBBC13621, Library = "SceNpCommon")]
    int NpCommonDebugFlag1() => (int)ConfigWord(0);
    /// <summary>(void): a flag (registry 0xA0495 on a test console), else configuration word +0x2C.</summary>
    [Hle("sceNpCommon_F585C86C", Nid = 0xF585C86C, Library = "SceNpCommon")]
    int NpCommonDebugFlag2() => (int)ConfigWord(0x2C);
}
