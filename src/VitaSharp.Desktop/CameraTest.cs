using System.Diagnostics;
using System.Runtime.InteropServices;
using VitaSharp.Core.Host;
using VitaSharp.Core.Media;
using VitaSharp.Desktop.Host;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>"camera-test [device] [out-prefix]": grabs one webcam frame through the same host path SceCamera uses and
/// writes the raw host frame plus a 640x480 Vita YUV422-packed round trip as PNGs.
/// "camera-test --cycles=N [device]": starts and stops the camera N times, alternating back and front the way games
/// switch sensors, and prints the process's USER/GDI object counts (Windows) so handle leaks show up.
/// "camera-test --retry [device]" (Windows): fills the process's USER object quota, starts the camera (which fails
/// with ERROR_NO_MORE_USER_HANDLES), frees the quota and checks that the capture recovers by itself.</summary>
public static partial class CameraTest
{
    public static int Run(string[] args)
    {
        var cyclesArg = args.FirstOrDefault(a => a.StartsWith("--cycles=", StringComparison.Ordinal));
        if (cyclesArg != null)
        {
            var rest = args.Where(a => a != cyclesArg).ToArray();
            return Cycles(rest.Length > 0 ? rest[0] : null, int.Parse(cyclesArg["--cycles=".Length..]));
        }
        if (args.Contains("--retry"))
        {
            var rest = args.Where(a => a != "--retry").ToArray();
            return Retry(rest.Length > 0 ? rest[0] : null);
        }
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
        // skip the first frames (auto exposure settles: about 2 s at 30 fps)
        while (stream.Frames.Sequence < 60 && sw.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(20);
        byte[] host = [];
        if (!stream.Frames.CopyLatest(ref host, out int w, out int h, out long seq))
        {
            Console.WriteLine($"no frame received after {sw.Elapsed.TotalSeconds:F1} s");
            return 1;
        }
        Console.WriteLine($"frame #{seq} {w}x{h} after {sw.Elapsed.TotalSeconds:F1} s ({seq / Math.Max(0.1, sw.Elapsed.TotalSeconds):F0} fps)");
        long sum = 0;
        for (int i = 0; i + 2 < host.Length; i += 4) sum += (host[i] * 77 + host[i + 1] * 150 + host[i + 2] * 29) >> 8;
        double luma = sum / Math.Max(1.0, host.Length / 4.0);
        Console.WriteLine($"mean brightness {luma:F0}/255{(luma < 24 ? " (very dark: lens covered, privacy shutter closed or a dark room?)" : "")}");
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

    static int Cycles(string? selection, int cycles)
    {
        var source = FlashCapCamera.TryCreate(selection);
        if (source == null) { Console.WriteLine("camera disabled"); return 1; }
        Console.WriteLine($"devices: {string.Join(", ", source.DeviceNames)}"); // enumerate before the first sample
        var (user0, gdi0) = GuiObjects();
        Console.WriteLine($"before: USER {user0} GDI {gdi0}");
        int failures = 0;
        for (int i = 0; i < cycles; i++)
        {
            var sw = Stopwatch.StartNew();
            var stream = source.Open(i % 2 == 0 ? CameraFacing.Back : CameraFacing.Front, 640, 480, 30);
            if (stream == null) { Console.WriteLine("no camera available"); source.Dispose(); return 1; }
            // a few new frames per start, like a game that switches sensors quickly
            long first = stream.Frames.Sequence;
            while (stream.Frames.Sequence < first + 10 && sw.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(10);
            long frames = stream.Frames.Sequence - first;
            var (userOn, gdiOn) = GuiObjects();
            stream.Dispose();
            // every third cycle the camera stays off past the linger time: the device really closes and reopens
            bool full = i % 3 == 2;
            if (full) Thread.Sleep(3500);
            var (user, gdi) = GuiObjects();
            if (frames == 0) failures++;
            Console.WriteLine($"cycle {i + 1}: {frames} frames in {sw.Elapsed.TotalSeconds:F1} s, USER {userOn}/{user} GDI {gdiOn}/{gdi} (running/{(full ? "closed" : "released")})");
        }
        source.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var (user1, gdi1) = GuiObjects();
        Console.WriteLine($"after {cycles} cycles and closing: USER {user1} ({user1 - user0:+0;-0;0}) GDI {gdi1} ({gdi1 - gdi0:+0;-0;0}), {failures} start(s) without frames");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>USER and GDI objects of this process (0, 0 off Windows).</summary>
    static (int User, int Gdi) GuiObjects()
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        nint self = GetCurrentProcess();
        return ((int)GetGuiResources(self, 1), (int)GetGuiResources(self, 0));
    }

    static int Retry(string? selection)
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("--retry needs Windows"); return 1; }
        using var source = FlashCapCamera.TryCreate(selection);
        if (source == null) { Console.WriteLine("camera disabled"); return 1; }
        _ = source.DeviceNames;
        // empty popup menus are USER objects: allocate them until the per-process quota (10000 by default) is reached
        var menus = new List<nint>();
        for (nint m; menus.Count < 20000 && (m = CreatePopupMenu()) != 0;) menus.Add(m);
        Console.WriteLine($"USER quota filled: {menus.Count} menus, USER {GuiObjects().User}");
        var sw = Stopwatch.StartNew();
        using var stream = source.Open(CameraFacing.Back, 640, 480, 30);
        if (stream == null) { Console.WriteLine("no camera available"); return 1; }
        Thread.Sleep(1500); // the start fails meanwhile (logged), and the capture waits to retry
        long during = stream.Frames.Sequence;
        foreach (var m in menus) DestroyMenu(m);
        Console.WriteLine($"{during} frames while the quota was full; menus freed at {sw.Elapsed.TotalSeconds:F1} s, USER {GuiObjects().User}");
        while (stream.Frames.Sequence < 10 && sw.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(20);
        long after = stream.Frames.Sequence;
        Console.WriteLine(after > 0 ? $"recovered: {after} frames after {sw.Elapsed.TotalSeconds:F1} s" : "not recovered: no frames after 20 s");
        return during == 0 && after > 0 ? 0 : 1;
    }

    [LibraryImport("user32.dll")] private static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyMenu(nint menu);
    [LibraryImport("user32.dll")] private static partial uint GetGuiResources(nint process, uint flags);
    [LibraryImport("kernel32.dll")] private static partial nint GetCurrentProcess();
}
