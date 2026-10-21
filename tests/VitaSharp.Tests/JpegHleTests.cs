using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>Managed JPEG decoder, YCbCr conversion and the SceJpegUser / SceJpegArm HLE.</summary>
[Collection("guest")]
public sealed class JpegHleTests(GuestMemoryFixture fx)
{
    static int Call<T>(T module, string name, params object[] args) =>
        (int)typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args)!;

    internal sealed record Planes(byte[] Y, byte[]? Cb, byte[]? Cr, int W, int H, int HDiv, int VDiv)
    {
        public int CW => (W + HDiv - 1) / HDiv;
        public int CH => (H + VDiv - 1) / VDiv;
    }

    /// <summary>Smooth synthetic image (gradients + waves) with chroma at ceil(w / hdiv) x ceil(h / vdiv).</summary>
    internal static Planes Synthetic(int w, int h, int hdiv, int vdiv, bool gray = false, int seed = 1)
    {
        var rnd = new Random(seed);
        int cw = (w + hdiv - 1) / hdiv, ch = (h + vdiv - 1) / vdiv;
        var y = new byte[w * h];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                y[j * w + i] = (byte)Math.Clamp(128 + 90 * Math.Sin(i * 0.11 + seed) * Math.Cos(j * 0.07) + rnd.Next(-6, 7), 0, 255);
        if (gray) return new Planes(y, null, null, w, h, 1, 1);
        var cb = new byte[cw * ch];
        var cr = new byte[cw * ch];
        for (int j = 0; j < ch; j++)
            for (int i = 0; i < cw; i++)
            {
                cb[j * cw + i] = (byte)(128 + 50 * Math.Sin(i * 0.09));
                cr[j * cw + i] = (byte)(128 + 50 * Math.Cos(j * 0.08 + i * 0.02));
            }
        return new Planes(y, cb, cr, w, h, hdiv, vdiv);
    }

    static readonly byte[] FineQuant = Enumerable.Range(0, 64).Select(i => (byte)(1 + i / 8)).ToArray();

    static byte[] Write(Planes p, bool progressive, int restart = 0) =>
        JpegTestWriter.Write(p.Y, p.Cb, p.Cr, p.W, p.H, p.HDiv, p.VDiv, FineQuant, progressive, restart);

    internal static double Psnr(ReadOnlySpan<byte> a, int aStride, ReadOnlySpan<byte> b, int bStride, int w, int h)
    {
        double se = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double d = a[y * aStride + x] - b[y * bStride + x];
                se += d * d;
            }
        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * w * h / se);
    }

    static bool SameImageArea(JpegImage a, JpegImage b)
    {
        for (int p = 0; p < a.Planes.Length; p++)
        {
            var x = a.Planes[p];
            var y = b.Planes[p];
            for (int r = 0; r < x.Height; r++)
                if (!x.Data.AsSpan(r * x.Stride, x.Width).SequenceEqual(y.Data.AsSpan(r * y.Stride, y.Width))) return false;
        }
        return true;
    }

    uint Put(byte[] data, string name)
    {
        uint a = fx.Memory.Alloc((uint)Math.Max(data.Length, 16), name);
        data.CopyTo(GuestMemory.Span(a, data.Length));
        return a;
    }

    // ------------------------------------------------------------------ codec

    /// <summary>Hand-assembled 16x8 grayscale baseline JPEG: quant table of 1s, a DC table with one 1-bit code
    /// (category 9) and an AC table with one 1-bit code (EOB). Block 1 DC = +256 (pixel 128 + 256/8 = 160), block 2
    /// DC diff = -256 (pixel 128). Entropy bits: 0 100000000 0 | 0 011111111 0 | 11 (padding) = 40 07 FB.</summary>
    [Fact]
    public void HandAssembledTinyJpeg()
    {
        var f = new List<byte> { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43, 0x00 };
        f.AddRange(Enumerable.Repeat((byte)1, 64));
        f.AddRange([0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x08, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00]);
        f.AddRange([0xFF, 0xC4, 0x00, 0x14, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x09]);
        f.AddRange([0xFF, 0xC4, 0x00, 0x14, 0x10, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x00]);
        f.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x40, 0x07, 0xFB, 0xFF, 0xD9]);
        var img = JpegDecoder.Decode(f.ToArray());
        Assert.True(img.Info.Gray);
        Assert.Equal(0x10101, img.Info.ColorSpace);
        Assert.Equal((16, 8), (img.Info.Width, img.Info.Height));
        var p = img.Planes[0];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 16; x++) Assert.Equal(x < 8 ? 160 : 128, p.Data[y * p.Stride + x]);
    }

    [Theory]
    [InlineData(2, 2, false, 0x20202)]
    [InlineData(2, 1, false, 0x20201)]
    [InlineData(1, 1, false, 0x20101)]
    [InlineData(1, 2, false, 0x20102)]
    [InlineData(4, 1, false, 0x20401)]
    [InlineData(1, 4, false, 0x20104)]
    [InlineData(1, 1, true, 0x10101)]
    public void SubsamplingVariantsDecode(int hdiv, int vdiv, bool gray, int colorSpace)
    {
        var src = Synthetic(100, 60, hdiv, vdiv, gray);
        var img = JpegDecoder.Decode(Write(src, false));
        Assert.Equal(colorSpace, img.Info.ColorSpace);
        Assert.Equal(gray ? 1 : 3, img.Planes.Length);
        Assert.True(Psnr(img.Planes[0].Data, img.Planes[0].Stride, src.Y, src.W, src.W, src.H) > 38);
        if (!gray)
        {
            Assert.Equal((src.CW, src.CH), (img.Planes[1].Width, img.Planes[1].Height));
            Assert.True(Psnr(img.Planes[1].Data, img.Planes[1].Stride, src.Cb, src.CW, src.CW, src.CH) > 38);
            Assert.True(Psnr(img.Planes[2].Data, img.Planes[2].Stride, src.Cr, src.CW, src.CW, src.CH) > 38);
        }
    }

    /// <summary>The progressive script (DC/AC spectral selection, successive approximation of both, non-interleaved
    /// chroma, EOB runs) must rebuild exactly the coefficients of the baseline encoding; the writer was cross-checked
    /// against the Windows (GDI+) decoder, which decodes both files to identical pixels.</summary>
    [Theory]
    [InlineData(2, 2, false, 0)]
    [InlineData(2, 1, false, 0)]
    [InlineData(1, 1, false, 7)]
    [InlineData(2, 2, false, 3)]
    [InlineData(1, 1, true, 0)]
    public void ProgressiveMatchesBaseline(int hdiv, int vdiv, bool gray, int restart)
    {
        var src = Synthetic(93, 61, hdiv, vdiv, gray, seed: 3);
        var baseline = JpegDecoder.Decode(Write(src, false, restart));
        var prog = JpegDecoder.Decode(Write(src, true, restart));
        Assert.True(prog.Info.Progressive);
        Assert.False(baseline.Info.Progressive);
        Assert.True(SameImageArea(baseline, prog));
    }

    [Fact]
    public void RestartIntervalsDecodeIdentically()
    {
        var src = Synthetic(70, 50, 2, 2);
        var a = JpegDecoder.Decode(Write(src, false));
        var b = JpegDecoder.Decode(Write(src, false, restart: 1));
        var c = JpegDecoder.Decode(Write(src, false, restart: 4));
        Assert.True(SameImageArea(a, b));
        Assert.True(SameImageArea(a, c));
    }

    [Fact]
    public void YcbcrToRgbaKnownColors()
    {
        // JFIF: neutral grey, pure red (Y 76 Cb 85 Cr 255), BT.601 limited white (235,128,128)
        byte[] y = [128, 76], cb = [128, 85], cr = [128, 255];
        var dst = new byte[8];
        YuvConvert.ToRgba(y, 2, cb, cr, 2, 2, 1, 1, 1, dst, 2);
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, dst[..4]);
        Assert.InRange(dst[4], 253, 255);
        Assert.InRange(dst[5], 0, 2);
        Assert.InRange(dst[6], 0, 2);
        YuvConvert.ToRgba(y, 2, cb, cr, 2, 2, 1, 1, 1, dst, 2, bgra: true);
        Assert.InRange(dst[6], 253, 255);
        Assert.InRange(dst[4], 0, 2);
        YuvConvert.ToRgba([235], 1, [128], [128], 1, 1, 1, 1, 1, dst, 1, bt601: true);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, dst[..4]);
        YuvConvert.ToRgba([16], 1, [128], [128], 1, 1, 1, 1, 1, dst, 1, bt601: true);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, dst[..4]);
    }

    [Fact]
    public void RgbaToYcbcrRoundTrip()
    {
        // primaries + white/black, 4x2 image at 4:2:2 and 4:4:4
        byte[] rgba =
        [
            255, 0, 0, 255, 255, 0, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255,
        ];
        var y = new byte[8];
        var cb = new byte[8];
        var cr = new byte[8];
        YuvConvert.FromRgba(rgba, 4, 4, 2, false, 1, 1, y, 4, cb, cr, 4);
        Assert.Equal(76, y[0]);
        Assert.Equal(85, cb[0]);
        Assert.Equal(255, cr[0]);
        Assert.Equal((byte)255, y[6]);
        Assert.Equal((128, 128), (cb[6], cr[6]));
        var back = new byte[32];
        YuvConvert.ToRgba(y, 4, cb, cr, 4, 4, 2, 1, 1, back, 4);
        for (int i = 0; i < 32; i++) Assert.InRange(back[i] - rgba[i], -2, 2);
        // 4:2:2: chroma is the average of each horizontal pair
        YuvConvert.FromRgba(rgba, 4, 4, 2, false, 2, 1, y, 4, cb, cr, 2);
        Assert.Equal((85, 255), (cb[0], cr[0]));
        Assert.Equal(((byte)128, (byte)128), (cb[3], cr[3])); // white/black average
        // BGRA input swaps R and B
        YuvConvert.FromRgba(rgba, 4, 4, 2, true, 1, 1, y, 4, cb, cr, 4);
        Assert.Equal(29, y[0]);
        Assert.Equal(255, cb[0]);
    }

    [Theory]
    [InlineData(new byte[] { 0x12, 0x34 }, JpegError.NoSoi)]
    [InlineData(new byte[] { 0xFF }, JpegError.ImageEmpty)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43, 0x05 }, JpegError.BadMarkerLength)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x03, 0x05 }, JpegError.BadDqtIndex)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC3, 0x00, 0x0B, 0x08, 0, 8, 0, 8, 1, 1, 0x11, 0 }, JpegError.NoLosslessSupport)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC9, 0x00, 0x0B, 0x08, 0, 8, 0, 8, 1, 1, 0x11, 0 }, JpegError.NoArithSupport)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x0C, 0, 8, 0, 8, 1, 1, 0x11, 0 }, JpegError.BadPrecision)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0E, 0x08, 0, 8, 0, 8, 2, 1, 0x11, 0, 2, 0x11, 0 }, JpegError.ComponentCount)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0, 8, 0, 8, 1, 1, 0x11, 0, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0, 8, 0, 8, 1, 1, 0x11, 0 }, JpegError.SofDuplicate)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC4, 0x00, 0x13, 0x25, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, JpegError.BadDhtIndex)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC4, 0x00, 0x13, 0x00, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, JpegError.BadDhtMarker)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDD, 0x00, 0x03, 0x00 }, JpegError.BadDriMarker)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 63, 0 }, JpegError.BadSosMarker)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0x05 }, JpegError.UnknownMarker)]
    public void MalformedStreamsReportTheirError(byte[] data, JpegError expected)
    {
        var e = Assert.Throws<JpegFormatException>(() => JpegDecoder.Decode(data));
        Assert.Equal(expected, e.Error);
    }

    [Fact]
    public void BadProgressiveScanParameters()
    {
        var src = Synthetic(16, 16, 1, 1, gray: true);
        var f = JpegTestWriter.Write(src.Y, null, null, 16, 16, 1, 1, FineQuant, true);
        // first SOS is the DC scan (Ss=0, Se=0): make it Se=5, which is illegal for a DC scan
        int sos = Enumerable.Range(0, f.Length - 1).First(i => f[i] == 0xFF && f[i + 1] == 0xDA);
        int ns = f[sos + 4];
        f[sos + 4 + 1 + 2 * ns + 1] = 5;
        var e = Assert.Throws<JpegFormatException>(() => JpegDecoder.Decode(f));
        Assert.Equal(JpegError.BadProgressiveParam, e.Error);
    }

    // ------------------------------------------------------------------ HLE

    [Fact]
    public void InitAndFinish()
    {
        var j = new Jpeg();
        Assert.Equal(0, Call(j, "sceJpegInitMJpeg", 2));
        Assert.True(j.Initialized);
        Assert.Equal(2, j.MaxSplitDecoders);
        Assert.Equal(0, Call(j, "sceJpegFinishMJpeg"));
        Assert.False(j.Initialized);
        uint p = fx.Memory.Alloc(0x10, "jpeg-init");
        GuestMemory.W32(p, 0xC);
        GuestMemory.W32(p + 4, 3);
        GuestMemory.W32(p + 8, 0);
        Assert.Equal(0, Call(j, "sceJpegInitMJpegWithParam", p));
        Assert.Equal(3, j.MaxSplitDecoders);
        Assert.Equal(Jpeg.ERR_INVALID_POINTER, Call(j, "sceJpegInitMJpegWithParam", 0u));
        Assert.Equal(0, Call(j, "sceJpegInitMJpeg", 1)); // second init is accepted (as Vita3K)
    }

    uint OutputInfo(Jpeg j, uint jpeg, int size, int format, int mode, out int ret, bool arm = false)
    {
        uint o = fx.Memory.Alloc(0x40, "jpeg-info");
        GuestMemory.Fill(o, 0xCD, 0x40);
        ret = arm
            ? Call(j, "sceJpegArmGetOutputInfo", jpeg, (uint)size, mode, format, o)
            : Call(j, "sceJpegGetOutputInfo", jpeg, (uint)size, format, mode, o);
        return o;
    }

    static (uint X, uint Y) Pitch(uint info, int i) => (GuestMemory.R32(info + 0x14 + (uint)i * 8), GuestMemory.R32(info + 0x18 + (uint)i * 8));

    [Fact]
    public void OutputInfo420And422()
    {
        var j = new Jpeg();
        var f420 = Write(Synthetic(100, 60, 2, 2), false);
        uint a = Put(f420, "j420");
        uint o = OutputInfo(j, a, f420.Length, -1, 0, out int r);
        Assert.Equal(0, r);
        Assert.Equal(0x20202u, GuestMemory.R32(o));
        Assert.Equal((ushort)100, GuestMemory.Read<ushort>(o + 4));
        Assert.Equal((ushort)60, GuestMemory.Read<ushort>(o + 6));
        Assert.Equal((112u, 64u), Pitch(o, 0));
        Assert.Equal((64u, 32u), Pitch(o, 1)); // 56 aligned to 16
        Assert.Equal((64u, 32u), Pitch(o, 2));
        Assert.Equal((0u, 0u), Pitch(o, 3));
        Assert.Equal(11264u, GuestMemory.R32(o + 8)); // 112*64 + 2*64*32
        Assert.Equal(0u, GuestMemory.R32(o + 0xC));
        Assert.Equal(0x100u, GuestMemory.R32(o + 0x10));
        Assert.Equal(0xCDCDCDCDu, GuestMemory.R32(o + 0x34)); // exactly 0x34 bytes written

        // RGBA: pitch[0].x in bytes, YCbCr size becomes the temp buffer
        o = OutputInfo(j, a, f420.Length, 0, 0, out r);
        Assert.Equal(0, r);
        Assert.Equal((448u, 64u), Pitch(o, 0));
        Assert.Equal((0u, 0u), Pitch(o, 1));
        Assert.Equal(28672u, GuestMemory.R32(o + 8));
        Assert.Equal(11264u, GuestMemory.R32(o + 0xC));

        // 1/2 downscale: align(112/2, 16) x align(64/2, 8)
        o = OutputInfo(j, a, f420.Length, -1, 0x10, out r);
        Assert.Equal(0, r);
        Assert.Equal((64u, 32u), Pitch(o, 0));
        Assert.Equal((32u, 16u), Pitch(o, 1));

        var f422 = Write(Synthetic(100, 60, 2, 1), false);
        uint b = Put(f422, "j422");
        o = OutputInfo(j, b, f422.Length, -1, 1, out r);
        Assert.Equal(0, r);
        Assert.Equal(0x20201u, GuestMemory.R32(o));
        Assert.Equal((112u, 64u), Pitch(o, 0));
        Assert.Equal((64u, 64u), Pitch(o, 1));
    }

    [Fact]
    public void OutputInfoModesAndErrors()
    {
        var j = new Jpeg();
        var f444 = Write(Synthetic(100, 60, 1, 1), false);
        uint a = Put(f444, "j444");
        OutputInfo(j, a, f444.Length, -1, 0, out int r);
        Assert.Equal(Jpeg.ERR_UNSUPPORT_SAMPLING, r);
        // extended mode: any sampling, pitch = MCU-aligned size (rows realigned to 1, not 8)
        uint o = OutputInfo(j, a, f444.Length, -1, 3, out r);
        Assert.Equal(0, r);
        Assert.Equal(0x20101u, GuestMemory.R32(o));
        Assert.Equal((104u, 64u), Pitch(o, 0));
        Assert.Equal((104u, 64u), Pitch(o, 1));
        OutputInfo(j, a, f444.Length, 0, 3, out r);
        Assert.Equal(Jpeg.ERR_UNSUPPORT_DOWNSCALE, r); // extended decoding has no CSC
        // Arm variant: mode and format swapped
        o = OutputInfo(j, a, f444.Length, -1, 3, out r, arm: true);
        Assert.Equal(0, r);
        Assert.Equal((104u, 64u), Pitch(o, 0));

        var gray = Write(Synthetic(64, 64, 1, 1, gray: true), false);
        uint g = Put(gray, "jgray");
        o = OutputInfo(j, g, gray.Length, -1, 0, out r);
        Assert.Equal(Jpeg.ERR_UNSUPPORT_COLORSPACE, r);
        Assert.Equal(0x10101u, GuestMemory.R32(o));
        o = OutputInfo(j, g, gray.Length, -1, 2, out r);
        Assert.Equal(0, r);
        Assert.Equal((64u, 64u), Pitch(o, 0));
        Assert.Equal((0u, 0u), Pitch(o, 1));
        Assert.Equal(4096u, GuestMemory.R32(o + 8));

        OutputInfo(j, a, f444.Length, 2, 3, out r);
        Assert.Equal(Jpeg.ERR_INVALID_COLOR_FORMAT, r);
        OutputInfo(j, 0, 10, -1, 0, out r);
        Assert.Equal(Jpeg.ERR_INVALID_POINTER, r);
        OutputInfo(j, a, 0, -1, 0, out r);
        Assert.Equal(Jpeg.ERR_INVALID_POINTER, r);
        uint bad = Put([0x00, 0x11, 0x22, 0x33], "jbad");
        OutputInfo(j, bad, 4, -1, 0, out r);
        Assert.Equal(Jpeg.ERR_NO_SOI, r);
    }

    [Fact]
    public void DecodeYCbCrLayoutAndRgba()
    {
        var j = new Jpeg();
        var src = Synthetic(100, 60, 2, 2);
        var file = Write(src, false);
        uint a = Put(file, "jdec");
        const int total = 11264;
        uint yuv = fx.Memory.Alloc(total + 0x100, "jyuv");
        GuestMemory.Fill(yuv + total, 0xEE, 0x100);
        int ret = Call(j, "sceJpegDecodeMJpegYCbCr", a, (uint)file.Length, yuv, (uint)total, 0, 0u, 0u);
        Assert.Equal((112 << 16) | 64, ret);
        Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(yuv + total)); // nothing past the layout
        var y = GuestMemory.Span(yuv, 112 * 64).ToArray();
        var cb = GuestMemory.Span(yuv + 112 * 64, 64 * 32).ToArray();
        var cr = GuestMemory.Span(yuv + 112 * 64 + 64 * 32, 64 * 32).ToArray();
        Assert.True(Psnr(y, 112, src.Y, src.W, src.W, src.H) > 38);
        Assert.True(Psnr(cb, 64, src.Cb, src.CW, src.CW, src.CH) > 38);
        Assert.True(Psnr(cr, 64, src.Cr, src.CW, src.CW, src.CH) > 38);
        // chroma padding (columns 56..63) replicates column 55 of the decoded MCU data
        Assert.Equal(cb[10 * 64 + 55], cb[10 * 64 + 63]);

        // Arm variant with its argument order
        uint yuv2 = fx.Memory.Alloc(total, "jyuv2");
        Assert.Equal(ret, Call(j, "sceJpegArmDecodeMJpegYCbCr", a, (uint)file.Length, 0, yuv2, (uint)total, 0u, 0u));
        Assert.True(GuestMemory.Span(yuv, total).SequenceEqual(GuestMemory.Span(yuv2, total)));

        // DecodeMJpeg (RGBA, line width = pitch) == DecodeMJpegYCbCr + MJpegCsc
        uint rgba = fx.Memory.Alloc(112 * 64 * 4, "jrgba");
        uint temp = fx.Memory.Alloc(total, "jtemp");
        ret = Call(j, "sceJpegDecodeMJpeg", a, (uint)file.Length, rgba, 112u * 64 * 4, 0, temp, (uint)total, 0u, 0u);
        Assert.Equal((112 << 16) | 64, ret);
        Assert.True(GuestMemory.Span(temp, total).SequenceEqual(GuestMemory.Span(yuv, total)));
        uint rgba2 = fx.Memory.Alloc(112 * 64 * 4, "jrgba2");
        Assert.Equal(0, Call(j, "sceJpegMJpegCsc", rgba2, yuv, (uint)((112 << 16) | 64), 112, 0, 0x202));
        Assert.True(GuestMemory.Span(rgba, 112 * 64 * 4).SequenceEqual(GuestMemory.Span(rgba2, 112 * 64 * 4)));
        Assert.Equal(0xFF, GuestMemory.Read<byte>(rgba + 3));
        // BGRA swaps channels
        Assert.Equal(0, Call(j, "sceJpegMJpegCsc", rgba2, yuv, (uint)((112 << 16) | 64), 112, 4, 0x202));
        Assert.Equal(GuestMemory.Read<byte>(rgba), GuestMemory.Read<byte>(rgba2 + 2));
        Assert.Equal(GuestMemory.Read<byte>(rgba + 2), GuestMemory.Read<byte>(rgba2));

        Assert.Equal(Jpeg.ERR_INVALID_COLOR_FORMAT, Call(j, "sceJpegMJpegCsc", rgba2, yuv, (uint)((112 << 16) | 64), 112, 1, 0x202));
        Assert.Equal(Jpeg.ERR_UNSUPPORT_IMAGE_SIZE, Call(j, "sceJpegMJpegCsc", rgba2, yuv, (uint)((32 << 16) | 64), 112, 0, 0x202));
        Assert.Equal(Jpeg.ERR_INVALID_POINTER, Call(j, "sceJpegDecodeMJpegYCbCr", 0u, 10u, yuv, (uint)total, 0, 0u, 0u));
    }

    [Fact]
    public void DecodeProgressiveDownscaleAndSizeLimit()
    {
        var j = new Jpeg();
        var src = Synthetic(128, 96, 2, 2);
        var prog = Write(src, true);
        uint a = Put(prog, "jprog");
        uint o = OutputInfo(j, a, prog.Length, -1, 0, out int r);
        Assert.Equal(0, r);
        Assert.True(GuestMemory.R32(o + 0x10) >= 128 * 96 * 3 / 2 * 2); // coefficients of the whole frame
        uint yuv = fx.Memory.Alloc(128 * 96 * 2, "jprogyuv");
        int ret = Call(j, "sceJpegDecodeMJpegYCbCr", a, (uint)prog.Length, yuv, 128u * 96 * 2, 0, 0u, 0u);
        Assert.Equal((128 << 16) | 96, ret);
        Assert.True(Psnr(GuestMemory.Span(yuv, 128 * 96), 128, src.Y, 128, 128, 96) > 38);

        // 1/4 downscale averages 4x4 boxes
        ret = Call(j, "sceJpegDecodeMJpegYCbCr", a, (uint)prog.Length, yuv, 128u * 96 * 2, 0x20, 0u, 0u);
        Assert.Equal((32 << 16) | 24, ret);
        var small = GuestMemory.Span(yuv, 32 * 24).ToArray();
        var expect = new byte[32 * 24];
        for (int y = 0; y < 24; y++)
            for (int x = 0; x < 32; x++)
            {
                int s = 0;
                for (int dy = 0; dy < 4; dy++)
                    for (int dx = 0; dx < 4; dx++) s += src.Y[(y * 4 + dy) * 128 + x * 4 + dx];
                expect[y * 32 + x] = (byte)((s + 8) / 16);
            }
        Assert.True(Psnr(small, 32, expect, 32, 32, 24) > 38);

        // the hardware decoder refuses output pitches outside 64x64..2032x1088 (checked after downscaling)
        uint rgba = fx.Memory.Alloc(128 * 96 * 4, "jprogrgba");
        Assert.Equal(Jpeg.ERR_UNSUPPORT_IMAGE_SIZE, Call(j, "sceJpegDecodeMJpeg", a, (uint)prog.Length, rgba, 128u * 96 * 4, 0x10, 0u, 0u, 0u, 0u));
        Assert.Equal((128 << 16) | 96, Call(j, "sceJpegDecodeMJpeg", a, (uint)prog.Length, rgba, 128u * 96 * 4, 0, 0u, 0u, 0u, 0u));
        // the Arm decoder has no such limit
        Assert.Equal((64 << 16) | 48, Call(j, "sceJpegArmDecodeMJpeg", a, (uint)prog.Length, 0x10, rgba, 128u * 96 * 4, 0u, 0u));
    }

    [Fact]
    public void CscGenericAndClampedOutput()
    {
        var j = new Jpeg();
        // 4:4:4 3x2 planes: grey and red
        byte[] planes = [128, 76, 128, 128, 128, 128, 128, 85, 128, 128, 128, 128, 128, 255, 128, 128, 128, 128];
        uint yuv = Put(planes, "jcsc");
        uint rgba = fx.Memory.Alloc(4 * 4 * 2, "jcscout");
        Assert.Equal(0, Call(j, "sceJpegCsc", rgba, yuv, (uint)((3 << 16) | 2), 4, 0, 0x101));
        Assert.Equal(0xFF808080u, GuestMemory.R32(rgba));
        Assert.InRange(GuestMemory.Read<byte>(rgba + 4), 253, 255);
        Assert.Equal(0xFF808080u, GuestMemory.R32(rgba + 16)); // second row at frameWidth 4
        Assert.Equal(Jpeg.ERR_INVALID_COLOR_FORMAT, Call(j, "sceJpegCsc", rgba, yuv, (uint)((3 << 16) | 2), 4, 4, 0x101));
        Assert.Equal(Jpeg.ERR_INVALID_COLOR_FORMAT, Call(j, "sceJpegCsc", rgba, yuv, (uint)((3 << 16) | 2), 4, 0, 0x202));
        Assert.Equal(0, Call(j, "sceJpegCsc", rgba, yuv, (uint)((3 << 16) | 2), 4, 0x10, 0x101)); // BT.601 flag accepted

        // a short output buffer truncates instead of overrunning
        var file = Write(Synthetic(64, 64, 2, 2), false);
        uint a = Put(file, "jshort");
        uint outp = fx.Memory.Alloc(0x2000, "jshortout");
        GuestMemory.Fill(outp, 0x5A, 0x2000);
        int ret = Call(j, "sceJpegDecodeMJpegYCbCr", a, (uint)file.Length, outp, 0x1000u, 0, 0u, 0u);
        Assert.Equal((64 << 16) | 64, ret);
        Assert.Equal(0x5A5A5A5Au, GuestMemory.R32(outp + 0x1000));
    }

    [Fact]
    public void SplitDecoderIsRefused()
    {
        var j = new Jpeg();
        uint ctrl = fx.Memory.Alloc(0x40, "jsplit");
        Assert.Equal(Jpeg.ERR_EXCEED_MAX_SPLIT_DECODER, Call(j, "sceJpegCreateSplitDecoder", ctrl, 0u, 0u, 0u));
        Assert.Equal(Jpeg.ERR_EXCEED_MAX_SPLIT_DECODER, Call(j, "sceJpegArmCreateSplitDecoder", ctrl, 0u, 0u, 0u));
        Assert.Equal(Jpeg.ERR_INVALID_STATE, Call(j, "sceJpegSplitDecodeMJpeg", ctrl));
        Assert.Equal(0, Call(j, "sceJpegDeleteSplitDecoder", ctrl));
        Assert.Equal(0, Call(j, "sceJpegArmDeleteSplitDecoder", ctrl));
    }

    [Fact]
    public void PitchRules()
    {
        // MCU alignment per sampling, standard vs extended row alignment
        Assert.Equal(new Jpeg.Pitch(32, 8), Jpeg.PitchInfo(30, 5, 1, 0x20401, standard: false)[0]);
        Assert.Equal(new Jpeg.Pitch(8, 32), Jpeg.PitchInfo(5, 30, 1, 0x20104, standard: false)[0]);
        Assert.Equal(new Jpeg.Pitch(8, 8), Jpeg.PitchInfo(5, 30, 1, 0x20104, standard: false)[1]);
        Assert.Equal(new Jpeg.Pitch(16, 24), Jpeg.PitchInfo(16, 17, 1, 0x20201, standard: true)[0]);
        Assert.Equal(new Jpeg.Pitch(16, 24), Jpeg.PitchInfo(16, 17, 1, 0x20201, standard: true)[1]);
        Assert.Equal(new Jpeg.Pitch(16, 3), Jpeg.PitchInfo(16, 17, 8, 0x20201, standard: false)[0]);
        Assert.Equal(new Jpeg.Pitch(16, 8), Jpeg.PitchInfo(16, 17, 8, 0x20201, standard: true)[0]);
        Assert.Equal(2, Jpeg.DownscaleRatio(0x10));
        Assert.Equal(8, Jpeg.DownscaleRatio(0x41));
        Assert.Equal(2, Jpeg.DownscaleRatio(0x70));
    }
}
