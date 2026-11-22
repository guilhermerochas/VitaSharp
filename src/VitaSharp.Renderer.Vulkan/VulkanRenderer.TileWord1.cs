using Silk.NET.Vulkan;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The second word of the 64-bit output registers of a scene's pixels (see <see cref="TileWord1Use"/>): on the PS Vita
/// the tile buffer keeps every pixel's output registers for the whole scene and stores only what the colour surface
/// holds, so with 64-bit registers on a 32-bit surface a native-colour program can leave data in o0.y that a later
/// program of the scene reads back. The host renders straight into the surface, which has no room for it.
/// <para>
/// The first draw of a scene whose fragment program writes that word attaches an R32_UINT image (the size and sample
/// count of the scene's colour samples: the 2x sample image of a supersampled scene, the samples of a multisampled
/// one; cleared to zero) at colour location 1 for the rest of the scene. The programs that use the word then run as
/// <see cref="ShaderHints.TileWord1"/> variants: they start with o0.y = the attachment (an input attachment at set 3
/// binding 3 in multisampled render passes, else a copy: binding 4 one-sample, binding 5 multisampled with
/// VITASHARP_MSAA_FETCH=copy, like programmable blending) and write it back when they write the word; every other
/// pipeline of the scene leaves it unwritten. The same holds for the tile-based renderings of mobile GPUs (the word is
/// colour attachment 1 of their deferred renderings). Scenes whose programs never write the word, and so every other
/// game, render exactly as before.
/// </para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private const Format TileWord1Format = Format.R32Uint;
    private const ImageUsageFlags TileWord1Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.InputAttachmentBit
        | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
    private const ImageUsageFlags TileWord1CopyUsage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit;

    private sealed class TileWordImage
    {
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public uint Width, Height;
        public SampleCountFlags Samples;
    }

    /// <summary>Tile-word images per size and sample count (their content lives for one scene).</summary>
    private readonly Dictionary<(uint, uint, SampleCountFlags), TileWordImage> _tw1Images = new();
    /// <summary>The current scene keeps the second output register word in <see cref="_tileWord1"/>.</summary>
    private bool _tw1Active;
    private TileWordImage? _tileWord1;
    private readonly HashSet<string> _tw1Logged = new();
    private long _statTw1Scenes;

    // copy read by the TileWord1 variants outside render passes (binding 4 one-sample, 5 multisampled)
    private Image _tw1Copy;
    private ImageView _tw1CopyView;
    private ImageMemoryPool.Allocation _tw1CopyMem;
    private uint _tw1CopyW, _tw1CopyH;
    private SampleCountFlags _tw1CopySamples;

    // 1x1 R32_UINT images for the copy binding the scene does not read (the shaders pick binding 4 or 5 by
    // rinfo.fb_samples, so both are statically used and must hold a valid unsigned-integer view)
    private Image _tw1DummyImage, _tw1DummyMsImage;
    private ImageView _tw1DummyView, _tw1DummyMsView;
    private ImageMemoryPool.Allocation _tw1DummyMem, _tw1DummyMsMem;

    /// <summary>Why the current scene cannot keep the word (null: it can).</summary>
    private string? TileWord1Unsupported()
    {
        if (_renderTarget == null)
        {
            return "no colour surface";
        }

        if (Formats.ColorBytesPerPixel(_renderTarget.ColorFormat) != 4)
        {
            return "not a 32-bit colour surface";
        }

        SampleCountFlags samples = SceneSamples;
        if (!_core.SupportsUnblendedColorAttachment(TileWord1Format))
        {
            return $"{TileWord1Format} is not a colour attachment format";
        }

        if ((_core.ImageSampleCounts(TileWord1Format, TileWord1Usage) & samples) == 0)
        {
            return $"{TileWord1Format} cannot have {samples}";
        }

        if (_multisampled && !_msaaInputFetch && (_core.ImageSampleCounts(TileWord1Format, TileWord1CopyUsage) & samples) == 0)
        {
            return $"{TileWord1Format} cannot be sampled with {samples} (multisampled framebuffer fetch through copies)";
        }

        return null;
    }

    /// <summary>A program writes the second output register word: the rest of the scene keeps it in an attachment.</summary>
    private void ActivateTileWord1()
    {
        if (TileWord1Unsupported() is { } why)
        {
            if (_tw1Logged.Add(why))
            {
                Log.Warn($"[gpu] a native-colour program keeps data in its output register beyond the colour, which the scene cannot keep ({why}): the data is lost");
            }

            return;
        }
        RenderTarget rt = _renderTarget!;
        SampleCountFlags samples = SceneSamples;
        // the scene's render area in attachment pixels: a supersampled scene renders into its 2x sample image (scenes
        // without downscale render at 2x into the sample surface, which is the target's image)
        uint w = rt.ImgW * SsScale, h = rt.ImgH * SsScale;
        EndRendering(resolve: false); // the next rendering has the attachment
        if (!_tw1Images.TryGetValue((w, h, samples), out TileWordImage? img))
        {
            img = new TileWordImage { Width = w, Height = h, Samples = samples };
            (img.Image, img.View, img.Mem) = CreateImage(MemCat.Msaa, w, h, TileWord1Format, TileWord1Usage, ImageAspectFlags.ColorBit, samples: samples);
            _tw1Images[(w, h, samples)] = img;
            Barrier(img.Image, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        }
        else
        {
            // the previous scene's renderings and copies are done with it
            Barrier(img.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
                PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
                AccessFlags.ColorAttachmentWriteBit | AccessFlags.InputAttachmentReadBit | AccessFlags.TransferReadBit, AccessFlags.TransferWriteBit);
        }
        // scene start: the word is zero (what the scene's draws before this one left in it when they did not write it)
        var clear = new ClearColorValue(0u, 0u, 0u, 0u);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        _vk.CmdClearColorImage(Cmd, img.Image, ImageLayout.General, in clear, 1, in range);
        Barrier(img.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TransferBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.InputAttachmentReadBit);
        _tileWord1 = img;
        _tw1Active = true;
        _statTw1Scenes++;
        if (_tw1Logged.Add($"rt{rt.Address:X8}"))
        {
            Log.Info($"[gpu] render target 0x{rt.Address:X8} {rt.Width}x{rt.Height}: a native-colour program keeps data in the second output register word, the scene keeps it in a {w}x{h} {samples} {TileWord1Format} attachment");
        }
    }

    /// <summary>Scene start/end: the word does not outlive its scene.</summary>
    private void EndTileWord1()
    {
        _tw1Active = false;
        _tileWord1 = null;
    }

    /// <summary>The TileWord1 variants outside render passes read a copy of the word (binding 4, or 5 with the samples of
    /// a multisampled scene), like programmable blending reads a copy of the colour; returns its view. The rendering
    /// must have ended (the copy reads what it wrote).</summary>
    private ImageView CopyTileWord1()
    {
        TileWordImage src = _tileWord1!;
        uint w = src.Width, h = src.Height;
        if (_tw1Copy.Handle == 0 || _tw1CopyW != w || _tw1CopyH != h || _tw1CopySamples != src.Samples)
        {
            if (_tw1Copy.Handle != 0)
            {
                (Image oi, ImageView ov, ImageMemoryPool.Allocation om) = (_tw1Copy, _tw1CopyView, _tw1CopyMem);
                DestroyAfterGpu(() =>
                {
                    _vk.DestroyImageView(_dev, ov, null);
                    _vk.DestroyImage(_dev, oi, null);
                    _imagePool.Free(om);
                });
            }
            (_tw1Copy, _tw1CopyView, _tw1CopyMem) = CreateImage(MemCat.Region, w, h, TileWord1Format, TileWord1CopyUsage, ImageAspectFlags.ColorBit, samples: src.Samples);
            (_tw1CopyW, _tw1CopyH, _tw1CopySamples) = (w, h, src.Samples);
            Barrier(_tw1Copy, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        }
        if (FetchRegion(SceneScale, w, h) is not (var offset, var extent))
        {
            return _tw1CopyView;
        }

        Barrier(src.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit, AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit);
        Barrier(_tw1Copy, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            SrcOffset = offset,
            DstOffset = offset,
            Extent = extent,
        };
        _vk.CmdCopyImage(Cmd, src.Image, ImageLayout.General, _tw1Copy, ImageLayout.General, 1, in copy);
        Barrier(_tw1Copy, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        Barrier(src.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
        return _tw1CopyView;
    }

    /// <summary>A 1x1 R32_UINT view for the copy binding the scene does not read (<paramref name="multisampled"/>: the
    /// multisampled one, binding 5).</summary>
    private ImageView TileWord1DummyView(bool multisampled)
    {
        if (multisampled ? _tw1DummyMsView.Handle != 0 : _tw1DummyView.Handle != 0)
        {
            return multisampled ? _tw1DummyMsView : _tw1DummyView;
        }

        SampleCountFlags samples = SampleCountFlags.Count1Bit;
        if (multisampled)
        {
            SampleCountFlags supported = _core.ImageSampleCounts(TileWord1Format, TileWord1CopyUsage);
            if ((supported & SampleCountFlags.Count4Bit) != 0)
            {
                samples = SampleCountFlags.Count4Bit;
            }
            else if ((supported & SampleCountFlags.Count2Bit) != 0)
            {
                samples = SampleCountFlags.Count2Bit;
            }
            else
            {
                // no multisampled R32_UINT image to sample: the four-sample colour dummy keeps the binding valid in
                // dimension (its branch never runs in one-sample scenes)
                return DummyMsView();
            }
        }
        (Image image, ImageView view, ImageMemoryPool.Allocation mem) = CreateImage(MemCat.Other, 1, 1, TileWord1Format, TileWord1CopyUsage, ImageAspectFlags.ColorBit, samples: samples);
        Barrier(image, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
        if (multisampled)
        {
            (_tw1DummyMsImage, _tw1DummyMsView, _tw1DummyMsMem) = (image, view, mem);
        }
        else
        {
            (_tw1DummyImage, _tw1DummyView, _tw1DummyMem) = (image, view, mem);
        }

        return view;
    }

    /// <summary>Adds the tile-word copy (binding 4 one-sample, 5 multisampled) and the dummy of the other binding to a
    /// framebuffer-fetch set. Outside a rendering (the dummies' first use records a layout barrier).</summary>
    private void WriteTileWord1Copy(DescriptorSet set, ImageView view, bool multisampled)
    {
        ImageView single = multisampled ? TileWord1DummyView(false) : view;
        ImageView ms = multisampled ? view : TileWord1DummyView(true);
        DescriptorImageInfo* info = stackalloc DescriptorImageInfo[2]
        {
            new(_dummySampler, single, ImageLayout.General),
            new(_dummySampler, ms, ImageLayout.General),
        };
        WriteDescriptorSet* w = stackalloc WriteDescriptorSet[2];
        for (int i = 0; i < 2; i++)
        {
            w[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 4 + (uint)i, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &info[i] };
        }

        _vk.UpdateDescriptorSets(_dev, 2, w, 0, null);
    }

    private string TileWord1Report() => $"tile-word attachment: {_statTw1Scenes} scenes";

    private void DestroyTileWord1()
    {
        foreach (TileWordImage img in _tw1Images.Values)
        {
            _vk.DestroyImageView(_dev, img.View, null);
            _vk.DestroyImage(_dev, img.Image, null);
        }
        _tw1Images.Clear();
        if (_tw1Copy.Handle != 0)
        {
            _vk.DestroyImageView(_dev, _tw1CopyView, null);
            _vk.DestroyImage(_dev, _tw1Copy, null);
            _tw1Copy = default;
        }
        if (_tw1DummyView.Handle != 0)
        {
            _vk.DestroyImageView(_dev, _tw1DummyView, null);
            _vk.DestroyImage(_dev, _tw1DummyImage, null);
            _tw1DummyView = default;
        }
        if (_tw1DummyMsView.Handle != 0)
        {
            _vk.DestroyImageView(_dev, _tw1DummyMsView, null);
            _vk.DestroyImage(_dev, _tw1DummyMsImage, null);
            _tw1DummyMsView = default;
        }
        _tileWord1 = null;
        _tw1Active = false;
    }
}
