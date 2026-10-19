using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Host;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>SceCamera HLE calls against guest memory with a fake host camera (struct layouts, state machine, Read).</summary>
[Collection("guest")]
public sealed class CameraHleTests(GuestMemoryFixture fx)
{
    sealed class FakeSource : ICameraSource
    {
        public readonly List<(CameraFacing, int, int, int)> Opens = new();
        public FakeStream? Last;
        public IReadOnlyList<string> DeviceNames => ["fake"];
        public ICameraStream? Open(CameraFacing facing, int width, int height, int fps)
        {
            Opens.Add((facing, width, height, fps));
            Last = new FakeStream();
            var img = new byte[64 * 48 * 4];
            for (int i = 0; i < img.Length; i += 4) { img[i] = 200; img[i + 1] = 100; img[i + 2] = 50; img[i + 3] = 255; }
            Last.Frames.Publish(img, 64, 48);
            return Last;
        }
        public void Dispose() { }
    }

    sealed class FakeStream : ICameraStream
    {
        public CameraFrameBuffer Frames { get; } = new();
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    const int PARAM = unchecked((int)0x802E0000), NOT_OPEN = unchecked((int)0x802E0004), NOT_START = unchecked((int)0x802E0006),
        ALREADY_OPEN = unchecked((int)0x802E0003), FORMAT_UNKNOWN = unchecked((int)0x802E0007), RES_UNKNOWN = unchecked((int)0x802E0008),
        BAD_FPS = unchecked((int)0x802E0009), ATTR_UNKNOWN = unchecked((int)0x802E000C), OTHER_STARTED = unchecked((int)0x802E0012);

    static int Call(Camera cam, string name, params object[] args) =>
        (int)typeof(Camera).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cam, args)!;

    void WriteInfo(uint info, ushort format, ushort res, ushort fps, uint buf, uint bufSize)
    {
        GuestMemory.Fill(info, 0, 0x30);
        GuestMemory.W32(info + 0x00, 0x30);
        GuestMemory.Write<ushort>(info + 0x06, format);
        GuestMemory.Write<ushort>(info + 0x08, res);
        GuestMemory.Write<ushort>(info + 0x0A, fps);
        GuestMemory.W32(info + 0x14, bufSize);
        GuestMemory.W32(info + 0x20, buf);
    }

    [Fact]
    public void OpenStartReadStopClose()
    {
        var mem = fx.Memory;
        uint info = mem.Alloc(0x1000, "caminfo");
        uint read = mem.Alloc(0x1000, "camread");
        const int w = 320, h = 240, size = w * h * 4;
        uint buf = mem.Alloc(size + 0x1000, "cambuf");
        var src = new FakeSource();
        var cam = new Camera { HostOverride = new HostServices { Camera = src } };
        try
        {
            GuestMemory.Fill(info, 0xEE, 0x40);
            WriteInfo(info, 5 /* ABGR */, 2 /* 320x240 */, 3 /* 3.75 fps: wide frame period keeps the no-wait check deterministic */, buf, size);
            Assert.Equal(PARAM, Call(cam, "sceCameraOpen", 2, info));
            Assert.Equal(PARAM, Call(cam, "sceCameraOpen", 0, 0u));
            GuestMemory.Write<ushort>(info + 6, 7);
            Assert.Equal(FORMAT_UNKNOWN, Call(cam, "sceCameraOpen", 0, info));
            GuestMemory.Write<ushort>(info + 6, 5);
            GuestMemory.Write<ushort>(info + 8, 7);
            Assert.Equal(RES_UNKNOWN, Call(cam, "sceCameraOpen", 0, info));
            GuestMemory.Write<ushort>(info + 8, 2);
            GuestMemory.Write<ushort>(info + 0xA, 25);
            Assert.Equal(BAD_FPS, Call(cam, "sceCameraOpen", 0, info));
            GuestMemory.Write<ushort>(info + 0xA, 3);

            Assert.Equal(0, Call(cam, "sceCameraOpen", 0, info));
            Assert.Equal((ushort)w, GuestMemory.Read<ushort>(info + 0x0C));
            Assert.Equal((ushort)h, GuestMemory.Read<ushort>(info + 0x0E));
            Assert.Equal(0xEEu, (uint)GuestMemory.Read<byte>(info + 0x30)); // nothing written past SceCameraInfo
            Assert.Equal(ALREADY_OPEN, Call(cam, "sceCameraOpen", 0, info));

            Assert.Equal(NOT_START, Call(cam, "sceCameraSetBrightness", 0, 200));
            Assert.Equal(NOT_START, Call(cam, "sceCameraIsActive", 0));
            Assert.Equal(0, Call(cam, "sceCameraStart", 0));
            Assert.Equal((CameraFacing.Front, w, h, 4), Assert.Single(src.Opens));
            Assert.Equal(1, Call(cam, "sceCameraIsActive", 0));
            Assert.Equal(NOT_OPEN, Call(cam, "sceCameraIsActive", 1));

            uint val = read + 0x100;
            Assert.Equal(0, Call(cam, "sceCameraGetBrightness", 0, val));
            Assert.Equal(128u, GuestMemory.R32(val));
            Assert.Equal(0, Call(cam, "sceCameraSetBrightness", 0, 200));
            Assert.Equal(0, Call(cam, "sceCameraGetBrightness", 0, val));
            Assert.Equal(200u, GuestMemory.R32(val));
            Assert.Equal(ATTR_UNKNOWN, Call(cam, "sceCameraGetGain", 0, val));

            // the other sensor cannot start while this one runs
            Assert.Equal(0, Call(cam, "sceCameraOpen", 1, info));
            Assert.Equal(OTHER_STARTED, Call(cam, "sceCameraStart", 1));
            Assert.Equal(0, Call(cam, "sceCameraClose", 1));

            // Read, waiting for the next frame
            GuestMemory.Fill(read, 0xEE, 0x40);
            GuestMemory.Fill(buf, 0, (uint)size + 16);
            GuestMemory.Fill(buf + size, 0xCC, 16);
            GuestMemory.W32(read + 0x00, 0x38);
            GuestMemory.W32(read + 0x04, 0); // WAIT_NEXTFRAME_ON
            Assert.Equal(0, Call(cam, "sceCameraRead", 0, read));
            Assert.Equal(0u, GuestMemory.R32(read + 0x0C)); // status ACTIVE
            ulong frame = GuestMemory.Read<ulong>(read + 0x10);
            Assert.True(frame >= 1);
            Assert.True(GuestMemory.Read<ulong>(read + 0x18) > 0);
            Assert.Equal(0xEEu, (uint)GuestMemory.Read<byte>(read + 0x38)); // nothing written past SceCameraRead
            var px = GuestMemory.Span(buf, size);
            for (int i = 0; i < size; i += 4) { Assert.Equal(200, px[i]); Assert.Equal(100, px[i + 1]); Assert.Equal(50, px[i + 2]); Assert.Equal(255, px[i + 3]); }
            Assert.Equal(0xCC, GuestMemory.Read<byte>(buf + size)); // buffer end respected

            // no-wait read right after: same frame, ALREADY_READ
            GuestMemory.W32(read + 0x04, 1);
            Assert.Equal(0, Call(cam, "sceCameraRead", 0, read));
            Assert.Equal(1u, GuestMemory.R32(read + 0x0C));
            Assert.Equal(frame, GuestMemory.Read<ulong>(read + 0x10));

            // waiting read delivers a newer frame
            GuestMemory.W32(read + 0x04, 0);
            Assert.Equal(0, Call(cam, "sceCameraRead", 0, read));
            Assert.True(GuestMemory.Read<ulong>(read + 0x10) > frame);

            Assert.Equal(0, Call(cam, "sceCameraStop", 0));
            Assert.True(src.Last!.Disposed);
            Assert.Equal(NOT_START, Call(cam, "sceCameraRead", 0, read));
            Assert.Equal(0, Call(cam, "sceCameraClose", 0));
            Assert.Equal(NOT_OPEN, Call(cam, "sceCameraClose", 0));
            Assert.Equal(0, Call(cam, "sceCameraOpen", 0, info)); // reopen after close
            Assert.Equal(0, Call(cam, "sceCameraClose", 0));
        }
        finally
        {
            mem.Free(info); mem.Free(read); mem.Free(buf);
        }
    }

    [Fact]
    public void GreyFramesWithoutHostCamera()
    {
        var mem = fx.Memory;
        uint info = mem.Alloc(0x1000, "caminfo");
        uint read = mem.Alloc(0x1000, "camread");
        const int w = 160, h = 120;
        uint y = mem.Alloc(0x10000, "y"), u = mem.Alloc(0x10000, "u"), v = mem.Alloc(0x10000, "v");
        var cam = new Camera { HostOverride = new HostServices() };
        try
        {
            WriteInfo(info, 3 /* YUV420 planar */, 3 /* 160x120 */, 30, 0, 0);
            GuestMemory.W32(info + 0x10, 0); // range FULL
            GuestMemory.Write<ushort>(info + 0x2E, 1); // SETBYREAD
            Assert.Equal(0, Call(cam, "sceCameraOpen", 0, info));
            Assert.Equal(0, Call(cam, "sceCameraStart", 0));
            GuestMemory.W32(read + 0x00, 0x38);
            GuestMemory.W32(read + 0x04, 0);
            GuestMemory.W32(read + 0x2C, 0); // SETBYREAD needs buffers in the read struct
            Assert.Equal(PARAM, Call(cam, "sceCameraRead", 0, read));
            GuestMemory.W32(read + 0x20, w * h); GuestMemory.W32(read + 0x24, w * h / 4); GuestMemory.W32(read + 0x28, w * h / 4);
            GuestMemory.W32(read + 0x2C, y); GuestMemory.W32(read + 0x30, u); GuestMemory.W32(read + 0x34, v);
            GuestMemory.Fill(y, 0, 0x10000); GuestMemory.Fill(u, 0, 0x10000); GuestMemory.Fill(v, 0, 0x10000);
            Assert.Equal(0, Call(cam, "sceCameraRead", 0, read));
            Assert.All(GuestMemory.Span(y, w * h).ToArray(), b => Assert.Equal(128, b));
            Assert.All(GuestMemory.Span(u, w * h / 4).ToArray(), b => Assert.Equal(128, b));
            Assert.All(GuestMemory.Span(v, w * h / 4).ToArray(), b => Assert.Equal(128, b));
            Assert.Equal(0, GuestMemory.Read<byte>(u + w * h / 4)); // chroma plane size respected
            Assert.Equal(0, Call(cam, "sceCameraClose", 0)); // close while started stops first
        }
        finally
        {
            mem.Free(info); mem.Free(read); mem.Free(y); mem.Free(u); mem.Free(v);
        }
    }
}
