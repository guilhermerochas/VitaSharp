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
    private sealed class SupersampleImage
    {
        public Image Image;
        public ImageView View, SrgbView;
        public ImageView RawView;     // integer view for raw-bit fragment outputs (see RawAttachmentView)
        public ImageMemoryPool.Allocation Mem;
        public uint Width, Height;
    }

    private readonly Dictionary<RenderTarget, SupersampleImage> _ssImages = new();

    /// <summary>The current scene renders 2x supersampled (MSAA downscale surface).</summary>
    private bool _supersampled;
    /// <summary>The sample image holds rendering that was not box-filtered into the surface yet.</summary>
    private bool _ssUnresolved;
    private uint SsScale => _supersampled ? 2u : 1u;

    /// <summary>
    /// The current scene uses MSAA without downscale: the colour surface stores every sample as a pixel of its own, so
    /// it is twice the render target's size (a 360x204 4X target writes a 720x408 surface, as Uncharted: Golden Abyss
    /// does for its quarter-resolution particle, mist and glow pass over the full-resolution HDR scene). As in Vita3K
    /// (render target, viewport and region clip doubled, res_multiplier 2) the scene renders straight into the surface
    /// at twice the target's size; each surface pixel is then one sample, and the depth/stencil surface (sample-sized
    /// as well) lines up with it.
    /// </summary>
    private bool _msaaNoDownscale;
    private readonly HashSet<uint> _msaaNoDownscaleLogged = new();

    /// <summary>Render-area scale of the current scene: supersampled and MSAA-without-downscale scenes render at 2x.</summary>
    /// Scaled targets multiply it by their scale (VulkanRenderer.ResScale.cs).
    private float SceneScale => (_supersampled || _msaaNoDownscale ? 2f : 1f) * RtScale;

    /// <summary>The scene's render area in attachment pixels (<see cref="_sceneW"/> x <see cref="_sceneH"/> at <see cref="SceneScale"/>).</summary>
    private Extent2D SceneArea
    {
        get
        {
            float s = SceneScale;
            uint w = ScaledLen(_sceneW, s), h = ScaledLen(_sceneH, s);
            if (_renderTarget != null && _renderTarget.Scale != 1f)
            {
                uint k = _supersampled ? 2u : 1u;
                w = Math.Min(w, _renderTarget.ImgW * k);
                h = Math.Min(h, _renderTarget.ImgH * k);
            }
            return new Extent2D(w, h);
        }
    }

    private readonly Dictionary<Format, bool> _ssFormatOk = new();

    private bool SupportsSupersample(Format format)
    {
        if (_ssFormatOk.TryGetValue(format, out bool ok))
        {
            return ok;
        }

        _core.Vk.GetPhysicalDeviceFormatProperties(_core.Physical, format, out FormatProperties props);
        const FormatFeatureFlags Need = FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit | FormatFeatureFlags.SampledImageFilterLinearBit
            | FormatFeatureFlags.ColorAttachmentBlendBit;
        return _ssFormatOk[format] = (props.OptimalTilingFeatures & Need) == Need;
    }

    private SupersampleImage GetSupersampleImage(RenderTarget rt)
    {
        if (_ssImages.TryGetValue(rt, out SupersampleImage? supersampleImage))
        {
            return supersampleImage;
        }

        supersampleImage = new SupersampleImage { Width = rt.ImgW * 2, Height = rt.ImgH * 2 };
        // input attachment: small programmable-blending draws read it in a render pass (VulkanRenderer.SingleFetch.cs)
        (supersampleImage.Image, supersampleImage.View, supersampleImage.Mem) = CreateImage(MemCat.Msaa, supersampleImage.Width, supersampleImage.Height, rt.Format,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.InputAttachmentBit,
            ImageAspectFlags.ColorBit, mutable: true);
        Format srgb = Formats.Srgb(rt.Format);
        if (srgb != rt.Format && _core.SupportsColorAttachment(srgb))
        {
            supersampleImage.SrgbView = CreateView(supersampleImage.Image, srgb, ImageAspectFlags.ColorBit, 1, 1, false, default);
        }

        _ssImages[rt] = supersampleImage;
        Log.Info($"[gpu] render target 0x{rt.Address:X8} {rt.Width}x{rt.Height} fmt 0x{rt.ColorFormat:X8}: MSAA downscale scene, rendering 2x supersampled");
        return supersampleImage;
    }

    private ImageView SupersampleView(RenderTarget rt)
    {
        SupersampleImage supersampleImage = GetSupersampleImage(rt);
        return _rtGamma && supersampleImage.SrgbView.Handle != 0 ? supersampleImage.SrgbView : supersampleImage.View;
    }

    private void DestroySupersampleLater(RenderTarget rt, bool now = false)
    {
        if (!_ssImages.Remove(rt, out SupersampleImage? supersampleImage))
        {
            return;
        }

        Release(() =>
        {
            _vk.DestroyImageView(_dev, supersampleImage.View, null);
            if (supersampleImage.SrgbView.Handle != 0)
            {
                _vk.DestroyImageView(_dev, supersampleImage.SrgbView, null);
            }

            if (supersampleImage.RawView.Handle != 0)
            {
                _vk.DestroyImageView(_dev, supersampleImage.RawView, null);
            }

            _vk.DestroyImage(_dev, supersampleImage.Image, null);
            _imagePool.Free(supersampleImage.Mem);
        }, now);
    }

    /// <summary>Scene start: every sample takes the surface's pixel (the hardware loads the surface into all samples).</summary>
    private void LoadSupersample(RenderTarget rt)
    {
        SupersampleImage supersampleImage = GetSupersampleImage(rt);
        EndRendering();
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        Barrier(supersampleImage.Image, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferReadBit, AccessFlags.TransferWriteBit);
        Blit(rt.Image, rt.ImgW, rt.ImgH, supersampleImage.Image, supersampleImage.Width, supersampleImage.Height, Filter.Nearest);
        Barrier(supersampleImage.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.TransferWriteBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
    }

    /// <summary>Downscale: each surface pixel becomes the average of its 2x2 samples.</summary>
    private void ResolveSupersample(RenderTarget rt)
    {
        SupersampleImage supersampleImage = GetSupersampleImage(rt);
        Barrier(supersampleImage.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit, AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit);
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.TransferBit,
            PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit | AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferWriteBit);
        Blit(supersampleImage.Image, supersampleImage.Width, supersampleImage.Height, rt.Image, rt.ImgW, rt.ImgH, Filter.Linear);
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.TransferBit | PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit | AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
        Barrier(supersampleImage.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit, AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
    }

    private void Blit(Image src, uint sw, uint sh, Image dst, uint dw, uint dh, Filter filter)
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
