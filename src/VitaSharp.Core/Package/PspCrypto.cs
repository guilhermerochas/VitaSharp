using System.Buffers.Binary;
using System.Security.Cryptography;
using static VitaSharp.Core.Util.BinaryHelpers;

namespace VitaSharp.Core.Package;

/// <summary>
/// The PSP NPDRM layers a PSP/PSone package carries inside its EBOOT.PBP and EDAT files, opened without a license the way
/// the public tools do (format and constants: mmozeiko/pkg2zip and lusid1/pkg2zip, from the PSP's KIRK/amctrl
/// documentation): the PGD/NPUMDIMG header key is the CMAC of the header under KIRK key 0x38 mixed with the amctrl hash
/// keys, so no rif or act.dat is involved. The data blocks use the amctrl "bbcipher" (AES-ECB decrypt of a counter
/// block, xored with the previous counter), and compressed ISO blocks use the PSP's LZRC.
/// </summary>
public static class PspCrypto
{
    static readonly byte[] Kirk7Key38 = FromHex("12468D7E1C42209BBA5426835EB03303");
    static readonly byte[] Kirk7Key39 = FromHex("C43BB6D653EE67493EA95FBC0CED6F8A");
    static readonly byte[] Kirk7Key63 = FromHex("9C9B1372F8C640CF1C62F5D592DDB582");
    static readonly byte[] AmHashKey3 = FromHex("E350ED1D910A1FD029BB1C3EF34077FB");
    static readonly byte[] AmHashKey4 = FromHex("135FA47CAB395BA476B8CCA98F3A0445");
    static readonly byte[] AmHashKey5 = FromHex("678D7FA32A9CA0D1508AD8385E4B017E");

    /// <summary>AES-128-CMAC (RFC 4493) of <paramref name="data"/> under <paramref name="key"/>.</summary>
    public static byte[] Cmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        Span<byte> l = stackalloc byte[16];
        aes.EncryptEcb(new byte[16], l, PaddingMode.None);
        var k1 = GfDouble(l);
        var k2 = GfDouble(k1);
        int blocks = Math.Max(1, (data.Length + 15) / 16);
        bool complete = data.Length > 0 && data.Length % 16 == 0;
        var x = new byte[16];
        Span<byte> y = stackalloc byte[16];
        for (int b = 0; b < blocks; b++)
        {
            var block = data.Slice(b * 16, Math.Min(16, data.Length - b * 16));
            y.Clear();
            block.CopyTo(y);
            if (b == blocks - 1)
            {
                if (complete) Xor(y, k1);
                else { y[block.Length] = 0x80; Xor(y, k2); }
            }
            Xor(y, x);
            aes.EncryptEcb(y, x, PaddingMode.None);
        }
        return x;
    }

    static byte[] GfDouble(ReadOnlySpan<byte> v)
    {
        var r = new byte[16];
        int carry = 0;
        for (int i = 15; i >= 0; i--)
        {
            r[i] = (byte)((v[i] << 1) | carry);
            carry = v[i] >> 7;
        }
        if ((v[0] & 0x80) != 0) r[15] ^= 0x87;
        return r;
    }

    static void Xor(Span<byte> a, ReadOnlySpan<byte> b)
    {
        for (int i = 0; i < 16; i++) a[i] ^= b[i];
    }

    /// <summary>
    /// The key and IV of a PGD/NPUMDIMG section: <paramref name="header"/> is the section's header, its CMAC under KIRK key
    /// 0x38 is <paramref name="mac"/>, <paramref name="keyAt"/> holds the encrypted key seed (KIRK-0x63-wrapped too for an
    /// EBOOT's NPUMDIMG) and <paramref name="ivAt"/> the per-section seed.
    /// </summary>
    public static BbCipher SectionCipher(ReadOnlySpan<byte> header, ReadOnlySpan<byte> mac, bool eboot, int keyAt, int ivAt)
    {
        Span<byte> tmp = stackalloc byte[16];
        if (eboot) AesEcbDecrypt(Kirk7Key63, header.Slice(keyAt, 16), tmp);
        else header.Slice(keyAt, 16).CopyTo(tmp);
        AesEcbDecrypt(Kirk7Key38, tmp, tmp);
        var iv = new byte[16];
        for (int i = 0; i < 16; i++) iv[i] = (byte)(mac[i] ^ tmp[i] ^ header[ivAt + i] ^ AmHashKey3[i] ^ AmHashKey5[i]);
        AesEcbDecrypt(Kirk7Key39, iv, iv);
        for (int i = 0; i < 16; i++) iv[i] ^= AmHashKey4[i];
        return new BbCipher(Kirk7Key63, iv);
    }

    /// <summary>The CMAC of a PGD/NPUMDIMG header (KIRK key 0x38).</summary>
    public static byte[] HeaderMac(ReadOnlySpan<byte> header, int size) => Cmac(Kirk7Key38, header[..size]);

    /// <summary>The 16-byte KEYS.BIN of a PSone Classic (the version key of its PGD header, lusid1/pkg2zip).</summary>
    public static byte[] PsxKeysBin(ReadOnlySpan<byte> pgdHeader)
    {
        var mac = HeaderMac(pgdHeader, 0x70);
        Span<byte> tmp = stackalloc byte[16];
        AesEcbDecrypt(Kirk7Key38, pgdHeader.Slice(0x70, 16), tmp);
        var keys = new byte[16];
        for (int i = 0; i < 16; i++) keys[i] = (byte)(mac[i] ^ tmp[i] ^ AmHashKey3[i]);
        return keys;
    }

    static void AesEcbDecrypt(byte[] key, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.DecryptEcb(src, dst, PaddingMode.None);
    }

    /// <summary>The amctrl block cipher of a PGD/NPUMDIMG section: block i decrypts as
    /// D(ctr(i+1)) ^ ctr(i), ctr(n) being the IV with its last word replaced by index + n.</summary>
    public sealed class BbCipher(byte[] key, byte[] iv) : IDisposable
    {
        readonly Aes _aes = CreateAes(key);
        public byte[] Iv { get; } = iv;

        static Aes CreateAes(byte[] key)
        {
            var aes = Aes.Create();
            aes.Key = key;
            return aes;
        }

        /// <summary>Decrypts <paramref name="data"/> (a multiple of 16) that starts at 16-byte block <paramref name="index"/>
        /// of the section.</summary>
        public void Decrypt(Span<byte> data, uint index)
        {
            if (data.Length % 16 != 0) throw new ArgumentException("size must be a multiple of 16", nameof(data));
            Span<byte> prev = stackalloc byte[16];
            Span<byte> block = stackalloc byte[16];
            Span<byte> outBlock = stackalloc byte[16];
            if (index == 0) prev.Clear();
            else
            {
                Iv.AsSpan(0, 12).CopyTo(prev);
                BinaryPrimitives.WriteUInt32LittleEndian(prev[12..], index);
            }
            Iv.CopyTo(block);
            uint counter = index;
            for (int i = 0; i < data.Length; i += 16)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(block[12..], ++counter);
                _aes.DecryptEcb(block, outBlock, PaddingMode.None);
                for (int k = 0; k < 16; k++) data[i + k] ^= (byte)(prev[k] ^ outBlock[k]);
                block.CopyTo(prev);
            }
        }

        public void Dispose() => _aes.Dispose();
    }

    /// <summary>
    /// The PSP's LZRC decompressor (a range coder with LZ matches; libkirk by tpu, as pkg2zip uses it for compressed
    /// NPUMDIMG blocks). Returns the number of bytes written to <paramref name="output"/>.
    /// </summary>
    public static int Lzrc(Span<byte> output, ReadOnlySpan<byte> input)
    {
        if (input.Length < 5) throw new InvalidDataException("LZRC: input too short");
        var rc = new RangeDecoder(input);
        byte lc = input[0];
        if ((lc & 0x80) != 0)
        {
            int n = (int)rc.Code;
            if (n > output.Length || 5 + n > input.Length) throw new InvalidDataException("LZRC: stored block too large");
            input.Slice(5, n).CopyTo(output);
            return n;
        }
        var bmLiteral = Filled(8 * 256);
        var bmDistBits = Filled(8 * 39);
        var bmDist = Filled(18 * 8);
        var bmMatch = Filled(8 * 8);
        var bmLen = Filled(8 * 31);
        int state = 0, outPtr = 0;
        byte last = 0;
        for (;;)
        {
            int step = 0;
            if (rc.Bit(bmMatch, state * 8 + step) == 0)
            {
                if (state > 0) state--;
                int b = rc.BitTree(bmLiteral, ((last >> lc) & 7) * 256, 0x100) - 0x100;
                if (outPtr == output.Length) throw new InvalidDataException("LZRC: output overflow");
                output[outPtr++] = last = (byte)b;
                continue;
            }
            int lenBits = 0;
            for (int i = 0; i < 7; i++)
            {
                step++;
                if (rc.Bit(bmMatch, state * 8 + step) == 0) break;
                lenBits++;
            }
            int matchLen;
            if (lenBits == 0) matchLen = 1;
            else
            {
                int lenState = ((lenBits - 1) << 2) + ((outPtr << (lenBits - 1)) & 3);
                matchLen = rc.Number(bmLen, state * 31 + lenState, lenBits);
                if (matchLen == 0xFF) return outPtr; // end of stream
            }
            int distState = 0, limit = 8;
            if (matchLen > 2) { distState += 7; limit = 44; }
            int distBits = rc.BitTree(bmDistBits, lenBits * 39 + distState, limit) - limit;
            int dist = distBits > 0 ? rc.Number(bmDist, distBits * 8, distBits) : 1;
            if (dist > outPtr) throw new InvalidDataException("LZRC: match distance out of range");
            if (outPtr + matchLen + 1 > output.Length) throw new InvalidDataException("LZRC: output overflow");
            int src = outPtr - dist;
            for (int i = 0; i <= matchLen; i++) output[outPtr++] = output[src++];
            last = output[src - 1];
            state = 6 + ((outPtr + 1) & 1);
        }
    }

    static byte[] Filled(int n)
    {
        var a = new byte[n];
        Array.Fill(a, (byte)0x80);
        return a;
    }

    ref struct RangeDecoder
    {
        readonly ReadOnlySpan<byte> _in;
        int _ptr;
        uint _range;
        public uint Code;

        public RangeDecoder(ReadOnlySpan<byte> input)
        {
            _in = input;
            _ptr = 5;
            _range = 0xFFFFFFFF;
            Code = BinaryPrimitives.ReadUInt32BigEndian(input[1..]);
        }

        void Normalize()
        {
            if (_range < 0x01000000)
            {
                // past the end of the block reads as zero (valid data ends with its end marker before that)
                _range <<= 8;
                Code = (Code << 8) + (_ptr < _in.Length ? _in[_ptr] : 0u);
                _ptr++;
            }
        }

        public int Bit(byte[] probs, int at)
        {
            Normalize();
            uint bound = (_range >> 8) * probs[at];
            probs[at] -= (byte)(probs[at] >> 3);
            if (Code < bound)
            {
                _range = bound;
                probs[at] += 31;
                return 1;
            }
            Code -= bound;
            _range -= bound;
            return 0;
        }

        public int BitTree(byte[] probs, int at, int limit)
        {
            int n = 1;
            do n = (n << 1) + Bit(probs, at + n);
            while (n < limit);
            return n;
        }

        public int Number(byte[] probs, int at, int bits)
        {
            int n = 1;
            if (bits > 3)
            {
                n = (n << 1) + Bit(probs, at + 3);
                if (bits > 4)
                {
                    n = (n << 1) + Bit(probs, at + 3);
                    if (bits > 5)
                    {
                        Normalize();
                        for (int i = 0; i < bits - 5; i++)
                        {
                            _range >>= 1;
                            n <<= 1;
                            if (Code < _range) n += 1;
                            else Code -= _range;
                        }
                    }
                }
            }
            if (bits > 0)
            {
                n = (n << 1) + Bit(probs, at);
                if (bits > 1)
                {
                    n = (n << 1) + Bit(probs, at + 1);
                    if (bits > 2) n = (n << 1) + Bit(probs, at + 2);
                }
            }
            return n;
        }
    }
}
