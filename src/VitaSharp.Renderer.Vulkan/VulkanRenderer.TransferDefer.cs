using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// VITASHARP_TRANSFER_DEFER=1: a CPU transfer (sceGxmTransferCopy/Downscale/Fill) that reads a render-target writeback
/// still in flight does not hold the GPU worker until that frame completes (240a8de made it finish every frame; on a
/// phone that was one full GPU wait per frame in Killzone). The transfer is queued on the frame that publishes the
/// writeback and runs when it retires, right after the writebacks and in order with the sync points queued after it
/// (a notification after the transfer is deferred behind it). Like the transfers of the Vita's GPU, which run after
/// the scenes before them without stopping the CPU. Once one transfer is deferred, every later transfer and fill is
/// queued behind it until it ran, so they keep their order.
/// <para>The guest memory gets the result late, but draws recorded before the transfer ran must not read it late: the
/// Vita's transfers run on the GPU in order with the scenes, so a draw after the transfer sees its result. A plain copy or
/// 2x2 downscale of 32-bit linear pixels whose source is on the GPU (a rendered surface, or the destination of an
/// earlier deferred transfer) is therefore also recorded on the GPU, in command order, into a transfer image
/// (<see cref="RecordGpuTransfer"/>): textures over its destination (<see cref="TransferImageFor"/>) and mip levels of
/// surface textures (VulkanRenderer.RtMips.cs) read that image until the CPU transfer ran. Killzone builds the mips of
/// its 384x64 light-shaft strip with a chain of four TransferDownscales and samples them in the same frame: read from
/// memory they were a frame or two late and the light shafts changed shape every frame on the phone. Other transfers
/// (colour keys, other formats or layouts) read the destination as it is (<see cref="WaitPendingTransferInto"/>).</para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>On by default on Android (the worker waiting for the GPU there costs a frame), off on desktop;
    /// VITASHARP_TRANSFER_DEFER=0/1 overrides.</summary>
    private static readonly bool s_transferDefer = Environment.GetEnvironmentVariable(EnvVars.TransferDefer)?.Trim() is { Length: > 0 } v ? v == "1" : OperatingSystem.IsAndroid();

    /// <summary><see cref="s_transferDefer"/>, or what <see cref="VulkanRendererOptions.TransferDefer"/> says.</summary>
    private bool TransferDeferOn => _options.TransferDefer ?? s_transferDefer;

    /// <summary>A deferred transfer's result made on the GPU (see the class notes): <see cref="Width"/> x
    /// <see cref="Height"/> pixels in the source surface's host format, in GENERAL layout.</summary>
    private sealed class TransferImage
    {
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public uint Width, Height, DstStride;
        public Format Format;
        /// <summary>The guest colour format of the surface the pixels came from (the texture swizzle depends on it).</summary>
        public uint ColorFormat;
        /// <summary>The frame whose retirement runs the CPU transfer; null once it ran (memory holds the result).</summary>
        public Frame? Pending;
        /// <summary>Bumped at every GPU transfer into the image (mip levels copied from it refresh on a change).</summary>
        public long Version;
        public long LastUsedFrame;
        /// <summary><see cref="Texture.SpaceId"/> of the destination memory (its process's end releases the image).</summary>
        public int SpaceId;
        /// <summary>The sampled view and what it was made for.</summary>
        public ImageView SampledView;
        public ComponentMapping SampledSwizzle;
        public bool SampledGamma;
    }

    /// <summary>Transfer images by destination address.</summary>
    private readonly Dictionary<uint, TransferImage> _xferImages = new();
    private long _statXferGpu, _statXferGpuReads;

    /// <summary>Frame the last deferred transfer runs at (later ones queue behind it until it retired).</summary>
    private Frame? _xferDeferFrame;
    /// <summary>Destinations of deferred transfers not run yet, with the frame they run at.</summary>
    private readonly List<(uint Address, uint Size, Frame Frame)> _xferPendingDst = new();
    /// <summary>Destinations of deferred transfers that ran: their cached textures and surfaces are refreshed at the
    /// next command boundary (a retirement can happen in the middle of a draw).</summary>
    private readonly List<(uint Address, uint Size)> _xferInvalidations = new();
    private long _statXferDeferred, _statXferLateReads;

    public bool DeferCallback(CallbackCommand cb)
    {
        if (!TransferDeferOn || DroppedAfterDeviceLost())
        {
            return false;
        }

        Frame? target = _xferDeferFrame is { Retired: false } d && (d.Recording || d.Submitted) ? d : null;
        if (target == null)
        {
            if (cb.ReadSize == 0)
            {
                return false;
            }
            // a source written back now: its copy is recorded into this frame, which then publishes it
            WriteBackTransferSource(cb);
            target = PendingWritebackFrame(cb.ReadAddress, (ulong)cb.ReadAddress + cb.ReadSize);
            if (target == null)
            {
                return false;
            }
        }
        else if (target != F && F.Recording && cb.ReadSize > 0)
        {
            // behind the queued transfers, and after a source written back now (which lands with the current frame)
            WriteBackTransferSource(cb);
            if (PendingWritebackFrame(cb.ReadAddress, (ulong)cb.ReadAddress + cb.ReadSize) == F)
            {
                target = F;
            }
        }
        AddressSpace? space = AddressSpace.Current;
        Frame frame = target;
        TransferImage? gpuResult = cb.Transfer is { } transfer ? RecordGpuTransfer(transfer, frame) : null;
        frame.Signals.Add(() =>
        {
            using (AddressSpace.Use(space))
            {
                try
                {
                    cb.Action();
                }
                catch (Exception e)
                {
                    Log.Error($"GPU callback failed: {e}");
                }
            }
            if (cb.WriteSize > 0)
            {
                _xferInvalidations.Add((cb.WriteAddress, cb.WriteSize));
            }

            _xferPendingDst.RemoveAll(p => p.Frame == frame);
            if (gpuResult != null && gpuResult.Pending == frame)
            {
                gpuResult.Pending = null; // memory holds the result from here on
            }

            if (_xferDeferFrame == frame)
            {
                _xferDeferFrame = null;
            }
        });
        if (cb.WriteSize > 0)
        {
            _xferPendingDst.Add((cb.WriteAddress, cb.WriteSize, frame));
        }

        _xferDeferFrame = frame;
        if (_statXferDeferred++ < 20 || s_traceSync)
        {
            Log.Info($"[gpu] transfer 0x{cb.ReadAddress:X8} -> 0x{cb.WriteAddress:X8} deferred to the {(frame == F && frame.Recording ? "recording" : "submitted")} frame's retirement (present {_presentCount})");
        }

        return true;
    }

    private void DrainTransferInvalidations()
    {
        foreach ((uint a, uint s) in _xferInvalidations)
        {
            InvalidateTextures(a, s);
        }

        _xferInvalidations.Clear();
    }

    /// <summary>A texture is about to read [addr, addr + size) from memory while a deferred transfer into it has not run:
    /// the frames the GPU already finished are published (their transfers run); a transfer whose frame is still on the
    /// GPU or being recorded is not waited for, the texture reads the destination as it is now (the previous run of that
    /// transfer: Killzone's mips of its 384x192 reflection target one frame late). Waiting there was a full GPU drain per
    /// frame (with 8 frames in flight: 7-10 vblank stalls).</summary>
    private void WaitPendingTransferInto(uint addr, uint size)
    {
        if (_xferPendingDst.Count == 0)
        {
            return;
        }

        bool overlaps = false;
        foreach ((uint a, uint s, Frame _) in _xferPendingDst)
        {
            if (a < addr + size && addr < a + s)
            {
                overlaps = true;
                break;
            }
        }

        if (!overlaps)
        {
            return;
        }

        PollRetire();
        foreach ((uint a, uint s, Frame? f) in _xferPendingDst)
        {
            if (a >= addr + size || addr >= a + s)
            {
                continue;
            }

            if (_statXferLateReads++ < 20 || s_traceSync)
            {
                Log.Info($"[gpu] texture 0x{addr:X8} reads transfer destination 0x{a:X8} before its deferred transfer ran ({(f.Recording ? "frame being recorded" : "frame on the GPU")}): read as it is now (present {_presentCount})");
            }
        }
        // only the texture cache now (a surface reload records commands: left to the command boundary)
        foreach ((uint a, uint s) in _xferInvalidations)
        {
            foreach (Texture t in _textures.Values)
            {
                if (t.DataStart < a + s && a < t.DataStart + t.DataSize)
                {
                    t.CheckedFrame = -1;
                    t.ForceCheck = true;
                }
            }
        }
    }

    /// <summary>
    /// Records <paramref name="transfer"/> on the GPU at this point of the command stream into the transfer image of its
    /// destination, when its source is on the GPU: a colour surface rendered there (not overwritten in memory since), or
    /// the transfer image of an earlier deferred transfer still pending. Only what a blit does exactly (up to rounding):
    /// 32-bit pixels, linear on both sides, the destination starting at its address, a surface in an 8-bit RGBA host
    /// format. Null when it does not apply (textures then read the memory as it is).
    /// </summary>
    private TransferImage? RecordGpuTransfer(GpuTransfer transfer, Frame frame)
    {
        if (transfer.BytesPerPixel != 4 || transfer.SrcType != 0 || transfer.DstType != 0 || transfer.DstX != 0 || transfer.DstY != 0
            || transfer.DstWidth == 0 || transfer.DstHeight == 0 || transfer.DstStride < transfer.DstWidth * 4)
        {
            return null;
        }

        uint w = transfer.DstWidth, h = transfer.DstHeight;
        uint sx = 0, sy = 0;
        Format format;
        uint colorFormat;
        RenderTarget? srcRt = null;
        TransferImage? srcXfer = null;
        if (_xferImages.TryGetValue(transfer.SrcAddress, out TransferImage? chained) && chained.Pending != null && transfer.SrcX == 0 && transfer.SrcY == 0
            && transfer.SrcStride == chained.DstStride && transfer.Width <= chained.Width && transfer.Height <= chained.Height)
        {
            srcXfer = chained;
            format = chained.Format;
            colorFormat = chained.ColorFormat;
        }
        else
        {
            srcRt = TransferSourceSurface(transfer, out sx, out sy);
            if (srcRt == null)
            {
                return null;
            }

            format = srcRt.Format;
            colorFormat = srcRt.ColorFormat;
        }

        if (!_xferImages.TryGetValue(transfer.DstAddress, out TransferImage? dst) || dst.Width != w || dst.Height != h || dst.Format != format)
        {
            if (dst != null)
            {
                ReleaseTransferImage(dst);
            }
            else if (_xferImages.Count >= MaxTransferImages)
            {
                EvictTransferImages();
            }

            dst = new TransferImage { Width = w, Height = h, Format = format, SpaceId = SpaceIdOf(transfer.DstAddress) };
            (dst.Image, dst.View, dst.Mem) = CreateImage(MemCat.Region, w, h, format, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
                ImageAspectFlags.ColorBit, mutable: true);
            _xferImages[transfer.DstAddress] = dst;
            BeginFrameIfNeeded();
            using OutsideScope init = BeginOutside(dst.View);
            Barrier(dst.Image, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        }

        dst.DstStride = transfer.DstStride;
        dst.ColorFormat = colorFormat;
        dst.LastUsedFrame = FrameCount;
        BeginFrameIfNeeded();
        using (OutsideScope outside = BeginOutside(dst.View, srcRt))
        {
            Image srcImage;
            if (srcRt != null)
            {
                srcRt.Dirty = true;
                SyncRenderTargetForRead(srcRt);
                srcImage = NativeImageOf(srcRt); // guest pixels, GENERAL (VulkanRenderer.ResScale.cs)
            }
            else
            {
                srcImage = srcXfer!.Image;
                // the earlier transfer's writes before this read
                Barrier(srcImage, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
                    PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
            }
            // the destination's earlier readers (draws, copies into mip levels) before this write
            Barrier(dst.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
                AccessFlags.ShaderReadBit | AccessFlags.TransferWriteBit | AccessFlags.TransferReadBit, AccessFlags.TransferWriteBit);
            DevStampOp();
            if (transfer.Downscale)
            {
                // a 2:1 linear blit samples between the four source pixels: their average, like the transfer
                var blit = new ImageBlit
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                };
                blit.SrcOffsets[0] = new Offset3D((int)sx, (int)sy, 0);
                blit.SrcOffsets[1] = new Offset3D((int)(sx + w * 2), (int)(sy + h * 2), 1);
                blit.DstOffsets[0] = new Offset3D(0, 0, 0);
                blit.DstOffsets[1] = new Offset3D((int)w, (int)h, 1);
                CostCopy((long)w * h * 5 / 2, Format.Undefined);
                _vk.CmdBlitImage(Cmd, srcImage, ImageLayout.General, dst.Image, ImageLayout.General, 1, in blit, Filter.Linear);
            }
            else
            {
                var copy = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    SrcOffset = new Offset3D((int)sx, (int)sy, 0),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(w, h, 1),
                };
                CostCopy(copy.Extent, Format.Undefined);
                _vk.CmdCopyImage(Cmd, srcImage, ImageLayout.General, dst.Image, ImageLayout.General, 1, in copy);
            }
            Barrier(dst.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.TransferBit,
                AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit);
        }

        dst.Pending = frame;
        dst.Version++;
        if (_statXferGpu++ < 20 || s_traceSync)
        {
            string from = srcRt != null ? $"render target 0x{srcRt.Address:X8} at {sx},{sy}" : "an earlier transfer";
            Log.Info($"[gpu] transfer 0x{transfer.SrcAddress:X8} -> 0x{transfer.DstAddress:X8} {w}x{h} ({(transfer.Downscale ? "downscale" : "copy")}) also made on the GPU from {from} (present {_presentCount})");
        }

        return dst;
    }

    /// <summary>The colour surface the transfer reads, most recently rendered first, with the transfer's top-left pixel
    /// in it: 32-bit guest pixels in an 8-bit RGBA host format, the transfer's row pitch, the whole source rectangle
    /// inside, and the memory not written since the surface was rendered. Null when there is none.</summary>
    private RenderTarget? TransferSourceSurface(GpuTransfer transfer, out uint x, out uint y)
    {
        foreach (RenderTarget candidate in TargetsContaining(transfer.SrcAddress))
        {
            uint rowBytes = candidate.Stride * 4;
            if (Formats.ColorBytesPerPixel(candidate.ColorFormat) != 4 || rowBytes != transfer.SrcStride
                || candidate.Format is not (Format.R8G8B8A8Unorm or Format.B8G8R8A8Unorm) || (candidate.ColorFormat & 0xF1800000) != 0)
            {
                continue;
            }

            uint offset = transfer.SrcAddress - candidate.Address;
            uint x0 = offset % rowBytes / 4 + transfer.SrcX, y0 = offset / rowBytes + transfer.SrcY;
            if (x0 + transfer.Width > candidate.Width || y0 + transfer.Height > candidate.Height)
            {
                continue;
            }

            if (candidate != _renderTarget && OverwrittenSinceRendered(candidate, transfer.SrcAddress, (ulong)transfer.SrcStride * (transfer.SrcY + transfer.Height)))
            {
                continue;
            }

            x = x0;
            y = y0;
            return candidate;
        }
        x = 0;
        y = 0;
        return null;
    }

    /// <summary>The transfer image of a pending deferred transfer whose destination this texture is exactly (2D,
    /// linear, one level, 32-bit, the transfer's size and row pitch): the result the guest will find in memory once the
    /// transfer ran. Null otherwise.</summary>
    private ImageView? TransferImageFor(GxmTextureDesc gxmTextureDesc, uint addr, uint w, uint h, bool gamma, bool asCube)
    {
        if (asCube || _xferImages.Count == 0 || !_xferImages.TryGetValue(addr, out TransferImage? image) || image.Pending == null
            || image.Width != w || image.Height != h || gxmTextureDesc.TextureType is not (GxmTextureDesc.TypeLinear or GxmTextureDesc.TypeLinearStrided))
        {
            return null;
        }

        uint rowPitch = gxmTextureDesc.TextureType == GxmTextureDesc.TypeLinearStrided ? gxmTextureDesc.StrideBytes : Align(w, 8) * 4;
        TexFormatInfo info = Formats.Texture(GxmFormats.BaseOf(gxmTextureDesc.Format));
        if (gxmTextureDesc.MipCount > 1 || info.Compressed || info.BitsPerPixel != 32 || rowPitch != image.DstStride
            || GxmFormats.BaseOf(gxmTextureDesc.Format) != TexBase.U8U8U8U8)
        {
            return null;
        }

        image.LastUsedFrame = FrameCount;
        ComponentMapping swizzle = RtSwizzle(image.Format, image.ColorFormat, gxmTextureDesc.Format);
        if (image.SampledView.Handle == 0 || !SameSwizzle(image.SampledSwizzle, swizzle) || image.SampledGamma != gamma)
        {
            if (image.SampledView.Handle != 0)
            {
                ImageView old = image.SampledView;
                BumpTexEpoch(1);
                DestroyAfterGpu(() => _vk.DestroyImageView(_dev, old, null));
            }

            Format viewFormat = image.Format is Format.B8G8R8A8Unorm ? Format.R8G8B8A8Unorm : image.Format;
            if (gamma)
            {
                viewFormat = Formats.Srgb(viewFormat);
            }

            image.SampledView = CreateView(image.Image, viewFormat, ImageAspectFlags.ColorBit, 1, 1, false, swizzle);
            image.SampledSwizzle = swizzle;
            image.SampledGamma = gamma;
        }
        _statXferGpuReads++;
        return image.SampledView;
    }

    /// <summary>The pending transfer image that is a mip level of a surface texture (its size and row pitch, the
    /// surface's host format), or null.</summary>
    private TransferImage? TransferImageForLevel(uint address, uint width, uint height, uint rowPitch, Format format)
    {
        if (_xferImages.Count == 0 || !_xferImages.TryGetValue(address, out TransferImage? image) || image.Pending == null
            || image.Width != width || image.Height != height || image.DstStride != rowPitch || image.Format != format)
        {
            return null;
        }

        image.LastUsedFrame = FrameCount;
        return image;
    }

    /// <summary>Transfer images kept before the ones unused for a while are dropped.</summary>
    private const int MaxTransferImages = 64;
    private readonly List<uint> _xferEvict = new();

    /// <summary>Drops the transfer images whose transfer ran and that no texture read for 60 frames.</summary>
    private void EvictTransferImages()
    {
        _xferEvict.Clear();
        foreach ((uint address, TransferImage image) in _xferImages)
        {
            if (image.Pending == null && FrameCount - image.LastUsedFrame > 60)
            {
                _xferEvict.Add(address);
            }
        }

        foreach (uint address in _xferEvict)
        {
            _xferImages.Remove(address, out TransferImage? image);
            ReleaseTransferImage(image!, false);
        }
        _xferEvict.Clear();
    }

    /// <summary>Destroys every transfer image (the renderer is disposed, the device is idle).</summary>
    private void DestroyTransferImages()
    {
        foreach (TransferImage image in _xferImages.Values)
        {
            ReleaseTransferImage(image, true);
        }

        _xferImages.Clear();
    }

    private void ReleaseTransferImage(TransferImage image, bool now = false)
    {
        if (image.SampledView.Handle != 0)
        {
            ImageView sampled = image.SampledView;
            if (now)
            {
                _vk.DestroyImageView(_dev, sampled, null);
            }
            else
            {
                BumpTexEpoch(1);
                DestroyAfterGpu(() => _vk.DestroyImageView(_dev, sampled, null));
            }
        }

        ReleaseImage(image.Image, image.View, image.Mem, now);
    }
}
