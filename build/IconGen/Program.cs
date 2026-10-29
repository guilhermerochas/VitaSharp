// IconGen <logo.svg> <out-dir>
// Writes the application icons rendered from the SVG logo:
//   icon-<N>.png    N = 16, 24, 32, 48, 64, 128, 256, 512, 1024 (window/taskbar icons, Linux hicolor theme)
//   VitaSharp.ico   Windows executable icon: 16, 24, 32, 48, 64 as 32-bit DIBs, 128 and 256 as PNG
//   VitaSharp.icns  macOS bundle icon: 16 to 1024 (including the @2x variants), PNG-encoded
// The output is deterministic (same SVG + same SkiaSharp version = same bytes), so regenerating without a logo change
// leaves git clean.
using SkiaSharp;
using Svg.Skia;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: IconGen <logo.svg> <out-dir>");
    return 2;
}
string svgPath = Path.GetFullPath(args[0]), outDir = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outDir);

using var svg = new SKSvg();
if (svg.Load(svgPath) is not { } picture)
{
    Console.Error.WriteLine($"could not load {svgPath}");
    return 1;
}
var cull = picture.CullRect;
Console.WriteLine($"{svgPath}: {cull.Width}x{cull.Height}");

int[] pngSizes = [16, 24, 32, 48, 64, 128, 256, 512, 1024];
var pngs = new Dictionary<int, byte[]>();
var pixels = new Dictionary<int, SKBitmap>();
foreach (int n in pngSizes)
{
    var bmp = Render(picture, cull, n);
    pixels[n] = bmp;
    using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
    pngs[n] = data.ToArray();
    Write($"icon-{n}.png", pngs[n]);
}

Write("VitaSharp.ico", Ico([16, 24, 32, 48, 64, 128, 256]));
Write("VitaSharp.icns", Icns());
return 0;

void Write(string name, byte[] bytes)
{
    File.WriteAllBytes(Path.Combine(outDir, name), bytes);
    Console.WriteLine($"  {name} ({bytes.Length:N0} bytes)");
}

static SKBitmap Render(SKPicture picture, SKRect cull, int n)
{
    // square canvas, logo scaled uniformly and centred (logo.svg is 256x256 already)
    float scale = n / Math.Max(cull.Width, cull.Height);
    var bmp = new SKBitmap(new SKImageInfo(n, n, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    using var surface = SKSurface.Create(new SKImageInfo(n, n, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    c.Translate((n - cull.Width * scale) / 2 - cull.Left * scale, (n - cull.Height * scale) / 2 - cull.Top * scale);
    c.Scale(scale);
    c.DrawPicture(picture);
    c.Flush();
    using var img = surface.Snapshot();
    img.ReadPixels(bmp.Info, bmp.GetPixels(), bmp.RowBytes, 0, 0);
    return bmp;
}

// ICO: header, directory, then one image per size. Small sizes as classic 32-bit BGRA DIBs (every Windows component
// reads those), 128/256 as PNG (smaller, supported since Vista).
byte[] Ico(int[] sizes)
{
    var images = sizes.Select(n => n >= 128 ? pngs[n] : Dib(pixels[n])).ToArray();
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        int n = sizes[i];
        w.Write((byte)(n >= 256 ? 0 : n)); w.Write((byte)(n >= 256 ? 0 : n));
        w.Write((byte)0); w.Write((byte)0);          // palette, reserved
        w.Write((ushort)1); w.Write((ushort)32);     // planes, bits per pixel
        w.Write(images[i].Length); w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var img in images) w.Write(img);
    return ms.ToArray();
}

static byte[] Dib(SKBitmap bmp)
{
    int n = bmp.Width;
    int maskStride = (n + 31) / 32 * 4;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    // BITMAPINFOHEADER; the height covers the XOR (colour) and AND (mask) bitmaps
    w.Write(40); w.Write(n); w.Write(n * 2); w.Write((ushort)1); w.Write((ushort)32);
    w.Write(0); w.Write(n * n * 4 + maskStride * n); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (int y = n - 1; y >= 0; y--) // bottom-up, BGRA, straight alpha
        for (int x = 0; x < n; x++)
        {
            var p = bmp.GetPixel(x, y);
            w.Write(p.Blue); w.Write(p.Green); w.Write(p.Red); w.Write(p.Alpha);
        }
    for (int y = n - 1; y >= 0; y--) // AND mask: 1 = transparent (ignored when alpha is present, kept for old readers)
    {
        var row = new byte[maskStride];
        for (int x = 0; x < n; x++)
            if (bmp.GetPixel(x, y).Alpha == 0) row[x >> 3] |= (byte)(0x80 >> (x & 7));
        w.Write(row);
    }
    return ms.ToArray();
}

// ICNS: "icns" + big-endian total length, then (type, big-endian length incl. 8-byte header, PNG data) entries.
byte[] Icns()
{
    (string Type, int Size)[] entries =
    [
        ("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024),
        ("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512), // @2x of 16, 32, 128, 256
    ];
    using var ms = new MemoryStream();
    void Be(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
    ms.Write("icns"u8);
    Be(8 + entries.Sum(e => 8 + pngs[e.Size].Length));
    foreach (var (type, size) in entries)
    {
        ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
        Be(8 + pngs[size].Length);
        ms.Write(pngs[size]);
    }
    return ms.ToArray();
}
