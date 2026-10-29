using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VitaSharp.Core.Shell;

namespace VitaSharp.Desktop.Launcher.Shell;

/// <summary>An app on the home screen: an installed game or a system app of the firmware.</summary>
public sealed record ShellApp(string TitleId, string Title, string Version, string Path, string? IconPath, bool IsSystem, GameEntry? Game);

/// <summary>
/// Bitmaps for the home screen and LiveArea: the theme wallpaper and shell textures from the firmware (decoded from
/// the RCO GIMs), icons and LiveArea images from the apps, all cached. Without firmware the wallpaper is null and the
/// views draw their own default look.
/// </summary>
public sealed class ShellVisuals
{
    public ShellAssets Assets { get; }
    public ShellTheme? Theme { get; private set; }
    public Bitmap? Wallpaper { get; private set; }
    readonly Dictionary<string, Bitmap?> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Bitmap?> _textures = new();

    public ShellVisuals(string fsRoot, string themeId)
    {
        Assets = new ShellAssets(fsRoot);
        SetTheme(themeId);
    }

    public void SetTheme(string themeId)
    {
        var themes = Assets.Themes(LiveAreaTemplate.VitaLanguage(System.Globalization.CultureInfo.CurrentUICulture));
        Theme = themes.FirstOrDefault(t => t.Id == themeId) ?? themes.FirstOrDefault();
        Wallpaper = Theme?.HomeBackgrounds.Count > 0 ? File(Theme.HomeBackgrounds[0]) : null;
    }

    /// <summary>An image file (PNG/JPEG), cached; null when missing or not decodable.</summary>
    public Bitmap? File(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_files.TryGetValue(path, out var b)) return b;
        try { b = System.IO.File.Exists(path) ? new Bitmap(path) : null; }
        catch (Exception) { b = null; }
        _files[path] = b;
        return b;
    }

    /// <summary>A shell RCO texture, or null without firmware.</summary>
    public Bitmap? Texture(string rco, string id)
    {
        string key = rco + "|" + id;
        if (_textures.TryGetValue(key, out var b)) return b;
        b = Assets.Texture(rco, id) is { } img ? ToBitmap(img) : null;
        _textures[key] = b;
        return b;
    }

    public static unsafe WriteableBitmap ToBitmap(RgbaImage img)
    {
        var wb = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = wb.Lock();
        for (int y = 0; y < img.Height; y++)
            Marshal.Copy(img.Pixels, y * img.Width * 4, fb.Address + y * fb.RowBytes, img.Width * 4);
        return wb;
    }

    /// <summary>The colour (0xAARRGGBB) as an Avalonia colour.</summary>
    public static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>The look without firmware: a deep blue gradient with soft light, like the Vita's default theme.</summary>
    public static readonly IBrush DefaultWallpaper = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#0716B8"), 0),
            new GradientStop(Color.Parse("#0F2FD6"), 0.5),
            new GradientStop(Color.Parse("#1748D8"), 1),
        },
    };
}
