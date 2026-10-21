using System.Numerics;
using Silk.NET.Vulkan;
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
    sealed class RtMipTexture
    {
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public long Version = -1;
        public ulong UpperHash;
        public long CheckedFrame = -1;
    }

    readonly Dictionary<(RenderTarget, uint X, uint Y, uint Format, uint W, uint H, uint Mips, bool Gamma), RtMipTexture> _rtMipTextures = new();

    ImageView? RenderTargetMipmapped(RenderTarget rt, GxmTextureDesc d, uint x0, uint y0, bool gamma)
    {
        uint w = d.Width, h = d.Height;
        uint mips = Math.Min(d.MipCount, (uint)BitOperations.Log2(Math.Max(w, h)) + 1);
        if (mips <= 1) return null;
        var info = Formats.Texture(GxmFormats.BaseOf(d.Format));
        // level 0 is copied from the surface image, the other levels are uploaded: both need the surface's layout
        var hostFormat = rt.Format is Format.B8G8R8A8Unorm ? Format.Undefined : rt.Format;
        if (info.Compressed || info.Convert != TexConvert.None || info.Format != hostFormat
            || HostBytesPerPixel(rt.Format) != Formats.ColorBytesPerPixel(rt.ColorFormat)) return null;
        var viewFormat = gamma ? Formats.Srgb(rt.Format) : rt.Format;
        if (!_core.SupportsSampled(viewFormat)) return null;

        var key = (rt, x0, y0, d.Format, w, h, mips, gamma);
        if (!_rtMipTextures.TryGetValue(key, out var t))
        {
            t = new RtMipTexture();
            (t.Image, t.View, t.Mem) = CreateImage(w, h, rt.Format, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit,
                mips, mutable: true);
            _vk.DestroyImageView(_dev, t.View, null);
            t.View = CreateView(t.Image, viewFormat, ImageAspectFlags.ColorBit, mips, 1, false, RtSwizzle(rt, d.Format));
            _rtMipTextures[key] = t;
            Log.WarnOnce($"rtmips-{d.DataAddress:X8}", $"[gpu] texture 0x{d.DataAddress:X8} {w}x{h} with {mips} levels: level 0 is render target 0x{rt.Address:X8}, the rest comes from memory");
            BeginFrameIfNeeded();
            EndRendering();
            Barrier(t.Image, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
        }

        // the levels after the surface live in guest memory: re-upload them when that memory changes
        var layout = ComputeLayout(d, info, w, h, mips, 1);
        uint upperStart = layout.Levels[1].Offset;
        bool uploadUpper = false;
        if (t.CheckedFrame != FrameCount)
        {
            t.CheckedFrame = FrameCount;
            ulong hash = System.IO.Hashing.XxHash3.HashToUInt64(GuestMemory.Span(d.DataAddress + upperStart, (int)(layout.TotalBytes - upperStart)));
            uploadUpper = hash != t.UpperHash || t.Version < 0;
            t.UpperHash = hash;
        }
        bool copyBase = t.Version != rt.Version;
        if (!copyBase && !uploadUpper) return t.View;

        if (copyBase) { rt.Dirty = true; SyncRenderTargetForRead(rt); }
        EndRendering();
        Barrier(t.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        if (copyBase)
        {
            t.Version = rt.Version;
            var copy = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                SrcOffset = new Offset3D((int)x0, (int)y0, 0),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                Extent = new Extent3D(w, h, 1),
            };
            _vk.CmdCopyImage(Cmd, rt.Image, ImageLayout.General, t.Image, ImageLayout.TransferDstOptimal, 1, in copy);
        }
        if (uploadUpper)
        {
            for (uint m = 1; m < mips; m++)
            {
                var lv = layout.Levels[m];
                using var data = TextureDecoder.Decode(d, info, d.DataAddress + lv.Offset, lv.Width, lv.Height, lv.RowPitch, out uint outW, out uint outH);
                var (buf, off) = UploadSpan(data.Span, 16);
                var region = new BufferImageCopy
                {
                    BufferOffset = off,
                    BufferRowLength = outW,
                    BufferImageHeight = outH,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m, 0, 1),
                    ImageExtent = new Extent3D(lv.Width, lv.Height, 1),
                };
                _vk.CmdCopyBufferToImage(Cmd, buf, t.Image, ImageLayout.TransferDstOptimal, 1, in region);
            }
        }
        Barrier(t.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        return t.View;
    }
}
