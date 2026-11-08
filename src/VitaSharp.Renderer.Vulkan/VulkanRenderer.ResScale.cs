using Silk.NET.Vulkan;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Internal resolution below native (VITASHARP_RES_SCALE=0.75 or 0.5, for slow phone GPUs) or above it (2, 3 or 4: the
/// desktop's "Resolução interna" per game, like PCSX2's upscaling); native by default. Like Vita3K's resolution
/// multiplier: colour targets and their depth/stencil images are created at <see cref="ResScale"/> times the guest
/// size, scenes render with the viewport, scissor, render area and line width scaled (shaders see gl_FragCoord in
/// guest pixels through res_multiplier), and textures that sample a target read the scaled image directly through
/// normalised coordinates. Fragment work and attachment bandwidth drop with the pixel count.
/// <para>
/// Guest pixel coordinates stay the API of every target (<see cref="RenderTarget.Width"/>): transfers that need guest
/// layout (writebacks, the CDRAM mirror that reinterpreted and mapped reads use, frame dumps) go through a native-size
/// copy (<see cref="NativeImageOf"/>, a nearest blit), and the guest memory a target starts from is uploaded at native
/// size and blitted down. Region, scene-start and framebuffer-fetch copies work in scaled pixels. Depth and stencil
/// planes of scaled scenes are not mirrored for mapped reads (those read guest memory, as with an unsynchronised
/// surface in Vita3K). Above native, stencil textures go through a scratch buffer (the scaled plane is larger than the
/// guest surface), so do the packed scaled rows of reinterpreted reads, and frame dumps/screenshots are taken at the internal
/// resolution. Targets smaller than <see cref="ResScaleMinSize"/> pixels in either direction, and formats the
/// GPU cannot blit, stay native.
/// </para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>VITASHARP_RES_SCALE: 1 (native) unless set to a value in [0.25, 1) or to a whole factor 2-4.</summary>
    static readonly float EnvResScale = ParseResScale(Environment.GetEnvironmentVariable(EnvVars.ResScale));

    /// <summary>This renderer's scale: <see cref="VulkanRendererOptions.ResScale"/> when set, else <see cref="EnvResScale"/>.</summary>
    internal readonly float ResScale;

    /// <summary>Largest internal resolution factor (4x: 3840x2176 for a 960x544 surface).</summary>
    internal const float MaxResScale = 4f;

    /// <summary>Targets with a side below this stay at native size (lookup tables, luminance reductions).</summary>
    const uint ResScaleMinSize = 32;

    internal static float ParseResScale(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 1f;
        if (!float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) return 1f;
        if (v is >= 0.25f and < 1f) return v;
        // above native only whole factors (PCSX2's 2x-4x): every guest pixel is a block of attachment pixels, so
        // scissors, region copies and fetch copies stay on pixel edges
        float k = MathF.Round(v);
        return k is >= 2f and <= MaxResScale && MathF.Abs(v - k) < 0.01f ? k : 1f;
    }

    /// <summary>A guest dimension at scale <paramref name="s"/> (at least 1 pixel).</summary>
    static uint ScaleDim(uint v, float s) => s == 1f ? v : Math.Max(1u, (uint)MathF.Round(v * s));

    readonly Dictionary<Format, bool> _blitOk = new();

    /// <summary>The scale a new colour target of this size and host format gets.</summary>
    float TargetScale(uint w, uint h, Format f)
    {
        if (ResScale == 1f || w < ResScaleMinSize || h < ResScaleMinSize) return 1f;
        if (!_blitOk.TryGetValue(f, out bool ok))
        {
            _core.Vk.GetPhysicalDeviceFormatProperties(_core.Physical, f, out var props);
            const FormatFeatureFlags need = FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit;
            _blitOk[f] = ok = (props.OptimalTilingFeatures & need) == need;
            if (!ok) Log.Warn($"[gpu] resolution scale: {f} cannot be blitted, its targets stay at native size");
        }
        return ok ? ResScale : 1f;
    }

    /// <summary>Scale of the current scene's attachments (1 without a colour target or for a native one).</summary>
    float RtScale => _rt?.Scale ?? 1f;

    /// <summary>A scene-space length (guest pixels) in attachment pixels at <paramref name="scale"/>.</summary>
    static uint ScaledLen(uint v, float scale) => scale == 1f ? v : scale == 2f ? v * 2 : (uint)MathF.Round(v * scale);

    /// <summary>Start (floor) and end (ceiling) of a guest pixel range at <paramref name="scale"/>: the scaled range covers it.</summary>
    static long ScaledLo(long v, float scale) => scale == 1f ? v : scale == 2f ? v * 2 : (long)MathF.Floor(v * scale);
    static long ScaledHi(long v, float scale) => scale == 1f ? v : scale == 2f ? v * 2 : (long)MathF.Ceiling(v * scale);

    /// <summary>A scissor in guest pixels at a fractional scale: the attachment pixels whose centres lie inside it.</summary>
    Rect2D ScaleScissor(Rect2D r, float scale)
    {
        var area = SceneArea;
        int x0 = (int)Math.Min(MathF.Round(r.Offset.X * scale), area.Width), y0 = (int)Math.Min(MathF.Round(r.Offset.Y * scale), area.Height);
        int x1 = (int)Math.Clamp(MathF.Round((r.Offset.X + r.Extent.Width) * scale), x0, area.Width);
        int y1 = (int)Math.Clamp(MathF.Round((r.Offset.Y + r.Extent.Height) * scale), y0, area.Height);
        return new Rect2D(new Offset2D(x0, y0), new Extent2D((uint)(x1 - x0), (uint)(y1 - y0)));
    }

    void EnsureNativeImage(RenderTarget rt)
    {
        if (rt.NativeImage.Handle != 0) return;
        (rt.NativeImage, rt.NativeView, rt.NativeMem) = CreateImage(MemCat.Region, rt.Width, rt.Height, rt.Format,
            ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit, mutable: true);
        Barrier(rt.NativeImage, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        rt.NativeVersion = -1;
    }

    /// <summary>
    /// The target's pixels at guest size in GENERAL layout, for transfers that address guest pixels: the image itself
    /// at native scale, else its native copy, refreshed (nearest blit, raw bits kept for same-format copies) when the
    /// target changed. Recorded outside renderings (callers end or pause the rendering first).
    /// </summary>
    Image NativeImageOf(RenderTarget rt)
    {
        if (rt.Scale == 1f) return rt.Image;
        EnsureNativeImage(rt);
        if (rt.NativeVersion == rt.Version) return rt.NativeImage;
        rt.NativeVersion = rt.Version;
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        Barrier(rt.NativeImage, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit, AccessFlags.TransferWriteBit);
        Blit(rt.Image, rt.ImgW, rt.ImgH, rt.NativeImage, rt.Width, rt.Height, Filter.Nearest);
        Barrier(rt.NativeImage, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit,
            AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit);
        return rt.NativeImage;
    }

    /// <summary>
    /// <see cref="NativeImageOf"/> for one guest-pixel rectangle (region writebacks of large targets): only that part
    /// of the native copy is refreshed, unless the whole copy is current anyway.
    /// </summary>
    Image NativeRectOf(RenderTarget rt, uint x, uint y, uint w, uint h)
    {
        if (rt.Scale == 1f) return rt.Image;
        if (x == 0 && y == 0 && w >= rt.Width && h >= rt.Height) return NativeImageOf(rt);
        EnsureNativeImage(rt);
        if (rt.NativeVersion == rt.Version) return rt.NativeImage;
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        Barrier(rt.NativeImage, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit, AccessFlags.TransferWriteBit);
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        float s = rt.Scale;
        int sx0 = (int)ScaledLo(x, s), sy0 = (int)ScaledLo(y, s);
        int sx1 = (int)Math.Min(ScaledHi(x + w, s), rt.ImgW), sy1 = (int)Math.Min(ScaledHi(y + h, s), rt.ImgH);
        if (sx1 <= sx0 || sy1 <= sy0) return rt.NativeImage;
        blit.SrcOffsets[0] = new Offset3D(sx0, sy0, 0);
        blit.SrcOffsets[1] = new Offset3D(sx1, sy1, 1);
        blit.DstOffsets[0] = new Offset3D((int)x, (int)y, 0);
        blit.DstOffsets[1] = new Offset3D((int)(x + w), (int)(y + h), 1);
        _vk.CmdBlitImage(Cmd, rt.Image, ImageLayout.General, rt.NativeImage, ImageLayout.General, 1, in blit, Filter.Nearest);
        Barrier(rt.NativeImage, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit,
            AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit);
        return rt.NativeImage;
    }

    /// <summary>Upload target of guest-layout pixels for <paramref name="rt"/>: its native copy when scaled (then
    /// <see cref="NativeToScaled"/> brings them into the image), else the image.</summary>
    Image UploadImageOf(RenderTarget rt)
    {
        if (rt.Scale == 1f) return rt.Image;
        EnsureNativeImage(rt);
        Barrier(rt.NativeImage, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit, AccessFlags.TransferWriteBit);
        return rt.NativeImage;
    }

    /// <summary>After an upload into <see cref="UploadImageOf"/>: the native copy is blitted into the scaled image.</summary>
    void NativeToScaled(RenderTarget rt)
    {
        if (rt.Scale == 1f) return;
        Barrier(rt.NativeImage, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        Blit(rt.NativeImage, rt.Width, rt.Height, rt.Image, rt.ImgW, rt.ImgH, Filter.Nearest);
        rt.NativeVersion = rt.Version;
    }

    // ------------------------------------------------------------------ reinterpretation at the internal resolution

    /// <summary>Targets whose image is in the CDRAM mirror in scaled layout (packed rows of <see cref="RenderTarget.ImgW"/>
    /// pixels at the target's address), with the version copied. The native layout (<see cref="_mirroredRt"/>) and this
    /// one share the memory: writing either drops the other.</summary>
    readonly Dictionary<RenderTarget, long> _mirroredRtScaled = new();

    /// <param name="Own">The offset is in the target's own buffer (<see cref="RenderTarget.ScaledRows"/>, above native), not in the mirror.</param>
    readonly record struct ReinterpretGeo(ulong Offset, uint RowTexels, uint W, uint H, uint CopyW, uint CopyH, bool Own = false);

    /// <summary>
    /// Where a linear texture of another texel size that reads a scaled target's memory lies in the scaled mirror
    /// layout (Uncharted's 720x408 F32F32 G-buffer sampled as two 1440x408 U8U8U8U8 / S8S8S8S8 textures at +0 and
    /// +4): rows of the texture must be rows of the surface, and the bytes of a scaled row must split into whole
    /// texels. Null at native scale or when the layouts do not line up (the native mirror is used then).
    /// </summary>
    ReinterpretGeo? ScaledReinterpretGeometry(RenderTarget rt, uint addr, uint w, uint h, uint texRow, uint rtBpp, uint texBpp)
    {
        if (rt.Scale == 1f) return null;
        uint rowBytes = rt.Stride * rtBpp;
        if (texRow != rowBytes || addr < rt.Address) return null;
        uint scaledRowBytes = rt.ImgW * rtBpp;
        if (scaledRowBytes % texBpp != 0) return null;
        uint off = addr - rt.Address, y0 = off / rowBytes, xb = off % rowBytes;
        uint px = xb / rtBpp, within = xb % rtBpp;
        if (y0 >= rt.Height || px >= rt.Width) return null;
        float s = rt.Scale;
        uint sy0 = Math.Min((uint)ScaledLo(y0, s), rt.ImgH - 1), spx = Math.Min((uint)ScaledLo(px, s), rt.ImgW - 1);
        ulong start = (ulong)sy0 * scaledRowBytes + (ulong)spx * rtBpp + within;
        if (start % texBpp != 0) return null;
        // the texture's width in surface pixels, scaled, then in texels of the texture
        uint spanPx = Math.Max(1, (uint)((ulong)w * texBpp / rtBpp));
        uint sw = Math.Max(1, (uint)((ulong)ScaleDim(spanPx, s) * rtBpp / texBpp));
        uint maxW = (uint)((scaledRowBytes - (start % scaledRowBytes)) / texBpp);
        uint sh = ScaleDim(h, s);
        uint copyW = Math.Min(sw, maxW), copyH = Math.Min(sh, rt.ImgH - sy0);
        if (copyW == 0 || copyH == 0) return null;
        // above native a scaled row is longer than the surface's stride and the scaled layout does not fit in the
        // target's memory: it goes into a buffer of the target's own (ScaledRowsOf)
        if (s > 1f) return new ReinterpretGeo(start, scaledRowBytes / texBpp, sw, sh, copyW, copyH, Own: true);
        return new ReinterpretGeo(rt.Address - Core.Memory.GuestMemory.CdramStart + start, scaledRowBytes / texBpp, sw, sh, copyW, copyH);
    }

    /// <summary>Copies a scaled target into the mirror in scaled layout when it changed (see <see cref="_mirroredRtScaled"/>).</summary>
    bool MirrorRenderTargetScaled(RenderTarget rt)
    {
        int guestBpp = Formats.ColorBytesPerPixel(rt.ColorFormat);
        if (rt.Format == Format.B8G8R8A8Unorm || HostBytesPerPixel(rt.Format) != guestBpp || rt.ImgW > rt.Stride) return false;
        rt.LastTouchedFrame = FrameCount;
        if (_mirroredRtScaled.TryGetValue(rt, out var v) && v == rt.Version) return true;
        _mirroredRtScaled[rt] = rt.Version;
        _mirroredRt.Remove(rt);
        rt.Dirty = true;
        SyncRenderTargetForRead(rt);
        EndRendering();
        BufferBarrier(_mirror, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit, AccessFlags.TransferWriteBit);
        var region = new BufferImageCopy
        {
            BufferOffset = rt.Address - Core.Memory.GuestMemory.CdramStart,
            BufferRowLength = rt.ImgW,
            BufferImageHeight = rt.ImgH,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(rt.ImgW, rt.ImgH, 1),
        };
        _vk.CmdCopyImageToBuffer(Cmd, rt.Image, ImageLayout.General, _mirror, 1, in region);
        BufferBarrier(_mirror, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit);
        return true;
    }

    /// <summary>
    /// Above native: the target's image in packed scaled rows in its own buffer (refreshed when it changed), for
    /// reinterpreted reads at the internal resolution. Default when the format cannot be copied raw.
    /// </summary>
    Silk.NET.Vulkan.Buffer ScaledRowsOf(RenderTarget rt)
    {
        int guestBpp = Formats.ColorBytesPerPixel(rt.ColorFormat);
        if (rt.Format == Format.B8G8R8A8Unorm || HostBytesPerPixel(rt.Format) != guestBpp) return default;
        rt.LastTouchedFrame = FrameCount;
        if (rt.ScaledRows.Handle == 0)
            (rt.ScaledRows, rt.ScaledRowsMem) = _core.CreateDeviceBuffer((ulong)rt.ImgW * rt.ImgH * (uint)guestBpp,
                BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit, MemCat.Region);
        if (rt.ScaledRowsVersion == rt.Version) return rt.ScaledRows;
        rt.ScaledRowsVersion = rt.Version;
        rt.Dirty = true;
        SyncRenderTargetForRead(rt);
        EndRendering();
        BufferBarrier(rt.ScaledRows, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit, AccessFlags.TransferWriteBit);
        var region = new BufferImageCopy
        {
            BufferRowLength = rt.ImgW,
            BufferImageHeight = rt.ImgH,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(rt.ImgW, rt.ImgH, 1),
        };
        _vk.CmdCopyImageToBuffer(Cmd, rt.Image, ImageLayout.General, rt.ScaledRows, 1, in region);
        BufferBarrier(rt.ScaledRows, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        return rt.ScaledRows;
    }

    void ReleaseNativeImage(RenderTarget rt, bool now)
    {
        _mirroredRtScaled.Remove(rt);
        if (rt.ScaledRows.Handle != 0)
        {
            var (b, m) = (rt.ScaledRows, rt.ScaledRowsMem);
            rt.ScaledRows = default;
            rt.ScaledRowsVersion = -1;
            Release(() => _core.DestroyBuffer(b, m), now);
        }
        if (rt.NativeImage.Handle == 0) return;
        var (img, view, mem) = (rt.NativeImage, rt.NativeView, rt.NativeMem);
        rt.NativeImage = default;
        Release(() =>
        {
            _vk.DestroyImageView(_dev, view, null);
            _vk.DestroyImage(_dev, img, null);
            _imagePool.Free(mem);
        }, now);
    }

    string ResScaleReport() => ResScale == 1f ? "" : $"; resolution scale {ResScale:0.##}";
}
