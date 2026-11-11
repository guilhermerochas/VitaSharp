using VitaSharp.Core.Media;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Host;

/// <summary>What one Vita camera shows: the host's default device, nothing (grey), a still picture or a named device.</summary>
public enum CameraSpecKind
{
    Default, None, Image, Device
}

/// <summary>
/// The source chosen for one Vita camera ("Camera frontal" / "Camera traseira", --camera-front= / --camera-back=):
/// "" = the default device, "none" = grey frames, a path to a .png/.jpg/.jpeg file = that still picture, anything else =
/// a host device by name (exact or substring) or 0-based index.
/// </summary>
public readonly record struct CameraSpec(CameraSpecKind Kind, string Value)
{
    public static readonly CameraSpec Default = new(CameraSpecKind.Default, "");
    public static readonly CameraSpec None = new(CameraSpecKind.None, "none");

    public static CameraSpec Parse(string? value)
    {
        var v = value?.Trim().Trim('"') ?? "";
        if (v.Length == 0)
        {
            return Default;
        }

        if (v.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        if (IsImagePath(v))
        {
            return new(CameraSpecKind.Image, v);
        }

        return new(CameraSpecKind.Device, v);
    }

    /// <summary>The picture files a camera can show (PNG 8-bit RGB/RGBA, baseline/progressive JPEG).</summary>
    public static bool IsImagePath(string v) =>
        v.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || v.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || v.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    /// <summary>The value for each camera from the run options: --camera= sets both, --camera-front= / --camera-back=
    /// override one (null when the option is absent).</summary>
    public static (string? Front, string? Back) FromOptions(string? both, string? front, string? back) => (front ?? both, back ?? both);

    /// <summary>
    /// Index into <paramref name="names"/> of the device for a Vita camera, -1 = none. The front camera's default is
    /// the first device; the back camera's default is the first device other than the front camera's, else the
    /// front camera's (one webcam serves both).
    /// </summary>
    public static int SelectDevice(IReadOnlyList<string> names, string? front, string? back, CameraFacing facing)
    {
        if (names.Count == 0)
        {
            return -1;
        }

        if (facing == CameraFacing.Front)
        {
            return Match(names, front) ?? (front == null ? 0 : -1);
        }

        if (back != null)
        {
            return Match(names, back) ?? -1;
        }

        int f = Match(names, front) ?? (front == null ? 0 : -1);
        for (int i = 0; i < names.Count; i++)
        {
            if (i != f)
            {
                return i;
            }
        }

        return f < 0 ? 0 : f;

        static int? Match(IReadOnlyList<string> names, string? sel)
        {
            if (sel == null)
            {
                return null;
            }

            if (int.TryParse(sel, out int idx) && idx >= 0 && idx < names.Count)
            {
                return idx;
            }

            for (int i = 0; i < names.Count; i++)
            {
                if (names[i].Equals(sel, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            for (int i = 0; i < names.Count; i++)
            {
                if (names[i].Contains(sel, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return null;
        }
    }
}

/// <summary>
/// Routes each Vita camera to its own host source (SCE_CAMERA_DEVICE_FRONT = 0, BACK = 1). A null source delivers grey
/// frames. Both cameras may share one source (one webcam for both: its capture is reused across a sensor switch).
/// </summary>
public sealed class FacingCameras : ICameraSource
{
    readonly ICameraSource?[] _sources;

    public FacingCameras(ICameraSource? front, ICameraSource? back) => _sources = [front, back];

    public ICameraSource? this[CameraFacing facing] => _sources[(int)facing & 1];

    public IReadOnlyList<string> DeviceNames =>
        _sources.Where(s => s != null).Distinct().SelectMany(s => s!.DeviceNames).Distinct().ToList();

    public ICameraStream? Open(CameraFacing facing, int width, int height, int fps)
    {
        var src = this[facing];
        if (src == null)
        {
            Log.Info($"Camera: {facing} camera set to none, grey frames");
            return null;
        }
        return src.Open(facing, width, height, fps);
    }

    public void Dispose()
    {
        foreach (var s in _sources.Where(s => s != null).Distinct())
        {
            s!.Dispose();
        }
    }
}

/// <summary>A still picture as a Vita camera (tells the cameras apart, games without a webcam, repeatable tests).</summary>
public sealed class StillImageCamera : ICameraSource
{
    readonly string _name;
    readonly byte[] _rgba;
    readonly int _w, _h;

    public StillImageCamera(string name, byte[] rgba, int width, int height)
    {
        _name = name;
        _rgba = rgba;
        _w = width;
        _h = height;
    }

    public int Width => _w;
    public int Height => _h;

    /// <summary>Loads a PNG or JPEG picture; null (and a log line) when it cannot be read.</summary>
    public static StillImageCamera? TryLoad(string path)
    {
        try
        {
            var (rgba, w, h) = Decode(File.ReadAllBytes(path), path);
            return new StillImageCamera(Path.GetFileName(path), rgba, w, h);
        }
        catch (Exception e)
        {
            Log.Warn($"Camera: picture '{path}' could not be read: {e.Message} (grey frames)");
            return null;
        }
    }

    /// <summary>RGBA pixels of a PNG (8-bit RGB/RGBA) or JPEG file.</summary>
    public static (byte[] Rgba, int W, int H) Decode(byte[] data, string pathForErrors)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
        {
            return PngReader.Decode(data, pathForErrors);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
        {
            return JpegToRgba(JpegDecoder.Decode(data));
        }

        throw new InvalidDataException($"{pathForErrors}: not a PNG or JPEG file");
    }

    /// <summary>JFIF YCbCr (full range, BT.601) or grayscale planes to RGBA, chroma upsampled by nearest neighbour.</summary>
    public static (byte[] Rgba, int W, int H) JpegToRgba(JpegImage img)
    {
        int w = img.Info.Width, h = img.Info.Height;
        var rgba = new byte[w * h * 4];
        var y = img.Planes[0];
        bool gray = img.Info.Gray || img.Planes.Length < 3;
        int hd = img.Info.HDiv, vd = img.Info.VDiv;
        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++)
            {
                int Y = y.Data[r * y.Stride + c];
                int o = (r * w + c) * 4;
                if (gray)
                {
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = (byte)Y;
                }
                else
                {
                    var cbP = img.Planes[1];
                    var crP = img.Planes[2];
                    int ci = r / vd * cbP.Stride + c / hd;
                    int cb = cbP.Data[ci] - 128, cr = crP.Data[ci] - 128;
                    rgba[o] = Clamp(Y + (91881 * cr >> 16));
                    rgba[o + 1] = Clamp(Y - ((22554 * cb + 46802 * cr) >> 16));
                    rgba[o + 2] = Clamp(Y + (116130 * cb >> 16));
                }
                rgba[o + 3] = 255;
            }
        }

        return (rgba, w, h);

        static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
    }

    public IReadOnlyList<string> DeviceNames => [_name];

    public ICameraStream? Open(CameraFacing facing, int width, int height, int fps)
    {
        var s = new Stream();
        s.Frames.Publish(_rgba, _w, _h);
        Log.Info($"Camera: {facing} camera -> picture '{_name}' {_w}x{_h}");
        return s;
    }

    public void Dispose()
    {
    }

    sealed class Stream : ICameraStream
    {
        public CameraFrameBuffer Frames { get; } = new();
        public void Dispose()
        {
        }
    }
}
