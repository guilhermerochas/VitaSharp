using VitaSharp.Core.Host;

namespace VitaSharp.Tests;

/// <summary>The front/back camera settings: parsing, defaults, routing by SCE_CAMERA_DEVICE_*, still pictures.</summary>
public sealed class CameraSetupTests
{
    [Theory]
    [InlineData(null, CameraSpecKind.Default)]
    [InlineData("", CameraSpecKind.Default)]
    [InlineData("  ", CameraSpecKind.Default)]
    [InlineData("none", CameraSpecKind.None)]
    [InlineData("NONE", CameraSpecKind.None)]
    [InlineData("C:\\pics\\back.png", CameraSpecKind.Image)]
    [InlineData("/tmp/face.JPG", CameraSpecKind.Image)]
    [InlineData("\"a b.jpeg\"", CameraSpecKind.Image)]
    [InlineData("Integrated Webcam", CameraSpecKind.Device)]
    [InlineData("1", CameraSpecKind.Device)]
    public void ParsesTheChoice(string? value, CameraSpecKind kind) => Assert.Equal(kind, CameraSpec.Parse(value).Kind);

    [Fact]
    public void BothOptionIsOverriddenPerCamera()
    {
        Assert.Equal(("cam", "cam"), CameraSpec.FromOptions("cam", null, null));
        Assert.Equal(("front", "cam"), CameraSpec.FromOptions("cam", "front", null));
        Assert.Equal(("cam", "back.png"), CameraSpec.FromOptions("cam", null, "back.png"));
        Assert.Equal(((string?)null, "none"), CameraSpec.FromOptions(null, null, "none"));
    }

    [Fact]
    public void DefaultsFrontFirstBackAnotherWebcam()
    {
        string[] two = ["Laptop Cam", "USB Cam"], one = ["Laptop Cam"];
        Assert.Equal(0, CameraSpec.SelectDevice(two, null, null, CameraFacing.Front));
        Assert.Equal(1, CameraSpec.SelectDevice(two, null, null, CameraFacing.Back));
        // one webcam serves both
        Assert.Equal(0, CameraSpec.SelectDevice(one, null, null, CameraFacing.Back));
        // the back default avoids the front camera's choice
        Assert.Equal(0, CameraSpec.SelectDevice(two, "usb", null, CameraFacing.Back));
        Assert.Equal(1, CameraSpec.SelectDevice(two, "usb", null, CameraFacing.Front));
        // explicit picks by index or name; unknown = none
        Assert.Equal(0, CameraSpec.SelectDevice(two, null, "0", CameraFacing.Back));
        Assert.Equal(1, CameraSpec.SelectDevice(two, null, "USB Cam", CameraFacing.Back));
        Assert.Equal(-1, CameraSpec.SelectDevice(two, "Missing", null, CameraFacing.Front));
        Assert.Equal(-1, CameraSpec.SelectDevice([], null, null, CameraFacing.Front));
    }

    sealed class Probe(string name) : ICameraSource
    {
        public readonly List<CameraFacing> Opened = new();
        public int Disposed;
        public IReadOnlyList<string> DeviceNames => [name];
        public ICameraStream? Open(CameraFacing facing, int width, int height, int fps)
        {
            Opened.Add(facing);
            return null;
        }
        public void Dispose() => Disposed++;
    }

    [Fact]
    public void RoutesEachVitaCameraToItsSource()
    {
        var a = new Probe("a");
        var b = new Probe("b");
        var cams = new FacingCameras(a, b);
        cams.Open(CameraFacing.Front, 640, 480, 30);
        cams.Open(CameraFacing.Back, 640, 480, 30);
        Assert.Equal([CameraFacing.Front], a.Opened);
        Assert.Equal([CameraFacing.Back], b.Opened);
        Assert.Equal(["a", "b"], cams.DeviceNames);

        // grey side, shared source disposed once
        var shared = new Probe("s");
        var c2 = new FacingCameras(shared, shared);
        c2.Dispose();
        Assert.Equal(1, shared.Disposed);
        Assert.Null(new FacingCameras(null, a).Open(CameraFacing.Front, 640, 480, 30));
    }

    [Fact]
    public void StillPictureDeliversItsPixels()
    {
        var rgba = new byte[4 * 2 * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 200;
            rgba[i + 1] = 10;
            rgba[i + 2] = 30;
            rgba[i + 3] = 255;
        }
        using var cam = new StillImageCamera("p.png", rgba, 4, 2);
        using var s = cam.Open(CameraFacing.Back, 640, 480, 30)!;
        byte[] dst = [];
        Assert.True(s.Frames.CopyLatest(ref dst, out int w, out int h, out _));
        Assert.Equal((4, 2), (w, h));
        Assert.Equal(rgba, dst);
    }

    [Fact]
    public void PngPictureDecodes()
    {
        // 2x1 RGB PNG: red, blue (filter 0)
        byte[] raw = [0, 255, 0, 0, 0, 0, 255];
        using var ms = new MemoryStream();
        void Chunk(string type, byte[] body)
        {
            var len = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
            ms.Write(len);
            ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
            ms.Write(body);
            ms.Write(new byte[4]);
        }
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10]);
        Chunk("IHDR", [0, 0, 0, 2, 0, 0, 0, 1, 8, 2, 0, 0, 0]);
        using (var z = new MemoryStream())
        {
            using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                zs.Write(raw);
            }

            Chunk("IDAT", z.ToArray());
        }
        Chunk("IEND", []);
        var (px, w, h) = StillImageCamera.Decode(ms.ToArray(), "t.png");
        Assert.Equal((2, 1), (w, h));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, px);
        Assert.Throws<InvalidDataException>(() => StillImageCamera.Decode([1, 2, 3], "x"));
        Assert.Null(StillImageCamera.TryLoad(Path.Combine(Path.GetTempPath(), "vitasharp-missing-" + Guid.NewGuid() + ".png")));
    }
}
