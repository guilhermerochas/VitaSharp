using System.Diagnostics;
using VitaSharp.Core.Host;
using VitaSharp.Core.Media;
using VitaSharp.Desktop.Host;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>"camera-test [device] [out-prefix]": grabs one webcam frame through the same host path SceCamera uses and
/// writes the raw host frame plus a 640x480 Vita YUV422-packed round trip as PNGs.</summary>
public static class CameraTest
{
    public static int Run(string[] args)
    {
        string? selection = args.Length > 0 ? args[0] : null;
        string prefix = args.Length > 1 ? args[1] : Path.Combine(Environment.CurrentDirectory, "camera-test");
        var devices = FlashCapCamera.ListDevices();
        Console.WriteLine($"{devices.Count} capture device(s):");
        for (int i = 0; i < devices.Count; i++) Console.WriteLine($"  [{i}] {devices[i].Name} ({devices[i].DeviceType})");
        using var source = FlashCapCamera.TryCreate(selection);
        if (source == null) { Console.WriteLine("camera disabled"); return 1; }
        var sw = Stopwatch.StartNew();
        using var stream = source.Open(CameraFacing.Front, 640, 480, 30);
        if (stream == null) { Console.WriteLine("no camera available"); return 1; }
        // skip the first frames (auto exposure settles)
        while (stream.Frames.Sequence < 10 && sw.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(20);
        byte[] host = [];
        if (!stream.Frames.CopyLatest(ref host, out int w, out int h, out long seq))
        {
            Console.WriteLine($"no frame received after {sw.Elapsed.TotalSeconds:F1} s");
            return 1;
        }
        Console.WriteLine($"frame #{seq} {w}x{h} after {sw.Elapsed.TotalSeconds:F1} s");
        Png.Write(prefix + "-host.png", host, w, h);

        // Vita pipeline: centre crop + scale to the guest resolution, encode as YUV422 packed, decode back
        const int vw = 640, vh = 480;
        var scaled = new byte[vw * vh * 4];
        CameraImage.Resample(host, w, h, scaled, vw, vh);
        var yuyv = new byte[vw * vh * 2];
        CameraImage.Encode(scaled, vw, vh, VitaCameraFormat.Yuv422Packed, false, vw, yuyv, default, default);
        var back = new byte[vw * vh * 4];
        CameraImage.DecodeToRgba(yuyv, CameraHostFormat.Yuyv, vw, vh, false, back);
        Png.Write(prefix + "-vita-yuv422.png", back, vw, vh);
        Console.WriteLine($"wrote {prefix}-host.png and {prefix}-vita-yuv422.png");
        return 0;
    }
}
