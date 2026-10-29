using System.Reflection;
using SkiaSharp;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Host;

/// <summary>
/// The VitaSharp logo as window/taskbar icon, from the PNGs and .ico embedded in the executable (rendered from
/// docs/assets/logo.svg by build/make-icons.ps1). The executable's own icon (Explorer, Alt+Tab before a window
/// exists) is the ApplicationIcon of the project; on macOS the bundle's VitaSharp.icns is used instead and the
/// per-window icons below are ignored by the OS.
/// </summary>
static class AppIcon
{
    static readonly int[] Sizes = [16, 24, 32, 48, 64, 128, 256];

    static Stream? Open(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    /// <summary>Launcher windows: the multi-size .ico on Windows (crisp at every DPI), the 256 px PNG elsewhere.</summary>
    public static Avalonia.Controls.WindowIcon? ForAvalonia()
    {
        try
        {
            using var s = Open(OperatingSystem.IsWindows() ? "VitaSharp.ico" : "icon-256.png");
            return s == null ? null : new Avalonia.Controls.WindowIcon(s);
        }
        catch (Exception e)
        {
            Log.Warn($"Launcher: could not load the window icon: {e.Message}");
            return null;
        }
    }

    /// <summary>Game window (GLFW): every size as straight-alpha RGBA, GLFW picks the closest one per use.</summary>
    public static Silk.NET.Core.RawImage[] ForGlfw()
    {
        var list = new List<Silk.NET.Core.RawImage>();
        foreach (int n in Sizes)
        {
            using var s = Open($"icon-{n}.png");
            if (s == null) continue;
            using var bmp = SKBitmap.Decode(s, new SKImageInfo(n, n, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            if (bmp == null) continue;
            list.Add(new Silk.NET.Core.RawImage(bmp.Width, bmp.Height, bmp.Bytes));
        }
        return list.ToArray();
    }
}
