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
/// The first draw of a scene whose fragment program writes that word attaches an R32_UINT image (same size and sample
/// count as the scene's colour samples, cleared to zero) at colour location 1 for the rest of the scene. The programs
/// that use the word then run as <see cref="ShaderHints.TileWord1"/> variants: they start with o0.y = the attachment
/// (an input attachment at set 3 binding 3 in multisampled render passes, else a copy at binding 4, like programmable
/// blending) and write it back when they write the word; every other pipeline of the scene leaves it unwritten. Scenes
/// whose programs never write the word, and so every other game, render exactly as before. Not done for supersampled
/// scenes, tile-based renderings (mobile) and the copy path of multisampled programmable blending (VITASHARP_MSAA_FETCH=copy):
/// there the word stays undefined, as before.
/// </para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    const Format TileWord1Format = Format.R32Uint;
    const ImageUsageFlags TileWord1Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.InputAttachmentBit
        | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;

    sealed class TileWordImage
    {
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public uint Width, Height;
        public SampleCountFlags Samples;
    }

    /// <summary>Tile-word images per size and sample count (their content lives for one scene).</summary>
    readonly Dictionary<(uint, uint, SampleCountFlags), TileWordImage> _tw1Images = new();
    /// <summary>The current scene keeps the second output register word in <see cref="_tw1"/>.</summary>
    bool _tw1Active;
    TileWordImage? _tw1;
    readonly HashSet<string> _tw1Logged = new();
    long _statTw1Scenes;

    // single-sampled copy read by the TileWord1 variants outside render passes (binding 4)
    Image _tw1Copy;
    ImageView _tw1CopyView;
    ImageMemoryPool.Allocation _tw1CopyMem;
    uint _tw1CopyW, _tw1CopyH;

    /// <summary>Why the current scene cannot keep the word (null: it can).</summary>
    string? TileWord1Unsupported()
    {
        if (_rt == null) return "no colour surface";
        if (Formats.ColorBytesPerPixel(_rt.ColorFormat) != 4) return "not a 32-bit colour surface";
        if (_ss) return "supersampled scene";
        if (TiledRenderings) return "tile-based renderings";
        if (_ms && !MsaaInputFetch) return "multisampled framebuffer fetch through copies";
        var samples = SceneSamples;
        if (!_core.SupportsUnblendedColorAttachment(TileWord1Format)) return $"{TileWord1Format} is not a colour attachment format";
        if ((_core.ImageSampleCounts(TileWord1Format, TileWord1Usage) & samples) == 0) return $"{TileWord1Format} cannot have {samples}";
        return null;
    }

    /// <summary>A program writes the second output register word: the rest of the scene keeps it in an attachment.</summary>
    void ActivateTileWord1()
    {
        if (TileWord1Unsupported() is { } why)
        {
            if (_tw1Logged.Add(why)) Log.Warn($"[gpu] a native-colour program keeps data in its output register beyond the colour, which the scene cannot keep ({why}): the data is lost");
            return;
        }
        var rt = _rt!;
        var samples = SceneSamples;
        // the scene's render area in attachment pixels (scenes without downscale render at 2x into the sample surface)
        uint w = rt.Width, h = rt.Height;
        EndRendering(resolve: false); // the next rendering has the attachment
        if (!_tw1Images.TryGetValue((w, h, samples), out var img))
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
        _tw1 = img;
        _tw1Active = true;
        _statTw1Scenes++;
        if (_tw1Logged.Add($"rt{rt.Address:X8}"))
            Log.Info($"[gpu] render target 0x{rt.Address:X8} {rt.Width}x{rt.Height}: a native-colour program keeps data in the second output register word, the scene keeps it in a {samples} {TileWord1Format} attachment");
    }

    /// <summary>Scene start/end: the word does not outlive its scene.</summary>
    void EndTileWord1()
    {
        _tw1Active = false;
        _tw1 = null;
    }

    /// <summary>The TileWord1 variants outside render passes read a copy of the word (binding 4), like programmable
    /// blending reads a copy of the colour; returns its view.</summary>
    ImageView CopyTileWord1()
    {
        var src = _tw1!;
        uint w = src.Width, h = src.Height;
        if (_tw1Copy.Handle == 0 || _tw1CopyW != w || _tw1CopyH != h)
        {
            if (_tw1Copy.Handle != 0)
            {
                var (oi, ov, om) = (_tw1Copy, _tw1CopyView, _tw1CopyMem);
                DestroyAfterGpu(() => { _vk.DestroyImageView(_dev, ov, null); _vk.DestroyImage(_dev, oi, null); _imagePool.Free(om); });
            }
            (_tw1Copy, _tw1CopyView, _tw1CopyMem) = CreateImage(MemCat.Region, w, h, TileWord1Format, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
            (_tw1CopyW, _tw1CopyH) = (w, h);
            Barrier(_tw1Copy, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        }
        if (FetchRegion(SceneScale, w, h) is not (var offset, var extent)) return _tw1CopyView;
        Barrier(src.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit, AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit);
        Barrier(_tw1Copy, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            SrcOffset = offset, DstOffset = offset, Extent = extent,
        };
        _vk.CmdCopyImage(Cmd, src.Image, ImageLayout.General, _tw1Copy, ImageLayout.General, 1, in copy);
        Barrier(_tw1Copy, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        Barrier(src.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
        return _tw1CopyView;
    }

    /// <summary>Adds the tile-word copy (binding 4) to a framebuffer-fetch set.</summary>
    void WriteTileWord1Copy(DescriptorSet set, ImageView view)
    {
        var info = new DescriptorImageInfo(_dummySampler, view, ImageLayout.General);
        var w = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 4, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &info };
        _vk.UpdateDescriptorSets(_dev, 1, &w, 0, null);
    }

    string TileWord1Report() => $"tile-word attachment: {_statTw1Scenes} scenes";

    void DestroyTileWord1()
    {
        foreach (var img in _tw1Images.Values) { _vk.DestroyImageView(_dev, img.View, null); _vk.DestroyImage(_dev, img.Image, null); }
        _tw1Images.Clear();
        if (_tw1Copy.Handle != 0) { _vk.DestroyImageView(_dev, _tw1CopyView, null); _vk.DestroyImage(_dev, _tw1Copy, null); _tw1Copy = default; }
        _tw1 = null;
        _tw1Active = false;
    }
}
