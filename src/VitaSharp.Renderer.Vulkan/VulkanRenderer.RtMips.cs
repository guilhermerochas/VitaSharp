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
    }

    private readonly Dictionary<(RenderTarget, uint X, uint Y, uint Format, uint W, uint H, uint Mips, bool Gamma), RtMipTexture> _rtMipTextures = new();

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

        (RenderTarget rt, uint x0, uint y0, uint Format, uint w, uint h, uint mips, bool gamma) key = (rt, x0, y0, gxmTextureDesc.Format, w, h, mips, gamma);
        if (!_rtMipTextures.TryGetValue(key, out RtMipTexture? rtMipTexture))
        {
            rtMipTexture = new RtMipTexture();
            (rtMipTexture.Image, rtMipTexture.View, rtMipTexture.Mem) = CreateImage(MemCat.Region, w, h, rt.Format, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit,
                mips, mutable: true);
            _vk.DestroyImageView(_dev, rtMipTexture.View, null);
            rtMipTexture.View = CreateView(rtMipTexture.Image, viewFormat, ImageAspectFlags.ColorBit, mips, 1, false, RtSwizzle(rt, gxmTextureDesc.Format));
            _rtMipTextures[key] = rtMipTexture;
            Log.WarnOnce($"rtmips-{gxmTextureDesc.DataAddress:X8}", $"[gpu] texture 0x{gxmTextureDesc.DataAddress:X8} {w}x{h} with {mips} levels: level 0 is render target 0x{rt.Address:X8}, the rest comes from memory");
            BeginFrameIfNeeded();
            using OutsideScope init = BeginOutside(rtMipTexture.View);
            Barrier(rtMipTexture.Image, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
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
            var copy = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                SrcOffset = new Offset3D((int)x0, (int)y0, 0),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                Extent = new Extent3D(w, h, 1),
            };
            DevStampOp();
            _vk.CmdCopyImage(Cmd, NativeImageOf(rt), ImageLayout.General, rtMipTexture.Image, ImageLayout.TransferDstOptimal, 1, in copy); // guest size (VulkanRenderer.ResScale.cs)
        }
        if (uploadUpper)
        {
            for (uint m = 1; m < mips; m++)
            {
                MipLayout mipLayout = layout.Levels[(int)m];
                using Texels data = TextureDecoder.Decode(gxmTextureDesc, info, gxmTextureDesc.DataAddress + mipLayout.Offset, mipLayout.Width, mipLayout.Height, mipLayout.RowPitch, out uint outW, out uint outH);
                (Silk.NET.Vulkan.Buffer buf, ulong off) = UploadSpan(data.Span, 16);
                var region = new BufferImageCopy
                {
                    BufferOffset = off,
                    BufferRowLength = outW,
                    BufferImageHeight = outH,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m, 0, 1),
                    ImageExtent = new Extent3D(mipLayout.Width, mipLayout.Height, 1),
                };
                DevStampOp();
                _vk.CmdCopyBufferToImage(Cmd, buf, rtMipTexture.Image, ImageLayout.TransferDstOptimal, 1, in region);
            }
        }
        Barrier(rtMipTexture.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        return rtMipTexture.View;
    }
}
