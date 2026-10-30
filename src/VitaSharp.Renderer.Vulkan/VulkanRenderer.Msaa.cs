using Silk.NET.Vulkan;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// 4x MSAA with downscale (render target created with SCE_GXM_MULTISAMPLE_2X/4X and a colour surface using
/// SCE_GXM_COLOR_SURFACE_SCALE_MSAA_DOWNSCALE). The hardware rasterises (and stencil/depth-tests) every pixel at four
/// sample positions and averages them when the tile is written to the 1x surface. Rendering such scenes with one
/// sample loses all of that coverage: edges become hard steps and features thinner than a pixel disappear. Games use
/// it for exactly that reason, e.g. glyph caches rasterised with a stencil fill (Killzone Mercenary's HUD/tutorial
/// text), where the single-sample result had ragged glyphs with missing strokes.
///
/// Emulated by supersampling, the technique Vita3K uses for MSAA surfaces without downscale (render target and
/// depth doubled, res_multiplier 2): the scene renders into a 2x image (and a 2x depth/stencil target, which matches
/// the sample-sized depth surfaces of MSAA render targets), initialised from the surface when the scene begins, and
/// box-filtered (2:1 linear blit = the average of the 4 samples) back into the surface whenever rendering pauses.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    sealed class SupersampleImage
    {
        public Image Image;
        public ImageView View, SrgbView;
        public ImageView RawView;     // integer view for raw-bit fragment outputs (see RawAttachmentView)
        public ImageMemoryPool.Allocation Mem;
        public uint Width, Height;
    }

    readonly Dictionary<RenderTarget, SupersampleImage> _ssImages = new();

    /// <summary>The current scene renders 2x supersampled (MSAA downscale surface).</summary>
    bool _ss;
    /// <summary>The sample image holds rendering that was not box-filtered into the surface yet.</summary>
    bool _ssUnresolved;
    uint SsScale => _ss ? 2u : 1u;

    /// <summary>
    /// The current scene uses MSAA without downscale: the colour surface stores every sample as a pixel of its own, so
    /// it is twice the render target's size (a 360x204 4X target writes a 720x408 surface, as Uncharted: Golden Abyss
    /// does for its quarter-resolution particle, mist and glow pass over the full-resolution HDR scene). As in Vita3K
    /// (render target, viewport and region clip doubled, res_multiplier 2) the scene renders straight into the surface
    /// at twice the target's size; each surface pixel is then one sample, and the depth/stencil surface (sample-sized
    /// as well) lines up with it.
    /// </summary>
    bool _msaaNoDownscale;
    readonly HashSet<uint> _msaaNoDownscaleLogged = new();

    /// <summary>Render-area scale of the current scene: supersampled and MSAA-without-downscale scenes render at 2x.</summary>
    uint SceneScale => _ss || _msaaNoDownscale ? 2u : 1u;

    readonly Dictionary<Format, bool> _ssFormatOk = new();

    bool SupportsSupersample(Format f)
    {
        if (_ssFormatOk.TryGetValue(f, out var ok)) return ok;
        _core.Vk.GetPhysicalDeviceFormatProperties(_core.Physical, f, out var props);
        const FormatFeatureFlags need = FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit | FormatFeatureFlags.SampledImageFilterLinearBit
            | FormatFeatureFlags.ColorAttachmentBlendBit;
        return _ssFormatOk[f] = (props.OptimalTilingFeatures & need) == need;
    }

    SupersampleImage GetSupersampleImage(RenderTarget rt)
    {
        if (_ssImages.TryGetValue(rt, out var ss)) return ss;
        ss = new SupersampleImage { Width = rt.Width * 2, Height = rt.Height * 2 };
        // input attachment: small programmable-blending draws read it in a render pass (VulkanRenderer.SingleFetch.cs)
        (ss.Image, ss.View, ss.Mem) = CreateImage(MemCat.Msaa, ss.Width, ss.Height, rt.Format,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.InputAttachmentBit,
            ImageAspectFlags.ColorBit, mutable: true);
        var srgb = Formats.Srgb(rt.Format);
        if (srgb != rt.Format && _core.SupportsColorAttachment(srgb))
            ss.SrgbView = CreateView(ss.Image, srgb, ImageAspectFlags.ColorBit, 1, 1, false, default);
        _ssImages[rt] = ss;
        Log.Info($"[gpu] render target 0x{rt.Address:X8} {rt.Width}x{rt.Height} fmt 0x{rt.ColorFormat:X8}: MSAA downscale scene, rendering 2x supersampled");
        return ss;
    }

    ImageView SupersampleView(RenderTarget rt)
    {
        var ss = GetSupersampleImage(rt);
        return _rtGamma && ss.SrgbView.Handle != 0 ? ss.SrgbView : ss.View;
    }

    void DestroySupersampleLater(RenderTarget rt, bool now = false)
    {
        if (!_ssImages.Remove(rt, out var ss)) return;
        Release(() =>
        {
            _vk.DestroyImageView(_dev, ss.View, null);
            if (ss.SrgbView.Handle != 0) _vk.DestroyImageView(_dev, ss.SrgbView, null);
            if (ss.RawView.Handle != 0) _vk.DestroyImageView(_dev, ss.RawView, null);
            _vk.DestroyImage(_dev, ss.Image, null);
            _imagePool.Free(ss.Mem);
        }, now);
    }

    /// <summary>Scene start: every sample takes the surface's pixel (the hardware loads the surface into all samples).</summary>
    void LoadSupersample(RenderTarget rt)
    {
        var ss = GetSupersampleImage(rt);
        EndRendering();
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        Barrier(ss.Image, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferReadBit, AccessFlags.TransferWriteBit);
        Blit(rt.Image, rt.Width, rt.Height, ss.Image, ss.Width, ss.Height, Filter.Nearest);
        Barrier(ss.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.TransferWriteBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
    }

    /// <summary>Downscale: each surface pixel becomes the average of its 2x2 samples.</summary>
    void ResolveSupersample(RenderTarget rt)
    {
        var ss = GetSupersampleImage(rt);
        Barrier(ss.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit, AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit);
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.TransferBit,
            PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit | AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferWriteBit);
        Blit(ss.Image, ss.Width, ss.Height, rt.Image, rt.Width, rt.Height, Filter.Linear);
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.TransferBit | PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit | AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
        Barrier(ss.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit, AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
    }

    void Blit(Image src, uint sw, uint sh, Image dst, uint dw, uint dh, Filter filter)
    {
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        blit.SrcOffsets[1] = new Offset3D((int)sw, (int)sh, 1);
        blit.DstOffsets[1] = new Offset3D((int)dw, (int)dh, 1);
        _vk.CmdBlitImage(Cmd, src, ImageLayout.General, dst, ImageLayout.General, 1, in blit, filter);
    }
}
