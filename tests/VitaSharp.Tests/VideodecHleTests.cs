using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceVideodec / SceAvcdec end to end: library and decoder setup checks (SCE_AVCDEC_ERROR_* 0x8062xxxx), then a lossless
/// I_PCM H.264 stream (<see cref="VideoDecoderTests.Stream"/>) decoded through sceAvcdecDecode into the game's buffers
/// in the three output layouts: YUV420 planar (exact), YUV420 packed (V before U) and RGBA with the BT.601 conversion.
/// </summary>
[Collection("guest")]
public sealed class VideodecHleTests(GuestMemoryFixture fx) : IDisposable
{
    private const int ERR_INVALID_PARAM = unchecked((int)0x80620002), ERR_INVALID_POINTER = unchecked((int)0x80620009);
    private const uint Avc = 0x1001;
    private const int W = VideoDecoderTests.W, H = VideoDecoderTests.H;

    private readonly HleHarness _h = new(fx, "videodec");
    private Videodec V => _h.Module<Videodec>();

    public void Dispose() => _h.Dispose();

    private uint Query(uint w, uint h, uint refs)
    {
        uint q = _h.Alloc(12);
        GuestMemory.W32(q, w);
        GuestMemory.W32(q + 4, h);
        GuestMemory.W32(q + 8, refs);
        return q;
    }

    [Fact]
    public void LibraryAndDecoderArguments()
    {
        Assert.Equal(0, Call(V, "sceVideodecInitLibrary", Avc, 0u));
        Assert.Equal(ERR_INVALID_PARAM, Call(V, "sceVideodecInitLibrary", 0x1002u, 0u));
        Assert.Equal(0, Call(V, "sceVideodecInitLibraryWithUnmapMem", Avc, 0u, 0u));
        Assert.Equal(ERR_INVALID_PARAM, Call(V, "sceVideodecInitLibraryWithUnmapMem", 0u, 0u, 0u));
        uint mem = _h.Word(0);
        Assert.Equal(0, Call(V, "sceVideodecQueryMemSize", Avc, 0u, mem));
        Assert.True(GuestMemory.R32(mem) > 0);
        Assert.Equal(ERR_INVALID_POINTER, Call(V, "sceVideodecQueryMemSize", Avc, 0u, 0u));
        Assert.Equal(ERR_INVALID_PARAM, Call(V, "sceVideodecQueryMemSize", 0u, 0u, mem));

        // the decoder memory covers the reference frames, in 256 KiB units; 1920x1088 is the largest picture
        Assert.Equal(0, Call(V, "sceAvcdecQueryDecoderMemSize", Avc, Query(1280, 720, 4), mem));
        uint big = GuestMemory.R32(mem);
        Assert.Equal(0u, big % 0x40000);
        Assert.True(big >= 1280 * 720 * 3 / 2 * 4);
        Assert.Equal(0, Call(V, "sceAvcdecQueryDecoderMemSize", Avc, Query(480, 272, 1), mem));
        Assert.True(GuestMemory.R32(mem) < big);
        Assert.Equal(ERR_INVALID_PARAM, Call(V, "sceAvcdecQueryDecoderMemSize", Avc, Query(1921, 1080, 1), mem));
        Assert.Equal(ERR_INVALID_PARAM, Call(V, "sceAvcdecQueryDecoderMemSize", Avc, Query(1920, 1089, 1), mem));
        Assert.Equal(ERR_INVALID_PARAM, Call(V, "sceAvcdecQueryDecoderMemSize", Avc, Query(640, 480, 17), mem));
        Assert.Equal(ERR_INVALID_POINTER, Call(V, "sceAvcdecQueryDecoderMemSize", Avc, 0u, mem));
        Assert.Equal(ERR_INVALID_PARAM, Call(V, "sceAvcdecQueryDecoderMemSize", 0u, Query(64, 64, 1), mem));

        uint ctrl = _h.Alloc(12);
        Assert.Equal(ERR_INVALID_POINTER, Call(V, "sceAvcdecCreateDecoder", Avc, 0u, Query(64, 64, 1)));
        Assert.Equal(ERR_INVALID_PARAM, Call(V, "sceAvcdecCreateDecoder", Avc, ctrl, Query(4096, 64, 1)));
        Assert.Equal(ERR_INVALID_PARAM, Call(V, "sceAvcdecCreateDecoder", 0u, ctrl, Query(64, 64, 1)));
        Assert.Equal(0, Call(V, "sceAvcdecCreateDecoder", Avc, ctrl, Query(64, 64, 1)));
        Assert.NotEqual(0u, GuestMemory.R32(ctrl));
        Assert.Equal(64 * 64 * 3 / 2, Call(V, "sceAvcdecDecodeAvailableSize", ctrl));
        Assert.Equal(ERR_INVALID_POINTER, Call(V, "sceAvcdecDecode", ctrl, 0u, 0u));
        Assert.Equal(0, Call(V, "sceAvcdecDecodeFlush", ctrl));
        Assert.Equal(0, Call(V, "sceAvcdecDecodeStop", ctrl, 0u));
        Assert.Equal(0, Call(V, "sceAvcdecDeleteDecoder", ctrl));
        // a deleted (or never created) handle is INVALID_PARAM, a NULL ctrl INVALID_POINTER
        foreach (string? fn in new[] { "sceAvcdecDecodeFlush", "sceAvcdecDecodeAvailableSize", "sceAvcdecDeleteDecoder" })
        {
            Assert.Equal(ERR_INVALID_PARAM, Call(V, fn, ctrl));
            Assert.Equal(ERR_INVALID_POINTER, Call(V, fn, 0u));
        }
        Assert.Equal(ERR_INVALID_PARAM, Call(V, "sceAvcdecDecodeStop", ctrl, 0u));
        Assert.Equal(ERR_INVALID_PARAM, Call(V, "sceAvcdecDecode", ctrl, _h.Alloc(24), _h.Alloc(12)));
        Assert.Equal(0, Call(V, "sceVideodecTermLibrary", Avc));
    }

    /// <summary>Decodes the stream with one picture per call and returns the bytes written per picture by pts.</summary>
    private Dictionary<long, byte[]> Decode(uint pixelType, uint pitch, uint bufH, uint bytes, byte alpha = 0xFF, byte csc = 0)
    {
        uint ctrl = _h.Alloc(12);
        Assert.Equal(0, Call(V, "sceAvcdecCreateDecoder", Avc, ctrl, Query(W, H, 1)));
        uint dst = _h.Alloc(bytes), picture = _h.Alloc(0x64), list = _h.Alloc(4), array = _h.Alloc(12), au = _h.Alloc(24);
        GuestMemory.W32(picture, 0x64);
        GuestMemory.W32(picture + 4, pixelType);
        GuestMemory.W32(picture + 8, pitch);
        GuestMemory.W32(picture + 12, (uint)W);
        GuestMemory.W32(picture + 16, bufH);
        GuestMemory.Write(picture + 4 + 0x28, alpha);
        GuestMemory.Write(picture + 4 + 0x29, csc);
        GuestMemory.W32(picture + 4 + 0x38, dst);
        GuestMemory.W32(list, picture);
        GuestMemory.W32(array + 4, 1);
        GuestMemory.W32(array + 8, list);
        var got = new Dictionary<long, byte[]>();
        void Collect()
        {
            if (GuestMemory.R32(array) == 0)
            {
                return;
            }

            Assert.Equal(1u, GuestMemory.R32(array));
            Assert.Equal((uint)W, GuestMemory.R32(picture + 4 + 16)); // horizontalSize
            Assert.Equal((uint)H, GuestMemory.R32(picture + 4 + 20)); // verticalSize
            long pts = ((long)GuestMemory.R32(picture + 0x5C) << 32) | GuestMemory.R32(picture + 0x60);
            got[pts] = GuestMemory.ReadSpan(dst, (int)bytes).ToArray();
        }
        List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> stream = VideoDecoderTests.Stream(3);
        uint es = _h.Alloc(64 * 1024);
        for (int i = 0; i < stream.Count; i++)
        {
            stream[i].Au.CopyTo(GuestMemory.Span(es, stream[i].Au.Length));
            GuestMemory.W32(au, 0);
            GuestMemory.W32(au + 4, (uint)(i * 3000));
            GuestMemory.W32(au + 16, es);
            GuestMemory.W32(au + 20, (uint)stream[i].Au.Length);
            Assert.Equal(0, Call(V, "sceAvcdecDecode", ctrl, au, array));
            Collect();
        }
        for (int k = 0; k < 4; k++)
        {
            Assert.Equal(0, Call(V, "sceAvcdecDecodeStop", ctrl, array));
            Collect();
        }
        Assert.Equal(0, Call(V, "sceAvcdecDeleteDecoder", ctrl));
        return got;
    }

    [FFmpegFact]
    public void DecodesIntoPlanarYuv420()
    {
        const uint Pitch = 80; // wider than the picture: the planes keep the pitch
        Dictionary<long, byte[]> got = Decode(0x10, Pitch, H, Pitch * H * 3 / 2);
        List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> stream = VideoDecoderTests.Stream(3);
        Assert.Equal([0L, 3000L, 6000L], got.Keys.Order().ToArray());
        for (int i = 0; i < 3; i++)
        {
            byte[] b = got[i * 3000];
            for (int y = 0; y < H; y++)
            {
                Assert.Equal(stream[i].Y.AsSpan(y * W, W).ToArray(), b.AsSpan(y * (int)Pitch, W).ToArray());
            }

            int uOff = (int)(Pitch * H), vOff = uOff + (int)(Pitch / 2 * (H / 2));
            for (int y = 0; y < H / 2; y++)
            {
                Assert.Equal(stream[i].U.AsSpan(y * W / 2, W / 2).ToArray(), b.AsSpan(uOff + y * (int)Pitch / 2, W / 2).ToArray());
                Assert.Equal(stream[i].V.AsSpan(y * W / 2, W / 2).ToArray(), b.AsSpan(vOff + y * (int)Pitch / 2, W / 2).ToArray());
            }
        }
    }

    [FFmpegFact]
    public void DecodesIntoPackedYvu420WithMacroblockPadding()
    {
        // a buffer taller than the picture (80 rows for 64): only up to the next macroblock row is padded, so here none
        const uint BufferHeight = 72;
        Dictionary<long, byte[]> got = Decode(0x20, W, BufferHeight, W * BufferHeight * 3 / 2);
        (byte[] Au, byte[] Y, byte[] U, byte[] V) s = VideoDecoderTests.Stream(1)[0];
        byte[] b = got[0];
        Assert.Equal(s.Y.AsSpan(0, W).ToArray(), b.AsSpan(0, W).ToArray());
        int uv = W * (int)BufferHeight;
        // V before U in each pair (YVU420P2)
        for (int x = 0; x < W / 2; x++)
        {
            Assert.Equal(s.V[x], b[uv + 2 * x]);
            Assert.Equal(s.U[x], b[uv + 2 * x + 1]);
        }
    }

    [FFmpegFact]
    public void DecodesIntoRgbaAndBgra()
    {
        (byte[] Au, byte[] Y, byte[] U, byte[] V) s = VideoDecoderTests.Stream(1)[0];
        static (int R, int G, int B) Bt601(byte y, byte u, byte v)
        {
            double Y = (y - 16) * 255.0 / 219, cb = (u - 128) * 255.0 / 224, cr = (v - 128) * 255.0 / 224;
            return ((int)Math.Clamp(Y + 1.402 * cr, 0, 255), (int)Math.Clamp(Y - 0.344136 * cb - 0.714136 * cr, 0, 255), (int)Math.Clamp(Y + 1.772 * cb, 0, 255));
        }
        byte[] rgba = Decode(0x0, W, H, W * H * 4, alpha: 0x80, csc: 1)[0];
        byte[] bgra = Decode(0x8, W, H, W * H * 4, alpha: 0x40, csc: 1)[0];
        foreach ((int x, int y) in new[] { (0, 0), (5, 3), (63, 63), (31, 40) })
        {
            (int r, int g, int b) = Bt601(s.Y[y * W + x], s.U[(y / 2) * (W / 2) + x / 2], s.V[(y / 2) * (W / 2) + x / 2]);
            int o = (y * W + x) * 4;
            Assert.InRange(rgba[o], r - 2, r + 2);
            Assert.InRange(rgba[o + 1], g - 2, g + 2);
            Assert.InRange(rgba[o + 2], b - 2, b + 2);
            Assert.Equal(0x80, rgba[o + 3]);
            Assert.Equal(rgba[o], bgra[o + 2]);
            Assert.Equal(rgba[o + 2], bgra[o]);
            Assert.Equal(0x40, bgra[o + 3]);
        }
    }

    [Fact]
    public void ParameterSetsAndIdrDetection()
    {
        List<(byte[] Au, byte[] Y, byte[] U, byte[] V)> s = VideoDecoderTests.Stream(2);
        Assert.True(Videodec.StartsIdr(s[0].Au));
        Assert.False(Videodec.StartsIdr(s[1].Au));
        byte[]? ps = Videodec.ParameterSets(s[0].Au);
        Assert.NotNull(ps);
        // SPS (type 7) then PPS (type 8), each behind a 4-byte start code
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, ps!.Take(4).ToArray());
        Assert.Equal(7, ps[4] & 0x1F);
        Assert.Null(Videodec.ParameterSets(s[1].Au));
    }

    [Fact]
    public void CodecEngineUnmapMemBlockAllocator()
    {
        CodecEngine ce = _h.Module<CodecEngine>();
        const int ERR = unchecked((int)0x80600009);
        Assert.Equal(ERR, Call(ce, "sceCodecEngineOpenUnmapMemBlock", 0u, 0x1000u));
        uint baseAddr = 0x90000000; // never touched: the allocator only does the bookkeeping
        int uid = Call(ce, "sceCodecEngineOpenUnmapMemBlock", baseAddr, 0x100000u);
        Assert.True(uid > 0);
        // 256-byte granules; a 1 MiB alignment that cannot be met inside the block is relaxed (Gravity Rush)
        uint a = (uint)Invoke(ce, "sceCodecEngineAllocMemoryFromUnmapMemBlock", uid, 0x40000u, 0u)!;
        Assert.Equal(baseAddr, a);
        uint b = (uint)Invoke(ce, "sceCodecEngineAllocMemoryFromUnmapMemBlock", uid, 0x80000u, 0x100000u)!;
        Assert.NotEqual(0u, b);
        Assert.True(b >= a + 0x40000 && b + 0x80000 <= baseAddr + 0x100000);
        Assert.Equal(0u, (uint)Invoke(ce, "sceCodecEngineAllocMemoryFromUnmapMemBlock", uid, 0x200000u, 0u)!); // larger than the block
        Assert.Equal(0u, (uint)Invoke(ce, "sceCodecEngineAllocMemoryFromUnmapMemBlock", uid, 0u, 0u)!);
        Assert.Equal(0u, (uint)Invoke(ce, "sceCodecEngineAllocMemoryFromUnmapMemBlock", 0x7FFF, 0x100u, 0u)!);
        Assert.Equal(0, Call(ce, "sceCodecEngineFreeMemoryFromUnmapMemBlock", uid, a));
        Assert.Equal(ERR, Call(ce, "sceCodecEngineFreeMemoryFromUnmapMemBlock", uid, a)); // twice
        Assert.Equal(0, Call(ce, "sceCodecEngineFreeMemoryFromUnmapMemBlock", uid, b));
        // everything merged back: the whole block fits again
        Assert.Equal(baseAddr, (uint)Invoke(ce, "sceCodecEngineAllocMemoryFromUnmapMemBlock", uid, 0x100000u, 0u)!);
        Assert.Equal(0, Call(ce, "sceCodecEngineUser_9A5E7B2F"));
        Assert.Equal(0, Call(ce, "sceCodecEngineUser_9AD7E66F"));
        Assert.Equal(0, Call(ce, "sceCodecEngineCloseUnmapMemBlock", uid));
        Assert.Equal(ERR, Call(ce, "sceCodecEngineCloseUnmapMemBlock", uid));
    }
}

/// <summary>[Fact] skipped when the FFmpeg libraries are not there (macOS uses the system FFmpeg, which a machine may
/// not have): the pictures are decoded by FFmpeg.</summary>
public sealed class FFmpegFactAttribute : FactAttribute
{
    public FFmpegFactAttribute()
    {
        if (!VitaSharp.Core.Media.FFmpegLoader.Available)
        {
            Skip = "FFmpeg not available";
        }
    }
}
