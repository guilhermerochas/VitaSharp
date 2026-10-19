using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Host;
using VitaSharp.Core.Media;

namespace VitaSharp.Tests;

/// <summary>SceCamera image pipeline: host decode, crop/scale, Vita format encode.</summary>
public sealed class CameraImageTests
{
    static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var a = new byte[w * h * 4];
        for (int i = 0; i < a.Length; i += 4) { a[i] = r; a[i + 1] = g; a[i + 2] = b; a[i + 3] = 255; }
        return a;
    }

    [Theory]
    [InlineData(1, 640, 480)] [InlineData(2, 320, 240)] [InlineData(3, 160, 120)] [InlineData(4, 352, 288)]
    [InlineData(5, 176, 144)] [InlineData(6, 480, 272)] [InlineData(8, 640, 360)] [InlineData(7, 0, 0)] [InlineData(0, 0, 0)]
    public void ResolutionSizes(int res, int w, int h) => Assert.Equal((w, h), Camera.ResolutionSize(res));

    [Fact]
    public void FrameRates()
    {
        Assert.Equal(3.75, Camera.FrameRate(3));
        Assert.Equal(7.5, Camera.FrameRate(7));
        Assert.Equal(30, Camera.FrameRate(30));
        Assert.Equal(0, Camera.FrameRate(25));
    }

    [Fact]
    public void Stride()
    {
        Assert.Equal(640, Camera.StridePixels(640, 0));
        Assert.Equal(1024, Camera.StridePixels(640, 1024));
        Assert.Equal(672, Camera.StridePixels(640, 32));
    }

    [Fact]
    public void PlaneSizes()
    {
        Assert.Equal((640 * 480, 320 * 480, 320 * 480), CameraImage.PlaneSizes(VitaCameraFormat.Yuv422Planar, 640, 480, 640));
        Assert.Equal((640 * 480, 320 * 240, 320 * 240), CameraImage.PlaneSizes(VitaCameraFormat.Yuv420Planar, 640, 480, 640));
        Assert.Equal((640 * 480 * 2, 0, 0), CameraImage.PlaneSizes(VitaCameraFormat.Yuv422Packed, 640, 480, 640));
        Assert.Equal((480 * 272 * 4, 0, 0), CameraImage.PlaneSizes(VitaCameraFormat.Abgr, 480, 272, 480));
        Assert.Equal((160 * 120, 0, 0), CameraImage.PlaneSizes(VitaCameraFormat.Raw8, 160, 120, 160));
    }

    [Fact]
    public void GreyIsNeutralInFullRange()
    {
        var img = Solid(4, 2, 128, 128, 128);
        var yuyv = new byte[4 * 2 * 2];
        CameraImage.Encode(img, 4, 2, VitaCameraFormat.Yuv422Packed, true, 4, yuyv, default, default);
        Assert.All(yuyv, b => Assert.Equal(128, b));
    }

    [Fact]
    public void RedInBothRanges()
    {
        var img = Solid(2, 2, 255, 0, 0);
        var y = new byte[4]; var u = new byte[2]; var v = new byte[2];
        CameraImage.Encode(img, 2, 2, VitaCameraFormat.Yuv422Planar, true, 2, y, u, v);
        Assert.InRange(y[0], 76, 77);
        Assert.InRange(u[0], 84, 86);
        Assert.Equal(255, v[0]);
        CameraImage.Encode(img, 2, 2, VitaCameraFormat.Yuv422Planar, false, 2, y, u, v);
        Assert.Equal(82, y[0]);
        Assert.InRange(u[0], 89, 91);
        Assert.Equal(240, v[0]);
    }

    [Fact]
    public void ArgbAndAbgrByteOrder()
    {
        var img = Solid(1, 1, 10, 20, 30);
        var o = new byte[4];
        CameraImage.Encode(img, 1, 1, VitaCameraFormat.Abgr, true, 1, o, default, default);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, o); // u32 0xAABBGGRR
        CameraImage.Encode(img, 1, 1, VitaCameraFormat.Argb, true, 1, o, default, default);
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, o); // u32 0xAARRGGBB
    }

    [Fact]
    public void Yuv420AveragesChromaOver2x2()
    {
        // left column white, right column black: chroma neutral, luma alternates
        var img = new byte[2 * 2 * 4];
        for (int y = 0; y < 2; y++) { img[y * 8] = img[y * 8 + 1] = img[y * 8 + 2] = 255; img[y * 8 + 3] = img[y * 8 + 7] = 255; }
        var yp = new byte[4]; var u = new byte[1]; var v = new byte[1];
        CameraImage.Encode(img, 2, 2, VitaCameraFormat.Yuv420Planar, true, 2, yp, u, v);
        Assert.Equal(new byte[] { 255, 0, 255, 0 }, yp);
        Assert.Equal(128, u[0]);
        Assert.Equal(128, v[0]);
    }

    [Fact]
    public void PitchIsHonouredAndPlanesNeverOverflow()
    {
        var img = Solid(4, 4, 255, 255, 255);
        // stride 6 pixels, but the plane is too short for row 2: rows 0-1 written, padding untouched, nothing beyond
        var plane = new byte[6 * 2 + 3];
        Array.Fill(plane, (byte)0xAA);
        CameraImage.Encode(img, 4, 4, VitaCameraFormat.Raw8, true, 6, plane, default, default);
        Assert.Equal(new byte[] { 255, 255, 255, 255, 0xAA, 0xAA, 255, 255, 255, 255, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA }, plane);
        // planar with missing chroma planes must not throw
        CameraImage.Encode(img, 4, 4, VitaCameraFormat.Yuv420Planar, true, 4, new byte[16], default, default);
    }

    [Fact]
    public void YuyvRoundTrip()
    {
        const int w = 16, h = 8;
        var img = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            img[i] = (byte)(x * 16); img[i + 1] = (byte)(y * 32); img[i + 2] = 200; img[i + 3] = 255;
        }
        // horizontally smooth content: 4:2:2 subsampling loses little
        var yuyv = new byte[w * h * 2];
        CameraImage.Encode(img, w, h, VitaCameraFormat.Yuv422Packed, false, w, yuyv, default, default);
        var back = new byte[w * h * 4];
        CameraImage.DecodeToRgba(yuyv, CameraHostFormat.Yuyv, w, h, false, back);
        for (int i = 0; i < img.Length; i++) Assert.InRange(back[i] - img[i], -12, 12);
    }

    [Fact]
    public void DecodeNv12AndBottomUpBgr()
    {
        // NV12 2x2, Y=235 (white in limited range), neutral chroma
        var nv12 = new byte[] { 235, 235, 235, 235, 128, 128 };
        var o = new byte[16];
        CameraImage.DecodeToRgba(nv12, CameraHostFormat.Nv12, 2, 2, false, o);
        Assert.All(o, b => Assert.Equal(255, b));

        // BGR24 1x2 bottom-up: first stored row is the bottom one; rows padded to 4 bytes
        var bgr = new byte[] { 0, 0, 255, 0, /* bottom: red */ 255, 0, 0, 0 /* top: blue */ };
        CameraImage.DecodeToRgba(bgr, CameraHostFormat.Bgr24, 1, 2, true, o.AsSpan(0, 8));
        Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, o[..8]);
    }

    [Fact]
    public void ResampleCropsCentreToTargetAspect()
    {
        // 16:9 source whose outer columns (outside a centred 4:3 window) are red, inside blue
        const int sw = 64, sh = 36;
        var src = Solid(sw, sh, 0, 0, 255);
        for (int y = 0; y < sh; y++)
        for (int x = 0; x < sw; x++)
            if (x < 8 || x >= 56) { int i = (y * sw + x) * 4; src[i] = 255; src[i + 2] = 0; }
        var dst = new byte[16 * 12 * 4];
        CameraImage.Resample(src, sw, sh, dst, 16, 12);
        for (int i = 0; i < dst.Length; i += 4) { Assert.True(dst[i] < 40, $"red leaked at {i / 4}"); Assert.True(dst[i + 2] > 215); }
    }

    [Fact]
    public void ResampleMirrorAndFlip()
    {
        var src = new byte[2 * 2 * 4];
        for (int p = 0; p < 4; p++) { src[p * 4] = (byte)(p * 50); src[p * 4 + 3] = 255; }
        var dst = new byte[16];
        CameraImage.Resample(src, 2, 2, dst, 2, 2, mirror: true);
        Assert.Equal(new byte[] { 50, 0, 150, 100 }, new[] { dst[0], dst[4], dst[8], dst[12] });
        CameraImage.Resample(src, 2, 2, dst, 2, 2, flip: true);
        Assert.Equal(new byte[] { 100, 150, 0, 50 }, new[] { dst[0], dst[4], dst[8], dst[12] });
    }

    [Fact]
    public void ResampleDownscalesAverage()
    {
        var src = Solid(320, 240, 90, 90, 90);
        var dst = new byte[160 * 120 * 4];
        CameraImage.Resample(src, 320, 240, dst, 160, 120);
        Assert.All(dst.Where((_, i) => i % 4 != 3), b => Assert.Equal(90, b));
    }

    [Fact]
    public void FrameBufferKeepsOnlyLatest()
    {
        var fb = new CameraFrameBuffer();
        byte[] dst = [];
        Assert.False(fb.CopyLatest(ref dst, out _, out _, out _));
        fb.Publish(Solid(2, 1, 1, 2, 3), 2, 1);
        fb.Publish(2, 2, s => s.Fill(7));
        Assert.True(fb.CopyLatest(ref dst, out int w, out int h, out long seq));
        Assert.Equal((2, 2, 2L), (w, h, seq));
        Assert.Equal(16, dst.Length);
        Assert.All(dst, b => Assert.Equal(7, b));
    }
}
