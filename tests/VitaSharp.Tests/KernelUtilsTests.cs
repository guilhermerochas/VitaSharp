using System.IO.Compression;
using System.Security.Cryptography;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceKernelUtilsForDriver (sysmem.skprx 3.74): SHA-1/224/256 by block (contexts in guest memory, the result kept and
/// the stream going on after it) and in one call, the HMACs, the Mersenne twister (MT19937's sequence) and the global
/// one, the raw deflate decoder (stored, fixed and dynamic blocks, the end of the stream, bad data, no room) and the
/// zlib and gzip containers with their header fields and trailers.
/// </summary>
[Collection("guest")]
public sealed class KernelUtilsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IllegalAddress = unchecked((int)0x80020006), Invalid = unchecked((int)0x80020005);
    private const int Format = unchecked((int)0x80020001), BadData = unchecked((int)0x80560100);
    private const int NoRoom = unchecked((int)0x80560101);

    private readonly HleHarness _harness = new(fixture, "kutils");

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    private uint Put(byte[] bytes)
    {
        uint address = _harness.Alloc((uint)Math.Max(bytes.Length, 1) + 16);
        bytes.CopyTo(GuestMemory.Span(address, bytes.Length));
        return address;
    }

    private static byte[] Data(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i * 31 + (i >> 3));
        }

        return bytes;
    }

    private byte[] Digest(string name, byte[] message, int size)
    {
        uint output = _harness.Alloc(32);
        Assert.Equal(0, Call(Sysmem, name, Put(message), (uint)message.Length, output));
        return GuestMemory.ReadSpan(output, size).ToArray();
    }

    [Fact]
    public void TheDigestsMatchTheStandardVectors()
    {
        byte[] abc = "abc"u8.ToArray();
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", Convert.ToHexStringLower(Digest("ksceSha1Digest", abc, 20)));
        Assert.Equal("23097d223405d8228642a477bda255b32aadbce4bda0b3f7e36c9da7", Convert.ToHexStringLower(Digest("ksceSha224Digest", abc, 28)));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Convert.ToHexStringLower(Digest("ksceSha256Digest", abc, 32)));
        foreach (int length in new[] { 0, 1, 55, 56, 63, 64, 65, 119, 120, 1000 })
        {
            byte[] message = Data(length);
            Assert.Equal(SHA1.HashData(message), Digest("ksceSha1Digest", message, 20));
            Assert.Equal(SHA256.HashData(message), Digest("ksceSha256Digest", message, 32));
        }
    }

    [Theory]
    [InlineData("Sha1", 20, 0x80)]
    [InlineData("Sha224", 28, 0x98)]
    [InlineData("Sha256", 32, 0x98)]
    public void TheBlockInterfaceStreamsAndKeepsTheResult(string hash, int size, int contextSize)
    {
        byte[] message = Data(300);
        uint data = Put(message);
        uint context = _harness.Alloc((uint)contextSize);
        uint first = _harness.Alloc(32);
        uint second = _harness.Alloc(32);
        Assert.Equal(IllegalAddress, Call(Sysmem, $"ksce{hash}BlockInit", 0u));
        Assert.Equal(0, Call(Sysmem, $"ksce{hash}BlockInit", context));
        Assert.Equal(IllegalAddress, Call(Sysmem, $"ksce{hash}BlockUpdate", context, 0u, 1u));
        Assert.Equal(IllegalAddress, Call(Sysmem, $"ksce{hash}BlockResult", context, 0u));
        uint[] pieces = [1, 62, 64, 0, 3, 100, 70];
        uint offset = 0;
        foreach (uint piece in pieces)
        {
            Assert.Equal(0, Call(Sysmem, $"ksce{hash}BlockUpdate", context, data + offset, piece));
            offset += piece;
        }

        Assert.Equal(300u, offset);
        Assert.Equal(0, Call(Sysmem, $"ksce{hash}BlockResult", context, first));
        Assert.Equal(0, Call(Sysmem, $"ksce{hash}BlockResult", context, second));
        Assert.Equal(Digest($"ksce{hash}Digest", message, size), GuestMemory.ReadSpan(first, size).ToArray());
        Assert.Equal(GuestMemory.ReadSpan(first, size).ToArray(), GuestMemory.ReadSpan(second, size).ToArray());

        // the stream goes on after a result
        Assert.Equal(0, Call(Sysmem, $"ksce{hash}BlockUpdate", context, data, 300u));
        Assert.Equal(0, Call(Sysmem, $"ksce{hash}BlockResult", context, second));
        Assert.Equal(Digest($"ksce{hash}Digest", [.. message, .. message], size), GuestMemory.ReadSpan(second, size).ToArray());
    }

    [Fact]
    public void TheHmacsMatchTheStandard()
    {
        foreach (int keyLength in new[] { 0, 20, 64, 65, 100 })
        {
            byte[] key = Data(keyLength + 3)[3..];
            byte[] message = Data(77);
            uint output = _harness.Alloc(32);
            Assert.Equal(0, Call(Sysmem, "ksceHmacSha1Digest", Put(key), (uint)key.Length, Put(message), (uint)message.Length, output));
            Assert.Equal(HMACSHA1.HashData(key, message), GuestMemory.ReadSpan(output, 20).ToArray());
            Assert.Equal(0, Call(Sysmem, "ksceHmacSha256Digest", Put(key), (uint)key.Length, Put(message), (uint)message.Length, output));
            Assert.Equal(HMACSHA256.HashData(key, message), GuestMemory.ReadSpan(output, 32).ToArray());
        }

        // RFC 4231 test case 1
        uint digest = _harness.Alloc(32);
        byte[] rfcKey = Enumerable.Repeat((byte)0x0B, 20).ToArray();
        Assert.Equal(0, Call(Sysmem, "ksceHmacSha224Digest", Put(rfcKey), 20u, Put("Hi There"u8.ToArray()), 8u, digest));
        Assert.Equal("896fb1128abbdf196832107cd49df33f47b4b1169912ba4f53684b22", Convert.ToHexStringLower(GuestMemory.ReadSpan(digest, 28)));
        Assert.Equal(IllegalAddress, Call(Sysmem, "ksceHmacSha1Digest", 0u, 0u, digest, 0u, digest));
        Assert.Equal(IllegalAddress, Call(Sysmem, "ksceHmacSha1Digest", digest, 0u, 0u, 0u, digest));
        Assert.Equal(IllegalAddress, Call(Sysmem, "ksceHmacSha1Digest", digest, 0u, digest, 0u, 0u));
    }

    [Fact]
    public void TheTwisterGivesTheMt19937Sequence()
    {
        uint context = _harness.Alloc(0x9C4);
        Assert.Equal(0, Call(Sysmem, "ksceMt19937Init", context, 5489u));
        uint[] expected = [3499211612, 581869302, 3890346734, 3586334585, 545404204];
        foreach (uint value in expected)
        {
            Assert.Equal(value, (uint)Invoke(Sysmem, "ksceMt19937UInt", context)!);
        }

        // the 10000th number (past many twists of the state)
        for (int i = 0; i < 9994; i++)
        {
            Invoke(Sysmem, "ksceMt19937UInt", context);
        }

        Assert.Equal(4123659995u, (uint)Invoke(Sysmem, "ksceMt19937UInt", context)!);
    }

    [Fact]
    public void TheGlobalTwisterFillsWords()
    {
        uint buffer = _harness.Alloc(16);
        Assert.Equal(Invalid, Call(Sysmem, "ksceMt19937GlobalFill", buffer + 2, 8u));
        Assert.Equal(Invalid, Call(Sysmem, "ksceMt19937GlobalFill", buffer, 6u));
        Assert.Equal(0, Call(Sysmem, "ksceMt19937GlobalInit", 5489u));
        Assert.Equal(0, Call(Sysmem, "ksceMt19937GlobalFill", buffer, 8u));
        Assert.Equal(3499211612u, GuestMemory.R32(buffer));
        Assert.Equal(581869302u, GuestMemory.R32(buffer + 4));
        Assert.Equal(0u, GuestMemory.R32(buffer + 8));
    }

    [Fact]
    public void SfmtGivesTheReferenceSequences()
    {
        // SFMT.19937.out.txt of the reference implementation: init_gen_rand(1234), init_by_array({0x1234, 0x5678,
        // 0x9ABC, 0xDEF0})
        uint[] fromSeed = [3440181298, 1564997079, 1510669302, 2930277156, 1452439940, 3796268453, 423124208];
        uint[] fromArray = [2920711183, 3885745737, 3501893680, 856470934, 1421864068, 277361036, 1518638004];
        uint context = _harness.Alloc(0x9D0);
        uint array = _harness.Alloc(1000 * 4);
        Assert.Equal(0, Call(Sysmem, "ksceSfmt19937InitGenRand", context, 1234u));
        Assert.Equal(-1, Call(Sysmem, "ksceSfmt19937FillArray32", context, array, 620u));
        Assert.Equal(-1, Call(Sysmem, "ksceSfmt19937FillArray32", context, array, 626u));
        Assert.Equal(0, Call(Sysmem, "ksceSfmt19937FillArray32", context, array, 1000u));
        for (int i = 0; i < fromSeed.Length; i++)
        {
            Assert.Equal(fromSeed[i], GuestMemory.R32(array + (uint)(i * 4)));
        }

        // the words one by one are the same sequence, past a regeneration of the state
        uint[] filled = GuestMemory.ReadSpan(array, 1000 * 4).ToArray().Chunk(4).Select(b => BitConverter.ToUInt32(b)).ToArray();
        Assert.Equal(0, Call(Sysmem, "ksceSfmt19937InitGenRand", context, 1234u));
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(filled[i], (uint)Invoke(Sysmem, "ksceSfmt19937GenRand32", context)!);
        }

        Assert.Equal(-1, Call(Sysmem, "ksceSfmt19937FillArray32", context, array, 624u));
        uint key = Put([.. BitConverter.GetBytes(0x1234u), .. BitConverter.GetBytes(0x5678u), .. BitConverter.GetBytes(0x9ABCu), .. BitConverter.GetBytes(0xDEF0u)]);
        Assert.Equal(0, Call(Sysmem, "ksceSfmt19937InitByArray", context, key, 4));
        foreach (uint value in fromArray)
        {
            Assert.Equal(value, (uint)Invoke(Sysmem, "ksceSfmt19937GenRand32", context)!);
        }

        // 64-bit draws pair the words; fill64 counts 64-bit words
        Assert.Equal(0, Call(Sysmem, "ksceSfmt19937InitGenRand", context, 1234u));
        Assert.Equal(fromSeed[0] | ((ulong)fromSeed[1] << 32), (ulong)Invoke(Sysmem, "ksceSfmt19937GenRand64", context)!);
        Assert.Equal(fromSeed[2] | ((ulong)fromSeed[3] << 32), (ulong)Invoke(Sysmem, "ksceSfmt19937GenRand64", context)!);
        Assert.Equal(fromSeed[4], (uint)Invoke(Sysmem, "ksceSfmt19937GenRand32", context)!);
        Assert.Equal(fromSeed[5], (ulong)Invoke(Sysmem, "ksceSfmt19937GenRand64", context)!);
        Assert.Equal(fromSeed[6], (uint)Invoke(Sysmem, "ksceSfmt19937GenRand32", context)!);
        Assert.Equal(0, Call(Sysmem, "ksceSfmt19937InitGenRand", context, 1234u));
        Assert.Equal(-1, Call(Sysmem, "ksceSfmt19937FillArray64", context, array, 311u));
        Assert.Equal(0, Call(Sysmem, "ksceSfmt19937FillArray64", context, array, 312u));
        Assert.Equal(fromSeed[0], GuestMemory.R32(array));
        Assert.Equal(fromSeed[1], GuestMemory.R32(array + 4));
    }

    [Fact]
    public void XorshiftStepsMarsagliasGenerator()
    {
        uint state = _harness.Alloc(16);
        Assert.Equal(0, Call(Sysmem, "ksceXorshift128Init", state));
        Assert.Equal(1u, GuestMemory.R32(state + 12));
        GuestMemory.W32(state, 123456789);
        GuestMemory.W32(state + 4, 362436069);
        GuestMemory.W32(state + 8, 521288629);
        GuestMemory.W32(state + 12, 88675123);
        Assert.Equal(0, Call(Sysmem, "ksceXorshift128Init", state));
        Assert.Equal(123456789u, GuestMemory.R32(state));

        // the reference sequence of Marsaglia's seed
        Assert.Equal(3701687786u, (uint)Invoke(Sysmem, "ksceXorshift128Next", state)!);
        Assert.Equal(458299110u, (uint)Invoke(Sysmem, "ksceXorshift128Next", state)!);
        Assert.Equal(458299110u, GuestMemory.R32(state + 12));
        Assert.Equal(88675123u, GuestMemory.R32(state + 4));
    }

    private static byte[] RawDeflate(byte[] data, CompressionLevel level)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, level, true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression, 70000)]
    [InlineData(CompressionLevel.Fastest, 40)]
    [InlineData(CompressionLevel.Optimal, 5000)]
    [InlineData(CompressionLevel.SmallestSize, 20000)]
    public void DeflateDecodesTheStreamAndReportsItsEnd(CompressionLevel level, int length)
    {
        byte[] data = Data(length);
        for (int i = 0; i < length; i += 7)
        {
            data[i] = (byte)'a';
        }

        byte[] compressed = RawDeflate(data, level);
        uint source = Put([.. compressed, 0xEE, 0xEE]);
        uint destination = _harness.Alloc((uint)length + 16);
        uint next = _harness.Word(0);
        Assert.Equal(length, Call(Sysmem, "ksceDeflateDecompress", destination, (uint)length, source, next));
        Assert.Equal(data, GuestMemory.ReadSpan(destination, length).ToArray());
        Assert.Equal(source + (uint)compressed.Length, GuestMemory.R32(next));
        Assert.Equal(NoRoom, Call(Sysmem, "ksceDeflateDecompress", destination, (uint)length - 1, source, 0u));
    }

    [Fact]
    public void DeflateRefusesBadData()
    {
        uint destination = _harness.Alloc(64);
        Assert.Equal(BadData, Call(Sysmem, "ksceDeflateDecompress", destination, 64u, Put([0x07, 0, 0]), 0u));
        Assert.Equal(BadData, Call(Sysmem, "ksceDeflateDecompress", destination, 64u, Put([0x01, 0x05, 0x00, 0x00, 0x00]), 0u));

        // a distance before the start of the output
        Assert.Equal(BadData, Call(Sysmem, "ksceDeflateDecompress", destination, 64u, Put([0x03, 0x02, 0x00, 0x00]), 0u));
    }

    [Fact]
    public void ZlibChecksTheHeaderAndReturnsTheAdler()
    {
        byte[] data = Data(3000);
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, true))
        {
            zlib.Write(data);
        }

        byte[] stream = output.ToArray();
        uint source = Put(stream);
        uint destination = _harness.Alloc(3000);
        uint adler = _harness.Word(0);
        Assert.Equal(3000, Call(Sysmem, "ksceZlibDecompress", destination, 3000u, source, adler));
        Assert.Equal(data, GuestMemory.ReadSpan(destination, 3000).ToArray());
        uint expectedAdler = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(stream.AsSpan(stream.Length - 4));
        Assert.Equal(expectedAdler, GuestMemory.R32(adler));

        uint cmf = _harness.Word(0);
        uint flg = _harness.Word(0);
        uint start = _harness.Word(0);
        Assert.Equal(0, Call(Sysmem, "ksceZlibGetInfo", source, cmf, flg, 0u, start));
        Assert.Equal(stream[0], GuestMemory.Read<byte>(cmf));
        Assert.Equal(stream[1], GuestMemory.Read<byte>(flg));
        Assert.Equal(source + 2, GuestMemory.R32(start));
        Assert.Equal(source + 2, (uint)Invoke(Sysmem, "ksceZlibGetCompressedData", source)!);

        uint dictionary = Put([0x78, 0xBB, 0x12, 0x34, 0x56, 0x78]);
        uint id = _harness.Word(0);
        Assert.Equal(0, Call(Sysmem, "ksceZlibGetInfo", dictionary, 0u, 0u, id, start));
        Assert.Equal(0x12345678u, GuestMemory.R32(id));
        Assert.Equal(dictionary + 6, GuestMemory.R32(start));
        Assert.Equal(Format, Call(Sysmem, "ksceZlibDecompress", destination, 3000u, dictionary, 0u));
        uint bad = Put([0x78, 0x9D]);
        Assert.Equal(Format, Call(Sysmem, "ksceZlibGetInfo", bad, 0u, 0u, 0u, 0u));
        Assert.Equal(0u, (uint)Invoke(Sysmem, "ksceZlibGetCompressedData", bad)!);
    }

    [Fact]
    public void GzipReadsItsHeaderAndTrailer()
    {
        byte[] data = Data(2500);
        byte[] deflated = RawDeflate(data, CompressionLevel.Optimal);
        uint crc32 = System.IO.Hashing.Crc32.HashToUInt32(data);
        var file = new List<byte> { 0x1F, 0x8B, 8, 0x08 | 0x10 | 0x02, 0, 0, 0, 0, 0, 3 };
        file.AddRange("name.bin\0"u8.ToArray());
        file.AddRange("a comment\0"u8.ToArray());
        file.AddRange([0x34, 0x12]);
        int dataOffset = file.Count;
        file.AddRange(deflated);
        file.AddRange(BitConverter.GetBytes(crc32));
        file.AddRange(BitConverter.GetBytes((uint)data.Length));
        uint source = Put([.. file]);

        Assert.Equal(1, Call(Sysmem, "ksceGzipIsValid", source));
        uint info = _harness.Alloc(0x20);
        GuestMemory.Fill(info, 0xAA, 0x20);
        Assert.Equal(0, Call(Sysmem, "ksceGzipGetInfo", source, info, info + 4, info + 8, info + 12, info + 16));
        Assert.Equal(0u, GuestMemory.R32(info));
        Assert.Equal(source + 10, GuestMemory.R32(info + 4));
        Assert.Equal(source + 19, GuestMemory.R32(info + 8));
        Assert.Equal((ushort)0x1234, GuestMemory.Read<ushort>(info + 12));
        Assert.Equal(source + (uint)dataOffset, GuestMemory.R32(info + 16));
        Assert.Equal(source + 10, (uint)Invoke(Sysmem, "ksceGzipGetName", source)!);
        Assert.Equal(source + 19, (uint)Invoke(Sysmem, "ksceGzipGetComment", source)!);
        Assert.Equal(source + (uint)dataOffset, (uint)Invoke(Sysmem, "ksceGzipGetCompressedData", source)!);

        uint destination = _harness.Alloc(2500);
        uint crc = _harness.Word(0);
        Assert.Equal(2500, Call(Sysmem, "ksceGzipDecompress", destination, 2500u, source, crc));
        Assert.Equal(data, GuestMemory.ReadSpan(destination, 2500).ToArray());
        Assert.Equal(crc32, GuestMemory.R32(crc));

        // a trailer length that is not the output's
        GuestMemory.W32(source + (uint)file.Count - 4, 2499);
        Assert.Equal(Format, Call(Sysmem, "ksceGzipDecompress", destination, 2500u, source, crc));
    }

    [Fact]
    public void GzipRefusesOtherHeaders()
    {
        uint info = _harness.Alloc(0x20);
        GuestMemory.Fill(info, 0xAA, 0x20);
        uint notGzip = Put([0x1F, 0x8C, 8, 0, 0, 0, 0, 0, 0, 3]);
        Assert.Equal(0, Call(Sysmem, "ksceGzipIsValid", notGzip));
        Assert.Equal(Format, Call(Sysmem, "ksceGzipGetInfo", notGzip, info, 0u, 0u, 0u, info + 16));
        Assert.Equal(0u, GuestMemory.R32(info));
        Assert.Equal(0u, GuestMemory.R32(info + 16));
        Assert.Equal(IllegalAddress, Call(Sysmem, "ksceGzipGetInfo", 0u, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(0u, (uint)Invoke(Sysmem, "ksceGzipGetName", notGzip)!);
        uint otherMethod = Put([0x1F, 0x8B, 7, 0, 0, 0, 0, 0, 0, 3, 3, 0]);
        Assert.Equal(Format, Call(Sysmem, "ksceGzipDecompress", info, 16u, otherMethod, 0u));

        // FEXTRA: the field reported is the length itself, and the skip counts from it
        uint extra = Put([0x1F, 0x8B, 8, 0x04, 0, 0, 0, 0, 0, 3, 4, 0, 0xAB, 0xCD, 0x03, 0x00]);
        Assert.Equal(0, Call(Sysmem, "ksceGzipGetInfo", extra, info, 0u, 0u, 0u, info + 16));
        Assert.Equal(extra + 10, GuestMemory.R32(info));
        Assert.Equal(extra + 14, GuestMemory.R32(info + 16));
    }
}
