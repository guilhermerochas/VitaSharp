namespace VitaSharp.Android.Media;

/// <summary>
/// The platform-neutral half of the Android camera backend (Camera2): which camera serves which Vita camera, the
/// capture size and frame-rate range for a request, the rotation that makes the sensor's image upright on the
/// landscape screen, and the YUV_420_888 to RGBA conversion (with that rotation) into the core's
/// <see cref="VitaSharp.Core.Host.CameraFrameBuffer"/>; the core then crops/scales it and converts it to the format
/// the game asked for (CameraImage). Pure functions, unit-tested.
/// </summary>
public static class CameraSupport
{
    /// <summary>CameraCharacteristics.LENS_FACING values.</summary>
    public const int LensFront = 0, LensBack = 1, LensExternal = 2;

    /// <summary>
    /// The camera for a Vita camera (<paramref name="vitaFacing"/> 0 = front, 1 = back): the first one facing the same
    /// way, else an external one, else any (a phone or TV box with a single camera still serves both Vita cameras, as
    /// the desktop's one webcam does). <paramref name="cameras"/> = (id, LENS_FACING) in CameraManager order.
    /// </summary>
    public static string? PickCamera(IReadOnlyList<(string Id, int Facing)> cameras, int vitaFacing)
    {
        int want = vitaFacing == 0 ? LensFront : LensBack;
        foreach (var c in cameras) if (c.Facing == want) return c.Id;
        foreach (var c in cameras) if (c.Facing == LensExternal) return c.Id;
        return cameras.Count > 0 ? cameras[0].Id : null;
    }

    /// <summary>
    /// The clockwise rotation (0, 90, 180, 270) that makes a camera's image upright on the screen:
    /// SENSOR_ORIENTATION is the clockwise rotation of the sensor image against the device's natural orientation, and
    /// <paramref name="displayRotation"/> (Display.getRotation, 0..3) the counter-clockwise rotation of the device.
    /// The image is not mirrored (the Vita's front camera delivers it as the sensor sees it; games mirror it with
    /// SCE_CAMERA_REVERSE), so a front camera adds the device rotation (the JPEG_ORIENTATION rule of the Camera2 docs).
    /// </summary>
    public static int UprightRotation(int sensorOrientation, bool front, int displayRotation)
    {
        int device = (displayRotation & 3) * 90;
        int r = front ? sensorOrientation + device : sensorOrientation - device;
        return ((r % 360) + 360) % 360 / 90 * 90;
    }

    /// <summary>
    /// The capture size for a request of <paramref name="width"/>x<paramref name="height"/> (the size after the
    /// upright rotation; rotated sizes are compared swapped): among the sizes that cover it, the closest aspect ratio
    /// (the least cropped away), then the smallest; the largest when none covers it. Small sizes keep the per-frame
    /// conversion cheap.
    /// </summary>
    public static (int W, int H)? ChooseSize(IReadOnlyList<(int W, int H)> sizes, int width, int height, bool rotated = false)
    {
        if (sizes.Count == 0) return null;
        if (rotated) (width, height) = (height, width);
        double aspect = (double)width / Math.Max(1, height);
        bool Covers((int W, int H) s) => s.W >= width && s.H >= height;
        double AspectError((int W, int H) s) => Math.Round(Math.Abs((double)s.W / s.H - aspect), 2);
        long Area((int W, int H) s) => (long)s.W * s.H;
        var valid = sizes.Where(s => s.W > 0 && s.H > 0).ToList();
        if (valid.Count == 0) return null;
        var pick = valid.Where(Covers).OrderBy(AspectError).ThenBy(Area).FirstOrDefault();
        if (pick != default) return pick;
        return valid.OrderByDescending(Area).First();
    }

    /// <summary>
    /// The CONTROL_AE_TARGET_FPS_RANGE for the rate the game asked for: a range that reaches it, the highest minimum
    /// first (steady timing; [30, 30] for 30), then the lowest maximum (no sensor work beyond what the game reads).
    /// Without one reaching it, the range with the highest maximum.
    /// </summary>
    public static (int Lo, int Hi)? ChooseFpsRange(IReadOnlyList<(int Lo, int Hi)> ranges, int fps)
    {
        if (ranges.Count == 0) return null;
        fps = Math.Max(1, fps);
        var reach = ranges.Where(r => r.Hi >= fps && r.Lo <= fps).ToList();
        if (reach.Count > 0) return reach.OrderByDescending(r => r.Lo).ThenBy(r => r.Hi).First();
        var above = ranges.Where(r => r.Lo >= fps).ToList();
        if (above.Count > 0) return above.OrderBy(r => r.Lo).ThenBy(r => r.Hi).First();
        return ranges.OrderByDescending(r => r.Hi).ThenByDescending(r => r.Lo).First();
    }

    /// <summary>Size of the image after a clockwise <paramref name="rotation"/> of a <paramref name="width"/>x<paramref name="height"/> one.</summary>
    public static (int W, int H) RotatedSize(int width, int height, int rotation) =>
        rotation is 90 or 270 ? (height, width) : (width, height);

    /// <summary>
    /// Converts a YUV_420_888 image (planes with their row and pixel strides, as android.media.Image gives them; U and
    /// V interleaved when the pixel stride is 2) to RGBA, rotated clockwise by <paramref name="rotation"/> (0, 90,
    /// 180, 270) into <paramref name="dst"/> of <see cref="RotatedSize"/> x 4 bytes. Camera YUV is full range
    /// BT.601 (JFIF), unlike the limited-range video of the webcams the core's CameraImage decodes.
    /// </summary>
    public static void ToRgba(YuvImageCopy.Plane y, YuvImageCopy.Plane u, YuvImageCopy.Plane v, int width, int height, int rotation, Span<byte> dst)
    {
        var (ow, oh) = RotatedSize(width, height, rotation);
        if (dst.Length < ow * oh * 4) throw new ArgumentException("destination too small", nameof(dst));
        for (int oy = 0; oy < oh; oy++)
        {
            var o = dst.Slice(oy * ow * 4, ow * 4);
            for (int ox = 0; ox < ow; ox++)
            {
                int sx, sy;
                switch (rotation)
                {
                    case 90: sx = oy; sy = height - 1 - ox; break;
                    case 180: sx = width - 1 - ox; sy = height - 1 - oy; break;
                    case 270: sx = width - 1 - oy; sy = ox; break;
                    default: sx = ox; sy = oy; break;
                }
                int luma = y.Data[sy * y.RowStride + sx * y.PixelStride];
                int cx = sx >> 1, cy = sy >> 1;
                int cb = u.Data[cy * u.RowStride + cx * u.PixelStride] - 128;
                int cr = v.Data[cy * v.RowStride + cx * v.PixelStride] - 128;
                int i = ox * 4;
                int l = luma << 8;
                o[i] = Clamp((l + 359 * cr + 128) >> 8);
                o[i + 1] = Clamp((l - 88 * cb - 183 * cr + 128) >> 8);
                o[i + 2] = Clamp((l + 454 * cb + 128) >> 8);
                o[i + 3] = 255;
            }
        }
    }

    static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
