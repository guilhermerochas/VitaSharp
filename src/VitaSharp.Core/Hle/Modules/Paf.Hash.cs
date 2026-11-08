using System.IO.Compression;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// libpaf's SHA-1 (the RFC 3174 interface, its context kept in guest memory: H0..H4 +0, length low +0x14 and high
/// +0x18 in bits, block index +0x1C (16-bit), the 64-byte block +0x1E, computed +0x60, corrupted +0x64; results 0 ok,
/// 1 NULL, 2 input too long, 3 state error) and its zlib decompression.
/// </summary>
public sealed partial class Paf
{
    const int ShaNull = 1, ShaTooLong = 2, ShaState = 3;

    [Hle("SHA1Reset", Nid = 0xB38C53C0)]
    int Sha1Reset(uint ctx)
    {
        if (ctx == 0) return ShaNull;
        U(ctx + 0x14, 0); U(ctx + 0x18, 0); H(ctx + 0x1C, 0);
        U(ctx, 0x67452301); U(ctx + 4, 0xEFCDAB89); U(ctx + 8, 0x98BADCFE); U(ctx + 0xC, 0x10325476); U(ctx + 0x10, 0xC3D2E1F0);
        U(ctx + 0x60, 0); U(ctx + 0x64, 0);
        return 0;
    }

    [Hle("SHA1Input", Nid = 0xE6BB459E)]
    int Sha1Input(uint ctx, uint data, uint len)
    {
        if (len == 0) return 0;
        if (ctx == 0 || data == 0) return ShaNull;
        if (U(ctx + 0x60) != 0) { U(ctx + 0x64, ShaState); return ShaState; }
        if (U(ctx + 0x64) != 0) return (int)U(ctx + 0x64);
        for (uint i = 0; i < len && U(ctx + 0x64) == 0; i++)
        {
            short idx = (short)H(ctx + 0x1C);
            B(ctx + 0x1E + (uint)idx, B(data + i));
            H(ctx + 0x1C, (ushort)(idx + 1));
            uint lo = U(ctx + 0x14) + 8;
            U(ctx + 0x14, lo);
            if (lo == 0)
            {
                uint hi = U(ctx + 0x18) + 1;
                U(ctx + 0x18, hi);
                if (hi == 0) U(ctx + 0x64, 1); // too long (corrupted)
            }
            if (idx + 1 == 64) Sha1Process(ctx);
        }
        return 0;
    }

    /// <summary>(ctx, uint8_t digest[20]): pads and finishes once, then gives the digest (big-endian words).</summary>
    [Hle("SHA1Result", Nid = 0xB3BF59FC)]
    int Sha1Result(uint ctx, uint digest)
    {
        if (ctx == 0 || digest == 0) return ShaNull;
        if (U(ctx + 0x64) != 0) return (int)U(ctx + 0x64);
        if (U(ctx + 0x60) == 0)
        {
            Sha1Pad(ctx);
            GuestMemory.Fill(ctx + 0x1E, 0, 64);
            U(ctx + 0x14, 0); U(ctx + 0x18, 0);
            U(ctx + 0x60, 1);
        }
        for (uint i = 0; i < 20; i++) B(digest + i, (byte)(U(ctx + (i & ~3u)) >> (int)(24 - 8 * (i & 3))));
        return 0;
    }

    /// <summary>Pads the last block: 0x80, zeros, the 64-bit bit length (big-endian), processing an extra block when
    /// the length does not fit.</summary>
    [Hle("SHA1PadMessage")]
    void Sha1Pad(uint ctx)
    {
        short idx = (short)H(ctx + 0x1C);
        B(ctx + 0x1E + (uint)idx++, 0x80);
        if (idx > 56)
        {
            while (idx < 64) B(ctx + 0x1E + (uint)idx++, 0);
            H(ctx + 0x1C, (ushort)idx);
            Sha1Process(ctx);
            idx = 0;
        }
        while (idx < 56) B(ctx + 0x1E + (uint)idx++, 0);
        uint hi = U(ctx + 0x18), lo = U(ctx + 0x14);
        for (int i = 0; i < 4; i++) { B(ctx + 0x1E + 56 + (uint)i, (byte)(hi >> (24 - 8 * i))); B(ctx + 0x1E + 60 + (uint)i, (byte)(lo >> (24 - 8 * i))); }
        H(ctx + 0x1C, 64);
        Sha1Process(ctx);
    }

    [Hle("SHA1ProcessMessageBlock")]
    void Sha1Process(uint ctx)
    {
        Span<uint> w = stackalloc uint[80];
        for (int t = 0; t < 16; t++)
        {
            uint p = ctx + 0x1E + (uint)t * 4;
            w[t] = (uint)B(p) << 24 | (uint)B(p + 1) << 16 | (uint)B(p + 2) << 8 | B(p + 3);
        }
        for (int t = 16; t < 80; t++) w[t] = uint.RotateLeft(w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16], 1);
        uint a = U(ctx), b = U(ctx + 4), c = U(ctx + 8), d = U(ctx + 0xC), e = U(ctx + 0x10);
        for (int t = 0; t < 80; t++)
        {
            uint f, k;
            if (t < 20) { f = (b & c) | (~b & d); k = 0x5A827999; }
            else if (t < 40) { f = b ^ c ^ d; k = 0x6ED9EBA1; }
            else if (t < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
            else { f = b ^ c ^ d; k = 0xCA62C1D6; }
            uint tmp = uint.RotateLeft(a, 5) + f + e + w[t] + k;
            e = d; d = c; c = uint.RotateLeft(b, 30); b = a; a = tmp;
        }
        U(ctx, U(ctx) + a); U(ctx + 4, U(ctx + 4) + b); U(ctx + 8, U(ctx + 8) + c); U(ctx + 0xC, U(ctx + 0xC) + d); U(ctx + 0x10, U(ctx + 0x10) + e);
        H(ctx + 0x1C, 0);
    }

    // ---- decompression
    /// <summary>A zlib stream at <paramref name="src"/> into dst; returns the bytes written or a negative error.</summary>
    static int Inflate(uint dst, uint dstSize, uint src, uint srcSize, out uint crc)
    {
        crc = 0;
        try
        {
            var input = GuestMemory.ReadSpan(src, (int)srcSize).ToArray();
            using var z = new ZLibStream(new MemoryStream(input), CompressionMode.Decompress);
            var outBuf = GuestMemory.Span(dst, (int)dstSize);
            int total = 0;
            while (total < outBuf.Length)
            {
                int n = z.Read(outBuf[total..]);
                if (n == 0) break;
                total += n;
            }
            if (total == outBuf.Length && z.ReadByte() >= 0) return -2; // does not fit
            crc = System.IO.Hashing.Crc32.HashToUInt32(outBuf[..total]);
            return total;
        }
        catch (InvalidDataException) { return -1; }
    }

    /// <summary>(void *out, size_t outSize, const void *in, uint32_t *crc32 or NULL): the zlib stream (up to 64 KiB of
    /// input is read, more than any libpaf resource holds compressed); errors are SceZlib's 0x80560xxx data error.</summary>
    [Hle("sceZlibDecompress", Library = "ScePafLowlayer")]
    int ZlibDecompress(uint dst, uint dstSize, uint src, uint crcOut)
    {
        if (dst == 0 || src == 0) return unchecked((int)0x80560102);
        int r = Inflate(dst, dstSize, src, 0x10000, out uint crc);
        if (r < 0) return r == -2 ? unchecked((int)0x80560104) : unchecked((int)0x80560103);
        if (crcOut != 0) U(crcOut, crc);
        return r;
    }

    /// <summary>(dst, dstSize, src, srcSize, type): type 1 is zlib; any other type is 0x80AF5700, bad data 0x80AF5704.</summary>
    [Hle("_ZN3paf8compress10DecompressEPvjPKvjNS0_14DecompressTypeE")]
    int Decompress(uint dst, uint dstSize, uint src, uint srcSize, int type)
    {
        if (type != 1) return unchecked((int)0x80AF5700);
        int r = Inflate(dst, dstSize, src, srcSize, out _);
        return r < 0 ? unchecked((int)0x80AF5704) : r;
    }
}
