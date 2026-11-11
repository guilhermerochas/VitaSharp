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
        /// <summary>Dirty-page epoch the contents were last validated at (<see cref="DirtyPages"/>).</summary>
        public uint Stamp;
        /// <summary>Set by GPU transfers into the texture's memory: re-check regardless of the dirty pages.</summary>
        public bool ForceCheck;
        /// <summary><see cref="AddressSpace.Id"/> of the process whose memory the contents were read from (0 for CDRAM
        /// and kernel memory, the same in every space): private memory at the same address is another process's data.</summary>
        public int SpaceId;
        /// <summary>How often the contents changed after the first upload (the content cache keeps images from the second
        /// change on: VulkanRenderer.TexContentCache.cs).</summary>
        public int Changes;
    }

    /// <summary>The id the texture cache keeps for data at <paramref name="addr"/> read now (see <see cref="Texture.SpaceId"/>).</summary>
    static int SpaceIdOf(uint addr) => GuestMemory.IsSharedAddress(addr) ? 0 : AddressSpace.Current?.Id ?? 0;

    /// <summary>Texture cache counters (PERF texcache line).</summary>
    long _texChecks, _texSkipped, _texHashed, _texHashedBytes, _texHashTicks, _texUploads, _texMissed;
    /// <summary>Time in texture uploads (decode, transcode, staging), and uploads of a content uploaded before.</summary>
    long _texUploadTicks, _texUploadRepeats;
    readonly HashSet<ulong> _uploadedContents = new();
    long _sweptFrame = -1;
    long _sweepTicks;

    /// <summary>Folds the CPU/HLE page dirty table into page versions, once per frame (GPU worker thread).</summary>
    void SweepDirtyPages()
    {
        if (_sweptFrame == FrameCount) return;
        _sweptFrame = FrameCount;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        DirtyPages.Sweep();
        _sweepTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>One-line texture cache report: how much guest memory was re-hashed per frame and how much was skipped.</summary>
    public string? Report() => TextureCacheReport() + "\n" + GpuTimeReport() + "\n" + SyncReport() + ReplayReport() + DrawRingReport() + NanReport() + "\n" + VramReport() + ResScaleReport() + MobileReport() + WorkerProfReport();

    public string TextureCacheReport()
    {
        double frames = Math.Max(1, FrameCount);
        double f = System.Diagnostics.Stopwatch.Frequency / 1000.0;
        return $"PERF texcache ({DirtyPages.Mode switch { "0" => "dirty tracking off", "verify" => "verify", _ => "dirty tracking" }}): " +
               $"{_texChecks / frames:F1} checks/frame, {_texSkipped / frames:F1} clean (skipped), {_texHashed / frames:F1} hashed " +
               $"({_texHashedBytes / frames / 1024:F0} KB/frame, {_texHashTicks / f / frames:F3} ms/frame), {_texUploads / frames:F2} uploads/frame ({_texUploadTicks / f / frames:F3} ms/frame, {_texUploadRepeats / frames:F2} of contents uploaded before), content cache {_texContentHits / frames:F2} images taken back/frame ({_contentCache.Count} kept, {_contentCacheBytes >> 20} MB), " +
               $"sweep {_sweepTicks / f / frames:F3} ms/frame" + (DirtyPages.Verify ? $", MISSED {_texMissed}" : "")
               + (_opt.InspectTextures ? $"; test inspection {_inspectTicks / f / frames:F3} ms/frame ({_inspectDone} inspected, {_inspectDuplicate} repeated contents skipped, {_inspectDeferred} over the frame budget: {_inspectBackground} inspected in the background, {_inspectStale} changed before their turn, {_inspectQueue.Count} still queued)" : "");
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
        (_dummyImage, _dummyView, _dummyMem) = CreateImage(MemCat.Other, 1, 1, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            ImageAspectFlags.ColorBit, layers: 6, cube: true); // cube-compatible: the dummy cube view below needs it
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
        DummyMsView();
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
        if (Environment.GetEnvironmentVariable(EnvVars.SamplerDebug) is { } sd)
        {
            if (sd == "nobias") sci.MipLodBias = 0;
            Log.Info($"[gpu] sampler w0=0x{w0:X8} type=0x{type:X8} mag={sci.MagFilter} min={sci.MinFilter} mip={sci.MipmapMode} bias={sci.MipLodBias} maxLod={sci.MaxLod}");
        }
        _vk.CreateSampler(_dev, in sci, null, out s).Ok("vkCreateSampler");
        _samplers[(key, type)] = s;
        return s;
    }

    // =================================================================== descriptor sets

    /// <summary>The last texture set built for each stage: consecutive draws that bind the same views and samplers reuse it
    /// (a set allocation and up to 16 descriptor writes per stage and draw were a large part of the GPU worker's time on
    /// a Mali phone). Valid until a frame's descriptor pools are reset (<see cref="_frameSerial"/>).</summary>
    sealed class TexSetCache
    {
        public readonly DescriptorImageInfo[] Infos = new DescriptorImageInfo[16];
        public DescriptorSet Set;
        public long Serial = -1;
        public int Units;
        public uint Mask;
        /// <summary>The set came from the persistent texture-set pool (VITASHARP_TEXSET_PERSIST): it may be kept across submits.</summary>
        public bool Kept;
    }
    readonly TexSetCache _texSetV = new(), _texSetF = new();

    /// <summary>The texture's view, or none when there is no GPU memory for it even after eviction (the draw samples the
    /// dummy texture instead). Kept out of <see cref="BuildTextureSet"/>: a handler inside its loop forced the loop's
    /// locals to memory.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    (ImageView, ImageLayout) ResolveTextureOrSkip(GxmTextureDesc desc, bool asCube)
    {
        try { return ResolveTexture(desc, asCube); }
        catch (VkOutOfMemoryException e)
        {
            _oomTextures++;
            LogOom($"[vram] out of GPU memory, texture 0x{desc.DataAddress:X8} {desc.Width}x{desc.Height} fmt 0x{desc.Format:X8} skipped for this draw (present {_presentCount}, draw {_drawSerial}): {e.Message}", force: _oomTextures <= 3);
            return default;
        }
    }

    /// <summary>VITASHARP_TEXLOOKUP=old: the texture lookups of main b29e26a, for A/B runs on the phone.</summary>
    internal static readonly bool TexLookupOld = Environment.GetEnvironmentVariable(EnvVars.TexLookup)?.Trim() == "old";

    /// <summary>The b29e26a loop (every unit visited, the handler inside the loop), kept for A/B runs.</summary>
    void FillTextureInfosOld(DescriptorImageInfo* infos, GxmTextureDesc[] textures, uint used, uint cubeSamplers, int units, bool vertexStage)
    {
        for (int i = 0; i < units; i++)
        {
            var desc = textures[i];
            ImageView view = _dummyView;
            Sampler sampler = _dummySampler;
            ImageLayout layout2 = ImageLayout.ShaderReadOnlyOptimal;
            bool cubeType = desc.TextureType is GxmTextureDesc.TypeCube or GxmTextureDesc.TypeCubeArbitrary;
            bool asCube = !cubeType && (cubeSamplers & (1u << i)) != 0;
            if (asCube) cubeType = true;
            if ((used & (1u << i)) != 0 && !desc.IsNull)
            {
                ImageView v;
                ImageLayout l;
                try { (v, l) = ResolveTexture(desc, asCube); }
                catch (VkOutOfMemoryException e)
                {
                    (v, l) = (default, default);
                    _oomTextures++;
                    LogOom($"[vram] out of GPU memory, texture 0x{desc.DataAddress:X8} {desc.Width}x{desc.Height} fmt 0x{desc.Format:X8} skipped for this draw (present {_presentCount}, draw {_drawSerial}): {e.Message}", force: _oomTextures <= 3);
                }
                if (v.Handle != 0) { view = v; layout2 = l; sampler = GetSampler(desc.W0, desc.TextureType); NoteSampled(v); }
                else if (cubeType) view = _dummyCubeView;
                if (_drawRing != null) DrawRingTexture(vertexStage, i, desc, v.Handle == 0);
            }
            else if (cubeType) view = _dummyCubeView;
            infos[i] = new DescriptorImageInfo(sampler, view, layout2);
        }
    }

    /// <summary>
    /// A small direct-mapped memo of texture units that resolved to guest-memory textures in this frame: a unit with the
    /// same descriptor takes the view and sampler again without the lookups (colour/depth target scans, the texture
    /// cache key, the sampler table). Consecutive draws mostly bind the same textures (F1 2011's ~1,400 draws a frame:
    /// unit resolution was about half of the GPU worker's per-draw time). An entry holds while the frame, the view epoch
    /// (<see cref="_texEpoch"/>: no image released or replaced) and the colour/depth target set (<see cref="_targetGen"/>)
    /// are the ones it was made in; colour-target sources are never memoised (their barriers and versions are per use).
    /// VITASHARP_UNIT_MEMO=0 turns it off (A/B).
    /// </summary>
    readonly struct UnitMemo(GxmTextureDesc desc, bool asCube, long frame, long epoch, long targetGen, int space, ImageView view, Sampler sampler, Texture tex)
    {
        readonly GxmTextureDesc _desc = desc;
        readonly bool _asCube = asCube;
        readonly long _frame = frame, _epoch = epoch, _targetGen = targetGen;
        public readonly int Space = space;
        public readonly ImageView View = view;
        public readonly Sampler Sampler = sampler;
        public readonly Texture? Tex = tex;
        public bool Hit(in GxmTextureDesc d, bool asCube, long frame, long epoch, long targetGen) =>
            _frame == frame && _epoch == epoch && _targetGen == targetGen && _asCube == asCube && _desc == d && Tex != null;
    }

    static readonly bool UnitMemoOn = Environment.GetEnvironmentVariable(EnvVars.UnitMemo) != "0";
    readonly UnitMemo[] _unitMemo = new UnitMemo[1024];
    long _statUnitMemoHits;
    /// <summary>Bumped whenever a colour or depth target is added or removed (texture sources may change).</summary>
    long _targetGen;

    static int UnitMemoSlot(in GxmTextureDesc d, bool asCube) =>
        (int)((d.W2 >> 4 ^ d.W2 >> 13 ^ d.W0 ^ d.W1 >> 7 ^ (asCube ? 0x55u : 0)) & 1023);

    DescriptorSet BuildTextureSet(DescriptorSetLayout layout, GxmTextureDesc[] textures, uint used, uint cubeSamplers)
    {
        ProfMark(ProfPhase.TexPre);
        bool vertexStage = layout.Handle == _texLayoutV.Handle;
        int units = vertexStage ? _texUnitsV : _texUnitsF;
        var infos = stackalloc DescriptorImageInfo[16];
        var writes = stackalloc WriteDescriptorSet[16];
        // only the units the program samples are resolved and written: the others are never read by its shader (a set
        // from a frame pool may leave them undefined). Visiting all 16 units per stage and draw (a descriptor built for
        // each, in a loop the exception handler kept out of registers) was the GPU worker's largest own cost in F1 2011
        // races on the phone.
        uint mask = units >= 32 ? used : used & ((1u << units) - 1);
        if (TexSetReuse && !TexLookupOld && _drawRing == null && ReusedTextureSet(vertexStage, textures, mask, cubeSamplers, units) is { } reused) { ProfMark(ProfPhase.TexHit); return reused; }
        _reuseOk = TexSetReuse && !TexLookupOld && _drawRing == null;
        _reuseDeps = 0;
        _reuseMem = 0;
        if (TexLookupOld) FillTextureInfosOld(infos, textures, used, cubeSamplers, units, vertexStage);
        else for (uint m = mask; m != 0; m &= m - 1)
        {
            int i = System.Numerics.BitOperations.TrailingZeroCount(m);
            var desc = textures[i];
            bool cubeType = desc.TextureType is GxmTextureDesc.TypeCube or GxmTextureDesc.TypeCubeArbitrary;
            // a 2D texture bound to a samplerCube (Resistance's window reflections): binding the 2D view there is
            // undefined in Vulkan (NVIDIA returned saturated red), so the texture is read as a cube whose six faces
            // are the 2D image
            bool asCube = !cubeType && (cubeSamplers & (1u << i)) != 0;
            if (asCube) cubeType = true;
            ref var info = ref infos[i];
            info.Sampler = _dummySampler;
            info.ImageView = cubeType ? _dummyCubeView : _dummyView;
            info.ImageLayout = ImageLayout.ShaderReadOnlyOptimal;
            if (desc.IsNull) continue;
            ProfMark(ProfPhase.TexResolve);
            ImageView v;
            ImageLayout l;
            ref var memo = ref _unitMemo[UnitMemoSlot(desc, asCube)];
            if (UnitMemoOn && memo.Hit(desc, asCube, FrameCount, _texEpoch, _targetGen) && memo.Space == SpaceIdOf(desc.DataAddress) && _drawRing == null)
            {
                // the same guest-memory texture as a unit resolved earlier this frame: nothing it depends on changed
                v = memo.View; l = ImageLayout.ShaderReadOnlyOptimal;
                _texSrc = TexSource.Memory; _texSrcTex = memo.Tex; _texSrcRt = null; _texSrcAddr = 0;
                memo.Tex!.LastUsedFrame = FrameCount;
                info.ImageView = v; info.ImageLayout = l; info.Sampler = memo.Sampler; NoteSampled(v);
                _statUnitMemoHits++;
                ProfUnit(TexSource.Memory | TexSource.Memo);
            }
            else
            {
                (v, l) = ResolveTextureOrSkip(desc, asCube);
                ProfUnit(_texSrc);
                if (v.Handle != 0)
                {
                    info.ImageView = v; info.ImageLayout = l; info.Sampler = GetSampler(desc.W0, desc.TextureType); NoteSampled(v);
                    if (UnitMemoOn && _texSrc == TexSource.Memory && _texSrcTex is { } t && t.View.Handle == v.Handle && !TracingThisFrame)
                        memo = new UnitMemo(desc, asCube, FrameCount, _texEpoch, _targetGen, SpaceIdOf(desc.DataAddress), v, info.Sampler, t);
                }
            }
            if (_drawRing != null) DrawRingTexture(vertexStage, i, desc, v.Handle == 0);
            if (_reuseOk) NoteReuseSource(i, v);
            ProfMark(ProfPhase.TexSampler);
        }
        ProfMark(ProfPhase.TexResolve);
        var cache = vertexStage ? _texSetV : _texSetF;
        if (cache.Serial == _frameSerial && cache.Units == units && cache.Mask == mask && cache.Set.Handle != 0)
        {
            bool same = true;
            for (int i = 0; i < units && same; i++)
            {
                if ((mask & (1u << i)) == 0) continue;
                ref var c = ref cache.Infos[i];
                same = c.Sampler.Handle == infos[i].Sampler.Handle && c.ImageView.Handle == infos[i].ImageView.Handle && c.ImageLayout == infos[i].ImageLayout;
            }
            if (same)
            {
                if (TexSetPersist && !cache.Kept) _reuseOk = false; // a frame-pool set: gone at the next pool reset
                StoreReusedSet(vertexStage, textures, mask, cubeSamplers, units, cache.Set, infos);
                return cache.Set;
            }
        }
        // a set that will be kept across submits comes from the persistent texture-set pool (VulkanRenderer.TexSetReuse.cs)
        bool kept = TexSetPersist && _reuseOk;
        var set = kept ? AllocKeptTexSet(layout) : AllocSet(layout);
        uint n = 0;
        for (int i = 0; i < units; i++)
        {
            if ((mask & (1u << i)) == 0) continue;
            writes[n++] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)i, DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &infos[i],
            };
            cache.Infos[i] = infos[i];
        }
        if (n > 0) _vk.UpdateDescriptorSets(_dev, n, writes, 0, null);
        ProfMark(ProfPhase.TexAlloc);
        _statDescWrites += n;
        cache.Mask = mask;
        cache.Set = set;
        cache.Kept = kept;
        cache.Serial = _frameSerial;
        cache.Units = units;
        StoreReusedSet(vertexStage, textures, mask, cubeSamplers, units, set, infos);
        return set;
    }

    // cached in _emptyFbSet for the renderer's lifetime: allocated from the persistent pool, not a frame pool
    DescriptorSet BuildEmptyFbSet() => BuildFetchSet(_dummyView, ImageLayout.ShaderReadOnlyOptimal, DummyMsView(), ImageLayout.General, persistent: true);

    DescriptorSet BuildSingleTextureSet(ImageView view, ImageLayout layout) => BuildFetchSet(view, layout, DummyMsView(), ImageLayout.General);

    /// <summary>Framebuffer-fetch set: <paramref name="view"/> single-sampled, <paramref name="msView"/> multisampled.</summary>
    DescriptorSet BuildFetchSet(ImageView view, ImageLayout layout, ImageView msView, ImageLayout msLayout, bool persistent = false)
    {
        var set = persistent ? AllocPersistentSet(_fbLayout) : AllocSet(_fbLayout);
        var info = stackalloc DescriptorImageInfo[2] { new(_dummySampler, view, layout), new(_dummySampler, msView, msLayout) };
        var w = stackalloc WriteDescriptorSet[2];
        for (int i = 0; i < 2; i++)
            w[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)i, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &info[i] };
        _vk.UpdateDescriptorSets(_dev, 2, w, 0, null);
        return set;
    }

    // programmable blending: shaders read a copy of the current colour target
    Image _fbCopy;
    ImageView _fbCopyView;
    ImageMemoryPool.Allocation _fbCopyMem;
    uint _fbCopyW, _fbCopyH;
    Format _fbCopyFormat;
    /// <summary>Framebuffer-fetch copies of other sizes/formats, kept for reuse (all in GENERAL layout).</summary>
    readonly Dictionary<(uint W, uint H, Format F), (Image Image, ImageView View, ImageMemoryPool.Allocation Mem)> _fbCopies = new();
    long _fbCopiesPixels;
    readonly HashSet<uint> _ssFetchLogged = new();

    /// <param name="tileWord1">the shader also reads the scene's tile-word attachment (a copy of it, binding 4)</param>
    DescriptorSet BuildFramebufferFetchSet(bool tileWord1)
    {
        var set = BuildFramebufferFetchSet();
        if (tileWord1 && _tw1 != null && _rt != null && !_ms) // (never the persistent empty set: there is a target)
        {
            EndRendering(resolve: false);
            WriteTileWord1Copy(set, CopyTileWord1());
        }
        return set;
    }

    DescriptorSet BuildFramebufferFetchSet()
    {
        if (_rt == null) return _emptyFbSet ??= BuildEmptyFbSet();
        if (_ms) return BuildMsaaFetchSet();
        // A supersampled (MSAA downscale) scene rasterises at 2x, so gl_FragCoord addresses samples and the shader
        // must read the sample image, not the 1x surface (reading the surface put a half-size copy of the scene into
        // the top-left quarter of every programmable-blending pass). Each sample reads its own value, as on hardware;
        // the samples are box-filtered into the surface when rendering pauses for something else.
        EndRendering(resolve: !_ss);
        var ss = _ss ? GetSupersampleImage(_rt) : null;
        if (ss != null && _ssFetchLogged.Add(_rt.Address)) Log.Info($"[gpu] programmable blending in supersampled scene 0x{_rt.Address:X8}: shaders read the {ss.Width}x{ss.Height} sample image");
        uint w = ss?.Width ?? _rt.ImgW, h = ss?.Height ?? _rt.ImgH;
        if (_fbCopy.Handle == 0 || _fbCopyW != w || _fbCopyH != h || _fbCopyFormat != _rtAttachFormat)
        {
            // games alternate between targets of different sizes: keep the copies per size/format instead of recreating
            // one (image, memory, deferred destroy closure) on every switch
            if (_fbCopy.Handle != 0)
            {
                var old = (_fbCopy, _fbCopyView, _fbCopyMem);
                // bounded: at most 32 spare copies and 4 M pixels (16 MB at 4 bytes per pixel)
                if (_fbCopies.Count < 32 && _fbCopiesPixels + (long)_fbCopyW * _fbCopyH <= 4L << 20)
                {
                    _fbCopies[(_fbCopyW, _fbCopyH, _fbCopyFormat)] = old;
                    _fbCopiesPixels += (long)_fbCopyW * _fbCopyH;
                }
                else
                {
                    var (oi, ov, om) = old;
                    DestroyAfterGpu(() => { _vk.DestroyImageView(_dev, ov, null); _vk.DestroyImage(_dev, oi, null); _imagePool.Free(om); });
                }
            }
            if (_fbCopies.Remove((w, h, _rtAttachFormat), out var cached))
            {
                _fbCopiesPixels -= (long)w * h;
                (_fbCopy, _fbCopyView, _fbCopyMem) = cached; // already in GENERAL layout
                _fbCopyW = w; _fbCopyH = h; _fbCopyFormat = _rtAttachFormat;
            }
            else
            {
                // with surface gamma the copy is sRGB too, so the shader reads linear values
                (_fbCopy, _fbCopyView, _fbCopyMem) = CreateImage(MemCat.Region, w, h, _rtAttachFormat, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
                _fbCopyW = w; _fbCopyH = h; _fbCopyFormat = _rtAttachFormat;
                Barrier(_fbCopy, ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
            }
        }
        var src = _rt.Image;
        if (ss != null)
        {
            src = ss.Image;
            Barrier(src, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
                PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
                AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        }
        else
        {
            _rt.Dirty = true;
            SyncRenderTargetForRead(_rt);
        }
        // only the draw's region clip is read (shaders fetch at their own fragment)
        if (FetchRegion(SceneScale, w, h) is not (var offset, var extent)) return BuildSingleTextureSet(_fbCopyView, ImageLayout.General);
        Barrier(_fbCopy, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            SrcOffset = offset, DstOffset = offset, Extent = extent,
        };
        DevStampOp();
        _vk.CmdCopyImage(Cmd, src, ImageLayout.General, _fbCopy, ImageLayout.General, 1, in copy);
        Barrier(_fbCopy, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        // the copy must finish before the scene renders into the samples / surface again
        Barrier(src, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit, PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
        return BuildSingleTextureSet(_fbCopyView, ImageLayout.General);
    }

    // =================================================================== texture resolution

    (ImageView, ImageLayout) ResolveTexture(GxmTextureDesc d, bool asCube = false)
    {
        uint addr = d.DataAddress;
        uint w = d.Width, h = d.Height;
        bool gamma = d.GammaMode != 0;
        if (TraceTexAddr != 0 && addr - TraceTexAddr < 0x100000) Log.Info($"[trace-tex] resolve-entry 0x{addr:X8} {w}x{h} type 0x{d.TextureType:X8} rt={RenderTargetContaining(addr) != null}");
        // a 2D texture sampled as a cube: only plain square textures (rendered surfaces keep the dummy cube)
        // _texSrc/_texSrcAddr: which path was taken, for the debug-mode draw ring (VulkanRenderer.DrawRing.cs)
        _texSrcAddr = 0;
        _texSrcRt = null;
        _texSrcTex = null;
        if (asCube && (w != h || RenderTargetContaining(addr) != null)) { _texSrc = TexSource.None; return (default, default); }
        if (asCube) { _texSrc = TexSource.MemoryCube; return ResolveMemoryTexture(d, addr, w, h, gamma, asCube); }
        // LOD min on a texture whose base level is a rendered surface: the sampler never reads level 0, it reads the
        // smaller levels that follow it in memory (which are usually not part of the surface)
        if (d.LodMin > 0 && d.MipCount > 1 && d.TextureType is not (GxmTextureDesc.TypeCube or GxmTextureDesc.TypeCubeArbitrary)
            && RenderTargetContaining(addr) != null && BaseLevelView(d) is { } lowered)
        {
            var res = ResolveTexture(lowered);
            _texSrc |= TexSource.LodLowered;
            return res;
        }
        // depth surfaces sampled as textures (X8U24 / F32 / U16): read the GPU depth, unless a colour target
        // rendered later reuses the same memory
        if (DepthTextureSource(addr, d) is { } depthSrc)
        {
            bool newerColor = false;
            foreach (var rt in _renderTargets.Values)
                if (rt.LastScene > depthSrc.LastScene && addr >= rt.Address && addr < rt.Address + rt.Stride * (uint)Formats.ColorBytesPerPixel(rt.ColorFormat) * rt.Height) { newerColor = true; break; }
            if (!newerColor) { _texSrc = TexSource.Depth; _texSrcAddr = depthSrc.Address; return DepthTexture(depthSrc, w, h); }
        }
        // a separate stencil plane sampled as U8: read the GPU stencil, unless a colour target rendered later reuses it
        if (StencilTextureSource(addr, d) is { } stencilSrc
            && (RenderTargetContaining(addr) is not { } laterRt || laterRt.LastScene < stencilSrc.LastScene))
        {
            _texSrc = TexSource.Depth; _texSrcAddr = stencilSrc.StencilAddress;
            return StencilTexture(stencilSrc, d);
        }
        if (DebugMode.Enabled) DbgTextureOverlap(d);
        // rendered surfaces are used directly (or a region of them is copied out)
        foreach (var target in TargetsContaining(addr))
        {
            var rt = target;
            // in use from here on: an allocation below (region, alias, mip chain, mirror) that runs out of memory must not evict it
            target.LastTouchedFrame = FrameCount;
            int bpp = Formats.ColorBytesPerPixel(rt.ColorFormat);
            uint rowBytes = rt.Stride * (uint)bpp;
            if (addr < rt.Address || addr >= rt.Address + rowBytes * rt.Height) continue;
            bool linear = d.TextureType is GxmTextureDesc.TypeLinear or GxmTextureDesc.TypeLinearStrided;
            uint texRow = d.TextureType == GxmTextureDesc.TypeLinearStrided ? d.StrideBytes : Align(w, 8) * (uint)bpp;
            uint off = addr - rt.Address;
            uint x0 = off % rowBytes / (uint)bpp, y0 = off / rowBytes;
            if (target != _rt && OverwrittenSinceRendered(target, addr, TextureBytes(d, w, h))) continue;
            // the surface being rendered, read where the draw itself renders: those tiles are still on chip, so the
            // texture sees the memory, i.e. the content from the start of the scene (or the last mid-scene flush)
            if (target == _rt && _inScene && SamplesOwnTiles(x0, y0, w, h)) rt = SceneStartSource(target);
            _texSrcAddr = target.Address;
            _texSrcRt = target;
            var sceneStart = rt != target ? TexSource.SceneStart : 0;
            if (linear && ReinterpretRenderTarget(rt, d, gamma) is { } alias) { _texSrc = TexSource.Reinterpret | sceneStart; return (alias, ImageLayout.ShaderReadOnlyOptimal); }
            if (d.TextureType == GxmTextureDesc.TypeLinear && texRow == rowBytes && x0 + w <= rt.Width && y0 + h <= rt.Height
                && RenderTargetMipmapped(rt, d, x0, y0, gamma) is { } mipped)
            {
                _texSrc = TexSource.RtMipmapped | sceneStart;
                return (mipped, ImageLayout.ShaderReadOnlyOptimal);
            }
            if (addr != rt.Address || w != rt.Width || h != rt.Height)
            {
                if (!linear || texRow != rowBytes || x0 >= rt.Width) continue;
                // a power-of-two texture laid over a smaller surface with the same row pitch (Persona 4 Golden's
                // 840x476 scene, stride 1024, sampled as a 1024x1024 texture): the surface fills its top-left corner.
                // When other colour targets lie in the texture's memory too (atlases of several targets), the guest
                // memory is read instead, but only if this surface is written back there: otherwise that memory is
                // stale. Dragon's Crown samples its 960x544 back buffer (pitch 1024) as a 1024x1024 texture that
                // also reaches the second back buffer 3 MB further on; read from memory, every other frame showed
                // old memory (a magenta block) where the post effect copies the frame.
                bool overhang = x0 + w > rt.Width || y0 + h > rt.Height;
                if (overhang && rt.WrittenBack && !OnlyTargetIn(rt, addr, texRow * h)) continue;
                _texSrc = TexSource.Region | sceneStart;
                return (RenderTargetRegion(rt, x0, y0, w, h, d.Format, gamma), ImageLayout.ShaderReadOnlyOptimal);
            }
            SyncRenderTargetForRead(rt);
            TraceSampledSnapshot(rt);
            var sw = RtSwizzle(rt, d.Format);
            if (rt.SampledView.Handle == 0 || !SameSwizzle(rt.SampledSwizzle, sw) || rt.SampledGamma != gamma)
            {
                if (rt.SampledView.Handle != 0) { var old = rt.SampledView; BumpTexEpoch(1); DestroyAfterGpu(() => _vk.DestroyImageView(_dev, old, null)); }
                var viewFormat = rt.Format is Format.B8G8R8A8Unorm ? Format.R8G8B8A8Unorm : rt.Format;
                if (gamma) viewFormat = Formats.Srgb(viewFormat);
                var viewSw = sw;
                if (!rt.CanView(viewFormat))
                {
                    // a target with a short format list (VITASHARP_RT_FORMAT_LIST): the RGBA reading of a BGRA image is a swizzle
                    if (rt.Format is Format.B8G8R8A8Unorm) viewSw = Formats.Compose(new ComponentMapping(ComponentSwizzle.B, ComponentSwizzle.G, ComponentSwizzle.R, ComponentSwizzle.A), sw);
                    viewFormat = gamma && rt.CanView(Formats.Srgb(rt.Format)) ? Formats.Srgb(rt.Format) : rt.Format;
                    if (gamma && viewFormat == rt.Format) Log.WarnOnce("rt-immutable-gamma-tex", "[afbc] render target without an sRGB view sampled with gamma: read linear (measurement only)");
                }
                rt.SampledView = CreateView(rt.Image, viewFormat, ImageAspectFlags.ColorBit, 1, 1, false, viewSw);
                rt.SampledSwizzle = sw;
                rt.SampledGamma = gamma;
            }
            _texSrc = TexSource.RenderTarget | sceneStart;
            return (rt.SampledView, ImageLayout.General);
        }
        _texSrc = TexSource.Memory;
        _texSrcAddr = 0;
        return ResolveMemoryTexture(d, addr, w, h, gamma, false);
    }

    /// <summary>
    /// The memory a texture reads inside a colour surface was written (by the CPU, a file read, a DMA copy...) after the
    /// surface was last rendered: that memory holds the newer content, not the surface. Memory is reused: WRC 4 renders
    /// its menus into a 960x544 surface and later loads the car's 1024x1024 livery texture into the same memory; sampling
    /// the stale surface instead drew every car black. Only pages inside the surface count (a texture that overhangs
    /// it may cover other data), and surfaces the renderer writes back are skipped (the writeback dirties their pages).
    /// </summary>
    bool OverwrittenSinceRendered(RenderTarget rt, uint addr, ulong bytes)
    {
        if (!DirtyPages.Enabled || rt.MemStamp == 0) return false;
        ulong rtEnd = rt.Address + (ulong)rt.Stride * (uint)Formats.ColorBytesPerPixel(rt.ColorFormat) * rt.Height;
        ulong start = Math.Max(addr, rt.Address), end = Math.Min(addr + bytes, rtEnd);
        // whole pages only, and not the first one: a surface's first and last pages are shared with whatever lies next
        // to it (Uncharted's 720x408 lighting buffer starts at 0x...EC0, under data the game writes every frame), and
        // a write near a page's end also marks the following page (DirtyPages). Such writes say nothing about the
        // surface's own memory; taking them as "overwritten" sampled the stale memory instead of the surface, now and
        // then, depending on when the game's write landed (dark title frames on the phone)
        const ulong page = 1UL << DirtyPages.PageShift;
        start = (start + page - 1 & ~(page - 1)) + page;
        end &= ~(page - 1);
        if (end <= start)
        {
            // a surface too small to own a whole page: the whole pages of the texture past the surface's last page (and
            // the one after it) tell instead; the renderer's writebacks of the surface never touch them. 99Vidas (Unity)
            // renders an 8x8 surface (written back) early on and later loads its partner logo, a 128x128 texture, into
            // the same memory; read from the stale surface, the logo was invisible
            start = (rtEnd + page - 1 & ~(page - 1)) + page;
            end = addr + bytes & ~(page - 1);
            if (end <= start) return false;
        }
        else if (rt.WrittenBack) return false; // the writeback dirties the surface's pages: CPU writes are not told apart
        SweepDirtyPages();
        if (!DirtyPages.WrittenSince((uint)start, (uint)(end - start), rt.MemStamp)) return false;

        if (DebugMode.Enabled && _dbgOverlapSeen.Add((addr, rt.Address, int.MinValue)))
            Dbg($"memory at 0x{addr:X8} (texture or transfer source) lies in render target 0x{rt.Address:X8} {rt.Width}x{rt.Height}, but was written after the target was last rendered: the memory is used");
        return true;
    }

    /// <summary>Bytes of a texture's base level (enough to tell which surface memory it reads).</summary>
    static ulong TextureBytes(GxmTextureDesc d, uint w, uint h)
    {
        if (d.TextureType == GxmTextureDesc.TypeLinearStrided) return (ulong)d.StrideBytes * h;
        if (d.TextureType == GxmTextureDesc.TypeLinear) w = Align(w, 8);
        int bits = Formats.Texture(GxmFormats.BaseOf(d.Format)).BitsPerPixel;
        return Math.Max(1, (ulong)w * h * (uint)Math.Max(bits, 1) / 8);
    }

    readonly List<RenderTarget> _rtCandidates = new();
    static readonly Comparison<RenderTarget> NewestSceneFirst = (a, b) => b.LastScene.CompareTo(a.LastScene);

    /// <summary>
    /// Colour targets whose memory contains <paramref name="addr"/>, the most recently rendered first. Memory reused by
    /// several surfaces (a 240x136 target inside a stale 960x544 one, Killzone's quarter-res passes) holds what was
    /// rendered last; in dictionary order a long-unused surface could be picked and its stale content sampled.
    /// </summary>
    List<RenderTarget> TargetsContaining(uint addr)
    {
        _rtCandidates.Clear();
        if (TexLookupOld)
        {
            foreach (var rt in _renderTargets.Values)
                if (addr >= rt.Address && addr < rt.Address + rt.Stride * (uint)Formats.ColorBytesPerPixel(rt.ColorFormat) * rt.Height)
                    _rtCandidates.Add(rt);
        }
        else
            foreach (ref readonly var r in RtRanges())
                if (addr >= r.Lo && addr < r.Hi) _rtCandidates.Add(r.Rt);
        if (_rtCandidates.Count > 1) _rtCandidates.Sort(NewestSceneFirst);
        return _rtCandidates;
    }

    /// <summary>A texture read from guest memory (cached, re-uploaded when its memory changes). <paramref name="asCube"/>:
    /// a 2D texture uploaded into all six faces of a cube image.</summary>
    (ImageView, ImageLayout) ResolveMemoryTexture(GxmTextureDesc d, uint addr, uint w, uint h, bool gamma, bool asCube)
    {
        var key = new TexKey(addr, d.Format, w, h, d.TextureType | (asCube ? 1u : 0u), d.MipCount | (d.LodMin << 8), d.TextureType == GxmTextureDesc.TypeLinearStrided ? d.StrideBytes : 0, d.PaletteAddress, gamma);
        if (_xferPendingDst.Count > 0) WaitPendingTransferInto(addr, Math.Max(w * h * 4, 1)); // VITASHARP_TRANSFER_DEFER
        _textures.TryGetValue(key, out var tex);
        _texSrcTex = tex;
        if (TraceTexAddr != 0 && addr - TraceTexAddr < 0x100000)
            Log.Info($"[trace-tex] t={System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 10000) / 10.0:F1}ms resolve 0x{addr:X8} {w}x{h} frame {FrameCount} cached={tex != null} checked={tex?.CheckedFrame == FrameCount} hash {System.IO.Hashing.XxHash32.HashToUInt32(GuestMemory.ReadSpan(addr, (int)Math.Min(w * 4, 4096u))):X8}");
        if (TracingThisFrame) TraceTexture(key, d, Formats.Texture(GxmFormats.BaseOf(d.Format)), w, h);
        if (tex != null && tex.CheckedFrame == FrameCount && tex.SpaceId == SpaceIdOf(addr)) { tex.LastUsedFrame = FrameCount; return (tex.View, ImageLayout.ShaderReadOnlyOptimal); }
        ProfMark(ProfPhase.TexMemLookup); // everything up to here: the unit's lookups

        var info = Formats.Texture(GxmFormats.BaseOf(d.Format));
        bool cube = asCube || d.TextureType is GxmTextureDesc.TypeCube or GxmTextureDesc.TypeCubeArbitrary;
        uint faces = cube ? 6u : 1u;
        uint mips = Math.Min(d.MipCount, (uint)BitOperations.Log2(Math.Max(w, h)) + 1);
        // as a cube, every face reads the same memory
        var layout = asCube ? ComputeLayout(d, info, w, h, mips, 1) with { FaceStride = 0 } : ComputeLayout(d, info, w, h, mips, faces);
        if (asCube) Log.WarnOnce($"tex2dcube{addr:X8}", $"[gpu] 2D texture 0x{addr:X8} {w}x{h} fmt 0x{d.Format:X8} is sampled as a cube map: its image is used for all six faces");
        uint total = layout.TotalBytes;
        // LOD min (texture streaming): levels below it are not resident, so the texture is uploaded from that level on
        uint lodMin = Math.Min(d.LodMin, mips - 1);
        uint baseW = Math.Max(w >> (int)lodMin, 1), baseH = Math.Max(h >> (int)lodMin, 1);
        uint residentOffset = layout.Levels[(int)lodMin].Offset;
        if (total == 0 || total > 64u << 20)
        {
            Log.WarnOnce($"texsize{d.Format:X}{w}{h}", $"[gpu] texture 0x{addr:X8} {w}x{h} fmt 0x{d.Format:X8} type 0x{d.TextureType:X8}: bad size {total}");
            return (default, default);
        }

        // only memory that may have been written since the texture was last validated is hashed again
        bool paletted = info.Convert is TexConvert.Palette4 or TexConvert.Palette8 && d.PaletteAddress != 0;
        uint paletteBytes = info.Convert == TexConvert.Palette4 ? 64u : 1024u;
        SweepDirtyPages();
        _texChecks++;
        int spaceId = SpaceIdOf(addr);
        bool written = tex == null || tex.ForceCheck || tex.SpaceId != spaceId
            || DirtyPages.WrittenSince(addr + residentOffset, total - residentOffset, tex.Stamp)
            || (paletted && DirtyPages.WrittenSince(d.PaletteAddress, paletteBytes, tex.Stamp));
        ProfMark(ProfPhase.TexMemCheck); // the layout and the dirty-page check
        if (!written && !DirtyPages.Verify)
        {
            _texSkipped++;
            tex!.CheckedFrame = FrameCount;
            tex.LastUsedFrame = FrameCount;
            return (tex.View, ImageLayout.ShaderReadOnlyOptimal);
        }
        long h0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var src = GuestMemory.ReadSpan(addr + residentOffset, (int)(total - residentOffset));
        ulong hash = System.IO.Hashing.XxHash3.HashToUInt64(src);
        if (paletted)
            hash ^= System.IO.Hashing.XxHash3.HashToUInt64(GuestMemory.ReadSpan(d.PaletteAddress, (int)paletteBytes)) * 31;
        _texHashTicks += System.Diagnostics.Stopwatch.GetTimestamp() - h0;
        _texHashed++;
        _texHashedBytes += src.Length;
        if (tex != null && !written && tex.Hash != hash && _texMissed++ < 50)
            Log.Warn($"[gpu] texdirty verify: texture 0x{addr:X8} {w}x{h} fmt 0x{d.Format:X8} changed without a dirty page (frame {FrameCount})");
        if (tex != null && tex.Hash == hash)
        {
            tex.CheckedFrame = FrameCount;
            tex.LastUsedFrame = FrameCount;
            tex.Stamp = DirtyPages.Epoch;
            tex.ForceCheck = false;
            tex.SpaceId = spaceId;
            return (tex.View, ImageLayout.ShaderReadOnlyOptimal);
        }

        // GPUs without BC support (most Android devices): transcoded to ASTC 4x4 where the GPU samples ASTC
        // (AstcTranscoder), else decoded on the CPU into RGBA8; PVRTC (always decoded on the CPU) likewise on such GPUs
        // (desktop GPUs keep their RGBA8 PVRTC, as before)
        var baseFormat = GxmFormats.BaseOf(d.Format);
        AstcChannels? astc = null;
        if (!_core.TextureCompressionBC && ((info.Compressed && info.Convert == TexConvert.None) || info.Convert == TexConvert.Pvrtc))
        {
            astc = AstcUsable ? AstcTranscoder.ChannelsFor(baseFormat, gamma) : null;
            if (astc != null) info = info with { Format = AstcTranscoder.FormatFor(gamma), Convert = info.Convert == TexConvert.Pvrtc ? TexConvert.AstcFromPvrtc : TexConvert.AstcFromBc };
            else if (info.Convert == TexConvert.None) info = info with { Format = BcSoftware.FormatFor(baseFormat), Convert = TexConvert.BcSoftware };
        }
        var format = info.Format;
        // texture gamma: the colour channels are sRGB-encoded and must be linearised when sampled
        if (gamma && Formats.Srgb(format) is var srgb && srgb != format && _core.SupportsSampled(srgb)) format = srgb;
        Log.WarnOnce($"texfmtinfo{d.Format:X}{d.TextureType:X}", $"[gpu] texture format 0x{d.Format:X8} ({GxmFormats.BaseOf(d.Format)}) type 0x{d.TextureType:X8} first seen {w}x{h} mips {mips}");
        if (!_core.SupportsSampled(format)) { Log.WarnOnce($"texfmt{format}", $"Texture format {format} not supported for sampling"); return (default, default); }

        var texSwizzle = Formats.TextureSwizzle(d.Format);
        // BC4/BC5 as ASTC luminance(+alpha): the channels come from L (and A)
        if (astc is { } ch) texSwizzle = AstcTranscoder.Compose(texSwizzle, ch);
        (Image, ImageView, ImageMemoryPool.Allocation) NewTextureImage() =>
            CreateImage(MemCat.Texture, baseW, baseH, format, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit,
                mips - lodMin, faces, cube: cube, swizzle: texSwizzle);
        // a decoder output read with the size of the picture before the newest: the GPU would have read that picture,
        // before the CPU decoded the next one over it (Gpu/DecoderOutputs.cs). Checked again on its next use.
        var earlier = VitaSharp.Core.Gpu.DecoderOutputs.EarlierFor(addr, w, h);
        // contents this texture (or another one with the same key layout) had before: take that image back, no upload
        bool fromCache = false;
        if (ContentCacheOn && earlier == null && hash != 0)
        {
            ulong oldHash = tex?.Hash ?? 0; // what the current image holds (a forced re-check does not change that)
            if (TakeCachedContent(key, hash) is { } cached)
            {
                if (tex == null)
                {
                    EvictOverlappingTextures(addr, total);
                    tex = new Texture();
                    _textures[key] = tex;
                    _texSrcTex = tex;
                    _fNewTextures++;
                }
                else KeepContent(key, oldHash, tex.Image, tex.View, tex.Mem, total);
                (tex.Image, tex.View, tex.Mem) = cached;
                tex.Changes++;
                fromCache = true;
                BumpTexEpoch(0); // the texture's view changed
            }
            else if (tex != null && tex.Changes > 0 && oldHash != 0 && total <= ContentCacheMaxTexture)
            {
                // a texture whose contents keep changing: its current image is kept for that content, the new content
                // goes into a new image
                KeepContent(key, oldHash, tex.Image, tex.View, tex.Mem, total);
                (tex.Image, tex.View, tex.Mem) = NewTextureImage();
                tex.Changes++;
                BumpTexEpoch(0);
            }
            else if (tex != null) tex.Changes++;
        }
        if (tex == null)
        {
            // textures of memory that now holds this one are stale: drop those unused for a while (streaming games
            // reuse memory for textures of other sizes/formats, whose cache entries otherwise stayed forever)
            EvictOverlappingTextures(addr, total);
            tex = new Texture();
            (tex.Image, tex.View, tex.Mem) = NewTextureImage();
            _textures[key] = tex;
            _texSrcTex = tex;
            _fNewTextures++;
            if (DebugMode.Enabled) DbgTextureFormat(d, w, h);
        }
        tex.Hash = hash;
        tex.Stamp = DirtyPages.Epoch;
        tex.ForceCheck = false;
        tex.SpaceId = spaceId;
        _texUploads++;
        tex.CheckedFrame = FrameCount;
        tex.LastUsedFrame = FrameCount;
        tex.DataStart = addr;
        tex.DataSize = total;
        if (fromCache)
        {
            _texContentHits++;
            _texUploads--; // counted above, nothing was uploaded
            ProfMark(ProfPhase.TexMemHash);
            return (tex.View, ImageLayout.ShaderReadOnlyOptimal);
        }
        if (earlier != null)
        {
            tex.Hash = 0;
            tex.ForceCheck = true;
            if (TraceTexAddr != 0 && addr == TraceTexAddr) Log.Info($"[trace-tex] 0x{addr:X8} {w}x{h} read from decoder picture {earlier.Seq}");
        }
        try
        {
            using var source = earlier != null ? TextureDecoder.UseSource(addr, earlier.Data) : default;
            long u0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ProfMark(ProfPhase.TexMemHash); // hashing the changed memory, a new image
            UploadTexture(tex, d, info, layout, w, h, mips, faces, format, lodMin);
            ProfMark(ProfPhase.TexMemUpload);
            _texUploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - u0;
            if (_uploadedContents.Count > 65536) _uploadedContents.Clear();
            if (!_uploadedContents.Add(hash ^ (ulong)d.Format * 0x9E3779B97F4A7C15UL ^ ((ulong)w << 48) ^ ((ulong)h << 32))) _texUploadRepeats++;
        }
        catch (VkOutOfMemoryException)
        {
            // no upload buffer: the image keeps its old content, so the texture must be checked again
            tex.Hash = 0;
            tex.ForceCheck = true;
            tex.CheckedFrame = -1;
            throw;
        }
        if (_opt.InspectTextures) InspectUploadedTextureBudgeted(d, info, w, h, hash, addr + residentOffset, (int)(total - residentOffset), !paletted);
        else if (DebugMode.Enabled && _inspectBudget-- > 0) InspectUploadedTexture(d, info, w, h);
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
        var levelLayout = ComputeLayout(d, info, w, h, mips, 1);
        var lv = levelLayout.Levels[(int)level];
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
    /// <summary>Field comparison (ComponentMapping.Equals(object) boxes both values on every texture lookup).</summary>
    static bool SameSwizzle(in ComponentMapping a, in ComponentMapping b) => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

    static ComponentMapping RtSwizzle(RenderTarget rt, uint texFormat)
    {
        bool is8888 = rt.Format is Format.R8G8B8A8Unorm or Format.B8G8R8A8Unorm && (rt.ColorFormat & 0xF1800000) == 0;
        if (!is8888 || GxmFormats.BaseOf(texFormat) != TexBase.U8U8U8U8) return default;
        // the texture swizzle is defined on memory; RGBA/BGRA surfaces hold logical components, seen here in memory
        // order first (Gal Gunvolt renders its playfield into RGBA surfaces, samples them as RGBA textures and alpha
        // tests .w: read reversed, the alpha test discarded the whole background)
        var tex = Formats.TextureSwizzle(texFormat);
        return Formats.LogicalOrderSwizzle(rt.ColorFormat) != 0 ? Formats.Compose(Formats.SurfaceMemoryOrder(rt.ColorFormat), tex) : tex;
    }

    sealed class RtRegion
    {
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public long Version = -1;
        public long LastUsedFrame;
    }

    readonly Dictionary<(RenderTarget, uint, uint, uint, uint, uint, bool), RtRegion> _rtRegions = new();    static readonly bool RegionCopySkip = Environment.GetEnvironmentVariable(EnvVars.RegionCopySkip) == "1";
    /// <summary>Region copies whose source is the scene's own colour surface / another surface (PERF gpu).</summary>
    long _statRegionOwn, _statRegionOther;

    /// <summary>VITASHARP_REGION_COPY_REUSE=0 copies a region again after every change of its surface (A/B).</summary>
    static readonly bool RegionCopyReuse = Environment.GetEnvironmentVariable(EnvVars.RegionCopyReuse)?.Trim() != "0";
    long _statRegionReused;

    /// <summary>A draw into <paramref name="rt"/> was recorded (its <see cref="RenderTarget.Version"/> already bumped):
    /// logs the rectangle it can write, its region clip and viewport (<see cref="SetDrawArea"/>) grown by a margin for
    /// wide points and lines. A draw without such bounds is not logged, so no region copy outlives it.</summary>
    void LogDraw(RenderTarget rt)
    {
        if (_drawMinX <= 0 && _drawMinY <= 0 && _drawMaxX == int.MaxValue && _drawMaxY == int.MaxValue) return;
        const int margin = 4;
        rt.DrawLog[rt.DrawLogNext] = (rt.Version, _drawMinX - margin, _drawMinY - margin,
            _drawMaxX >= int.MaxValue - margin ? int.MaxValue : _drawMaxX + margin, _drawMaxY >= int.MaxValue - margin ? int.MaxValue : _drawMaxY + margin);
        rt.DrawLogNext = (rt.DrawLogNext + 1) % rt.DrawLog.Length;
    }

    /// <summary>A version bump that changes no pixel (a rendering begun with the colour loaded): logged with an empty
    /// rectangle, so a mid-scene break between two draws does not invalidate the region copies.</summary>
    static void LogUnchanged(RenderTarget rt)
    {
        rt.DrawLog[rt.DrawLogNext] = (rt.Version, 0, 0, -1, -1);
        rt.DrawLogNext = (rt.DrawLogNext + 1) % rt.DrawLog.Length;
    }

    /// <summary>Every change of <paramref name="rt"/> after version <paramref name="since"/> was a logged draw outside
    /// the rectangle (guest pixels). Any other change (clear, upload, resolve, programmable-blending pass, a draw
    /// without bounds) bumps the version without a log entry, so the counts differ and the region is copied again.</summary>
    static bool RegionUntouchedSince(RenderTarget rt, long since, uint x, uint y, uint w, uint h)
    {
        long changes = rt.Version - since;
        if (changes <= 0 || changes > rt.DrawLog.Length) return false;
        long x1 = (long)x + w - 1, y1 = (long)y + h - 1;
        int logged = 0;
        foreach (var e in rt.DrawLog)
        {
            if (e.V <= since || e.V > rt.Version) continue;
            if (e.X0 <= x1 && e.X1 >= x && e.Y0 <= y1 && e.Y1 >= y) return false;
            logged++;
        }
        return logged == changes;
    }
    /// <summary>True when <paramref name="rt"/> is the only colour target whose memory intersects [addr, addr + bytes).</summary>
    bool OnlyTargetIn(RenderTarget rt, uint addr, uint bytes)
    {
        ulong end = (ulong)addr + bytes;
        foreach (var other in _renderTargets.Values)
        {
            if (other == rt) continue;
            ulong oStart = other.Address, oEnd = oStart + (ulong)other.Stride * (uint)Formats.ColorBytesPerPixel(other.ColorFormat) * other.Height;
            if (oStart < end && addr < oEnd) return false;
        }
        return true;
    }

    /// <summary>Copies a sub-rectangle of a render target into its own image (textures that point inside a surface).
    /// The part of a texture that extends past the surface's right or bottom edge reads as transparent black.</summary>
    ImageView RenderTargetRegion(RenderTarget rt, uint x, uint y, uint w, uint h, uint texFormat, bool gamma)
    {
        var key = (rt, x, y, w, h, texFormat, gamma);
        if (!_rtRegions.TryGetValue(key, out var r))
        {
            r = new RtRegion();
            var viewFormat = rt.Format is Format.B8G8R8A8Unorm ? Format.R8G8B8A8Unorm : rt.Format;
            if (gamma) viewFormat = Formats.Srgb(viewFormat);
            // at the target's internal resolution (VulkanRenderer.ResScale.cs)
            (r.Image, r.View, r.Mem) = CreateImage(MemCat.Region, ScaleDim(w, rt.Scale), ScaleDim(h, rt.Scale), rt.Format, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit,
                mutable: true);
            _vk.DestroyImageView(_dev, r.View, null);
            r.View = CreateView(r.Image, viewFormat, ImageAspectFlags.ColorBit, 1, 1, false, RtSwizzle(rt, texFormat));
            _rtRegions[key] = r;
            BeginFrameIfNeeded();
            using var clearOutside = BeginOutside(r.View);
            Barrier(r.Image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
            var clear = new ClearColorValue(0f, 0f, 0f, 0f);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            _vk.CmdClearColorImage(Cmd, r.Image, ImageLayout.TransferDstOptimal, in clear, 1, in range);
            Barrier(r.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        }
        r.LastUsedFrame = FrameCount;
        if (r.Version == rt.Version) return r.View;
        // only draws elsewhere in the surface since the copy (an atlas of post-processing passes, each drawing into its
        // own part and reading another): the copy is still current
        if (r.Version >= 0 && RegionCopyReuse && RegionUntouchedSince(rt, r.Version, x, y, w, h)) { r.Version = rt.Version; _statRegionReused++; return r.View; }
        // VITASHARP_REGION_COPY_SKIP=1 (measurement only, wrong picture): an existing region is not refreshed, to bound what
        // the copies cost on a tiler
        if (RegionCopySkip && r.Version >= 0) return r.View;
        if (rt == _rt) _statRegionOwn++; else _statRegionOther++;
        r.Version = rt.Version;
        // another surface's region is copied before the open rendering; a region of the scene's own surface too when
        // the rendering has not drawn there, else it ends the rendering
        using var outside = BeginOutside(r.View, rt, readRect: (x, y, Math.Min(w, rt.Width - x), Math.Min(h, rt.Height - y)));
        rt.Dirty = true;
        SyncRenderTargetForRead(rt);
        Barrier(r.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        uint sx = x, sy = y, sw = w, sh = h;
        if (rt.Scale != 1f)
        {
            sx = Math.Min((uint)MathF.Round(x * rt.Scale), rt.ImgW); sy = Math.Min((uint)MathF.Round(y * rt.Scale), rt.ImgH);
            sw = ScaleDim(w, rt.Scale); sh = ScaleDim(h, rt.Scale);
        }
        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            SrcOffset = new Offset3D((int)sx, (int)sy, 0),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(Math.Min(sw, rt.ImgW - sx), Math.Min(sh, rt.ImgH - sy), 1),
        };
        DevStampOp();
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
        public long LastUsedFrame;
        public uint W, H;
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
        // a scaled target is reinterpreted at its internal resolution: its image goes into the mirror with packed
        // scaled rows and the texture reads the matching scaled rectangle (VulkanRenderer.ResScale.cs)
        var geo = ScaledReinterpretGeometry(rt, addr, w, h, texRow, (uint)rtBpp, texBpp);
        // above native the scaled rows are in the target's own buffer, else in the mirror
        var src = _mirror;
        if (geo is { Own: true } && ScaledRowsCopyable(rt) && ScaledRowsOf(rt) is { Handle: not 0 } own) src = own;
        else if (geo is { Own: true }) geo = null;
        if (geo is null ? !MirrorRenderTarget(rt) : !geo.Value.Own && !MirrorRenderTargetScaled(rt)) return null;
        uint aw = geo?.W ?? w, ah = geo?.H ?? h;

        var key = (addr, d.Format, w, h, texRow, gamma);
        if (_rtAliases.TryGetValue(key, out var stale) && (stale.W != aw || stale.H != ah))
        {
            _rtAliases.Remove(key);
            BumpTexEpoch(2);
            var (si, sv, sm) = (stale.Image, stale.View, stale.Mem);
            DestroyAfterGpu(() => { _vk.DestroyImageView(_dev, sv, null); _vk.DestroyImage(_dev, si, null); _imagePool.Free(sm); });
        }
        if (!_rtAliases.TryGetValue(key, out var a))
        {
            a = new RtAlias { W = aw, H = ah };
            (a.Image, a.View, a.Mem) = CreateImage(MemCat.Region, aw, ah, format, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit,
                swizzle: Formats.TextureSwizzle(d.Format));
            _rtAliases[key] = a;
            Log.WarnOnce($"rtalias-{addr:X8}-{d.Format:X8}", $"[gpu] texture 0x{addr:X8} {w}x{h} fmt 0x{d.Format:X8} reinterprets render target 0x{rt.Address:X8} {rt.Width}x{rt.Height} fmt 0x{rt.ColorFormat:X8}");
            BeginFrameIfNeeded();
            PauseRendering();
            Barrier(a.Image, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit, 0, AccessFlags.ShaderReadBit);
        }
        a.LastUsedFrame = FrameCount;
        if (a.Source == rt && a.Version == rt.Version) return a.View;
        a.Source = rt;
        a.Version = rt.Version;
        PauseRendering(rt);
        BufferBarrier(src, PipelineStageFlags.TransferBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.TransferWriteBit | AccessFlags.ShaderReadBit, AccessFlags.TransferReadBit);
        Barrier(a.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        var region = new BufferImageCopy
        {
            BufferOffset = geo?.Offset ?? addr - GuestMemory.CdramStart,
            BufferRowLength = geo?.RowTexels ?? texRow / texBpp,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(geo?.CopyW ?? w, geo?.CopyH ?? h, 1),
        };
        DevStampOp();
        _vk.CmdCopyBufferToImage(Cmd, src, a.Image, ImageLayout.TransferDstOptimal, 1, in region);
        Barrier(a.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        return a.View;
    }

    readonly record struct MipLayout(uint Offset, uint Width, uint Height, uint RowPitch, uint Bytes);
    /// <summary>Per-level layout stored inline (at most 16 levels): computed for every texture check, so no array per call.</summary>
    [System.Runtime.CompilerServices.InlineArray(16)]
    struct MipChain { MipLayout _level; }

    readonly record struct TexLayout(MipChain Levels, uint FaceStride, uint TotalBytes);

    static uint Align(uint v, uint a) => (v + a - 1) / a * a;

    static TexLayout ComputeLayout(GxmTextureDesc d, TexFormatInfo info, uint w, uint h, uint mips, uint faces)
    {
        var levels = new MipChain();
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
                // swizzled-arbitrary blocks are morton ordered in the power-of-two block grid (TextureDecoder): the
                // level reaches the morton index of its last block. Croixleur Sigma's 960x40 UBC3 banners span 61120
                // bytes, not 38400; the texture cache and GPU dumps covered only the first 38400 (the right third
                // of the banner was lost in replays)
                if (swizzled && !pow2 && info.Convert != TexConvert.Pvrtc)
                    bytes = Math.Max(bytes, (TextureLayout.Morton(bw - 1, bh - 1, BitOperations.RoundUpToPowerOf2(bw), BitOperations.RoundUpToPowerOf2(bh)) + 1) * (uint)info.BlockBytes);
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
            levels[(int)m] = new MipLayout(offset, mw, mh, rowPitch, bytes);
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
        uint faceStride = cube
            ? TextureLayout.CubeFaceStride(d.MipCountRaw, mips, w, h, info.BitsPerPixel, info.Compressed, (uint)info.BlockW, (uint)info.BlockH, (uint)info.BlockBytes, offset)
            : offset;
        return new TexLayout(levels, faceStride, faceStride * (faces - 1) + offset);
    }

    /// <summary>VITASHARP_TRACE_TEX=&lt;hex address&gt;: logs each upload of the texture at that address (timing studies).</summary>
    static readonly uint TraceTexAddr = Convert.ToUInt32(Environment.GetEnvironmentVariable(VitaSharp.Core.Util.EnvVars.TraceTex) is { Length: > 0 } tt ? tt : "0", 16);

    void UploadTexture(Texture tex, GxmTextureDesc d, TexFormatInfo info, TexLayout layout, uint w, uint h, uint mips, uint faces, Format format, uint baseLevel = 0)
    {
        using var ledger = FrameLedger.Time(FrameLedger.Cat.TextureUpload);
        if (TraceTexAddr != 0 && d.DataAddress == TraceTexAddr)
            Log.Info($"[trace-tex] t={System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 10000) / 10.0:F1}ms upload 0x{d.DataAddress:X8} {w}x{h} stride {d.StrideBytes} hash {System.IO.Hashing.XxHash32.HashToUInt32(GuestMemory.ReadSpan(d.DataAddress, (int)Math.Min(w * 4, 4096u))):X8}");
        // reads guest memory only: recorded before the open rendering unless a draw of it already samples the texture
        using var outside = BeginOutside(tex.View);
        Barrier(tex.Image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, PipelineStageFlags.TransferBit, AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);

        for (uint face = 0; face < faces; face++)
        {
            for (uint m = baseLevel; m < mips; m++)
            {
                var lv = layout.Levels[(int)m];
                uint baseAddr = d.DataAddress + face * layout.FaceStride + lv.Offset;
                using var linear = TextureDecoder.Decode(d, info, baseAddr, lv.Width, lv.Height, lv.RowPitch, out uint outW, out uint outH);
                var (buf, off) = UploadSpan(linear.Span, 16);
                var r = new BufferImageCopy
                {
                    BufferOffset = off,
                    BufferRowLength = info.Compressed && info.Convert == TexConvert.None ? Align(outW, (uint)info.BlockW) : IsAstc(info) ? Align(outW, 4) : outW,
                    BufferImageHeight = info.Compressed && info.Convert == TexConvert.None ? Align(outH, (uint)info.BlockH) : IsAstc(info) ? Align(outH, 4) : outH,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m - baseLevel, face, 1),
                    ImageExtent = new Extent3D(lv.Width, lv.Height, 1),
                };
                // every region may live in a different overflow buffer: submit one copy per region
                DevStampOp();
                _vk.CmdCopyBufferToImage(Cmd, buf, tex.Image, ImageLayout.TransferDstOptimal, 1, in r);
            }
        }
        Barrier(tex.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
    }

    void InvalidateTextures(uint addr, uint size)
    {
        foreach (var t in _textures.Values)
            if (t.DataStart < addr + size && addr < t.DataStart + t.DataSize) { t.CheckedFrame = -1; t.ForceCheck = true; }
        // a CPU transfer into a render target's memory: reload it
        foreach (var rt in _renderTargets.Values)
            if (rt.Address < addr + size && addr < rt.Address + rt.Stride * rt.Height * 4) UploadSurfaceFromGuest(rt);
    }

    void DestroyTextures()
    {
        FlushContentCache(now: true);
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
        foreach (var (img, view, _) in _fbCopies.Values) { _vk.DestroyImageView(_dev, view, null); _vk.DestroyImage(_dev, img, null); }
    }
}
