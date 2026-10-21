using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>Managed JPEG encoder and the SceJpegEncUser / SceJpegEncArm HLE.</summary>
[Collection("guest")]
public sealed class JpegEncHleTests(GuestMemoryFixture fx)
{
    static int Call<T>(T module, string name, params object[] args)
    {
        var r = typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args)!;
        return r is uint u ? (int)u : (int)r;
    }

    static byte[] Encode(JpegHleTests.Planes p, int ratio, bool tables = true, int restart = 0)
    {
        var (lq, cq) = JpegEncoder.TablesForRatio(ratio);
        return JpegEncoder.Encode(p.Y, p.W, p.Cb ?? [], p.Cr ?? [], p.CW, p.W, p.H, p.HDiv, p.VDiv, lq, cq, tables, restart);
    }

    static double PlanePsnr(JpegImage img, int plane, byte[] src, int w, int h) =>
        JpegHleTests.Psnr(img.Planes[plane].Data, img.Planes[plane].Stride, src, w, w, h);

    static bool HasMarker(byte[] f, int m) => Enumerable.Range(0, f.Length - 1).Any(i => f[i] == 0xFF && f[i + 1] == m);

    // ------------------------------------------------------------------ codec

    [Theory]
    [InlineData(2, 2, false)]
    [InlineData(2, 1, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 1, true)]
    public void EncoderDecoderRoundTrip(int hdiv, int vdiv, bool gray)
    {
        var src = JpegHleTests.Synthetic(157, 83, hdiv, vdiv, gray, seed: 7);
        foreach (var (ratio, minPsnr) in new[] { (0, 40.0), (64, 33.0), (255, 24.0) })
        {
            var img = JpegDecoder.Decode(Encode(src, ratio));
            Assert.Equal((157, 83), (img.Info.Width, img.Info.Height));
            Assert.Equal((gray ? 1 : hdiv, gray ? 1 : vdiv), (img.Info.HDiv, img.Info.VDiv));
            Assert.True(PlanePsnr(img, 0, src.Y, src.W, src.H) > minPsnr, $"luma ratio {ratio}");
            if (!gray)
            {
                Assert.True(PlanePsnr(img, 1, src.Cb!, src.CW, src.CH) > minPsnr, $"cb ratio {ratio}");
                Assert.True(PlanePsnr(img, 2, src.Cr!, src.CW, src.CH) > minPsnr, $"cr ratio {ratio}");
            }
        }
    }

    [Fact]
    public void CompressionRatioShrinksOutput()
    {
        var src = JpegHleTests.Synthetic(128, 96, 2, 2);
        int s0 = Encode(src, 0).Length, s64 = Encode(src, 64).Length, s255 = Encode(src, 255).Length;
        Assert.True(s0 > s64 && s64 > s255, $"{s0} {s64} {s255}");
        var (lq, cq) = JpegEncoder.TablesForRatio(64); // scale 4/8: the "quality 75" tables
        Assert.Equal(8, lq[0]);
        Assert.Equal(9, cq[0]);
        Assert.True(JpegEncoder.TablesForRatio(0).Luma.Min() >= 1); // never 0
    }

    [Fact]
    public void MjpegFramesOmitTablesAndDecodeWithDefaults()
    {
        var src = JpegHleTests.Synthetic(64, 48, 2, 1);
        var jpeg = Encode(src, 64);
        var mjpeg = Encode(src, 64, tables: false);
        Assert.True(HasMarker(jpeg, 0xC4) && HasMarker(jpeg, 0xE0));
        Assert.False(HasMarker(mjpeg, 0xC4) || HasMarker(mjpeg, 0xE0));
        var a = JpegDecoder.Decode(jpeg);
        var b = JpegDecoder.Decode(mjpeg);
        for (int p = 0; p < 3; p++) Assert.Equal(a.Planes[p].Data, b.Planes[p].Data);
    }

    [Fact]
    public void RestartMarkersRoundTrip()
    {
        var src = JpegHleTests.Synthetic(100, 70, 2, 2);
        var plain = JpegDecoder.Decode(Encode(src, 32));
        var rst = Encode(src, 32, restart: 2);
        Assert.True(HasMarker(rst, 0xDD) && HasMarker(rst, 0xD0) && HasMarker(rst, 0xD7));
        var withRst = JpegDecoder.Decode(rst);
        for (int p = 0; p < 3; p++) Assert.Equal(plain.Planes[p].Data, withRst.Planes[p].Data);
    }

    // ------------------------------------------------------------------ HLE

    uint Alloc(int size, string name) => fx.Memory.Alloc((uint)size, name);

    uint PutPlanes(JpegHleTests.Planes p, string name)
    {
        uint a = Alloc(p.W * p.H + 2 * p.CW * p.CH, name);
        p.Y.CopyTo(GuestMemory.Span(a, p.Y.Length));
        p.Cb!.CopyTo(GuestMemory.Span(a + (uint)p.Y.Length, p.Cb!.Length));
        p.Cr!.CopyTo(GuestMemory.Span(a + (uint)(p.Y.Length + p.Cb!.Length), p.Cr!.Length));
        return a;
    }

    [Fact]
    public void EncoderInitValidation()
    {
        var e = new JpegEnc();
        Assert.Equal(0x20, Call(e, "sceJpegEncoderGetContextSize"));
        uint ctx = Alloc(0x20, "enc-ctx"), outp = Alloc(0x1000, "enc-out");
        Assert.Equal(JpegEnc.ERR_INVALID_POINTER, Call(e, "sceJpegEncoderInit", 0u, 64, 64, 8, outp, 0x1000u));
        Assert.Equal(JpegEnc.ERR_INVALID_POINTER, Call(e, "sceJpegEncoderInit", ctx, 64, 64, 8, 0u, 0x1000u));
        Assert.Equal(JpegEnc.ERR_INVALID_PIXELFORMAT, Call(e, "sceJpegEncoderInit", ctx, 64, 64, 7, outp, 0x1000u));
        Assert.Equal(JpegEnc.ERR_INVALID_PIXELFORMAT, Call(e, "sceJpegEncoderInit", ctx, 64, 64, 0x28, outp, 0x1000u));
        Assert.Equal(JpegEnc.ERR_IMAGE_SIZE, Call(e, "sceJpegEncoderInit", ctx, 0, 64, 8, outp, 0x1000u));
        Assert.Equal(0, Call(e, "sceJpegEncoderInit", ctx, 64, 64, 8 | 16, outp, 0x1000u));

        Assert.Equal(JpegEnc.ERR_INVALID_COMPRATIO, Call(e, "sceJpegEncoderSetCompressionRatio", ctx, 256));
        Assert.Equal(JpegEnc.ERR_INVALID_COMPRATIO, Call(e, "sceJpegEncoderSetCompressionRatio", ctx, -1));
        Assert.Equal(0, Call(e, "sceJpegEncoderSetCompressionRatio", ctx, 0));
        Assert.Equal(JpegEnc.ERR_INVALID_HEADER_MODE, Call(e, "sceJpegEncoderSetHeaderMode", ctx, 2));
        Assert.Equal(0, Call(e, "sceJpegEncoderSetHeaderMode", ctx, 1));
        Assert.Equal(JpegEnc.ERR_IMAGE_SIZE, Call(e, "sceJpegEncoderSetValidRegion", ctx, 65, 64));
        Assert.Equal(JpegEnc.ERR_IMAGE_SIZE, Call(e, "sceJpegEncoderSetValidRegion", ctx, 0, 64));
        Assert.Equal(0, Call(e, "sceJpegEncoderSetValidRegion", ctx, 32, 16));
        Assert.Equal(JpegEnc.ERR_INVALID_POINTER, Call(e, "sceJpegEncoderSetOutputAddr", ctx, 0u, 0x100u));
        Assert.Equal(0, Call(e, "sceJpegEncoderEnd", ctx));
        Assert.Equal(JpegEnc.ERR_INVALID_POINTER, Call(e, "sceJpegEncoderSetCompressionRatio", ctx, 10)); // ended

        // InitWithParam reads SceJpegEncoderInitParam (0x1C)
        uint param = Alloc(0x1C, "enc-param");
        uint[] f = [0x1C, 96, 48, 9, outp, 0x1000, 1];
        for (int i = 0; i < f.Length; i++) GuestMemory.W32(param + (uint)i * 4, f[i]);
        Assert.Equal(0, Call(e, "sceJpegEncoderInitWithParam", ctx, param));
        Assert.Equal(JpegEnc.ERR_INVALID_POINTER, Call(e, "sceJpegEncoderInitWithParam", ctx, 0u));
        Assert.Equal(JpegEnc.ERR_INVALID_POINTER, Call(e, "sceJpegEncoderEncode", ctx, 0u));
    }

    [Theory]
    [InlineData(8, 2)]
    [InlineData(9, 1)]
    public void EncodeWritesJpegAndReturnsSize(int format, int vdiv)
    {
        var e = new JpegEnc();
        var src = JpegHleTests.Synthetic(96, 64, 2, vdiv, seed: 11);
        uint input = PutPlanes(src, "enc-in");
        uint ctx = Alloc(0x20, "enc-ctx2"), outp = Alloc(0x8000, "enc-out2");
        GuestMemory.Fill(outp, 0xAA, 0x8000);
        Assert.Equal(0, Call(e, "sceJpegEncoderInit", ctx, 96, 64, format, outp, 0x8000u));
        int size = Call(e, "sceJpegEncoderEncode", ctx, input);
        Assert.InRange(size, 200, 0x8000);
        Assert.Equal(0xAAu, GuestMemory.Read<byte>(outp + (uint)size)); // nothing written past the stream
        var bytes = GuestMemory.Span(outp, size).ToArray();
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, bytes[..2]);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, bytes[^2..]);
        var img = JpegDecoder.Decode(bytes);
        Assert.Equal((96, 64, 2, vdiv), (img.Info.Width, img.Info.Height, img.Info.HDiv, img.Info.VDiv));
        Assert.True(PlanePsnr(img, 0, src.Y, src.W, src.H) > 33);
        Assert.True(PlanePsnr(img, 1, src.Cb!, src.CW, src.CH) > 33);

        // higher ratio, smaller stream
        Assert.Equal(0, Call(e, "sceJpegEncoderSetCompressionRatio", ctx, 200));
        Assert.True(Call(e, "sceJpegEncoderEncode", ctx, input) < size);

        // valid region: top-left 48x32 of the same buffer
        Assert.Equal(0, Call(e, "sceJpegEncoderSetCompressionRatio", ctx, 0));
        Assert.Equal(0, Call(e, "sceJpegEncoderSetValidRegion", ctx, 48, 32));
        int rs = Call(e, "sceJpegEncoderEncode", ctx, input);
        var region = JpegDecoder.Decode(GuestMemory.Span(outp, rs));
        Assert.Equal((48, 32), (region.Info.Width, region.Info.Height));
        Assert.True(JpegHleTests.Psnr(region.Planes[0].Data, region.Planes[0].Stride, src.Y, src.W, 48, 32) > 38);
        Assert.True(JpegHleTests.Psnr(region.Planes[2].Data, region.Planes[2].Stride, src.Cr, src.CW, 24, 32 / vdiv) > 38);

        // MJPEG header mode: no DHT, still decodable (decodeMode WITHOUT_DHT)
        Assert.Equal(0, Call(e, "sceJpegEncoderSetHeaderMode", ctx, 1));
        int ms = Call(e, "sceJpegEncoderEncode", ctx, input);
        Assert.True(ms < rs);
        Assert.False(HasMarker(GuestMemory.Span(outp, ms).ToArray(), 0xC4));
        var j = new Jpeg();
        uint yuv = Alloc(0x4000, "enc-dec");
        Assert.Equal((48 << 16) | 32, Call(j, "sceJpegDecodeMJpegYCbCr", outp, (uint)ms, yuv, 0x4000u, 1, 0u, 0u));

        // output buffer too small
        Assert.Equal(0, Call(e, "sceJpegEncoderSetOutputAddr", ctx, outp, 100u));
        Assert.Equal(JpegEnc.ERR_INSUFFICIENT_BUFFER, Call(e, "sceJpegEncoderEncode", ctx, input));
    }

    [Fact]
    public void CscRgbaToContextLayout()
    {
        var e = new JpegEnc();
        const int w = 33, h = 21, pitch = 40; // odd sizes: chroma planes are ceil(w/2) x ceil(h/2)
        uint ctx = Alloc(0x20, "csc-ctx"), outp = Alloc(0x4000, "csc-out");
        var rgba = new byte[pitch * h * 4];
        var rnd = new Random(3);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * pitch + x) * 4;
                rgba[i] = (byte)(x * 7); rgba[i + 1] = (byte)(y * 11); rgba[i + 2] = (byte)(100 + rnd.Next(-4, 5) + (x + y) * 2); rgba[i + 3] = 0xFF;
            }
        uint src = Alloc(rgba.Length, "csc-src");
        rgba.CopyTo(GuestMemory.Span(src, rgba.Length));
        uint yuv = Alloc(w * h * 2, "csc-yuv");
        Assert.Equal(0, Call(e, "sceJpegEncoderInit", ctx, w, h, 8 | 16, outp, 0x4000u));
        Assert.Equal(0, Call(e, "sceJpegEncoderCsc", ctx, yuv, src, pitch, 0));
        int cw = 17, ch = 11;
        var ey = new byte[w * h];
        var ecb = new byte[cw * ch];
        var ecr = new byte[cw * ch];
        YuvConvert.FromRgba(rgba, pitch, w, h, false, 2, 2, ey, w, ecb, ecr, cw);
        Assert.Equal(ey, GuestMemory.Span(yuv, w * h).ToArray());
        Assert.Equal(ecb, GuestMemory.Span(yuv + (uint)(w * h), cw * ch).ToArray());
        Assert.Equal(ecr, GuestMemory.Span(yuv + (uint)(w * h + cw * ch), cw * ch).ToArray());

        // Csc + Encode + decode + RGBA comes back close to the source
        int size = Call(e, "sceJpegEncoderEncode", ctx, yuv);
        Assert.True(size > 0);
        var img = JpegDecoder.Decode(GuestMemory.Span(outp, size));
        var back = new byte[w * h * 4];
        var p = img.Planes;
        YuvConvert.ToRgba(p[0].Data, p[0].Stride, p[1].Data, p[2].Data, p[1].Stride, w, h, 2, 2, back, w);
        double se = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++) { double d = back[(y * w + x) * 4 + c] - rgba[(y * pitch + x) * 4 + c]; se += d * d; }
        double psnr = 10 * Math.Log10(255.0 * 255 * w * h * 3 / se);
        Assert.True(psnr > 30, $"psnr {psnr}");

        // BGRA input swaps R and B
        Assert.Equal(0, Call(e, "sceJpegEncoderCsc", ctx, yuv, src, pitch, 4));
        YuvConvert.FromRgba(rgba, pitch, w, h, true, 2, 2, ey, w, ecb, ecr, cw);
        Assert.Equal(ecb, GuestMemory.Span(yuv + (uint)(w * h), cw * ch).ToArray());

        Assert.Equal(JpegEnc.ERR_INVALID_PIXELFORMAT, Call(e, "sceJpegEncoderCsc", ctx, yuv, src, pitch, 8));
        Assert.Equal(JpegEnc.ERR_INVALID_POINTER, Call(e, "sceJpegEncoderCsc", ctx, 0u, src, pitch, 0));
        // a context whose format has no YCbCr subsampling cannot convert
        uint ctx2 = Alloc(0x20, "csc-ctx2");
        Assert.Equal(0, Call(e, "sceJpegEncoderInit", ctx2, w, h, 0, outp, 0x4000u));
        Assert.Equal(JpegEnc.ERR_INVALID_PIXELFORMAT, Call(e, "sceJpegEncoderCsc", ctx2, yuv, src, pitch, 0));
        Assert.Equal(JpegEnc.ERR_INVALID_PIXELFORMAT, Call(e, "sceJpegEncoderEncode", ctx2, yuv));
    }

    [Fact]
    public void ArmEncoder()
    {
        var e = new JpegEnc();
        Assert.Equal(0x20, Call(e, "sceJpegArmEncoderGetContextSize"));
        var src = JpegHleTests.Synthetic(64, 32, 2, 1);
        uint input = PutPlanes(src, "arm-in");
        uint ctx = Alloc(0x20, "arm-ctx"), outp = Alloc(0x4000, "arm-out");
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_PIXELFORMAT, Call(e, "sceJpegArmEncoderInit", ctx, (ushort)64, (ushort)32, 0, outp, 0x4000u));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_PIXELFORMAT, Call(e, "sceJpegArmEncoderInit", ctx, (ushort)64, (ushort)32, 8 | 16, outp, 0x4000u));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_POINTER, Call(e, "sceJpegArmEncoderInit", 0u, (ushort)64, (ushort)32, 9, outp, 0x4000u));
        Assert.Equal(JpegEnc.ARM_ERR_IMAGE_SIZE, Call(e, "sceJpegArmEncoderInit", ctx, (ushort)0, (ushort)32, 9, outp, 0x4000u));
        Assert.Equal(0, Call(e, "sceJpegArmEncoderInit", ctx, (ushort)64, (ushort)32, 9, outp, 0x4000u));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_COMP_RATIO, Call(e, "sceJpegArmEncoderSetCompressionRatio", ctx, (byte)0));
        Assert.Equal(0, Call(e, "sceJpegArmEncoderSetCompressionRatio", ctx, (byte)1));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_HEADER_MODE, Call(e, "sceJpegArmEncoderSetHeaderMode", ctx, 3));
        Assert.Equal(JpegEnc.ARM_ERR_IMAGE_SIZE, Call(e, "sceJpegArmEncoderSetValidRegion", ctx, (ushort)64, (ushort)33));
        Assert.Equal(0, Call(e, "sceJpegArmEncoderSetValidRegion", ctx, (ushort)64, (ushort)32));
        int size = Call(e, "sceJpegArmEncoderEncode", ctx, input);
        Assert.True(size > 0);
        var img = JpegDecoder.Decode(GuestMemory.Span(outp, size));
        Assert.True(PlanePsnr(img, 0, src.Y, 64, 32) > 40);
        Assert.Equal(0, Call(e, "sceJpegArmEncoderSetOutputAddr", ctx, outp, 64u));
        Assert.Equal(JpegEnc.ARM_ERR_INSUFFICIENT_BUFFER, Call(e, "sceJpegArmEncoderEncode", ctx, input));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_POINTER, Call(e, "sceJpegArmEncoderSetOutputAddr", ctx, 0u, 64u));
        Assert.Equal(0, Call(e, "sceJpegArmEncoderEnd", ctx));
        Assert.Equal(JpegEnc.ARM_ERR_INVALID_POINTER, Call(e, "sceJpegArmEncoderEncode", ctx, input));
        // user and Arm contexts are separate namespaces
        Assert.Equal(JpegEnc.ERR_INVALID_POINTER, Call(e, "sceJpegEncoderEncode", ctx, input));
    }
}
