using System.Numerics;
using VitaSharp.Android.Input;
using VitaSharp.Android.Media;
using VitaSharp.Core.Host;

namespace VitaSharp.Tests;

/// <summary>The platform-neutral halves of the Android motion sensors (SceMotion) and cameras (SceCamera):
/// axis mapping, re-centring, source selection, camera choice, rotation and YUV_420_888 conversion.</summary>
public sealed class AndroidSensorTests
{
    const float G = 9.80665f;

    static void Near(Vector3 expected, Vector3 actual, float tol = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tol, $"expected {expected}, got {actual}");

    // ---------------------------------------------------------------- motion axes

    [Theory]
    // held upright in front of the user: the accelerometer reads +g along the device axis that points up
    [InlineData(0, 0f, G, 0f)]   // portrait (natural orientation): top edge up
    [InlineData(1, G, 0f, 0f)]   // ROTATION_90, turned counter-clockwise: the right edge points up
    [InlineData(2, 0f, -G, 0f)]  // upside down: the bottom edge points up
    [InlineData(3, -G, 0f, 0f)]  // ROTATION_270, turned clockwise: the left edge points up
    public void PhoneHeldUprightIsTheVitaHeldUpright(int rotation, float x, float y, float z)
    {
        var s = AndroidMotionAxes.FromPhone(1, new Vector3(x, y, z), Vector3.Zero, rotation);
        Near(new Vector3(0, -1, 0), s.Acceleration); // the Vita reads gravity towards its bottom edge
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void PhoneLyingFlatIsTheVitaLyingFlat(int rotation)
    {
        var s = AndroidMotionAxes.FromPhone(5, new Vector3(0, 0, G), new Vector3(0, 0, 0.5f), rotation);
        Near(new Vector3(0, 0, -1), s.Acceleration);
        Near(new Vector3(0, 0, 0.5f), s.AngularVelocity); // turning on the table is about z in every rotation
        Assert.Equal(5UL, s.TimestampUs);
    }

    [Fact]
    public void LandscapeTiltAndGyroFollowTheScreenAxes()
    {
        // ROTATION_90: the screen's right edge is the device's bottom edge (-y), its top edge the device's right edge (+x).
        // Lying flat with the screen's right edge lowered: the upward reaction leans towards the screen's left edge,
        // which is the device's top (+y); the Vita's gravity then leans towards its right edge (+x)
        var s = AndroidMotionAxes.FromPhone(0, new Vector3(0, 0.3f * G, 0.95f * G), Vector3.Zero, 1);
        Near(new Vector3(0.3f, 0, -0.95f), s.Acceleration);
        // a turn about the screen's vertical axis is a turn about the device's x axis
        Near(new Vector3(0, 1.5f, 0), AndroidMotionAxes.FromPhone(0, Vector3.Zero, new Vector3(1.5f, 0, 0), 1).AngularVelocity);
        Near(new Vector3(0, -1.5f, 0), AndroidMotionAxes.FromPhone(0, Vector3.Zero, new Vector3(1.5f, 0, 0), 3).AngularVelocity);
        // ROTATION_270 is ROTATION_90 turned half a turn
        Near(new Vector3(-0.3f, 0, -0.95f), AndroidMotionAxes.FromPhone(0, new Vector3(0, 0.3f * G, 0.95f * G), Vector3.Zero, 3).Acceleration);
    }

    [Fact]
    public void ControllerLyingFlatIsTheVitaLyingFlat()
    {
        // SDL/hid-playstation frame: y out of the face, reading +g when the controller lies on the desk
        var s = AndroidMotionAxes.FromController(0, new Vector3(0, G, 0), new Vector3(0, 2f, 0));
        Near(new Vector3(0, 0, -1), s.Acceleration);
        Near(new Vector3(0, 0, 2f), s.AngularVelocity);
    }

    [Fact]
    public void RecentreTurnsTheHeldPoseIntoLyingFlat()
    {
        var held = Vector3.Normalize(new Vector3(0.1f, -0.7f, -0.7f)); // tilted towards the user, a bit to the right
        Near(new Vector3(0, 0, -1), Vector3.Transform(held, AndroidMotionAxes.Recentre(held)));
        Near(new Vector3(0, 0, -1), Vector3.Transform(new Vector3(0, 0, 1), AndroidMotionAxes.Recentre(new Vector3(0, 0, 1)))); // face down
        Assert.True(AndroidMotionAxes.Recentre(new Vector3(0, 0, -1)).IsIdentity);
        Assert.True(AndroidMotionAxes.Recentre(Vector3.Zero).IsIdentity);
    }

    [Fact]
    public void SensorSourcePairsGyroWithTheLatestAccelerometer()
    {
        var src = new SensorMotionSource(SensorFrame.Phone, "test") { DisplayRotation = 1 };
        src.OnGyroscope(1_000_000, Vector3.Zero); // no acceleration yet: no sample
        Assert.False(src.HasSensors);
        src.OnAccelerometer(new Vector3(G, 0, 0)); // upright in landscape
        Assert.True(src.HasSensors);
        src.OnGyroscope(2_000_000, new Vector3(1, 0, 0));
        src.OnGyroscope(3_000_000, new Vector3(0, 0, 1));
        var buf = new MotionSample[8];
        Assert.Equal(2, src.ReadSamples(buf));
        Assert.Equal(2000UL, buf[0].TimestampUs); // ns -> us
        Assert.Equal(3000UL, buf[1].TimestampUs);
        Near(new Vector3(0, -1, 0), buf[0].Acceleration);
        Near(new Vector3(0, 1, 0), buf[0].AngularVelocity);
        Assert.Equal(0, src.ReadSamples(buf)); // drained
    }

    [Fact]
    public void SensorSourceKeepsTheNewestWhenTheReaderIsBehind()
    {
        var src = new SensorMotionSource(SensorFrame.Phone, "test");
        src.OnAccelerometer(new Vector3(0, 0, G));
        for (int i = 1; i <= SensorMotionSource.Capacity + 10; i++) src.OnGyroscope(i * 1000L, Vector3.Zero);
        var buf = new MotionSample[4];
        Assert.Equal(4, src.ReadSamples(buf));
        Assert.Equal((ulong)(SensorMotionSource.Capacity + 10), buf[3].TimestampUs);
        Assert.Equal((ulong)(SensorMotionSource.Capacity + 7), buf[0].TimestampUs);
    }

    [Fact]
    public void SensorSourceRecentre()
    {
        var src = new SensorMotionSource(SensorFrame.Phone, "test") { DisplayRotation = 1 };
        src.OnAccelerometer(new Vector3(G, 0, 0)); // held upright
        src.Recenter();
        src.OnGyroscope(1000, Vector3.Zero);
        var buf = new MotionSample[1];
        Assert.Equal(1, src.ReadSamples(buf));
        Near(new Vector3(0, 0, -1), buf[0].Acceleration, 1e-3f); // now reads as lying flat
        // re-centring while lying flat clears it
        for (int i = 0; i < 200; i++) src.OnAccelerometer(new Vector3(0, 0, G));
        src.Recenter();
        Assert.True(Quaternion.Dot(Quaternion.Identity, src.RecentreRotation) > 0.9999f);
    }

    sealed class Fake(bool has) : IMotionSource
    {
        public bool HasSensors { get; set; } = has;
        public int ReadSamples(Span<MotionSample> dest) { dest[0] = new MotionSample(42, default, default); return 1; }
    }

    [Fact]
    public void MotionPrefersTheSensorDeviceThenTheKeyboard()
    {
        var keys = new Fake(true);
        var sensors = new SensorMotionSource(SensorFrame.Phone, "phone");
        var m = new AndroidMotion(AndroidMotionMode.Phone) { Sensors = sensors, Fallback = keys };
        var buf = new MotionSample[4];
        // the phone has not delivered yet: the tilt keys drive the Vita
        Assert.True(m.HasSensors);
        Assert.Equal(1, m.ReadSamples(buf));
        Assert.Equal(42UL, buf[0].TimestampUs);
        sensors.OnAccelerometer(new Vector3(0, 0, G));
        sensors.OnGyroscope(7000, Vector3.Zero);
        Assert.Equal(1, m.ReadSamples(buf));
        Assert.Equal(7UL, buf[0].TimestampUs);
        // nothing at all: at rest
        Assert.False(new AndroidMotion(AndroidMotionMode.Controller) { Fallback = new Fake(false) }.HasSensors);
        Assert.False(new AndroidMotion(AndroidMotionMode.Off) { Sensors = sensors, Fallback = keys }.HasSensors);
    }

    [Theory]
    [InlineData("", true, AndroidMotionMode.Phone)]
    [InlineData("", false, AndroidMotionMode.Controller)]
    [InlineData("phone", false, AndroidMotionMode.Phone)]
    [InlineData("controller", true, AndroidMotionMode.Controller)]
    [InlineData("off", true, AndroidMotionMode.Off)]
    [InlineData("junk", true, AndroidMotionMode.Phone)]
    public void MotionSettingResolves(string value, bool phoneHasSensors, AndroidMotionMode expected) =>
        Assert.Equal(expected, AndroidMotionModes.Resolve(value, phoneHasSensors));

    // ---------------------------------------------------------------- camera

    [Fact]
    public void CameraPickFollowsTheVitaCamera()
    {
        var both = new List<(string, int)> { ("0", CameraSupport.LensBack), ("1", CameraSupport.LensFront) };
        Assert.Equal("1", CameraSupport.PickCamera(both, 0));
        Assert.Equal("0", CameraSupport.PickCamera(both, 1));
        var backOnly = new List<(string, int)> { ("5", CameraSupport.LensBack) };
        Assert.Equal("5", CameraSupport.PickCamera(backOnly, 0)); // one camera serves both
        Assert.Equal("9", CameraSupport.PickCamera([("5", CameraSupport.LensBack), ("9", CameraSupport.LensExternal)], 0));
        Assert.Null(CameraSupport.PickCamera([], 1));
    }

    [Theory]
    // the usual phone: back sensor at 90, front at 270; the game screen is landscape (rotation 1 or 3)
    [InlineData(90, false, 1, 0)]
    [InlineData(90, false, 3, 180)]
    [InlineData(270, true, 1, 0)]
    [InlineData(270, true, 3, 180)]
    [InlineData(90, false, 0, 90)]  // portrait
    [InlineData(270, true, 0, 270)]
    [InlineData(0, false, 0, 0)]    // a landscape tablet / TV box
    [InlineData(0, true, 2, 180)]
    public void CameraUprightRotation(int sensor, bool front, int display, int expected) =>
        Assert.Equal(expected, CameraSupport.UprightRotation(sensor, front, display));

    [Fact]
    public void CameraSizeCoversTheRequest()
    {
        (int, int)[] sizes = [(1920, 1080), (1280, 720), (640, 480), (640, 360), (320, 240), (176, 144)];
        Assert.Equal((640, 480), CameraSupport.ChooseSize(sizes, 640, 480));
        Assert.Equal((320, 240), CameraSupport.ChooseSize(sizes, 320, 240));
        Assert.Equal((320, 240), CameraSupport.ChooseSize(sizes, 160, 120));
        Assert.Equal((640, 360), CameraSupport.ChooseSize(sizes, 480, 272)); // 16:9-ish
        Assert.Equal((640, 480), CameraSupport.ChooseSize(sizes, 352, 288)); // CIF: no 11:9 size, the smallest covering
        Assert.Equal((1920, 1080), CameraSupport.ChooseSize(sizes, 4000, 3000)); // nothing covers: the largest
        Assert.Equal((640, 480), CameraSupport.ChooseSize([(480, 640), (640, 480)], 480, 640, rotated: true));
        Assert.Null(CameraSupport.ChooseSize([], 640, 480));
    }

    [Fact]
    public void CameraFpsRangeFollowsTheGame()
    {
        (int, int)[] ranges = [(15, 15), (7, 30), (15, 30), (30, 30), (24, 24)];
        Assert.Equal((30, 30), CameraSupport.ChooseFpsRange(ranges, 30));
        Assert.Equal((15, 15), CameraSupport.ChooseFpsRange(ranges, 15));
        Assert.Equal((15, 30), CameraSupport.ChooseFpsRange(ranges, 20)); // reached by [7, 30] and [15, 30]: the steadier
        Assert.Equal((30, 30), CameraSupport.ChooseFpsRange(ranges, 60)); // beyond the camera: its fastest
        Assert.Null(CameraSupport.ChooseFpsRange([], 30));
    }

    /// <summary>A planar 4:2:0 image with row padding: Y = <paramref name="luma"/>(x, y), neutral chroma unless given.</summary>
    static (byte[] Y, byte[] U, byte[] V) Planar(int w, int h, int pad, Func<int, int, byte> luma, byte u = 128, byte v = 128)
    {
        int ys = w + pad, cs = (w + 1) / 2 + pad;
        var y = new byte[ys * h];
        for (int j = 0; j < h; j++) for (int i = 0; i < w; i++) y[j * ys + i] = luma(i, j);
        var uu = new byte[cs * ((h + 1) / 2)];
        var vv = new byte[cs * ((h + 1) / 2)];
        Array.Fill(uu, u); Array.Fill(vv, v);
        return (y, uu, vv);
    }

    static byte[] Convert((byte[] Y, byte[] U, byte[] V) p, int w, int h, int pad, int rotation)
    {
        var (ow, oh) = CameraSupport.RotatedSize(w, h, rotation);
        var dst = new byte[ow * oh * 4];
        int cs = (w + 1) / 2 + pad;
        CameraSupport.ToRgba(new YuvImageCopy.Plane(p.Y, w + pad, 1), new YuvImageCopy.Plane(p.U, cs, 1), new YuvImageCopy.Plane(p.V, cs, 1), w, h, rotation, dst);
        return dst;
    }

    [Fact]
    public void CameraYuvIsFullRangeBt601()
    {
        var px = Convert(Planar(2, 2, 3, (_, _) => 128), 2, 2, 3, 0);
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, px[..4]);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Convert(Planar(2, 2, 0, (_, _) => 0), 2, 2, 0, 0)[..4]); // not lifted to 16
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Convert(Planar(2, 2, 0, (_, _) => 255), 2, 2, 0, 0)[..4]);
        // pure red in full-range BT.601: Y 76, U 85, V 255
        var red = Convert(Planar(2, 2, 0, (_, _) => 76, 85, 255), 2, 2, 0, 0);
        Assert.InRange(red[0], 250, 255);
        Assert.InRange(red[1], 0, 5);
        Assert.InRange(red[2], 0, 5);
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public void CameraRotationTurnsTheImageClockwise(int rotation)
    {
        const int w = 4, h = 2;
        var img = Planar(w, h, 1, (x, y) => (byte)(10 + y * w + x));
        var dst = Convert(img, w, h, 1, rotation);
        var (ow, oh) = CameraSupport.RotatedSize(w, h, rotation);
        byte At(int x, int y) => dst[(y * ow + x) * 4]; // grey: R = Y
        // where the source's top-left pixel (10) and top-right pixel (13) land after a clockwise turn
        switch (rotation)
        {
            case 0: Assert.Equal(10, At(0, 0)); Assert.Equal(13, At(ow - 1, 0)); break;
            case 90: Assert.Equal(10, At(ow - 1, 0)); Assert.Equal(13, At(ow - 1, oh - 1)); break;
            case 180: Assert.Equal(10, At(ow - 1, oh - 1)); Assert.Equal(13, At(0, oh - 1)); break;
            case 270: Assert.Equal(10, At(0, oh - 1)); Assert.Equal(13, At(0, 0)); break;
        }
    }

    [Fact]
    public void CameraInterleavedChroma()
    {
        // NV21-style memory (V U V U ...) described as two planes of pixel stride 2, rows padded to 8 bytes
        const int w = 4, h = 2, ys = 8;
        var y = new byte[ys * h];
        Array.Fill(y, (byte)128);
        var vu = new byte[ys];
        for (int i = 0; i < w; i += 2) { vu[i] = 255; vu[i + 1] = 128; } // V = 255 (red), U = 128
        var dst = new byte[w * h * 4];
        CameraSupport.ToRgba(new YuvImageCopy.Plane(y, ys, 1), new YuvImageCopy.Plane(vu.AsSpan(1), ys, 2), new YuvImageCopy.Plane(vu, ys, 2), w, h, 0, dst);
        for (int p = 0; p < w * h; p++)
        {
            Assert.Equal(255, dst[p * 4]);       // R pushed to the top by V
            Assert.True(dst[p * 4 + 1] < 128);   // G pulled down
            Assert.Equal(128, dst[p * 4 + 2]);   // B untouched by V
        }
    }
}
