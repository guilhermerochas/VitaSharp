using System.IO.Compression;
using System.Text;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// SceZlibForDriver, the kernel's zlib 1.2.5 (SceSblPostSsMgr): the one-shot calls, inflate fed and drained a byte at
/// a time with the bytes after the end handed back, the zlib / gzip / raw / automatic wrappers, deflate with flushes
/// and small output buffers (checked by the host's decoder), preset dictionaries, the version and stream checks,
/// stream copies, inflateSync, inflateBack and the checksums and strings.
/// </summary>
[Collection("guest")]
public sealed class KernelZlibTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint InflateInit = 0x723495A5, InflateInit2 = 0x0BDDF66A, Inflate = 0xD4A85178, InflateEnd = 0x134E91EA;
    private const uint DeflateInit = 0x25F28DA7, DeflateInit2 = 0xE6EB524C, Deflate = 0x5B718E55, DeflateEnd = 0x5492B3F2;
    private const int ZOk = 0, ZStreamEnd = 1, ZNeedDict = 2, ZStreamError = -2, ZDataError = -3, ZBufError = -5, ZVersionError = -6;

    private readonly HleHarness _harness = new(fixture, "kzlib");

    public void Dispose() => _harness.Dispose();

    private unsafe int Call(GuestThread thread, uint nid, params uint[] args)
    {
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not implemented");
        for (int i = 0; i < 4; i++)
        {
            thread.Core.State->R[i] = i < args.Length ? args[i] : 0xA5A5A5A5;
        }

        uint stack = thread.Core.State->R[13];
        thread.Core.State->R[13] = stack - 0x40;
        for (int i = 4; i < args.Length; i++)
        {
            GuestMemory.W32(stack - 0x40 + (uint)(i - 4) * 4, args[i]);
        }

        implementation(thread.Hle);
        thread.Core.State->R[13] = stack;
        return (int)thread.Core.State->R[0];
    }

    private uint Bytes(ReadOnlySpan<byte> data)
    {
        uint address = _harness.Alloc((uint)Math.Max(data.Length, 1) + 16);
        data.CopyTo(GuestMemory.Span(address, data.Length));
        return address;
    }

    private uint Version => _harness.Str("1.2.5");

    private static byte[] Sample()
    {
        var text = new StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            text.Append("line ").Append(i % 37).Append(" of the kernel zlib sample; ");
        }

        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static byte[] HostCompress(byte[] data, string wrapper)
    {
        using var output = new MemoryStream();
        using (Stream compressor = wrapper switch
        {
            "gzip" => new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true),
            "raw" => new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true),
            _ => new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true),
        })
        {
            compressor.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] HostDecompress(byte[] data)
    {
        using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Inflates <paramref name="compressed"/> fed <paramref name="inStep"/> bytes and drained
    /// <paramref name="outStep"/> bytes at a time; the output, the last result and what was left in avail_in.</summary>
    private (byte[] Output, int Result, uint LeftIn) InflatePieces(GuestThread t, uint stream, byte[] compressed, int inStep, int outStep)
    {
        uint input = Bytes(compressed);
        uint output = _harness.Alloc((uint)outStep + 16);
        var result = new List<byte>();
        int fed = 0;
        int status = ZOk;
        for (int guard = 0; guard < 10_000_000; guard++)
        {
            if (GuestMemory.R32(stream + 4) == 0 && fed < compressed.Length)
            {
                int piece = Math.Min(inStep, compressed.Length - fed);
                GuestMemory.W32(stream, input + (uint)fed);
                GuestMemory.W32(stream + 4, (uint)piece);
                fed += piece;
            }

            GuestMemory.W32(stream + 0xC, output);
            GuestMemory.W32(stream + 0x10, (uint)outStep);
            status = Call(t, Inflate, stream, 0);
            int produced = outStep - (int)GuestMemory.R32(stream + 0x10);
            result.AddRange(GuestMemory.Span(output, produced).ToArray());
            if (status != ZOk && status != ZBufError)
            {
                break;
            }

            if (status == ZBufError && fed >= compressed.Length && GuestMemory.R32(stream + 4) == 0)
            {
                break;
            }
        }

        return (result.ToArray(), status, GuestMemory.R32(stream + 4));
    }

    [Fact]
    public void CompressAndUncompressRoundTrip()
    {
        byte[] sample = Sample();
        _harness.OnGuest(t =>
        {
            uint source = Bytes(sample);
            uint bound = (uint)Call(t, 0x7048F14C, (uint)sample.Length);
            Assert.Equal((uint)(sample.Length + (sample.Length >> 12) + (sample.Length >> 14) + 13), bound);
            uint compressed = _harness.Alloc(bound);
            uint compressedLength = _harness.Word(bound);
            Assert.Equal(ZOk, Call(t, 0x82167CD9, compressed, compressedLength, source, (uint)sample.Length, 9));
            byte[] packed = GuestMemory.Span(compressed, (int)GuestMemory.R32(compressedLength)).ToArray();
            Assert.Equal(sample, HostDecompress(packed));
            Assert.Equal(ZStreamError, Call(t, 0x82167CD9, compressed, compressedLength, source, (uint)sample.Length, 10));
            Assert.Equal(ZBufError, Call(t, 0x6ED5B677, compressed, _harness.Word(4), source, (uint)sample.Length));

            uint restored = _harness.Alloc((uint)sample.Length);
            uint restoredLength = _harness.Word((uint)sample.Length);
            Assert.Equal(ZOk, Call(t, 0x89A13883, restored, restoredLength, compressed, (uint)packed.Length));
            Assert.Equal((uint)sample.Length, GuestMemory.R32(restoredLength));
            Assert.Equal(sample, GuestMemory.Span(restored, sample.Length).ToArray());
            Assert.Equal(ZBufError, Call(t, 0x89A13883, restored, _harness.Word(100), compressed, (uint)packed.Length));
            Assert.Equal(ZDataError, Call(t, 0x89A13883, restored, _harness.Word((uint)sample.Length), compressed, (uint)packed.Length / 2));
            GuestMemory.Write(compressed, (byte)0x79); // a header that fails its check
            Assert.Equal(ZDataError, Call(t, 0x89A13883, restored, _harness.Word((uint)sample.Length), compressed, (uint)packed.Length));
        });
    }

    [Theory]
    [InlineData("zlib", 15)]
    [InlineData("gzip", 31)]
    [InlineData("raw", -15)]
    [InlineData("gzip", 47)]
    [InlineData("zlib", 47)]
    public void InflateFedAndDrainedAByteAtATimeHandsBackWhatFollowsTheStream(string wrapper, int windowBits)
    {
        byte[] sample = Sample();
        byte[] compressed = [.. HostCompress(sample, wrapper), 0xEE, 0xEE, 0xEE];
        _harness.OnGuest(t =>
        {
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, InflateInit2, stream, unchecked((uint)windowBits), Version, 0x38));
            (byte[] output, int result, uint leftIn) = InflatePieces(t, stream, compressed, 1, 7);
            Assert.Equal(ZStreamEnd, result);
            Assert.Equal(sample, output);
            Assert.Equal((uint)sample.Length, GuestMemory.R32(stream + 0x14));
            Assert.True(leftIn <= 3);
            Assert.Equal(ZOk, Call(t, InflateEnd, stream));
            Assert.Equal(ZStreamError, Call(t, InflateEnd, stream));
        });
    }

    [Fact]
    public void InflateInLargePiecesLeavesTheTrailingBytesInAvailIn()
    {
        byte[] sample = Sample();
        byte[] compressed = [.. HostCompress(sample, "zlib"), 1, 2, 3, 4, 5];
        _harness.OnGuest(t =>
        {
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, InflateInit, stream, Version, 0x38));
            (byte[] output, int result, uint leftIn) = InflatePieces(t, stream, compressed, compressed.Length, sample.Length + 100);
            Assert.Equal(ZStreamEnd, result);
            Assert.Equal(sample, output);
            Assert.Equal(5u, leftIn);
            Assert.Equal((uint)(compressed.Length - 5), GuestMemory.R32(stream + 8));
            uint adler = GuestMemory.R32(stream + 0x30);
            Assert.Equal((uint)Call(t, 0x7E823337, 1, Bytes(sample), (uint)sample.Length), adler);
        });
    }

    [Fact]
    public void BadDataEndsWithADataErrorAndAMessage()
    {
        _harness.OnGuest(t =>
        {
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, InflateInit2, stream, unchecked((uint)-15), Version, 0x38));
            (_, int result, _) = InflatePieces(t, stream, [0xFF, 0xFF, 0xFF, 0xFF], 4, 64); // block type 3
            Assert.Equal(ZDataError, result);
            Assert.Equal("data error", GuestMemory.ReadCString(GuestMemory.R32(stream + 0x18)));
        });
    }

    [Theory]
    [InlineData(15, 0)]
    [InlineData(31, 1)]
    [InlineData(-15, 2)]
    public void DeflateWithSmallOutputBuffersMakesWhatTheHostDecodes(int windowBits, int kind)
    {
        byte[] sample = Sample();
        _harness.OnGuest(t =>
        {
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, DeflateInit2, stream, 6, 8, unchecked((uint)windowBits), 8, 0, Version, 0x38));
            uint input = Bytes(sample);
            uint output = _harness.Alloc(64);
            var packed = new List<byte>();
            int fed = 0;
            int status;
            do
            {
                int flush = 0;
                if (GuestMemory.R32(stream + 4) == 0)
                {
                    int piece = Math.Min(5000, sample.Length - fed);
                    GuestMemory.W32(stream, input + (uint)fed);
                    GuestMemory.W32(stream + 4, (uint)piece);
                    fed += piece;
                }

                if (fed >= sample.Length)
                {
                    flush = 4;
                }
                else if (fed == 10000)
                {
                    flush = 2;
                }

                GuestMemory.W32(stream + 0xC, output);
                GuestMemory.W32(stream + 0x10, 64);
                status = Call(t, Deflate, stream, (uint)flush);
                packed.AddRange(GuestMemory.Span(output, 64 - (int)GuestMemory.R32(stream + 0x10)).ToArray());
                Assert.True(status is ZOk or ZStreamEnd or ZBufError, $"deflate: {status}");
            }
            while (status != ZStreamEnd);

            Assert.Equal(ZOk, Call(t, DeflateEnd, stream));
            byte[] bytes = packed.ToArray();
            Stream decoder = kind switch
            {
                1 => new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress),
                2 => new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress),
                _ => new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress),
            };
            using var decoded = new MemoryStream();
            decoder.CopyTo(decoded);
            Assert.Equal(sample, decoded.ToArray());

            // and the kernel's own inflate reads it back
            uint back = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, InflateInit2, back, unchecked((uint)windowBits), Version, 0x38));
            (byte[] restored, int result, _) = InflatePieces(t, back, bytes, 333, 1000);
            Assert.Equal(ZStreamEnd, result);
            Assert.Equal(sample, restored);
        });
    }

    [Fact]
    public void TheVersionAndTheStreamAreChecked()
    {
        _harness.OnGuest(t =>
        {
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZVersionError, Call(t, DeflateInit, stream, 6, _harness.Str("2.0"), 0x38));
            Assert.Equal(ZVersionError, Call(t, DeflateInit, stream, 6, Version, 0x30));
            Assert.Equal(ZVersionError, Call(t, InflateInit, stream, 0, 0x38));
            Assert.Equal(ZStreamError, Call(t, DeflateInit, 0, 6, Version, 0x38));
            Assert.Equal(ZStreamError, Call(t, DeflateInit, stream, 10, Version, 0x38));
            Assert.Equal(ZStreamError, Call(t, InflateInit2, stream, 7, Version, 0x38));
            Assert.Equal(ZStreamError, Call(t, Inflate, stream, 0)); // no state
            Assert.Equal(ZStreamError, Call(t, Deflate, stream, 0));
            Assert.Equal(ZOk, Call(t, InflateInit, stream, Version, 0x38));
            Assert.Equal(ZStreamError, Call(t, Deflate, stream, 0)); // an inflate stream
            GuestMemory.W32(stream + 0xC, 0);
            Assert.Equal(ZStreamError, Call(t, Inflate, stream, 0)); // no next_out
            Assert.Equal(ZOk, Call(t, 0x408311E8, stream)); // inflateReset
            Assert.Equal(ZOk, Call(t, InflateEnd, stream));
        });
    }

    [Fact]
    public void APresetDictionaryIsAskedForAndChecked()
    {
        byte[] dictionary = Encoding.ASCII.GetBytes("the kernel dictionary");
        byte[] payload = Encoding.ASCII.GetBytes("hello");
        _harness.OnGuest(t =>
        {
            uint dictionaryId = (uint)Call(t, 0x7E823337, 1, Bytes(dictionary), (uint)dictionary.Length);
            uint payloadAdler = (uint)Call(t, 0x7E823337, 1, Bytes(payload), (uint)payload.Length);
            // CMF 0x78, FLG with FDICT, the dictionary id, one stored block, the trailer
            int flags = 0x20;
            flags += 31 - ((0x78 << 8) | flags) % 31;
            var stream = new List<byte> { 0x78, (byte)flags };
            stream.AddRange([(byte)(dictionaryId >> 24), (byte)(dictionaryId >> 16), (byte)(dictionaryId >> 8), (byte)dictionaryId]);
            stream.AddRange([1, (byte)payload.Length, 0, (byte)~payload.Length, 0xFF]);
            stream.AddRange(payload);
            stream.AddRange([(byte)(payloadAdler >> 24), (byte)(payloadAdler >> 16), (byte)(payloadAdler >> 8), (byte)payloadAdler]);
            byte[] compressed = stream.ToArray();

            uint z = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, InflateInit, z, Version, 0x38));
            uint input = Bytes(compressed);
            uint output = _harness.Alloc(64);
            GuestMemory.W32(z, input);
            GuestMemory.W32(z + 4, (uint)compressed.Length);
            GuestMemory.W32(z + 0xC, output);
            GuestMemory.W32(z + 0x10, 64);
            Assert.Equal(ZNeedDict, Call(t, Inflate, z, 0));
            Assert.Equal(dictionaryId, GuestMemory.R32(z + 0x30));
            Assert.Equal(ZDataError, Call(t, 0x00561385, z, Bytes(payload), (uint)payload.Length));
            Assert.Equal(ZOk, Call(t, 0x00561385, z, Bytes(dictionary), (uint)dictionary.Length));
            Assert.Equal(ZStreamEnd, Call(t, Inflate, z, 0));
            Assert.Equal(payload, GuestMemory.Span(output, payload.Length).ToArray());

            uint deflater = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, DeflateInit, deflater, 6, Version, 0x38));
            Assert.Equal(ZOk, Call(t, 0x4EE6C080, deflater, Bytes(dictionary), (uint)dictionary.Length));
            Assert.Equal(dictionaryId, GuestMemory.R32(deflater + 0x30));
        });
    }

    [Fact]
    public void ACopiedStreamGoesOnOnItsOwn()
    {
        byte[] sample = Sample();
        byte[] compressed = HostCompress(sample, "zlib");
        _harness.OnGuest(t =>
        {
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, InflateInit, stream, Version, 0x38));
            uint input = Bytes(compressed);
            uint output = _harness.Alloc((uint)sample.Length);
            GuestMemory.W32(stream, input);
            GuestMemory.W32(stream + 4, (uint)compressed.Length / 2);
            GuestMemory.W32(stream + 0xC, output);
            GuestMemory.W32(stream + 0x10, (uint)sample.Length);
            Assert.Equal(ZOk, Call(t, Inflate, stream, 0));
            uint copy = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, 0x4C27A382, copy, stream)); // inflateCopy
            Assert.NotEqual(GuestMemory.R32(stream + 0x1C), GuestMemory.R32(copy + 0x1C));
            uint copyOutput = _harness.Alloc((uint)sample.Length);
            uint produced = GuestMemory.R32(stream + 0x14);
            GuestMemory.W32(copy + 0xC, copyOutput + produced);
            foreach (uint z in new[] { stream, copy })
            {
                GuestMemory.W32(z, input + (uint)compressed.Length / 2);
                GuestMemory.W32(z + 4, (uint)(compressed.Length - compressed.Length / 2));
                Assert.Equal(ZStreamEnd, Call(t, Inflate, z, 0));
            }

            GuestMemory.Span(output, (int)produced).CopyTo(GuestMemory.Span(copyOutput, (int)produced));
            Assert.Equal(sample, GuestMemory.Span(output, sample.Length).ToArray());
            Assert.Equal(sample, GuestMemory.Span(copyOutput, sample.Length).ToArray());
        });
    }

    [Fact]
    public void InflateSyncFindsTheNextFlushPoint()
    {
        _harness.OnGuest(t =>
        {
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, InflateInit2, stream, unchecked((uint)-15), Version, 0x38));
            Assert.Equal(ZBufError, Call(t, 0x35E0108C, stream));
            byte[] data = [9, 9, 9, 0, 0, 0xFF, 0xFF, 1, 2, 0xFD, 0xFF, (byte)'a', (byte)'b'];
            GuestMemory.W32(stream, Bytes(data));
            GuestMemory.W32(stream + 4, (uint)data.Length);
            Assert.Equal(ZOk, Call(t, 0x35E0108C, stream));
            Assert.Equal(6u, GuestMemory.R32(stream + 4)); // right after 00 00 FF FF
            Assert.Equal(1, Call(t, 0x4CB63BCD, stream)); // inflateSyncPoint
        });
    }

    [Fact]
    public void InflateBackGivesTheOutputToTheCallback()
    {
        byte[] sample = Sample();
        byte[] compressed = [.. HostCompress(sample, "raw"), 7, 7];
        _harness.OnGuest(t =>
        {
            // out(desc, buf, len): str r2, [r0]; movs r0, #0; bx lr (keeps the last length)
            uint output = _harness.Alloc(8);
            GuestMemory.Write(output, (ushort)0x6002);
            GuestMemory.Write(output + 2, (ushort)0x2000);
            GuestMemory.Write(output + 4, (ushort)0x4770);
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, InflateInit2, stream, unchecked((uint)-15), Version, 0x38));
            GuestMemory.W32(stream, Bytes(compressed));
            GuestMemory.W32(stream + 4, (uint)compressed.Length);
            uint descriptor = _harness.Word(0);
            Assert.Equal(ZStreamEnd, Call(t, 0x5377643A, stream, 0, 0, output | 1, descriptor));
            Assert.Equal((uint)(sample.Length % 0x10000 == 0 ? 0x10000 : sample.Length % 0x10000), GuestMemory.R32(descriptor));
            Assert.Equal(2u, GuestMemory.R32(stream + 4));
        });
    }

    [Fact]
    public void ChecksumsAndStringsAreZlibs()
    {
        _harness.OnGuest(t =>
        {
            Assert.Equal(0xCBF43926u, (uint)Call(t, 0x3370B9AD, 0, Bytes(Encoding.ASCII.GetBytes("123456789")), 9));
            Assert.Equal(0x11E60398u, (uint)Call(t, 0x7E823337, 1, Bytes(Encoding.ASCII.GetBytes("Wikipedia")), 9));
            Assert.Equal(1u, (uint)Call(t, 0x7E823337, 5, 0, 9));
            Assert.Equal(0u, (uint)Call(t, 0x3370B9AD, 5, 0, 9));
            Assert.Equal("1.2.5", GuestMemory.ReadCString((uint)Call(t, 0x517BC5F7)));
            Assert.Equal("data error", GuestMemory.ReadCString((uint)Call(t, 0x3B4466F4, unchecked((uint)ZDataError))));
            Assert.Equal("need dictionary", GuestMemory.ReadCString((uint)Call(t, 0x3F33F55F, 2)));
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, DeflateInit, stream, 6, Version, 0x38));
            Assert.Equal((uint)(1000 + (1000 >> 12) + (1000 >> 14) + 13), (uint)Call(t, 0x05F712FE, stream, 1000));
        });
    }

    /// <summary>Saves the zlib streams of the module with the guest's z_stream (what a save state holds), ends the
    /// stream (as a session that went on would; deflateEnd of an unfinished stream answers Z_DATA_ERROR, as zlib's),
    /// and loads both back.</summary>
    private void SaveEndAndLoad(GuestThread t, uint stream, uint endNid)
    {
        var state = (VitaSharp.Core.SaveState.ISnapshotState)_harness.Module<VitaSharp.Core.Hle.Modules.SystemServicesVsh>();
        var writer = new VitaSharp.Core.SaveState.SnapshotWriter();
        state.SaveState(writer);
        byte[] guestStream = GuestMemory.Span(stream, 0x38).ToArray();
        Assert.Contains(Call(t, endNid, stream), new[] { ZOk, ZDataError });
        guestStream.CopyTo(GuestMemory.Span(stream, 0x38));
        state.LoadState(new VitaSharp.Core.SaveState.SnapshotReader(writer.Data.ToArray()));
    }

    [Fact]
    public void ASaveStateInTheMiddleOfAnInflateCarriesTheStream()
    {
        byte[] sample = Sample();
        byte[] compressed = HostCompress(sample, "zlib");
        _harness.OnGuest(t =>
        {
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, InflateInit, stream, Version, 0x38));
            uint input = Bytes(compressed);
            uint output = _harness.Alloc((uint)sample.Length + 64);
            int half = compressed.Length / 2;
            GuestMemory.W32(stream, input);
            GuestMemory.W32(stream + 4, (uint)half);
            GuestMemory.W32(stream + 0xC, output);
            GuestMemory.W32(stream + 0x10, (uint)sample.Length + 64);
            Assert.Equal(ZOk, Call(t, Inflate, stream, 0));
            SaveEndAndLoad(t, stream, InflateEnd);
            GuestMemory.W32(stream + 4, GuestMemory.R32(stream + 4) + (uint)(compressed.Length - half));
            Assert.Equal(ZStreamEnd, Call(t, Inflate, stream, 0));
            Assert.Equal(sample, GuestMemory.Span(output, sample.Length).ToArray());
        });
    }

    [Fact]
    public void ASaveStateInTheMiddleOfADeflateCarriesTheStream()
    {
        byte[] sample = Sample();
        _harness.OnGuest(t =>
        {
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, DeflateInit, stream, 6, Version, 0x38));
            uint input = Bytes(sample);
            uint output = _harness.Alloc((uint)sample.Length + 256);
            int half = sample.Length / 2;
            GuestMemory.W32(stream, input);
            GuestMemory.W32(stream + 4, (uint)half);
            GuestMemory.W32(stream + 0xC, output);
            GuestMemory.W32(stream + 0x10, (uint)sample.Length + 256);
            Assert.Equal(ZOk, Call(t, Deflate, stream, 0));
            SaveEndAndLoad(t, stream, DeflateEnd);
            GuestMemory.W32(stream + 4, (uint)(sample.Length - half));
            Assert.Equal(ZStreamEnd, Call(t, Deflate, stream, 4));
            int packed = (int)GuestMemory.R32(stream + 0x14);
            using var source = new MemoryStream(GuestMemory.Span(output, packed).ToArray());
            using var decoder = new ZLibStream(source, CompressionMode.Decompress);
            using var plain = new MemoryStream();
            decoder.CopyTo(plain);
            Assert.Equal(sample, plain.ToArray());
        });
    }

    /// <summary>Deflates <paramref name="data"/> in one deflate(Z_FINISH) with room for all of it; the bytes.</summary>
    private byte[] DeflateAll(GuestThread t, uint stream, byte[] data, int flush = 4)
    {
        uint input = Bytes(data);
        uint output = _harness.Alloc((uint)data.Length * 2 + 1024);
        GuestMemory.W32(stream, input);
        GuestMemory.W32(stream + 4, (uint)data.Length);
        GuestMemory.W32(stream + 0xC, output);
        GuestMemory.W32(stream + 0x10, (uint)data.Length * 2 + 1024);
        int status = Call(t, Deflate, stream, (uint)flush);
        Assert.Equal(flush == 4 ? ZStreamEnd : ZOk, status);
        return GuestMemory.Span(output, (int)((uint)data.Length * 2 + 1024 - GuestMemory.R32(stream + 0x10))).ToArray();
    }

    /// <summary>The bytes with their first <paramref name="bits"/> bits (LSB first, as deflate packs them) removed.</summary>
    private static byte[] DropBits(byte[] bytes, int bits)
    {
        int total = bytes.Length * 8 - bits;
        byte[] result = new byte[(total + 7) / 8];
        for (int i = 0; i < total; i++)
        {
            int source = i + bits;
            if ((bytes[source >> 3] >> (source & 7) & 1) != 0)
            {
                result[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        return result;
    }

    private static byte[] HostInflateRaw(byte[] bytes)
    {
        using var decoder = new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var plain = new MemoryStream();
        decoder.CopyTo(plain);
        return plain.ToArray();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 5)]
    [InlineData(7, 0x55)]
    [InlineData(8, 0xA5)]
    [InlineData(13, 0x1ABC)]
    [InlineData(16, 0xBEEF)]
    public void PrimedBitsComeBeforeTheFirstBlockOnTheSameByte(int bits, int value)
    {
        byte[] sample = Sample();
        _harness.OnGuest(t =>
        {
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, DeflateInit2, stream, 6, 8, unchecked((uint)-15), 8, 0, Version, 0x38));
            // the value's bits above the count are dropped (bi_buf = value & ((1 << bits) - 1))
            Assert.Equal(ZOk, Call(t, 0x0FA805A3, stream, (uint)bits, (uint)(value | (0x7 << bits))));
            byte[] packed = DeflateAll(t, stream, sample);
            int primed = packed[0] | (packed[1] << 8);
            Assert.Equal(value, primed & ((1 << bits) - 1));
            // the block follows the bits directly, not on the next byte
            Assert.Equal(sample, HostInflateRaw(DropBits(packed, bits)));
            Assert.Equal(ZOk, Call(t, DeflateEnd, stream));
        });
    }

    [Fact]
    public void APrimeReplacesTheBitsOfAnEarlierOneAndFollowsAZlibHeader()
    {
        byte[] sample = Encoding.ASCII.GetBytes("primed after the zlib header, primed after the zlib header");
        _harness.OnGuest(t =>
        {
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, DeflateInit, stream, 6, Version, 0x38));
            Assert.Equal(ZOk, Call(t, 0x1C344E27, stream, 6, 0x3F));
            Assert.Equal(ZOk, Call(t, 0x1C344E27, stream, 2, 0x2));
            byte[] packed = DeflateAll(t, stream, sample);
            // the two header bytes are written as bytes, the primed bits start the third
            Assert.Equal(0x78, packed[0]);
            Assert.Equal(0, ((packed[0] << 8) | packed[1]) % 31);
            Assert.Equal(2, packed[2] & 3);
            byte[] body = DropBits(packed[2..^4], 2);
            Assert.Equal(sample, HostInflateRaw(body));
            Assert.Equal(ZStreamError, Call(t, 0x0FA805A3, 0, 3, 1));
            Assert.Equal(ZOk, Call(t, DeflateEnd, stream));
        });
    }

    [Fact]
    public void ABlockFlushLeavesTheStreamOffAByteAndTheNextBlockFollowsIt()
    {
        byte[] first = Encoding.ASCII.GetBytes("first part of the stream, first part of the stream ");
        byte[] second = Encoding.ASCII.GetBytes("second part, which refers back to the first part of the stream");
        _harness.OnGuest(t =>
        {
            for (int flush = 1; flush <= 5; flush++)
            {
                if (flush == 4)
                {
                    continue;
                }

                uint stream = _harness.Alloc(0x38);
                Assert.Equal(ZOk, Call(t, DeflateInit2, stream, 6, 8, unchecked((uint)-15), 8, 0, Version, 0x38));
                byte[] head = DeflateAll(t, stream, first, flush);
                byte[] tail = DeflateAll(t, stream, second);
                byte[] whole = [.. head, .. tail];
                Assert.Equal([.. first, .. second], HostInflateRaw(whole));
                if (flush is 2 or 3)
                {
                    // the sync and full flushes end on a byte with an empty stored block
                    Assert.Equal([0, 0, 0xFF, 0xFF], head[^4..]);
                }

                Assert.Equal(ZOk, Call(t, DeflateEnd, stream));
            }
        });
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(6, 1)]
    [InlineData(6, 2)]
    [InlineData(6, 3)]
    [InlineData(6, 4)]
    [InlineData(9, 0)]
    public void EveryLevelAndStrategyMakesAStreamTheHostDecodes(int level, int strategy)
    {
        var random = new Random(level * 7 + strategy);
        byte[] sample = [.. Sample(), .. Enumerable.Range(0, 70000).Select(_ => (byte)random.Next(4)), .. new byte[3000]];
        _harness.OnGuest(t =>
        {
            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, DeflateInit2, stream, (uint)level, 8, 15, 8, (uint)strategy, Version, 0x38));
            byte[] packed = DeflateAll(t, stream, sample);
            Assert.Equal(sample, HostDecompress(packed));
            if (level > 0 && strategy != 2)
            {
                Assert.True(packed.Length < sample.Length / 2, $"level {level} strategy {strategy}: {packed.Length} of {sample.Length}");
            }

            // FLEVEL by level, 0 for Z_HUFFMAN_ONLY and up
            int expectedLevel = strategy >= 2 || level < 2 ? 0 : level < 6 ? 1 : level == 6 ? 2 : 3;
            Assert.Equal(expectedLevel, packed[1] >> 6);
            Assert.Equal(ZOk, Call(t, DeflateEnd, stream));
        });
    }

    [Fact]
    public void ADeflatePresetDictionaryIsInTheHeaderAndTheDataRefersToIt()
    {
        byte[] dictionary = Encoding.ASCII.GetBytes("a dictionary of words the payload repeats: kernel zlib stream");
        byte[] payload = Encoding.ASCII.GetBytes("kernel zlib stream: words the payload repeats");
        _harness.OnGuest(t =>
        {
            uint plain = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, DeflateInit, plain, 9, Version, 0x38));
            byte[] without = DeflateAll(t, plain, payload);

            uint stream = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, DeflateInit, stream, 9, Version, 0x38));
            Assert.Equal(ZOk, Call(t, 0x4EE6C080, stream, Bytes(dictionary), (uint)dictionary.Length));
            uint dictionaryId = GuestMemory.R32(stream + 0x30);
            byte[] packed = DeflateAll(t, stream, payload);
            Assert.Equal(0x20, packed[1] & 0x20);
            Assert.Equal(dictionaryId, (uint)((packed[2] << 24) | (packed[3] << 16) | (packed[4] << 8) | packed[5]));
            Assert.True(packed.Length - 4 < without.Length, "the data refers to the dictionary");
            // a zlib stream takes the dictionary only before its first deflate
            Assert.Equal(ZStreamError, Call(t, 0x4EE6C080, stream, Bytes(dictionary), (uint)dictionary.Length));

            uint z = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, InflateInit, z, Version, 0x38));
            uint output = _harness.Alloc(256);
            GuestMemory.W32(z, Bytes(packed));
            GuestMemory.W32(z + 4, (uint)packed.Length);
            GuestMemory.W32(z + 0xC, output);
            GuestMemory.W32(z + 0x10, 256);
            Assert.Equal(ZNeedDict, Call(t, Inflate, z, 0));
            Assert.Equal(ZOk, Call(t, 0x00561385, z, Bytes(dictionary), (uint)dictionary.Length));
            Assert.Equal(ZStreamEnd, Call(t, Inflate, z, 0));
            Assert.Equal(payload, GuestMemory.Span(output, payload.Length).ToArray());
        });
    }

    [Fact]
    public void TheDataTypeIsTextOrBinaryFromTheFirstBlock()
    {
        _harness.OnGuest(t =>
        {
            uint text = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, DeflateInit, text, 6, Version, 0x38));
            Assert.Equal(2u, GuestMemory.R32(text + 0x2C));
            DeflateAll(t, text, Encoding.ASCII.GetBytes("plain text\r\n"));
            Assert.Equal(1u, GuestMemory.R32(text + 0x2C));

            uint binary = _harness.Alloc(0x38);
            Assert.Equal(ZOk, Call(t, DeflateInit, binary, 6, Version, 0x38));
            DeflateAll(t, binary, [1, 2, 3, 0x41, 0x42]);
            Assert.Equal(0u, GuestMemory.R32(binary + 0x2C));
        });
    }
}
