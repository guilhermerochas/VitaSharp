using System.Numerics;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

public sealed unsafe partial class VulkanRenderer
{
    sealed class Texture
    {
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public ulong Hash;
        public long CheckedFrame = -1;
        public uint DataStart, DataSize;
        public long LastUsedFrame;
    }

    readonly record struct TexKey(uint Data, uint Format, uint Width, uint Height, uint Type, uint Mips, uint Stride, uint Palette, bool Gamma);

    readonly Dictionary<TexKey, Texture> _textures = new();
    readonly Dictionary<(uint, uint), Sampler> _samplers = new();
    Image _dummyImage;
    ImageView _dummyView, _dummyCubeView;
    ImageMemoryPool.Allocation _dummyMem;
    Sampler _dummySampler;
    DescriptorSet? _emptyFbSet;

    void CreateDummyTexture()
    {
        BeginFrameIfNeeded();
        (_dummyImage, _dummyView, _dummyMem) = CreateImage(1, 1, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            ImageAspectFlags.ColorBit, layers: 6, cube: false);
        _vk.DestroyImageView(_dev, _dummyView, null);
        _dummyView = CreateView(_dummyImage, Format.R8G8B8A8Unorm, ImageAspectFlags.ColorBit, 1, 1, false, default);
        _dummyCubeView = CreateView(_dummyImage, Format.R8G8B8A8Unorm, ImageAspectFlags.ColorBit, 1, 6, true, default);
        Barrier(_dummyImage, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        var clear = new ClearColorValue(1f, 1f, 1f, 1f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 6);
        _vk.CmdClearColorImage(Cmd, _dummyImage, ImageLayout.TransferDstOptimal, in clear, 1, in range);
        Barrier(_dummyImage, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        _dummySampler = GetSampler(0, 0);
    }

    // =================================================================== samplers

    /// <summary>SceGxmTextureFilter: POINT = 0, LINEAR = 1, MIPMAP_LINEAR = 2, MIPMAP_POINT = 3.</summary>
    static Filter GxmFilter(uint f) => f is 1 or 2 ? Filter.Linear : Filter.Nearest;

    Sampler GetSampler(uint w0, uint type)
    {
        bool strided = type == GxmTextureDesc.TypeLinearStrided;
        uint key = strided ? (w0 & 0x000031F8u) | 0x80000000u : w0 & 0x07FE3FF8u;
        if (_samplers.TryGetValue((key, type), out var s)) return s;
        // linear strided textures have no min filter field: the mag filter applies to minification too
        uint mag = (w0 >> 12) & 3, min = strided ? mag : (w0 >> 10) & 3, mip = strided ? 0 : (w0 >> 9) & 1;
        uint u = (w0 >> 6) & 7, v = (w0 >> 3) & 7;
        // signed: the field is biased by 31 (31 = no bias); unsigned subtraction wrapped every negative bias to +huge (1x1 mip)
        float lodBias = strided ? 0 : ((int)((w0 >> 21) & 0x3F) - 31) / 8f;
        var sci = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = GxmFilter(mag),
            MinFilter = GxmFilter(min),
            // the mip filter only chooses blending between levels; the mip chain is always used (lod clamp is the
            // texture's own level count, applied by the image view)
            MipmapMode = mip != 0 ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
            AddressModeU = Formats.Address(u),
            AddressModeV = Formats.Address(v),
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MipLodBias = Math.Clamp(lodBias, -_core.Properties.Limits.MaxSamplerLodBias, _core.Properties.Limits.MaxSamplerLodBias),
            MinLod = 0,
            MaxLod = strided ? 0.25f : 1000f,
            BorderColor = BorderColor.FloatTransparentBlack,
        };
        if (Environment.GetEnvironmentVariable("VITASHARP_SAMPLER_DEBUG") is { } sd)
        {
            if (sd == "nobias") sci.MipLodBias = 0;
            Log.Info($"[gpu] sampler w0=0x{w0:X8} type=0x{type:X8} mag={sci.MagFilter} min={sci.MinFilter} mip={sci.MipmapMode} bias={sci.MipLodBias} maxLod={sci.MaxLod}");
        }
        _vk.CreateSampler(_dev, in sci, null, out s).Ok("vkCreateSampler");
        _samplers[(key, type)] = s;
        return s;
    }

    // =================================================================== descriptor sets

    DescriptorSet BuildTextureSet(DescriptorSetLayout layout, GxmTextureDesc[] textures, uint used)
    {
        var set = AllocSet(layout);
        var infos = stackalloc DescriptorImageInfo[16];
        var writes = stackalloc WriteDescriptorSet[16];
        for (int i = 0; i < 16; i++)
        {
            var desc = textures[i];
            ImageView view = _dummyView;
            Sampler sampler = _dummySampler;
            ImageLayout layout2 = ImageLayout.ShaderReadOnlyOptimal;
            bool cubeType = desc.TextureType is GxmTextureDesc.TypeCube or GxmTextureDesc.TypeCubeArbitrary;
            if ((used & (1u << i)) != 0 && !desc.IsNull)
            {
                var (v, l) = ResolveTexture(desc);
                if (v.Handle != 0) { view = v; layout2 = l; sampler = GetSampler(desc.W0, desc.TextureType); }
                else if (cubeType) view = _dummyCubeView;
            }
            else if (cubeType) view = _dummyCubeView;
            infos[i] = new DescriptorImageInfo(sampler, view, layout2);
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)i, DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &infos[i],
            };
        }
        _vk.UpdateDescriptorSets(_dev, 16, writes, 0, null);
        return set;
    }

    // cached in _emptyFbSet for the renderer's lifetime: allocated from the persistent pool, not a frame pool
    DescriptorSet BuildEmptyFbSet() => BuildSingleTextureSet(_dummyView, ImageLayout.ShaderReadOnlyOptimal, persistent: true);

    DescriptorSet BuildSingleTextureSet(ImageView view, ImageLayout layout, bool persistent = false)
    {
        var set = persistent ? AllocPersistentSet(_fbLayout) : AllocSet(_fbLayout);
        var info = new DescriptorImageInfo(_dummySampler, view, layout);
        var w = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &info };
        _vk.UpdateDescriptorSets(_dev, 1, &w, 0, null);
        return set;
    }

    // programmable blending: shaders read a copy of the current colour target
    Image _fbCopy;
    ImageView _fbCopyView;
    ImageMemoryPool.Allocation _fbCopyMem;
    uint _fbCopyW, _fbCopyH;
    Format _fbCopyFormat;

    DescriptorSet BuildFramebufferFetchSet()
    {
        if (_rt == null) return _emptyFbSet ??= BuildEmptyFbSet();
        EndRendering();
        if (_fbCopy.Handle == 0 || _fbCopyW != _rt.Width || _fbCopyH != _rt.Height || _fbCopyFormat != _rtAttachFormat)
        {
            if (_fbCopy.Handle != 0)
            {
                var (oi, ov, om) = (_fbCopy, _fbCopyView, _fbCopyMem);
                F.Deferred.Add(() => { _vk.DestroyImageView(_dev, ov, null); _vk.DestroyImage(_dev, oi, null); _imagePool.Free(om); });
            }
            // with surface gamma the copy is sRGB too, so the shader reads linear values
            (_fbCopy, _fbCopyView, _fbCopyMem) = CreateImage(_rt.Width, _rt.Height, _rtAttachFormat, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
            _fbCopyW = _rt.Width; _fbCopyH = _rt.Height; _fbCopyFormat = _rtAttachFormat;
            Barrier(_fbCopy, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        }
        _rt.Dirty = true;
        SyncRenderTargetForRead(_rt);
        Barrier(_fbCopy, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(_rt.Width, _rt.Height, 1),
        };
        _vk.CmdCopyImage(Cmd, _rt.Image, ImageLayout.General, _fbCopy, ImageLayout.General, 1, in copy);
        Barrier(_fbCopy, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        return BuildSingleTextureSet(_fbCopyView, ImageLayout.General);
    }

    // =================================================================== texture resolution

    (ImageView, ImageLayout) ResolveTexture(GxmTextureDesc d)
    {
        uint addr = d.DataAddress;
        uint w = d.Width, h = d.Height;
        bool gamma = d.GammaMode != 0;
        // LOD min on a texture whose base level is a rendered surface: the sampler never reads level 0, it reads the
        // smaller levels that follow it in memory (which are usually not part of the surface)
        if (d.LodMin > 0 && d.MipCount > 1 && d.TextureType is not (GxmTextureDesc.TypeCube or GxmTextureDesc.TypeCubeArbitrary)
            && RenderTargetContaining(addr) != null && BaseLevelView(d) is { } lowered)
            return ResolveTexture(lowered);
        // depth surfaces sampled as textures (X8U24 / F32 / U16): read the GPU depth, unless a colour target
        // rendered later reuses the same memory
        if (DepthTextureSource(addr, d) is { } depthSrc)
        {
            bool newerColor = false;
            foreach (var rt in _renderTargets.Values)
                if (rt.LastScene > depthSrc.LastScene && addr >= rt.Address && addr < rt.Address + rt.Stride * (uint)Formats.ColorBytesPerPixel(rt.ColorFormat) * rt.Height) { newerColor = true; break; }
            if (!newerColor) return DepthTexture(depthSrc, w, h);
        }
        // rendered surfaces are used directly (or a region of them is copied out)
        foreach (var target in _renderTargets.Values)
        {
            var rt = target;
            int bpp = Formats.ColorBytesPerPixel(rt.ColorFormat);
            uint rowBytes = rt.Stride * (uint)bpp;
            if (addr < rt.Address || addr >= rt.Address + rowBytes * rt.Height) continue;
            bool linear = d.TextureType is GxmTextureDesc.TypeLinear or GxmTextureDesc.TypeLinearStrided;
            uint texRow = d.TextureType == GxmTextureDesc.TypeLinearStrided ? d.StrideBytes : Align(w, 8) * (uint)bpp;
            uint off = addr - rt.Address;
            uint x0 = off % rowBytes / (uint)bpp, y0 = off / rowBytes;
            // the surface being rendered, read where the draw itself renders: those tiles are still on chip, so the
            // texture sees the memory, i.e. the content from the start of the scene (or the last mid-scene flush)
            if (target == _rt && _inScene && SamplesOwnTiles(x0, y0, w, h)) rt = SceneStartSource(target);
            if (linear && ReinterpretRenderTarget(rt, d, gamma) is { } alias) return (alias, ImageLayout.ShaderReadOnlyOptimal);
            if (d.TextureType == GxmTextureDesc.TypeLinear && texRow == rowBytes && x0 + w <= rt.Width && y0 + h <= rt.Height
                && RenderTargetMipmapped(rt, d, x0, y0, gamma) is { } mipped)
                return (mipped, ImageLayout.ShaderReadOnlyOptimal);
            if (addr != rt.Address || w != rt.Width || h != rt.Height)
            {
                if (!linear || texRow != rowBytes || x0 + w > rt.Width || y0 + h > rt.Height) continue;
                return (RenderTargetRegion(rt, x0, y0, w, h, d.Format, gamma), ImageLayout.ShaderReadOnlyOptimal);
            }
            SyncRenderTargetForRead(rt);
            var sw = RtSwizzle(rt, d.Format);
            if (rt.SampledView.Handle == 0 || !rt.SampledSwizzle.Equals(sw) || rt.SampledGamma != gamma)
            {
                if (rt.SampledView.Handle != 0) { var old = rt.SampledView; F.Deferred.Add(() => _vk.DestroyImageView(_dev, old, null)); }
                var viewFormat = rt.Format is Format.B8G8R8A8Unorm ? Format.R8G8B8A8Unorm : rt.Format;
                if (gamma) viewFormat = Formats.Srgb(viewFormat);
                rt.SampledView = CreateView(rt.Image, viewFormat, ImageAspectFlags.ColorBit, 1, 1, false, sw);
                rt.SampledSwizzle = sw;
                rt.SampledGamma = gamma;
            }
            return (rt.SampledView, ImageLayout.General);
        }

        var key = new TexKey(addr, d.Format, w, h, d.TextureType, d.MipCount | (d.LodMin << 8), d.TextureType == GxmTextureDesc.TypeLinearStrided ? d.StrideBytes : 0, d.PaletteAddress, gamma);
        _textures.TryGetValue(key, out var tex);
        if (TracingThisFrame) TraceTexture(key, d, Formats.Texture(GxmFormats.BaseOf(d.Format)), w, h);
        if (tex != null && tex.CheckedFrame == FrameCount) { tex.LastUsedFrame = FrameCount; return (tex.View, ImageLayout.ShaderReadOnlyOptimal); }

        var info = Formats.Texture(GxmFormats.BaseOf(d.Format));
        bool cube = d.TextureType is GxmTextureDesc.TypeCube or GxmTextureDesc.TypeCubeArbitrary;
        uint faces = cube ? 6u : 1u;
        uint mips = Math.Min(d.MipCount, (uint)BitOperations.Log2(Math.Max(w, h)) + 1);
        var layout = ComputeLayout(d, info, w, h, mips, faces);
        uint total = layout.TotalBytes;
        // LOD min (texture streaming): levels below it are not resident, so the texture is uploaded from that level on
        uint lodMin = Math.Min(d.LodMin, mips - 1);
        uint baseW = Math.Max(w >> (int)lodMin, 1), baseH = Math.Max(h >> (int)lodMin, 1);
        uint residentOffset = layout.Levels[lodMin].Offset;
        if (total == 0 || total > 64u << 20)
        {
            Log.WarnOnce($"texsize{d.Format:X}{w}{h}", $"[gpu] texture 0x{addr:X8} {w}x{h} fmt 0x{d.Format:X8} type 0x{d.TextureType:X8}: bad size {total}");
            return (default, default);
        }

        var src = GuestMemory.Span(addr + residentOffset, (int)(total - residentOffset));
        ulong hash = System.IO.Hashing.XxHash3.HashToUInt64(src);
        if (info.Convert is TexConvert.Palette4 or TexConvert.Palette8 && d.PaletteAddress != 0)
            hash ^= System.IO.Hashing.XxHash3.HashToUInt64(GuestMemory.Span(d.PaletteAddress, info.Convert == TexConvert.Palette4 ? 64 : 1024)) * 31;
        if (tex != null && tex.Hash == hash)
        {
            tex.CheckedFrame = FrameCount;
            tex.LastUsedFrame = FrameCount;
            return (tex.View, ImageLayout.ShaderReadOnlyOptimal);
        }

        // GPUs without BC support (most Android devices): decode the blocks on the CPU into RGBA8
        if (info.Compressed && info.Convert == TexConvert.None && !_core.TextureCompressionBC)
            info = info with { Format = Format.R8G8B8A8Unorm, Convert = TexConvert.BcSoftware };
        var format = info.Format;
        // texture gamma: the colour channels are sRGB-encoded and must be linearised when sampled
        if (gamma && Formats.Srgb(format) is var srgb && srgb != format && _core.SupportsSampled(srgb)) format = srgb;
        Log.WarnOnce($"texfmtinfo{d.Format:X}{d.TextureType:X}", $"[gpu] texture format 0x{d.Format:X8} ({GxmFormats.BaseOf(d.Format)}) type 0x{d.TextureType:X8} first seen {w}x{h} mips {mips}");
        if (!_core.SupportsSampled(format)) { Log.WarnOnce($"texfmt{format}", $"Texture format {format} not supported for sampling"); return (default, default); }

        if (tex == null)
        {
            tex = new Texture();
            var swizzle = Formats.TextureSwizzle(d.Format);
            (tex.Image, tex.View, tex.Mem) = CreateImage(baseW, baseH, format, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit,
                mips - lodMin, faces, cube: cube, swizzle: swizzle);
            _textures[key] = tex;
        }
        tex.Hash = hash;
        tex.CheckedFrame = FrameCount;
        tex.LastUsedFrame = FrameCount;
        tex.DataStart = addr;
        tex.DataSize = total;
        UploadTexture(tex, d, info, layout, w, h, mips, faces, format, lodMin);
        if (_opt.InspectTextures) InspectUploadedTexture(d, info, w, h);
        return (tex.View, ImageLayout.ShaderReadOnlyOptimal);
    }

    /// <summary>
    /// The texture as seen from its first sampled level (min(LOD min, last level)): a descriptor whose base level is
    /// that level's data, with the remaining mips and no LOD min. Null when the format has no simple level layout.
    /// </summary>
    static GxmTextureDesc? BaseLevelView(GxmTextureDesc d)
    {
        var info = Formats.Texture(GxmFormats.BaseOf(d.Format));
        if (info.Convert is TexConvert.Yuv420P2 or TexConvert.Yuv420P3 or TexConvert.Yuv422) return null;
        uint w = d.Width, h = d.Height;
        uint mips = Math.Min(d.MipCount, (uint)BitOperations.Log2(Math.Max(w, h)) + 1);
        uint level = Math.Min(d.LodMin, mips - 1);
        if (level == 0) return null;
        var lv = ComputeLayout(d, info, w, h, mips, 1).Levels[level];
        bool pow2 = d.TextureType is GxmTextureDesc.TypeSwizzled or GxmTextureDesc.TypeCube;
        uint w1 = pow2
            ? (d.W1 & ~0x000F000Fu) | ((uint)BitOperations.Log2(lv.Width) << 16) | (uint)BitOperations.Log2(lv.Height)
            : (d.W1 & ~0x00FFFFFFu) | ((lv.Width - 1) << 12) | (lv.Height - 1);
        uint w0 = (d.W0 & ~(0xFu << 17)) | ((mips - level - 1) << 17);
        uint w2 = (d.DataAddress + lv.Offset) & ~3u;   // the low two bits are LOD min bits 2-3
        uint w3 = d.W3 & ~(3u << 26);                  // LOD min bits 0-1
        return new GxmTextureDesc(w0, w1, w2, w3);
    }

    /// <summary>
    /// Swizzle for sampling a rendered surface. 8888 surfaces keep memory byte order, so the texture swizzle applies;
    /// other surfaces are stored with logical channels (packed formats may have been widened), so they read as-is.
    /// </summary>
    static ComponentMapping RtSwizzle(RenderTarget rt, uint texFormat)
    {
        bool is8888 = rt.Format is Format.R8G8B8A8Unorm or Format.B8G8R8A8Unorm && (rt.ColorFormat & 0xF1800000) == 0;
        return is8888 && GxmFormats.BaseOf(texFormat) == TexBase.U8U8U8U8 ? Formats.TextureSwizzle(texFormat) : default;
    }

    sealed class RtRegion
    {
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public long Version = -1;
    }

    readonly Dictionary<(RenderTarget, uint, uint, uint, uint, uint, bool), RtRegion> _rtRegions = new();

    /// <summary>Copies a sub-rectangle of a render target into its own image (textures that point inside a surface).</summary>
    ImageView RenderTargetRegion(RenderTarget rt, uint x, uint y, uint w, uint h, uint texFormat, bool gamma)
    {
        var key = (rt, x, y, w, h, texFormat, gamma);
        if (!_rtRegions.TryGetValue(key, out var r))
        {
            r = new RtRegion();
            var viewFormat = rt.Format is Format.B8G8R8A8Unorm ? Format.R8G8B8A8Unorm : rt.Format;
            if (gamma) viewFormat = Formats.Srgb(viewFormat);
            (r.Image, r.View, r.Mem) = CreateImage(w, h, rt.Format, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit,
                mutable: true);
            _vk.DestroyImageView(_dev, r.View, null);
            r.View = CreateView(r.Image, viewFormat, ImageAspectFlags.ColorBit, 1, 1, false, RtSwizzle(rt, texFormat));
            _rtRegions[key] = r;
            BeginFrameIfNeeded();
            EndRendering();
            Barrier(r.Image, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
        }
        if (r.Version == rt.Version) return r.View;
        r.Version = rt.Version;
        rt.Dirty = true;
        SyncRenderTargetForRead(rt);
        EndRendering();
        Barrier(r.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            SrcOffset = new Offset3D((int)x, (int)y, 0),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(w, h, 1),
        };
        _vk.CmdCopyImage(Cmd, rt.Image, ImageLayout.General, r.Image, ImageLayout.TransferDstOptimal, 1, in copy);
        Barrier(r.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        return r.View;
    }

    sealed class RtAlias
    {
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public RenderTarget? Source;
        public long Version = -1;
    }

    readonly Dictionary<(uint Addr, uint Format, uint W, uint H, uint Row, bool Gamma), RtAlias> _rtAliases = new();

    /// <summary>
    /// A linear texture that reads a render target's memory with a different texel size, e.g. Uncharted's 720x408
    /// F32F32 G-buffer (each pixel = packed RGBA8 albedo + S8x4 normal, written as raw bits) sampled as two
    /// 1440x408 textures: U8U8U8U8 at the surface address and S8S8S8S8 at +4. The bytes are taken from the CDRAM
    /// mirror (render target copied in guest layout) and copied into an image of the texture's format, which
    /// reinterprets them exactly like the hardware does. Null when this is not such a case.
    /// </summary>
    ImageView? ReinterpretRenderTarget(RenderTarget rt, GxmTextureDesc d, bool gamma)
    {
        int rtBpp = Formats.ColorBytesPerPixel(rt.ColorFormat);
        var info = Formats.Texture(GxmFormats.BaseOf(d.Format));
        if (info.Compressed || info.Convert != TexConvert.None || info.BitsPerPixel < 8 || info.BitsPerPixel % 8 != 0) return null;
        uint texBpp = (uint)info.BitsPerPixel / 8;
        if (texBpp == rtBpp) return null;
        uint addr = d.DataAddress, w = d.Width, h = d.Height;
        uint texRow = d.TextureType == GxmTextureDesc.TypeLinearStrided ? d.StrideBytes : Align(w, 8) * texBpp;
        if (addr < GuestMemory.CdramStart || texRow % texBpp != 0 || (addr - GuestMemory.CdramStart) % Math.Max(texBpp, 4) != 0) return null;
        if ((ulong)addr + (ulong)texRow * (h - 1) + w * texBpp > GuestMemory.CdramEnd) return null;
        var format = info.Format;
        if (gamma && Formats.Srgb(format) is var srgb && srgb != format && _core.SupportsSampled(srgb)) format = srgb;
        if (!_core.SupportsSampled(format)) return null;
        EnsureMirror();
        if (!MirrorRenderTarget(rt)) return null;

        var key = (addr, d.Format, w, h, texRow, gamma);
        if (!_rtAliases.TryGetValue(key, out var a))
        {
            a = new RtAlias();
            (a.Image, a.View, a.Mem) = CreateImage(w, h, format, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit,
                swizzle: Formats.TextureSwizzle(d.Format));
            _rtAliases[key] = a;
            Log.WarnOnce($"rtalias-{addr:X8}-{d.Format:X8}", $"[gpu] texture 0x{addr:X8} {w}x{h} fmt 0x{d.Format:X8} reinterprets render target 0x{rt.Address:X8} {rt.Width}x{rt.Height} fmt 0x{rt.ColorFormat:X8}");
            BeginFrameIfNeeded();
            EndRendering();
            Barrier(a.Image, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
        }
        if (a.Source == rt && a.Version == rt.Version) return a.View;
        a.Source = rt;
        a.Version = rt.Version;
        EndRendering();
        BufferBarrier(_mirror, PipelineStageFlags.TransferBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.TransferWriteBit | AccessFlags.ShaderReadBit, AccessFlags.TransferReadBit);
        Barrier(a.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var region = new BufferImageCopy
        {
            BufferOffset = addr - GuestMemory.CdramStart,
            BufferRowLength = texRow / texBpp,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(w, h, 1),
        };
        _vk.CmdCopyBufferToImage(Cmd, _mirror, a.Image, ImageLayout.TransferDstOptimal, 1, in region);
        Barrier(a.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        return a.View;
    }

    readonly record struct MipLayout(uint Offset, uint Width, uint Height, uint RowPitch, uint Bytes);
    readonly record struct TexLayout(MipLayout[] Levels, uint FaceStride, uint TotalBytes);

    static uint Align(uint v, uint a) => (v + a - 1) / a * a;

    static TexLayout ComputeLayout(GxmTextureDesc d, TexFormatInfo info, uint w, uint h, uint mips, uint faces)
    {
        var levels = new MipLayout[mips];
        uint type = d.TextureType;
        bool swizzled = type is GxmTextureDesc.TypeSwizzled or GxmTextureDesc.TypeCube or GxmTextureDesc.TypeSwizzledArbitrary or GxmTextureDesc.TypeCubeArbitrary;
        bool pow2 = type is GxmTextureDesc.TypeSwizzled or GxmTextureDesc.TypeCube;
        bool mipmappedLayout = mips > 1 && d.MipCountRaw != 0xF;
        uint offset = 0;
        for (uint m = 0; m < mips; m++)
        {
            uint mw = Math.Max(w >> (int)m, 1), mh = Math.Max(h >> (int)m, 1);
            uint pw = mw, ph = mh;
            if (pow2 || (swizzled && !info.Compressed)) { pw = BitOperations.RoundUpToPowerOf2(mw); ph = BitOperations.RoundUpToPowerOf2(mh); }
            uint rowPitch;
            uint bytes;
            bool yuv420 = info.Convert is TexConvert.Yuv420P2 or TexConvert.Yuv420P3;
            if (yuv420 && !swizzled && type != GxmTextureDesc.TypeTiled)
            {
                // planar YUV: rowPitch is the luma pitch; the chroma planes follow the luma plane
                rowPitch = type == GxmTextureDesc.TypeLinearStrided ? d.StrideBytes : Align(mw, 8);
                bytes = rowPitch * mh * 3 / 2;
            }
            else if (info.Compressed)
            {
                uint bw = Math.Max(Align(pw, (uint)info.BlockW) / (uint)info.BlockW, 1), bh = Math.Max(Align(ph, (uint)info.BlockH) / (uint)info.BlockH, 1);
                if (info.Convert == TexConvert.Pvrtc) { bw = Math.Max(bw, 2); bh = Math.Max(bh, 2); }
                rowPitch = bw * (uint)info.BlockBytes;
                bytes = rowPitch * bh;
            }
            else if (type == GxmTextureDesc.TypeLinearStrided)
            {
                rowPitch = d.StrideBytes;
                bytes = rowPitch * (mh - 1) + (uint)((mw * info.BitsPerPixel + 7) / 8);
            }
            else if (type == GxmTextureDesc.TypeTiled)
            {
                uint tw = Align(mw, 32), th = Align(mh, 32);
                rowPitch = tw * (uint)info.BitsPerPixel / 8;
                bytes = rowPitch * th;
            }
            else if (swizzled)
            {
                rowPitch = (uint)(pw * info.BitsPerPixel / 8);
                bytes = (uint)((ulong)pw * ph * (uint)info.BitsPerPixel / 8);
            }
            else
            {
                // linear: rows are aligned to 8 pixels
                uint aw = Align(mw, 8);
                rowPitch = (uint)(aw * info.BitsPerPixel / 8);
                bytes = rowPitch * mh;
            }
            levels[m] = new MipLayout(offset, mw, mh, rowPitch, bytes);
            // mipmapped linear/tiled textures reserve each level at the power-of-two size (rows keep their own pitch):
            // a 384x64 level 0 occupies 512x64 texels before level 1 starts (as in Vita3K's texture cache). A raw mip
            // count of 0xF (no mips) packs the data without padding.
            if (mipmappedLayout && !info.Compressed && !yuv420 && type is GxmTextureDesc.TypeLinear or GxmTextureDesc.TypeTiled)
            {
                uint lw = Math.Max(BitOperations.RoundUpToPowerOf2(w) >> (int)m, 1), lh = Math.Max(BitOperations.RoundUpToPowerOf2(h) >> (int)m, 1);
                uint aw = type == GxmTextureDesc.TypeTiled ? 32u : 8u, ah = type == GxmTextureDesc.TypeTiled ? 32u : 1u;
                offset += (uint)((ulong)Align(lw, aw) * Align(lh, ah) * (uint)info.BitsPerPixel / 8);
            }
            else offset += bytes;
        }
        bool cube = type is GxmTextureDesc.TypeCube or GxmTextureDesc.TypeCubeArbitrary;
        uint faceStride = cube ? Align(offset, 4096) : offset;
        return new TexLayout(levels, faceStride, faceStride * (faces - 1) + offset);
    }

    void UploadTexture(Texture tex, GxmTextureDesc d, TexFormatInfo info, TexLayout layout, uint w, uint h, uint mips, uint faces, Format format, uint baseLevel = 0)
    {
        EndRendering();
        Barrier(tex.Image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var regions = new List<BufferImageCopy>();
        for (uint face = 0; face < faces; face++)
        {
            for (uint m = baseLevel; m < mips; m++)
            {
                var lv = layout.Levels[m];
                uint baseAddr = d.DataAddress + face * layout.FaceStride + lv.Offset;
                byte[] linear = TextureDecoder.Decode(d, info, baseAddr, lv.Width, lv.Height, lv.RowPitch, out uint outW, out uint outH);
                var (buf, off) = UploadSpan(linear, 16);
                regions.Add(new BufferImageCopy
                {
                    BufferOffset = off,
                    BufferRowLength = info.Compressed && info.Convert == TexConvert.None ? Align(outW, (uint)info.BlockW) : outW,
                    BufferImageHeight = info.Compressed && info.Convert == TexConvert.None ? Align(outH, (uint)info.BlockH) : outH,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m - baseLevel, face, 1),
                    ImageExtent = new Extent3D(lv.Width, lv.Height, 1),
                });
                // every region may live in a different overflow buffer: submit one copy per region
                var r = regions[^1];
                _vk.CmdCopyBufferToImage(Cmd, buf, tex.Image, ImageLayout.TransferDstOptimal, 1, in r);
            }
        }
        Barrier(tex.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
    }

    void InvalidateTextures(uint addr, uint size)
    {
        foreach (var t in _textures.Values)
            if (t.DataStart < addr + size && addr < t.DataStart + t.DataSize) t.CheckedFrame = -1;
        // a CPU transfer into a render target's memory: reload it
        foreach (var rt in _renderTargets.Values)
            if (rt.Address < addr + size && addr < rt.Address + rt.Stride * rt.Height * 4) UploadSurfaceFromGuest(rt);
    }

    void DestroyTextures()
    {
        foreach (var t in _textures.Values)
        {
            _vk.DestroyImageView(_dev, t.View, null);
            _vk.DestroyImage(_dev, t.Image, null);
        }
        foreach (var s in _samplers.Values) _vk.DestroySampler(_dev, s, null);
        _vk.DestroyImageView(_dev, _dummyView, null);
        _vk.DestroyImageView(_dev, _dummyCubeView, null);
        _vk.DestroyImage(_dev, _dummyImage, null);
        if (_fbCopy.Handle != 0) { _vk.DestroyImageView(_dev, _fbCopyView, null); _vk.DestroyImage(_dev, _fbCopy, null); }
    }
}
