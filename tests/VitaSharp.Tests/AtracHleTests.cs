using System.Buffers.Binary;
using System.Reflection;
using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>SceAtrac: .at9 container parsing, decoder group/handle management, streaming ring and loop bookkeeping.</summary>
[Collection("guest")]
public sealed class AtracHleTests(GuestMemoryFixture fx)
{
    static object? Invoke<T>(T module, string name, params object[] args) =>
        typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);
    static int Call<T>(T module, string name, params object[] args) => (int)Invoke(module, name, args)!;
    static Ptr<T> P<T>(uint a) where T : unmanaged => new(a);

    const int SfBytes = 1024, SfSamples = 1024;
    // 48 kHz (index 7), 256-byte frames, 4 frames per superframe; channel config 0 = mono, 2 = stereo
    static byte[] Config(int channels) => [0xFE, (byte)(channels == 2 ? 0x74 : 0x70), 0x1F, 0xF0];

    /// <summary>Superframe k's first two bytes hold k; the fake decoder turns that into samples whose value is the full
    /// (delay-including) sample index, negated on the second channel.</summary>
    sealed class FakeDecoder : IAt9SuperframeDecoder
    {
        public int Resets, Decodes;
        public void Reset() => Resets++;
        public void Decode(byte[] sf, short[][] pcm)
        {
            Decodes++;
            int k = BinaryPrimitives.ReadUInt16LittleEndian(sf);
            for (int c = 0; c < pcm.Length; c++)
                for (int i = 0; i < SfSamples; i++) pcm[c][i] = (short)((c == 0 ? 1 : -1) * (k * SfSamples + i));
        }
    }

    static byte[] BuildAt9(int channels, int totalSamples, int delay, int blocks, int smplStart = -1, int smplEnd = -1, byte[]? guid = null)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(0); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(0x34);
        w.Write((ushort)0xFFFE); w.Write((ushort)channels); w.Write(48000); w.Write(48000 * SfBytes / SfSamples);
        w.Write((ushort)SfBytes); w.Write((ushort)0); w.Write((ushort)0x22); w.Write((ushort)SfSamples);
        w.Write(channels == 2 ? 3 : 4); w.Write(guid ?? At9Container.Atrac9Guid); w.Write(1); w.Write(Config(channels)); w.Write(0);
        w.Write("fact"u8); w.Write(12); w.Write(totalSamples); w.Write(delay); w.Write(delay);
        if (smplStart >= 0)
        {
            w.Write("smpl"u8); w.Write(0x3C);
            for (int i = 0; i < 7; i++) w.Write(0);
            w.Write(1); w.Write(0);                                   // one loop, no sampler data
            w.Write(0); w.Write(0); w.Write(smplStart); w.Write(smplEnd); w.Write(0); w.Write(0);
        }
        w.Write("data"u8); w.Write(blocks * SfBytes);
        for (int k = 0; k < blocks; k++) { var sf = new byte[SfBytes]; BinaryPrimitives.WriteUInt16LittleEndian(sf, (ushort)k); w.Write(sf); }
        w.Flush();
        var b = ms.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), b.Length - 8);
        return b;
    }

    uint Upload(byte[] data, int bufferSize, int readSize)
    {
        uint p = fx.Memory.Alloc((uint)bufferSize, "at9");
        data.AsSpan(0, readSize).CopyTo(GuestMemory.Span(p, readSize));
        return p;
    }

    Atrac NewModule(uint totalCh = 4)
    {
        var a = new Atrac { DecoderFactory = _ => new FakeDecoder() };
        uint g = fx.Memory.Alloc(0x100, "atracgrp");
        GuestMemory.W32(g, 0xC); GuestMemory.W32(g + 4, 16); GuestMemory.W32(g + 8, totalCh);
        Assert.Equal(0, Call(a, "sceAtracCreateDecoderGroup", 0x2003u, g, fx.Memory.Alloc(0x1000, "atracwork"), 0));
        return a;
    }

    /// <summary>Decodes until ALL_DATA_WAS_DECODED, returning channel 0 of everything output.</summary>
    List<int> DecodeAll(Atrac a, int h, Func<bool>? beforeEach = null, int max = 10000)
    {
        uint outBuf = fx.Memory.Alloc(0x4000, "atracpcm"), outN = fx.Memory.Alloc(0x10, "atracn");
        var got = new List<int>();
        for (int guard = 0; guard < max; guard++)
        {
            beforeEach?.Invoke();
            int r = Call(a, "sceAtracDecode", h, outBuf, P<uint>(outN), P<uint>(outN + 4));
            if (r == Atrac.ERR_INVALID_HANDLE) throw new InvalidOperationException();
            if (r == unchecked((int)0x80630011)) break;
            if (r == AtracStream.ERR_DATA_SHORTAGE_IN_BUFFER && beforeEach != null) continue;
            Assert.Equal(0, r);
            uint n = GuestMemory.R32(outN);
            for (int i = 0; i < n; i++) got.Add(GuestMemory.Read<short>(outBuf + (uint)(i * 4)));
        }
        return got;
    }


    [Fact]
    public void ContainerParsesFieldsAndLoop()
    {
        var f = BuildAt9(2, 5000, 300, 6, smplStart: 1300, smplEnd: 3299);
        Assert.Equal(At9ParseResult.Ok, At9Container.Parse(f, out var info));
        Assert.Equal(2, info.Channels);
        Assert.Equal(48000, info.SampleRate);
        Assert.Equal(SfBytes, info.BlockAlign);
        Assert.Equal(SfSamples, info.SamplesPerBlock);
        Assert.Equal(Config(2), info.Config);
        Assert.Equal(5000, info.TotalSamples);
        Assert.Equal(300, info.EncoderDelay);
        Assert.Equal(1000, info.LoopStart);   // smpl includes the delay
        Assert.Equal(2999, info.LoopEnd);
        Assert.Equal((uint)(12 + 8 + 0x34 + 8 + 12 + 8 + 0x3C + 8), info.DataOffset);
        Assert.Equal((uint)(6 * SfBytes), info.DataSize);
        Assert.Equal((uint)f.Length, info.FileSize);

        Assert.Equal(At9ParseResult.Ok, At9Container.Parse(BuildAt9(1, 100, 0, 1), out var mono));
        Assert.False(mono.HasLoop);
        Assert.Equal(-1, mono.LoopStart);

        Assert.Equal(At9ParseResult.TooSmall, At9Container.Parse(f.AsSpan(0, 40), out _));
        Assert.Equal(At9ParseResult.TooSmall, At9Container.Parse(f.AsSpan(0, (int)info.DataOffset - 4), out _));
        Assert.Equal(At9ParseResult.Ok, At9Container.Parse(f.AsSpan(0, (int)info.DataOffset), out _));
        var bad = (byte[])f.Clone(); bad[0] = (byte)'X';
        Assert.Equal(At9ParseResult.Invalid, At9Container.Parse(bad, out _));
        Assert.Equal(At9ParseResult.Unsupported, At9Container.Parse(BuildAt9(2, 100, 0, 1, guid: new byte[16]), out _));
    }

    [Fact]
    public void DecoderGroupLifecycle()
    {
        var a = new Atrac { DecoderFactory = _ => new FakeDecoder() };
        uint g = fx.Memory.Alloc(0x100, "grp"), work = fx.Memory.Alloc(0x1000, "work");
        GuestMemory.W32(g, 0xC); GuestMemory.W32(g + 4, 16); GuestMemory.W32(g + 8, 3);
        Assert.Equal(3 << 10, Call(a, "sceAtracQueryDecoderGroupMemSize", 0x2003u, g));
        Assert.Equal(Atrac.ERR_INVALID_TYPE, Call(a, "sceAtracQueryDecoderGroupMemSize", 0x1003u, g));
        Assert.Equal(Atrac.ERR_INVALID_POINTER, Call(a, "sceAtracQueryDecoderGroupMemSize", 0x2003u, 0u));
        GuestMemory.W32(g + 8, 17);
        Assert.Equal(Atrac.ERR_INVALID_TOTAL_CH, Call(a, "sceAtracQueryDecoderGroupMemSize", 0x2003u, g));
        GuestMemory.W32(g + 8, 3);
        GuestMemory.W32(g + 4, 24);
        Assert.Equal(Atrac.ERR_INVALID_WORD_LENGTH, Call(a, "sceAtracCreateDecoderGroup", 0x2003u, g, work, 0));
        GuestMemory.W32(g + 4, 16);
        Assert.Equal(Atrac.ERR_INVALID_ALIGNMENT, Call(a, "sceAtracCreateDecoderGroup", 0x2003u, g, work + 4, 0));
        Assert.Equal(Atrac.ERR_NOT_CREATED, Call(a, "sceAtracDeleteDecoderGroup", 0x2003u, 0));
        Assert.Equal(0, Call(a, "sceAtracCreateDecoderGroup", 0x2003u, g, work, 0));
        Assert.Equal(Atrac.ERR_ALREADY_CREATED, Call(a, "sceAtracCreateDecoderGroup", 0x2003u, g, work, 0));

        var file = BuildAt9(2, 3000, 0, 3);
        uint buf = Upload(file, 0x1000 * 4, file.Length);
        int h = Call(a, "sceAtracSetDataAndAcquireHandle", buf, (uint)file.Length, 0x4000u);
        Assert.Equal(0x2003, h >> 16);
        uint created = fx.Memory.Alloc(0x100, "c"), avail = created + 0x10;
        GuestMemory.W32(created, 0xC); GuestMemory.W32(avail, 0xC);
        Assert.Equal(0, Call(a, "sceAtracGetDecoderGroupInfo", 0x2003u, created, avail));
        Assert.Equal(3u, GuestMemory.R32(created + 8));
        Assert.Equal(1u, GuestMemory.R32(avail + 8));
        Assert.Equal(16u, GuestMemory.R32(avail + 4));
        // a second stereo stream does not fit in the remaining channel
        Assert.Equal(Atrac.ERR_SHORTAGE_OF_CH, Call(a, "sceAtracSetDataAndAcquireHandle", buf, (uint)file.Length, 0x4000u));
        Assert.Equal(Atrac.ERR_REMAIN_VALID_HANDLE, Call(a, "sceAtracDeleteDecoderGroup", 0x2003u, 0));
        Assert.Equal(0, Call(a, "sceAtracReleaseHandle", h));
        Assert.Equal(Atrac.ERR_INVALID_HANDLE, Call(a, "sceAtracReleaseHandle", h));
        Assert.Equal(Atrac.ERR_INVALID_HANDLE, Call(a, "sceAtracReleaseHandle", 5));
        Assert.Equal(0, Call(a, "sceAtracDeleteDecoderGroup", 0x2003u, 0));
    }

    [Fact]
    public void AcquireErrors()
    {
        var a = NewModule();
        var file = BuildAt9(2, 3000, 0, 3);
        uint buf = Upload(file, 0x4000, file.Length);
        Assert.Equal(Atrac.ERR_INVALID_POINTER, Call(a, "sceAtracSetDataAndAcquireHandle", 0u, 0x100u, 0x4000u));
        Assert.Equal(Atrac.ERR_INVALID_ALIGNMENT, Call(a, "sceAtracSetDataAndAcquireHandle", buf + 0x10, 0x100u, 0x4000u));
        Assert.Equal(Atrac.ERR_INVALID_ALIGNMENT, Call(a, "sceAtracSetDataAndAcquireHandle", buf, 0x100u, 0x4010u));
        Assert.Equal(Atrac.ERR_READ_SIZE_OVER_BUFFER, Call(a, "sceAtracSetDataAndAcquireHandle", buf, 0x4100u, 0x4000u));
        Assert.Equal(Atrac.ERR_READ_SIZE_IS_TOO_SMALL, Call(a, "sceAtracSetDataAndAcquireHandle", buf, 0x40u, 0x4000u));
        GuestMemory.W32(buf + 8, 0x12345678);
        Assert.Equal(Atrac.ERR_INVALID_DATA, Call(a, "sceAtracSetDataAndAcquireHandle", buf, (uint)file.Length, 0x4000u));
        // streaming with a buffer that cannot hold the header plus one superframe
        var big = BuildAt9(2, 20000, 0, 20);
        uint b2 = Upload(big, 0x400, 0x400);
        Assert.Equal(Atrac.ERR_MAIN_BUFFER_SIZE_IS_TOO_SMALL, Call(a, "sceAtracSetDataAndAcquireHandle", b2, 0x400u, 0x400u));
    }

    [Fact]
    public void AllOnMemoryDecodeAndEnd()
    {
        var a = NewModule();
        var file = BuildAt9(2, 3000, 100, 4);
        uint buf = Upload(file, 0x2000, file.Length);
        int h = Call(a, "sceAtracSetDataAndAcquireHandle", buf, (uint)file.Length, 0x2000u);
        Assert.True(h > 0);
        uint p = fx.Memory.Alloc(0x100, "q");
        Assert.Equal(0, Call(a, "sceAtracGetOutputSamples", h, P<uint>(p)));
        Assert.Equal(256u, GuestMemory.R32(p));
        Assert.Equal(0, Call(a, "sceAtracGetDecoderStatus", h, P<uint>(p)));
        Assert.Equal(2u | 4u, GuestMemory.R32(p));
        Assert.Equal(0, Call(a, "sceAtracGetRemainSamples", h, P<long>(p)));
        Assert.Equal(3000L, GuestMemory.Read<long>(p));

        GuestMemory.W32(p, 0x34);
        Assert.Equal(0, Call(a, "sceAtracGetContentInfo", h, p));
        Assert.Equal(2u, GuestMemory.R32(p + 8));
        Assert.Equal(48000u, GuestMemory.R32(p + 0xC));
        Assert.Equal(2999u, GuestMemory.R32(p + 0x10));
        Assert.Equal(uint.MaxValue, GuestMemory.R32(p + 0x14));
        Assert.Equal(1024u, GuestMemory.R32(p + 0x20));
        Assert.Equal(1024u, GuestMemory.R32(p + 0x24));
        Assert.Equal(256u, GuestMemory.R32(p + 0x28));
        Assert.Equal(384u, GuestMemory.R32(p + 0x1C)); // 1024 B * 8 * 48000 / 1024 / 1000
        GuestMemory.W32(p, 0x30);
        Assert.Equal(Atrac.ERR_INVALID_SIZE, Call(a, "sceAtracGetContentInfo", h, p));

        var got = DecodeAll(a, h);
        Assert.Equal(Enumerable.Range(100, 3000).ToList(), got);
        Assert.Equal(0, Call(a, "sceAtracGetDecoderStatus", h, P<uint>(p)));
        Assert.Equal(1u, GuestMemory.R32(p) & 1);
        uint o = fx.Memory.Alloc(0x1000, "o");
        Assert.Equal(unchecked((int)0x80630011), Call(a, "sceAtracDecode", h, o, P<uint>(p), P<uint>(p + 4)));
        Assert.Equal(Atrac.ERR_INVALID_ALIGNMENT, Call(a, "sceAtracDecode", h, o + 1, P<uint>(p), P<uint>(p + 4)));
        Assert.Equal(Atrac.ERR_INVALID_POINTER, Call(a, "sceAtracDecode", h, o, P<uint>(0), P<uint>(p + 4)));

        // seek back: output restarts exactly at the requested sample
        Assert.Equal(0, Call(a, "sceAtracResetNextOutputPosition", h, 1500u));
        Assert.Equal(0, Call(a, "sceAtracGetNextOutputPosition", h, P<uint>(p)));
        Assert.Equal(1500u, GuestMemory.R32(p));
        Assert.Equal(Enumerable.Range(1600, 1500).ToList(), DecodeAll(a, h));
        Assert.Equal(unchecked((int)0x80630015), Call(a, "sceAtracResetNextOutputPosition", h, 3000u));
    }

    [Fact]
    public void OutputSamplesValidation()
    {
        var a = NewModule();
        var file = BuildAt9(2, 3000, 0, 3);
        uint buf = Upload(file, 0x1000, file.Length);
        int h = Call(a, "sceAtracSetDataAndAcquireHandle", buf, (uint)file.Length, 0x1000u);
        Assert.Equal(0, Call(a, "sceAtracSetOutputSamples", h, 1024u));
        Assert.Equal(0, Call(a, "sceAtracSetOutputSamples", h, 2048u));
        Assert.Equal(Atrac.ERR_INVALID_MAX_OUTPUT_SAMPLES, Call(a, "sceAtracSetOutputSamples", h, 0u));
        Assert.Equal(Atrac.ERR_INVALID_MAX_OUTPUT_SAMPLES, Call(a, "sceAtracSetOutputSamples", h, 300u));
        Assert.Equal(Atrac.ERR_INVALID_MAX_OUTPUT_SAMPLES, Call(a, "sceAtracSetOutputSamples", h, 2304u));
        Assert.Equal(Atrac.ERR_INVALID_HANDLE, Call(a, "sceAtracSetOutputSamples", h + 1, 256u));
        uint p = fx.Memory.Alloc(0x10, "p");
        Assert.Equal(0, Call(a, "sceAtracGetOutputSamples", h, P<uint>(p)));
        Assert.Equal(2048u, GuestMemory.R32(p));
        Assert.Equal(Atrac.ERR_INVALID_POINTER, Call(a, "sceAtracGetOutputSamples", h, P<uint>(0)));
        // no loop in the file
        Assert.Equal(AtracStream.ERR_INVALID_LOOP_STATUS, Call(a, "sceAtracSetLoopNum", h, 1));
        Assert.Equal(Atrac.ERR_INVALID_LOOP_NUM, Call(a, "sceAtracSetLoopNum", h, -2));
        Assert.Equal(0, Call(a, "sceAtracIsSubBufferNeeded", h));
        Assert.Equal(Atrac.ERR_NO_NEED_SUB_BUFFER, Call(a, "sceAtracGetSubBufferInfo", h, p, p + 4, p + 8));
    }

    static List<int> Expected(int delay, int total, int loopStart, int loopEnd, int loops)
    {
        var e = new List<int>();
        e.AddRange(Enumerable.Range(0, loopEnd + 1));
        for (int i = 0; i < loops; i++) e.AddRange(Enumerable.Range(loopStart, loopEnd - loopStart + 1));
        e.AddRange(Enumerable.Range(loopEnd + 1, total - loopEnd - 1));
        return e.Select(x => x + delay).ToList();
    }

    [Fact]
    public void LoopInMemoryRepeatsExactly()
    {
        var a = NewModule();
        // output loop [1000, 2999] (smpl stores it with the 200-sample delay)
        var file = BuildAt9(2, 5000, 200, 6, smplStart: 1200, smplEnd: 3199);
        uint buf = Upload(file, 0x2000, file.Length);
        int h = Call(a, "sceAtracSetDataAndAcquireHandle", buf, (uint)file.Length, 0x2000u);
        uint p = fx.Memory.Alloc(0x40, "p");
        GuestMemory.W32(p, 0x34);
        Assert.Equal(0, Call(a, "sceAtracGetContentInfo", h, p));
        Assert.Equal(1000u, GuestMemory.R32(p + 0x14));
        Assert.Equal(2999u, GuestMemory.R32(p + 0x18));
        Assert.Equal(0, Call(a, "sceAtracSetLoopNum", h, 2));
        Assert.Equal(0, Call(a, "sceAtracGetRemainSamples", h, P<long>(p)));
        Assert.Equal(5000L + 2 * 2000, GuestMemory.Read<long>(p));
        Assert.Equal(0, Call(a, "sceAtracGetLoopInfo", h, P<int>(p), P<uint>(p + 4)));
        Assert.Equal(2u, GuestMemory.R32(p));
        Assert.Equal(1u, GuestMemory.R32(p + 4));
        Assert.Equal(Expected(200, 5000, 1000, 2999, 2), DecodeAll(a, h));
        Assert.Equal(0, Call(a, "sceAtracGetLoopInfo", h, P<int>(p), P<uint>(p + 4)));
        Assert.Equal(0u, GuestMemory.R32(p));
        Assert.Equal(0u, GuestMemory.R32(p + 4));

        Assert.Equal(0, Call(a, "sceAtracResetNextOutputPosition", h, 0u));
        Assert.Equal(0, Call(a, "sceAtracSetLoopNum", h, -1));
        Assert.Equal(0, Call(a, "sceAtracGetRemainSamples", h, P<long>(p)));
        Assert.Equal(-1L, GuestMemory.Read<long>(p));
    }

    /// <summary>Plays a file through a small ring the way a game does: GetStreamInfo, copy, AddStreamData, Decode.</summary>
    List<int> Stream(Atrac a, int h, byte[] file)
    {
        uint si = fx.Memory.Alloc(0x10, "si");
        GuestMemory.W32(si, 0x10);
        return DecodeAll(a, h, () =>
        {
            Assert.Equal(0, Call(a, "sceAtracGetStreamInfo", h, si));
            uint wp = GuestMemory.R32(si + 4), rp = GuestMemory.R32(si + 8), n = GuestMemory.R32(si + 12);
            n = Math.Min(n, 0x500); // feed in odd-sized pieces
            if (n == 0) return true;
            file.AsSpan((int)rp, (int)n).CopyTo(GuestMemory.Span(wp, (int)n));
            Assert.Equal(0, Call(a, "sceAtracAddStreamData", h, n));
            return true;
        });
    }

    [Fact]
    public void StreamingThroughRing()
    {
        var a = NewModule();
        var file = BuildAt9(2, 9500, 100, 10);
        uint buf = Upload(file, 0xC00, 0x600);
        int h = Call(a, "sceAtracSetDataAndAcquireHandle", buf, 0x600u, 0xC00u);
        Assert.True(h > 0);
        uint p = fx.Memory.Alloc(0x10, "p");
        Assert.Equal(0, Call(a, "sceAtracGetVacantSize", h, P<uint>(p)));
        Assert.Equal(0xC00u - (0x600u - (uint)file.AsSpan().IndexOf("data"u8) - 8), GuestMemory.R32(p));
        Assert.Equal(AtracStream.ERR_ADDED_DATA_IS_TOO_BIG, Call(a, "sceAtracAddStreamData", h, 0x1000u));
        Assert.Equal(Enumerable.Range(100, 9500).ToList(), Stream(a, h, file));
        Assert.Equal(0, Call(a, "sceAtracGetDecoderStatus", h, P<uint>(p)));
        Assert.Equal(1u | 4u, GuestMemory.R32(p));
    }

    [Fact]
    public void StreamingDataShortage()
    {
        var a = NewModule();
        var file = BuildAt9(2, 9500, 0, 10);
        int dataOff = file.AsSpan().IndexOf("data"u8) + 8;
        uint buf = Upload(file, 0x1000, dataOff + 100);
        int h = Call(a, "sceAtracSetDataAndAcquireHandle", buf, (uint)(dataOff + 100), 0x1000u);
        uint o = fx.Memory.Alloc(0x1000, "o"), p = fx.Memory.Alloc(0x10, "p");
        Assert.Equal(AtracStream.ERR_DATA_SHORTAGE_IN_BUFFER, Call(a, "sceAtracDecode", h, o, P<uint>(p), P<uint>(p + 4)));
        Assert.Equal(0, Call(a, "sceAtracGetOutputableSamples", h, P<long>(p)));
        Assert.Equal(0L, GuestMemory.Read<long>(p));
    }

    [Fact]
    public void StreamingWithLoopAndSeek()
    {
        var a = NewModule();
        // loop [1500, 4999] in output samples; the file is far larger than the ring
        var file = BuildAt9(2, 9000, 50, 10, smplStart: 1550, smplEnd: 5049);
        // the game reads a big first chunk, past the loop end: SetLoopNum must rewind the reader
        uint buf = Upload(file, 0x1C00, 0x1C00);
        int h = Call(a, "sceAtracSetDataAndAcquireHandle", buf, 0x1C00u, 0x1C00u);
        Assert.Equal(0, Call(a, "sceAtracSetLoopNum", h, 1));
        Assert.Equal(Expected(50, 9000, 1500, 4999, 1), Stream(a, h, file));

        // seek into the middle of the file: the ring restarts and is refilled by the game
        Assert.Equal(0, Call(a, "sceAtracResetNextOutputPosition", h, 6000u));
        Assert.Equal(Enumerable.Range(6050, 3000).ToList(), Stream(a, h, file));
    }
}
