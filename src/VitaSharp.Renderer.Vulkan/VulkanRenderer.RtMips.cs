using Silk.NET.Vulkan;
using System.Numerics;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Mipmapped textures whose base level is (part of) a render target. Games render into a surface and sample it as
/// a linear texture with a mip chain, but nothing on the GPU generates the smaller levels: those are read from the
/// memory that follows the surface, like any other texture level. Sampling only the rendered surface would make
/// every minified (or LOD-biased) lookup read level 0. The image built here has the surface as level 0 and the
/// guest memory levels after it, so the sampler picks levels exactly like the hardware.
/// <para>
/// Above or below native resolution the chain is built at the target's scale: level 0 is the target's scaled pixels
/// (not its guest-size copy, which would lose the internal resolution and look like the native picture enlarged), and
/// each level after it is the guest level (from memory, or from a pending transfer) uploaded at guest size into a
/// staging chain and blitted (nearest, so every guest texel stays a block) to the same scale. At native scale the
/// chain is the one the hardware samples, unchanged.
/// </para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private sealed class RtMipTexture
    {
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public long Version = -1;
        public long LastUsedFrame;
        public ulong UpperHash;
        public long CheckedFrame = -1;
        public uint Stamp; // dirty-page epoch the upper levels were last hashed at
        /// <summary>The versions of the pending transfer images last copied into the upper levels (0 = none).</summary>
        public long TransferStamp;
        /// <summary>Size of level 0 (the target's region at its scale; the guest size at native scale).</summary>
        public uint BaseWidth, BaseHeight;
        /// <summary>Guest-size levels 1.. (staging level k = guest level k + 1), blitted to the scaled chain; only above
        /// or below native scale.</summary>
        public Image Upper;
        public ImageMemoryPool.Allocation UpperMem;
    }

    /// <summary>Destroys a mip chain over a target and its staging levels.</summary>
    private void ReleaseRtMip(RtMipTexture rtMipTexture, bool now)
    {
        ReleaseImage(rtMipTexture.Image, rtMipTexture.View, rtMipTexture.Mem, now);
        if (rtMipTexture.Upper.Handle != 0)
        {
            ReleaseImage(rtMipTexture.Upper, default, rtMipTexture.UpperMem, now);
        }
    }

    private readonly Dictionary<(RenderTarget, uint X, uint Y, uint Format, uint W, uint H, uint Mips, bool Gamma), RtMipTexture> _rtMipTextures = new();

    /// <summary>Mip chains built over a target (tests: the chain path was taken).</summary>
    internal int RtMipChainCount => _rtMipTextures.Count;

    private ImageView? RenderTargetMipmapped(RenderTarget rt, GxmTextureDesc gxmTextureDesc, uint x0, uint y0, bool gamma)
    {
        uint w = gxmTextureDesc.Width, h = gxmTextureDesc.Height;
        uint mips = Math.Min(gxmTextureDesc.MipCount, (uint)BitOperations.Log2(Math.Max(w, h)) + 1);
        if (mips <= 1)
        {
            return null;
        }

        TexFormatInfo info = Formats.Texture(GxmFormats.BaseOf(gxmTextureDesc.Format));
        // level 0 is copied from the surface image, the other levels are uploaded: both need the surface's layout
        Format hostFormat = rt.Format is Format.B8G8R8A8Unorm ? Format.Undefined : rt.Format;
        if (info.Compressed || info.Convert != TexConvert.None || info.Format != hostFormat
            || HostBytesPerPixel(rt.Format) != Formats.ColorBytesPerPixel(rt.ColorFormat))
        {
            return null;
        }

        Format viewFormat = gamma ? Formats.Srgb(rt.Format) : rt.Format;
        if (!_core.SupportsSampled(viewFormat))
        {
            return null;
        }

        // above native the chain is built at the target's scale from its own image (level 0 keeps the internal
        // resolution); at or below native it is built at guest size from the guest-size pixels (below native the
        // smaller scaled image would also leave the chain with more levels than its size allows)
        float scale = rt.Scale;
        bool scaled = scale > 1f;
        int scaledX = (int)ScaledLo(x0, scale);
        int scaledY = (int)ScaledLo(y0, scale);
        (RenderTarget rt, uint x0, uint y0, uint Format, uint w, uint h, uint mips, bool gamma) key = (rt, x0, y0, gxmTextureDesc.Format, w, h, mips, gamma);
        if (!_rtMipTextures.TryGetValue(key, out RtMipTexture? rtMipTexture))
        {
            rtMipTexture = new RtMipTexture
            {
                BaseWidth = !scaled ? w : Math.Min(ScaledLen(w, scale), rt.ImgW - (uint)scaledX),
                BaseHeight = !scaled ? h : Math.Min(ScaledLen(h, scale), rt.ImgH - (uint)scaledY),
            };
            (rtMipTexture.Image, rtMipTexture.View, rtMipTexture.Mem) = CreateImage(MemCat.Region, rtMipTexture.BaseWidth, rtMipTexture.BaseHeight, rt.Format,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit, mips, mutable: true);
            _vk.DestroyImageView(_dev, rtMipTexture.View, null);
            rtMipTexture.View = CreateView(rtMipTexture.Image, viewFormat, ImageAspectFlags.ColorBit, mips, 1, false, RtSwizzle(rt, gxmTextureDesc.Format));
            _rtMipTextures[key] = rtMipTexture;
            Log.WarnOnce($"rtmips-{gxmTextureDesc.DataAddress:X8}", $"[gpu] texture 0x{gxmTextureDesc.DataAddress:X8} {w}x{h} with {mips} levels: level 0 is render target 0x{rt.Address:X8}, the rest comes from memory");
            BeginFrameIfNeeded();
            using OutsideScope init = BeginOutside(rtMipTexture.View);
            Barrier(rtMipTexture.Image, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
            if (scaled)
            {
                ImageView upperView;
                (rtMipTexture.Upper, upperView, rtMipTexture.UpperMem) = CreateImage(MemCat.Region, Math.Max(1u, w >> 1), Math.Max(1u, h >> 1), rt.Format,
                    ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit, mips - 1, mutable: true);
                _vk.DestroyImageView(_dev, upperView, null);
                Barrier(rtMipTexture.Upper, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
            }
        }

        rtMipTexture.LastUsedFrame = FrameCount;
        // the levels after the surface live in guest memory: re-upload them when that memory changes
        TexLayout layout = ComputeLayout(gxmTextureDesc, info, w, h, mips, 1);
        uint upperStart = layout.Levels[1].Offset;
        bool uploadUpper = false;
        if (rtMipTexture.CheckedFrame != FrameCount)
        {
            rtMipTexture.CheckedFrame = FrameCount;
            SweepDirtyPages();
            if (rtMipTexture.Version < 0 || DirtyPages.Verify || DirtyPages.WrittenSince(gxmTextureDesc.DataAddress + upperStart, layout.TotalBytes - upperStart, rtMipTexture.Stamp))
            {
                ulong hash = System.IO.Hashing.XxHash3.HashToUInt64(GuestMemory.ReadSpan(gxmTextureDesc.DataAddress + upperStart, (int)(layout.TotalBytes - upperStart)));
                uploadUpper = hash != rtMipTexture.UpperHash || rtMipTexture.Version < 0;
                rtMipTexture.UpperHash = hash;
                rtMipTexture.Stamp = DirtyPages.Epoch;
            }
        }
        // levels that a deferred transfer (VITASHARP_TRANSFER_DEFER) is about to write from the level above: its result
        // as the GPU made it in command order, not the memory, which gets it only when the frame retires (Killzone's
        // light-shaft mips, built by a chain of TransferDownscales and sampled in the same frame)
        long transferStamp = 0;
        if (_xferImages.Count > 0)
        {
            for (uint m = 1; m < mips; m++)
            {
                MipLayout level = layout.Levels[(int)m];
                if (TransferImageForLevel(gxmTextureDesc.DataAddress + level.Offset, level.Width, level.Height, level.RowPitch, rt.Format) is { } levelImage)
                {
                    transferStamp = transferStamp * 31 + levelImage.Version * 16 + m;
                }
            }
        }
        if (transferStamp != rtMipTexture.TransferStamp)
        {
            rtMipTexture.TransferStamp = transferStamp;
            uploadUpper = true;
        }

        bool copyBase = rtMipTexture.Version != rt.Version;
        if (!copyBase && !uploadUpper)
        {
            return rtMipTexture.View;
        }

        // another surface (and the levels from memory) go before the open rendering; the scene's own surface ends it
        using OutsideScope outside = BeginOutside(rtMipTexture.View, rt);
        if (copyBase)
        {
            rt.Dirty = true;
            SyncRenderTargetForRead(rt);
        }
        Barrier(rtMipTexture.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        if (copyBase)
        {
            rtMipTexture.Version = rt.Version;
            if (!scaled)
            {
                var copy = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    SrcOffset = new Offset3D((int)x0, (int)y0, 0),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(w, h, 1),
                };
                DevStampOp();
                CostCopy(copy.Extent, rt.Format);
                _vk.CmdCopyImage(Cmd, NativeImageOf(rt), ImageLayout.General, rtMipTexture.Image, ImageLayout.TransferDstOptimal, 1, in copy); // guest size (VulkanRenderer.ResScale.cs)
            }
            else
            {
                CopyScaledBase(rt, rtMipTexture, scaledX, scaledY);
            }
        }
        if (uploadUpper)
        {
            // native scale: straight into the chain; otherwise into the guest-size staging levels, blitted below
            Image levelTarget = !scaled ? rtMipTexture.Image : rtMipTexture.Upper;
            ImageLayout levelLayout = !scaled ? ImageLayout.TransferDstOptimal : ImageLayout.General;
            uint levelBase = !scaled ? 0u : 1u;
            if (scaled)
            {
                Barrier(rtMipTexture.Upper, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
                    PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit, AccessFlags.TransferWriteBit);
            }
            for (uint m = 1; m < mips; m++)
            {
                MipLayout mipLayout = layout.Levels[(int)m];
                if (transferStamp != 0
                    && TransferImageForLevel(gxmTextureDesc.DataAddress + mipLayout.Offset, mipLayout.Width, mipLayout.Height, mipLayout.RowPitch, rt.Format) is { } levelImage)
                {
                    var levelCopy = new ImageCopy
                    {
                        SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                        DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m - levelBase, 0, 1),
                        Extent = new Extent3D(mipLayout.Width, mipLayout.Height, 1),
                    };
                    DevStampOp();
                    CostCopy(levelCopy.Extent, rt.Format);
                    _vk.CmdCopyImage(Cmd, levelImage.Image, ImageLayout.General, levelTarget, levelLayout, 1, in levelCopy);
                    continue;
                }

                using Texels data = TextureDecoder.Decode(gxmTextureDesc, info, gxmTextureDesc.DataAddress + mipLayout.Offset, mipLayout.Width, mipLayout.Height, mipLayout.RowPitch, SceneYuvProfiles, out uint outW, out uint outH);
                (Silk.NET.Vulkan.Buffer buf, ulong off) = UploadSpan(data.Span, 16);
                var region = new BufferImageCopy
                {
                    BufferOffset = off,
                    BufferRowLength = outW,
                    BufferImageHeight = outH,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m - levelBase, 0, 1),
                    ImageExtent = new Extent3D(mipLayout.Width, mipLayout.Height, 1),
                };
                DevStampOp();
                CostCopy(region.ImageExtent, rt.Format);
                _vk.CmdCopyBufferToImage(Cmd, buf, levelTarget, levelLayout, 1, in region);
            }
            if (scaled)
            {
                BlitUpperLevels(rtMipTexture, layout, mips);
            }
        }
        Barrier(rtMipTexture.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        return rtMipTexture.View;
    }

    /// <summary>Level 0 of a scaled chain: the target's region at its scale, copied from the target's own image.</summary>
    private void CopyScaledBase(RenderTarget rt, RtMipTexture rtMipTexture, int scaledX, int scaledY)
    {
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            SrcOffset = new Offset3D(scaledX, scaledY, 0),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(rtMipTexture.BaseWidth, rtMipTexture.BaseHeight, 1),
        };
        DevStampOp();
        CostCopy(copy.Extent, rt.Format);
        _vk.CmdCopyImage(Cmd, rt.Image, ImageLayout.General, rtMipTexture.Image, ImageLayout.TransferDstOptimal, 1, in copy);
        Barrier(rt.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit,
            AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit);
    }

    /// <summary>The guest-size staging levels of a scaled chain blitted (nearest) to levels 1.. at the chain's scale.</summary>
    private void BlitUpperLevels(RtMipTexture rtMipTexture, TexLayout layout, uint mips)
    {
        Barrier(rtMipTexture.Upper, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        for (uint m = 1; m < mips; m++)
        {
            MipLayout mipLayout = layout.Levels[(int)m];
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m, 0, 1),
            };
            uint destWidth = Math.Max(1u, rtMipTexture.BaseWidth >> (int)m);
            uint destHeight = Math.Max(1u, rtMipTexture.BaseHeight >> (int)m);
            blit.SrcOffsets[1] = new Offset3D((int)mipLayout.Width, (int)mipLayout.Height, 1);
            blit.DstOffsets[1] = new Offset3D((int)destWidth, (int)destHeight, 1);
            DevStampOp();
            CostCopy((long)mipLayout.Width * mipLayout.Height + (long)destWidth * destHeight, Format.Undefined);
            _vk.CmdBlitImage(Cmd, rtMipTexture.Upper, ImageLayout.General, rtMipTexture.Image, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Nearest);
        }
    }
}
