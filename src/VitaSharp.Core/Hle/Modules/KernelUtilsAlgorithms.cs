using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The block functions of SHA-1 and SHA-256 (FIPS 180-4) on a state the caller keeps, for the block interface of
/// SceKernelUtils, whose contexts live in guest memory (the .NET hashes do not expose their intermediate state).
/// </summary>
internal static class ShaBlocks
{
    /// <summary>The initial state of SHA-1.</summary>
    public static ReadOnlySpan<uint> Sha1Initial => [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0];

    /// <summary>The initial state of SHA-224.</summary>
    public static ReadOnlySpan<uint> Sha224Initial =>
        [0xC1059ED8, 0x367CD507, 0x3070DD17, 0xF70E5939, 0xFFC00B31, 0x68581511, 0x64F98FA7, 0xBEFA4FA4];

    /// <summary>The initial state of SHA-256.</summary>
    public static ReadOnlySpan<uint> Sha256Initial =>
        [0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19];

    private static ReadOnlySpan<uint> Sha256Constants =>
    [
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
    ];

    /// <summary>One 64-byte block into a SHA-1 state of five words.</summary>
    public static void Sha1(Span<uint> state, ReadOnlySpan<byte> block)
    {
        Span<uint> words = stackalloc uint[80];
        for (int i = 0; i < 16; i++)
        {
            words[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);
        }

        for (int i = 16; i < 80; i++)
        {
            words[i] = uint.RotateLeft(words[i - 3] ^ words[i - 8] ^ words[i - 14] ^ words[i - 16], 1);
        }

        uint a = state[0];
        uint b = state[1];
        uint c = state[2];
        uint d = state[3];
        uint e = state[4];
        for (int i = 0; i < 80; i++)
        {
            uint mix;
            uint constant;
            if (i < 20)
            {
                mix = (b & c) | (~b & d);
                constant = 0x5A827999;
            }
            else if (i < 40)
            {
                mix = b ^ c ^ d;
                constant = 0x6ED9EBA1;
            }
            else if (i < 60)
            {
                mix = (b & c) | (b & d) | (c & d);
                constant = 0x8F1BBCDC;
            }
            else
            {
                mix = b ^ c ^ d;
                constant = 0xCA62C1D6;
            }

            uint next = uint.RotateLeft(a, 5) + mix + e + constant + words[i];
            e = d;
            d = c;
            c = uint.RotateLeft(b, 30);
            b = a;
            a = next;
        }

        state[0] += a;
        state[1] += b;
        state[2] += c;
        state[3] += d;
        state[4] += e;
    }

    /// <summary>One 64-byte block into a SHA-256 (or SHA-224) state of eight words.</summary>
    public static void Sha256(Span<uint> state, ReadOnlySpan<byte> block)
    {
        Span<uint> words = stackalloc uint[64];
        for (int i = 0; i < 16; i++)
        {
            words[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);
        }

        for (int i = 16; i < 64; i++)
        {
            uint low = words[i - 15];
            uint high = words[i - 2];
            uint sigma0 = uint.RotateRight(low, 7) ^ uint.RotateRight(low, 18) ^ (low >> 3);
            uint sigma1 = uint.RotateRight(high, 17) ^ uint.RotateRight(high, 19) ^ (high >> 10);
            words[i] = words[i - 16] + sigma0 + words[i - 7] + sigma1;
        }

        uint a = state[0];
        uint b = state[1];
        uint c = state[2];
        uint d = state[3];
        uint e = state[4];
        uint f = state[5];
        uint g = state[6];
        uint h = state[7];
        for (int i = 0; i < 64; i++)
        {
            uint sum1 = uint.RotateRight(e, 6) ^ uint.RotateRight(e, 11) ^ uint.RotateRight(e, 25);
            uint choice = (e & f) ^ (~e & g);
            uint first = h + sum1 + choice + Sha256Constants[i] + words[i];
            uint sum0 = uint.RotateRight(a, 2) ^ uint.RotateRight(a, 13) ^ uint.RotateRight(a, 22);
            uint majority = (a & b) ^ (a & c) ^ (b & c);
            uint second = sum0 + majority;
            h = g;
            g = f;
            f = e;
            e = d + first;
            d = c;
            c = b;
            b = a;
            a = first + second;
        }

        state[0] += a;
        state[1] += b;
        state[2] += c;
        state[3] += d;
        state[4] += e;
        state[5] += f;
        state[6] += g;
        state[7] += h;
    }

    /// <summary>The padding of the last blocks: the buffered bytes, 0x80, zeros and the bit length (big-endian),
    /// compressed into <paramref name="state"/> by <paramref name="compress"/>.</summary>
    public static void Finish(Span<uint> state, ReadOnlySpan<byte> buffered, ulong totalBytes, BlockFunction compress)
    {
        Span<byte> tail = stackalloc byte[128];
        tail.Clear();
        buffered.CopyTo(tail);
        tail[buffered.Length] = 0x80;
        int length = buffered.Length + 9 <= 64 ? 64 : 128;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(tail[(length - 8)..], totalBytes * 8);
        for (int offset = 0; offset < length; offset += 64)
        {
            compress(state, tail.Slice(offset, 64));
        }
    }

    /// <summary>A block function: <see cref="Sha1"/> or <see cref="Sha256"/>.</summary>
    public delegate void BlockFunction(Span<uint> state, ReadOnlySpan<byte> block);
}

/// <summary>
/// SFMT19937 (SFMT 1.4, parameters 122-18-1-11-1, masks DFFFFFEF DDFECB7F BFFAFFFF BFFFFFF6, parity 1 0 0 13C9E684)
/// on the 624-word state the caller keeps.
/// </summary>
internal static class Sfmt
{
    private const int Blocks = 156;
    private const int Words = Blocks * 4;
    private const int Position1 = 122;

    private static ReadOnlySpan<uint> Masks => [0xDFFFFFEF, 0xDDFECB7F, 0xBFFAFFFF, 0xBFFFFFF6];

    private static ReadOnlySpan<uint> Parity => [0x00000001, 0x00000000, 0x00000000, 0x13C9E684];

    /// <summary>Fixes the state so that the period is 2^19937 - 1.</summary>
    public static void CertifyPeriod(Span<uint> state)
    {
        uint inner = 0;
        for (int i = 0; i < 4; i++)
        {
            inner ^= state[i] & Parity[i];
        }

        for (int shift = 16; shift > 0; shift >>= 1)
        {
            inner ^= inner >> shift;
        }

        if ((inner & 1) == 1)
        {
            return;
        }

        for (int i = 0; i < 4; i++)
        {
            if (Parity[i] != 0)
            {
                state[i] ^= Parity[i] & (~Parity[i] + 1);
                return;
            }
        }
    }

    /// <summary>The array seeding of SFMT; the firmware skips the key and its length when the key is NULL.</summary>
    public static void SeedByArray(Span<uint> state, uint key, int length)
    {
        const int Lag = 11;
        const int Middle = (Words - Lag) / 2;
        state.Fill(0x8B8B8B8B);
        int count = Math.Max(Words, length + 1);
        int i = 0;
        int used = 0;
        for (int step = 0; step < count; step++)
        {
            uint mixed = state[i] ^ state[(i + Middle) % Words] ^ state[(i + Words - 1) % Words];
            uint r = (mixed ^ (mixed >> 27)) * 1664525;
            state[(i + Middle) % Words] += r;
            if (key != 0)
            {
                if (step == 0)
                {
                    r += (uint)length;
                }
                else if (used < length)
                {
                    r += GuestMemory.R32(key + (uint)(used * 4));
                    used++;
                }
            }

            r += (uint)i;
            state[(i + Middle + Lag) % Words] += r;
            state[i] = r;
            i = (i + 1) % Words;
        }

        for (int step = 0; step < Words; step++)
        {
            uint mixed = state[i] + state[(i + Middle) % Words] + state[(i + Words - 1) % Words];
            uint r = (mixed ^ (mixed >> 27)) * 1566083941;
            state[(i + Middle) % Words] ^= r;
            r -= (uint)i;
            state[(i + Middle + Lag) % Words] ^= r;
            state[i] = r;
            i = (i + 1) % Words;
        }

        CertifyPeriod(state);
    }

    /// <summary>The next words: into <paramref name="output"/> (a multiple of 4, at least 624 words) when given, the
    /// state becoming the last 624; without an output the state is regenerated in place.</summary>
    public static void Generate(Span<uint> state, Span<uint> output)
    {
        int produced = output.IsEmpty ? Words : output.Length;
        uint[] sequence = new uint[Words + produced];
        state.CopyTo(sequence);
        for (int block = Blocks; block < (Words + produced) / 4; block++)
        {
            Recursion(sequence, block);
        }

        if (!output.IsEmpty)
        {
            sequence.AsSpan(Words, produced).CopyTo(output);
        }

        sequence.AsSpan(produced, Words).CopyTo(state);
    }

    /// <summary>Block <paramref name="block"/> of the sequence from the four before it in the recursion.</summary>
    private static void Recursion(uint[] sequence, int block)
    {
        int a = (block - Blocks) * 4;
        int b = (block - Blocks + Position1) * 4;
        int c = (block - 2) * 4;
        int d = (block - 1) * 4;
        ulong aHigh = ((ulong)sequence[a + 3] << 32) | sequence[a + 2];
        ulong aLow = ((ulong)sequence[a + 1] << 32) | sequence[a];
        ulong xHigh = (aHigh << 8) | (aLow >> 56);
        ulong xLow = aLow << 8;
        ulong cHigh = ((ulong)sequence[c + 3] << 32) | sequence[c + 2];
        ulong cLow = ((ulong)sequence[c + 1] << 32) | sequence[c];
        ulong yHigh = cHigh >> 8;
        ulong yLow = (cLow >> 8) | (cHigh << 56);
        int target = block * 4;
        for (int lane = 0; lane < 4; lane++)
        {
            uint x = lane < 2 ? (uint)(xLow >> (lane * 32)) : (uint)(xHigh >> ((lane - 2) * 32));
            uint y = lane < 2 ? (uint)(yLow >> (lane * 32)) : (uint)(yHigh >> ((lane - 2) * 32));
            sequence[target + lane] = sequence[a + lane] ^ x ^ ((sequence[b + lane] >> 11) & Masks[lane]) ^ y
                ^ (sequence[d + lane] << 18);
        }
    }
}

/// <summary>
/// A raw deflate decoder (RFC 1951) reading guest memory, for SceKernelUtils: it reports where the stream ended (the
/// gzip and zlib trailers follow it) and the firmware's two failures, bad data and an output that does not fit.
/// </summary>
internal sealed class GuestInflater
{
    /// <summary>The data is not a valid deflate stream (0x80560100).</summary>
    public const int BadData = unchecked((int)0x80560100);

    /// <summary>The output does not fit in the destination (0x80560101).</summary>
    public const int NoRoom = unchecked((int)0x80560101);

    private const int MaxBits = 15;

    private static ReadOnlySpan<ushort> LengthBase => [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];

    private static ReadOnlySpan<byte> LengthExtra => [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    private static ReadOnlySpan<ushort> DistanceBase => [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];

    private static ReadOnlySpan<byte> DistanceExtra => [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    private static ReadOnlySpan<byte> CodeLengthOrder => [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private readonly uint _destination;
    private readonly uint _capacity;
    private uint _source;
    private uint _written;
    private uint _bitBuffer;
    private int _bitCount;

    private GuestInflater(uint destination, uint capacity, uint source)
    {
        _destination = destination;
        _capacity = capacity;
        _source = source;
    }

    /// <summary>A Huffman code: the count of codes of each length and the symbols in code order.</summary>
    private sealed class Huffman(int symbols)
    {
        public readonly short[] Counts = new short[MaxBits + 1];
        public readonly short[] Symbols = new short[symbols];
    }

    /// <summary>Inflates the stream at <paramref name="source"/> into <paramref name="destination"/>: the bytes written,
    /// <see cref="BadData"/> or <see cref="NoRoom"/>; <paramref name="end"/> is the byte after the stream.</summary>
    public static int Inflate(uint destination, uint capacity, uint source, out uint end)
    {
        var inflater = new GuestInflater(destination, capacity, source);
        int result = inflater.Run();
        end = inflater._source;
        return result < 0 ? result : (int)inflater._written;
    }

    private int Run()
    {
        bool last;
        do
        {
            if (!Bits(1, out uint final) || !Bits(2, out uint type))
            {
                return BadData;
            }

            last = final != 0;
            int result = type switch
            {
                0 => Stored(),
                1 => Codes(FixedLengths(), FixedDistances()),
                2 => Dynamic(),
                _ => BadData,
            };
            if (result < 0)
            {
                return result;
            }
        }
        while (!last);

        return 0;
    }

    private bool Bits(int count, out uint value)
    {
        while (_bitCount < count)
        {
            _bitBuffer |= (uint)GuestMemory.Read<byte>(_source) << _bitCount;
            _source++;
            _bitCount += 8;
        }

        value = _bitBuffer & (uint)((1UL << count) - 1);
        _bitBuffer >>= count;
        _bitCount -= count;
        return true;
    }

    private int Stored()
    {
        _bitBuffer = 0;
        _bitCount = 0;
        uint length = GuestMemory.Read<ushort>(_source);
        uint complement = GuestMemory.Read<ushort>(_source + 2);
        if ((length ^ complement) != 0xFFFF)
        {
            return BadData;
        }

        _source += 4;
        if (_written + length > _capacity)
        {
            return NoRoom;
        }

        GuestMemory.Span(_source, (int)length).CopyTo(GuestMemory.Span(_destination + _written, (int)length));
        _source += length;
        _written += length;
        return 0;
    }

    private int Decode(Huffman code)
    {
        int first = 0;
        int index = 0;
        int value = 0;
        for (int length = 1; length <= MaxBits; length++)
        {
            Bits(1, out uint bit);
            value |= (int)bit;
            int count = code.Counts[length];
            if (value - count < first)
            {
                return code.Symbols[index + (value - first)];
            }

            index += count;
            first += count;
            first <<= 1;
            value <<= 1;
        }

        return -1;
    }

    /// <summary>Builds a canonical code from the code lengths; false for an over-subscribed set.</summary>
    private static bool Build(Huffman code, ReadOnlySpan<short> lengths)
    {
        Array.Clear(code.Counts);
        foreach (short length in lengths)
        {
            code.Counts[length]++;
        }

        if (code.Counts[0] == lengths.Length)
        {
            return true;
        }

        int left = 1;
        for (int length = 1; length <= MaxBits; length++)
        {
            left <<= 1;
            left -= code.Counts[length];
            if (left < 0)
            {
                return false;
            }
        }

        Span<short> offsets = stackalloc short[MaxBits + 1];
        offsets[1] = 0;
        for (int length = 1; length < MaxBits; length++)
        {
            offsets[length + 1] = (short)(offsets[length] + code.Counts[length]);
        }

        for (short symbol = 0; symbol < lengths.Length; symbol++)
        {
            if (lengths[symbol] != 0)
            {
                code.Symbols[offsets[lengths[symbol]]++] = symbol;
            }
        }

        return true;
    }

    private static Huffman? _fixedLengths;
    private static Huffman? _fixedDistances;

    private static Huffman FixedLengths()
    {
        if (_fixedLengths == null)
        {
            Span<short> lengths = stackalloc short[288];
            for (int symbol = 0; symbol < 288; symbol++)
            {
                lengths[symbol] = symbol switch
                {
                    < 144 => 8,
                    < 256 => 9,
                    < 280 => 7,
                    _ => 8,
                };
            }

            var code = new Huffman(288);
            Build(code, lengths);
            _fixedLengths = code;
        }

        return _fixedLengths;
    }

    private static Huffman FixedDistances()
    {
        if (_fixedDistances == null)
        {
            Span<short> lengths = stackalloc short[30];
            lengths.Fill(5);
            var code = new Huffman(30);
            Build(code, lengths);
            _fixedDistances = code;
        }

        return _fixedDistances;
    }

    private int Dynamic()
    {
        Bits(5, out uint lengthCodes);
        Bits(5, out uint distanceCodes);
        Bits(4, out uint codeLengthCodes);
        int literals = (int)lengthCodes + 257;
        int distances = (int)distanceCodes + 1;
        if (literals > 286 || distances > 30)
        {
            return BadData;
        }

        Span<short> lengths = stackalloc short[320];
        lengths.Clear();
        for (int i = 0; i < (int)codeLengthCodes + 4; i++)
        {
            Bits(3, out uint length);
            lengths[CodeLengthOrder[i]] = (short)length;
        }

        var lengthCode = new Huffman(19);
        if (!Build(lengthCode, lengths[..19]))
        {
            return BadData;
        }

        int index = 0;
        Span<short> all = lengths[..(literals + distances)];
        all.Clear();
        while (index < literals + distances)
        {
            int symbol = Decode(lengthCode);
            if (symbol < 0)
            {
                return BadData;
            }

            if (symbol < 16)
            {
                all[index++] = (short)symbol;
                continue;
            }

            short repeated = 0;
            uint times;
            if (symbol == 16)
            {
                if (index == 0)
                {
                    return BadData;
                }

                repeated = all[index - 1];
                Bits(2, out times);
                times += 3;
            }
            else if (symbol == 17)
            {
                Bits(3, out times);
                times += 3;
            }
            else
            {
                Bits(7, out times);
                times += 11;
            }

            if (index + times > literals + distances)
            {
                return BadData;
            }

            for (uint i = 0; i < times; i++)
            {
                all[index++] = repeated;
            }
        }

        if (all[256] == 0)
        {
            return BadData;
        }

        var literalCode = new Huffman(286);
        var distanceCode = new Huffman(30);
        if (!Build(literalCode, all[..literals]) || !Build(distanceCode, all.Slice(literals, distances)))
        {
            return BadData;
        }

        return Codes(literalCode, distanceCode);
    }

    private int Codes(Huffman literalCode, Huffman distanceCode)
    {
        while (true)
        {
            int symbol = Decode(literalCode);
            if (symbol < 0)
            {
                return BadData;
            }

            if (symbol < 256)
            {
                if (_written >= _capacity)
                {
                    return NoRoom;
                }

                GuestMemory.Write(_destination + _written, (byte)symbol);
                _written++;
                continue;
            }

            if (symbol == 256)
            {
                return 0;
            }

            symbol -= 257;
            if (symbol >= 29)
            {
                return BadData;
            }

            Bits(LengthExtra[symbol], out uint lengthBits);
            uint length = LengthBase[symbol] + lengthBits;
            int distanceSymbol = Decode(distanceCode);
            if (distanceSymbol is < 0 or >= 30)
            {
                return BadData;
            }

            Bits(DistanceExtra[distanceSymbol], out uint distanceBits);
            uint distance = DistanceBase[distanceSymbol] + distanceBits;
            if (distance > _written)
            {
                return BadData;
            }

            if (_written + length > _capacity)
            {
                return NoRoom;
            }

            for (uint i = 0; i < length; i++)
            {
                byte value = GuestMemory.Read<byte>(_destination + _written - distance);
                GuestMemory.Write(_destination + _written, value);
                _written++;
            }
        }
    }
}
