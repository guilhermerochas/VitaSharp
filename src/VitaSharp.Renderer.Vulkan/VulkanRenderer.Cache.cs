using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Per-title persistent caches, under &lt;ShaderCacheDir&gt;/titles/&lt;TITLEID&gt;/:
/// <list type="bullet">
/// <item>shaders.pack: (GXP program hash, shader hints, generator version) → translation metadata + SPIR-V, so a
/// known program skips GLSL generation and compilation entirely;</item>
/// <item>pipelines.list: every pipeline the title used (see <see cref="PipelineRecipe"/>: shader identities,
/// fixed-function state, guest vertex attributes/streams), in first-use order. Independent of the generator version.
/// On the next boot a background thread compiles them all before/while the game starts, so the first frames of a
/// scene do not hitch on pipeline compilation;</item>
/// <item>programs.pack: the GXP bytes and hints of every shader the title used, in first-use order
/// (<see cref="ShaderRecipeStore"/>), independent of the generator version. When shaders.pack is stale (generator
/// bump) or partial, the boot rebuild (<see cref="RebuildFromRecipes"/>) retranslates them on the shader workers;</item>
/// <item>vk_&lt;vendor&gt;_&lt;device&gt;.cache: the driver's VkPipelineCache blob (driver-side compiled code), only
/// reused on the same GPU and driver.</item>
/// </list>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Bump when the renderer changes what it puts in pipelines (vertex input layout, fixed-function state).</summary>
    /// <remarks>2: version 1 lists could hold vertex layouts built from shader-pack metadata that had lost the raw
    /// (register-format) attribute sizes, i.e. normalised/float fetches where the shader expects raw integers.
    /// 3: records carry the rasterization sample count (real MSAA).
    /// 4: <see cref="PipelineRecipe"/> records (guest vertex description instead of the resolved Vulkan layout), and
    /// the file is no longer keyed by the generator version. A version 3 list of the current generator is still
    /// prewarmed once (<see cref="ReadLegacyPipelineList"/>) and migrates as the game uses its pipelines.</remarks>
    const uint PipelineFormatVersion = 4;
    const uint LegacyPipelineFormatVersion = 3;
    /// <summary>Bump when the shader-pack record layout changes (2: vertex inputs carry the raw attribute size/count;
    /// 3: the flags carry the raw colour output component size).</summary>
    const uint ShaderPackFormatVersion = 3;
    const uint VkCacheMagic = 0x43505356; // "VSPC"
    const uint VkCacheFormatVersion = 1;
    const int VkCacheHeaderSize = 48;
    /// <summary>Save the driver cache after this many newly compiled pipelines (a killed process keeps most of them).</summary>
    const int SaveEveryPipelines = 96;

    readonly object _titleLock = new();
    string? _titleId;
    string? _vkCachePath;
    BlobPack? _shaderPack;
    BlobPack? _pipelineList;
    ShaderRecipeStore? _recipes;
    /// <summary>Records of a version 3 pipelines.list of the current generator (prewarmed once, then migrated).</summary>
    List<byte[]>? _legacyPipelines;
    Thread? _prewarmThread;
    volatile bool _stopPrewarm;
    readonly object _vkCacheLock = new();
    bool _vkCacheClosed;
    int _newPipelines, _saveQueued;

    // statistics (GPU thread unless noted)
    int _statShaderMemHits, _statShaderPackHits, _statShaderMisses, _statPrewarmUsed, _statGpuPipelines;
    long _statShaderTicks, _statGpuPipelineTicks, _statGpuPipelineMaxTicks;
    int _statPrewarmed, _statPrewarmModules; // prewarm thread

    // frames whose GPU-thread work included shader translation/compilation or pipeline compilation (hitches)
    const double HitchMs = 4;
    long _frameStallTicks, _statWorstFrameStallTicks, _statPipelineWaitTicks;
    int _statHitchFrames, _statLongHitchFrames;
    /// <summary>A stall this long costs a whole 60 Hz frame (a visible hitch rather than a late frame).</summary>
    const double LongHitchMs = 16.7;

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    void NoteShaderTime(long ticks)
    {
        _statShaderTicks += ticks;
        _frameStallTicks += ticks;
    }

    /// <summary>Called once per presented frame (GPU thread): counts frames stalled by shader/pipeline work.</summary>
    void EndFrameStallStats()
    {
        if (_frameStallTicks == 0) return;
        if (Ms(_frameStallTicks) >= HitchMs) _statHitchFrames++;
        if (Ms(_frameStallTicks) >= LongHitchMs) _statLongHitchFrames++;
        if (_frameStallTicks > _statWorstFrameStallTicks) _statWorstFrameStallTicks = _frameStallTicks;
        _frameStallTicks = 0;
    }

    // =================================================================== title setup

    public void BeginTitle(string titleId)
    {
        lock (_titleLock)
        {
            if (_titleId != null || _opt.ShaderCacheDir == null) return;
            _titleId = titleId;
            var safe = string.Concat(titleId.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
            var dir = Path.Combine(_opt.ShaderCacheDir, "titles", safe.Length > 0 ? safe : "unknown");
            try { Directory.CreateDirectory(dir); }
            catch (Exception e) { Log.Warn($"[shadercache] cannot create {dir}: {e.Message}"); return; }
            OpenAstcCache(dir);

            var props = _core.Properties;
            // drivers whose cache UUID does not identify the build (custom Adreno/Turnip drivers): one file per build
            string build = _core.Caps.Has(Core.Gpu.GpuQuirk.PipelineCachePerDriverBuild)
                ? $"_{System.IO.Hashing.XxHash32.HashToUInt32(System.Text.Encoding.UTF8.GetBytes(_core.DriverBuildId)):x8}" : "";
            _vkCachePath = Path.Combine(dir, $"vk_{props.VendorID:x4}_{props.DeviceID:x4}{build}.cache");
            string vkStatus = LoadVkPipelineCache(_vkCachePath, out long vkBytes);

            // debug shader overrides and draw traces need freshly generated GLSL: only the driver cache is used then
            if (ShaderGenerator.DebugOverridesActive || _opt.TraceDrawsFrame >= 0)
            {
                Log.Info($"[shadercache] {titleId}: shader/pipeline caches bypassed (debug shader overrides or draw trace active); driver cache {vkStatus}");
                // VITASHARP_DEBUG_NAN=draws: every pipeline is an instrumented variant, kept out of the driver cache file
                if (ShaderGenerator.NanDraws) _vkCachePath = null;
                return;
            }
            _shaderPack = BlobPack.Open(Path.Combine(dir, "shaders.pack"), ShaderPackFormatVersion << 16 | (uint)ShaderGenerator.CacheVersion);
            string listPath = Path.Combine(dir, "pipelines.list");
            _legacyPipelines = ReadLegacyPipelineList(listPath);
            _pipelineList = BlobPack.Open(listPath, PipelineFormatVersion << 16);
            _recipes = ShaderRecipeStore.Open(Path.Combine(dir, "programs.pack"));
            var recipes = _recipes?.Recipes(out _statRecipesCorrupted) ?? [];
            int missing = _shaderPack == null ? 0 : recipes.Count(r => !_shaderPack.Contains(ShaderPackKey(r.ProgramHash, r.HintKey)));
            int shaders = _shaderPack?.Count ?? 0, pipelines = _pipelineList?.Count ?? 0, legacy = _legacyPipelines?.Count ?? 0;
            string head = $"[shadercache] {titleId}: driver cache {vkStatus} ({vkBytes / 1024} KB), {shaders} shaders (generator v{ShaderGenerator.CacheVersion}), "
                + $"{recipes.Count} shader recipes and {pipelines} pipelines on record"
                + (legacy > 0 ? $" (+{legacy} in a version {LegacyPipelineFormatVersion} list)" : "")
                + (_statRecipesCorrupted > 0 ? $", {_statRecipesCorrupted} unreadable recipes skipped" : "");
            if (_shaderPack == null || _pipelineList == null)
            {
                Log.Info(head);
                return;
            }
            if (missing > 0 && RebuildEnabled)
            {
                // stale (generator bump) or partial shader pack: rebuild from the recipes. With background compilation
                // off it runs now, behind the boot (the guest has not started yet), instead of during gameplay
                bool sync = !_asyncShaders;
                Log.Info($"{head}; {missing} shaders missing from the shader pack: rebuilding " + (sync ? "before the game starts (background compilation is off)" : "in the background"));
                _rebuildActive = true;
                _prewarmThread = new Thread(() => RebuildFromRecipes(recipes, missing, sync)) { Name = "Shader rebuild", IsBackground = true };
                _prewarmThread.Start();
                if (sync) _prewarmThread.Join();
                return;
            }
            if (pipelines + legacy == 0)
            {
                Log.Info(head + " - first run, nothing to prewarm");
                return;
            }
            _prewarmThread = new Thread(() => Prewarm(head)) { Name = "Shader prewarm", IsBackground = true, Priority = ThreadPriority.BelowNormal };
            _prewarmThread.Start();
        }
    }

    /// <summary>The records of a version 3 pipelines.list written by the current generator (its resolved vertex layouts
    /// are still valid), or null. The file is then reset to the version 4 format by the caller.</summary>
    static List<byte[]>? ReadLegacyPipelineList(string path)
    {
        uint legacy = LegacyPipelineFormatVersion << 16 | (uint)ShaderGenerator.CacheVersion;
        if (BlobPack.PeekVersion(path) != legacy) return null;
        using var old = BlobPack.Open(path, legacy);
        if (old == null || old.IsReadOnly) return null; // (in use by another session: leave it alone)
        var list = new List<byte[]>();
        foreach (ulong k in old.Keys())
            if (old.TryRead(k) is { } rec) list.Add(rec);
        return list;
    }

    /// <summary>GPU thread, first use of a shader in this session: records its program and hints in programs.pack.</summary>
    void RecordShaderRecipe(GxpProgram program, ulong hintKey, Func<DrawCommand, uint, ShaderHints> hints, DrawCommand d, uint colorFormat)
    {
        var store = _recipes;
        if (store == null || store.IsReadOnly || store.Has(program.Hash, hintKey)) return;
        var h = hints(d, colorFormat);
        if (h.Key() != hintKey)
        {
            Log.WarnOnce("recipe-key", $"[shadercache] shader {program.Hash:X16}: hints do not reproduce the key, not recorded");
            return;
        }
        store.Add(program, hintKey, h);
    }

    // =================================================================== shader pack

    static ulong ShaderPackKey(ulong programHash, ulong hintKey)
    {
        // the per-sample framebuffer fetch choice changes the GLSL of programmable-blending shaders
        // and so does the Adreno dynamic-indexing lowering (driver quirk)
        ulong gen = (uint)ShaderGenerator.CacheVersion | (ShaderGenerator.PerSampleFramebufferFetch ? 0 : 1ul << 32) | (ShaderGenerator.LowerDynamicIndexing ? 1ul << 33 : 0);
        Span<ulong> k = stackalloc ulong[3] { programHash, hintKey, gen };
        return XxHash64.HashToUInt64(MemoryMarshal.AsBytes(k));
    }

    // record: flags u8 (vertex, uses frag colour, writes depth, attribute fallback, bits 4-5 raw colour output size:
    // 0 none, 1 = 8, 2 = 16, 3 = 32 bits, bits 6-7 TileWord1Use), input count u16,
    // inputs (reg u32, location i32, kind u16, components i32, location count i32, raw size i32, raw components i32),
    // then the SPIR-V. Every VertexInputInfo field must round-trip: BuildVertexInput derives the Vulkan vertex formats
    // from them, and a lost raw size turns a raw U8/U16 register-format fetch into a normalised/float one (garbage
    // positions and texture coordinates, only when the shader came from the pack)
    const int InputRecordSize = 26;

    void StorePackedShader(ulong programHash, ulong hintKey, GeneratedShader gen, byte[] spv)
    {
        var pack = _shaderPack;
        if (pack == null) return;
        int n = gen.VertexInputs.Count;
        var buf = new byte[3 + n * InputRecordSize + spv.Length];
        buf[0] = (byte)((gen.IsVertex ? 1 : 0) | (gen.UsesFragColor ? 2 : 0) | (gen.WritesDepth ? 4 : 0) | (gen.AttributeFallback ? 8 : 0)
            | (gen.RawColorBits switch { 8 => 1, 16 => 2, 32 => 3, _ => 0 }) << 4 | ((int)gen.TileWord1 & 3) << 6);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(1), (ushort)n);
        int o = 3;
        foreach (var vi in gen.VertexInputs)
        {
            var s = buf.AsSpan(o);
            BinaryPrimitives.WriteUInt32LittleEndian(s, vi.RegIndex);
            BinaryPrimitives.WriteInt32LittleEndian(s[4..], vi.Location);
            BinaryPrimitives.WriteUInt16LittleEndian(s[8..], vi.Kind);
            BinaryPrimitives.WriteInt32LittleEndian(s[10..], vi.Components);
            BinaryPrimitives.WriteInt32LittleEndian(s[14..], vi.LocationCount);
            BinaryPrimitives.WriteInt32LittleEndian(s[18..], vi.RawSize);
            BinaryPrimitives.WriteInt32LittleEndian(s[22..], vi.RawComponents);
            o += InputRecordSize;
        }
        spv.CopyTo(buf, o);
        pack.Add(ShaderPackKey(programHash, hintKey), buf);
    }

    /// <summary>Module + metadata from the shader pack, or null (not stored, corrupted, or no pack).</summary>
    ModuleEntry? LoadPackedShader(ulong programHash, ulong hintKey, bool vertex)
    {
        var data = _shaderPack?.TryRead(ShaderPackKey(programHash, hintKey));
        if (data == null || data.Length < 3) return null;
        byte flags = data[0];
        if (((flags & 1) != 0) != vertex) return null;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1));
        int spvOffset = 3 + n * InputRecordSize;
        int spvLength = data.Length - spvOffset;
        if (spvLength < 20 || (spvLength & 3) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(spvOffset)) != 0x07230203) return null;
        var gen = new GeneratedShader
        {
            Glsl = "",
            IsVertex = vertex,
            UsesFragColor = (flags & 2) != 0,
            WritesDepth = (flags & 4) != 0,
            AttributeFallback = (flags & 8) != 0,
            RawColorBits = ((flags >> 4) & 3) switch { 1 => 8, 2 => 16, 3 => 32, _ => 0 },
            TileWord1 = (TileWord1Use)((flags >> 6) & 3),
        };
        for (int i = 0; i < n; i++)
        {
            var s = data.AsSpan(3 + i * InputRecordSize);
            gen.VertexInputs.Add(new VertexInputInfo(BinaryPrimitives.ReadUInt32LittleEndian(s), BinaryPrimitives.ReadInt32LittleEndian(s[4..]),
                (char)BinaryPrimitives.ReadUInt16LittleEndian(s[8..]), BinaryPrimitives.ReadInt32LittleEndian(s[10..]), BinaryPrimitives.ReadInt32LittleEndian(s[14..]),
                BinaryPrimitives.ReadInt32LittleEndian(s[18..]), BinaryPrimitives.ReadInt32LittleEndian(s[22..])));
        }
        return new ModuleEntry(CreateShaderModule(data.AsSpan(spvOffset, spvLength)), gen);
    }

    // =================================================================== pipeline list

    /// <summary>GPU thread, first use of a pipeline in this session: records it (no-op when already on record).</summary>
    void RecordPipeline(in PipelineKey key, VsEntry vs, FsEntry fs, PrimitiveTopology topology)
    {
        var list = _pipelineList;
        // pipelines with the tile-word attachment are not recorded (the recipe has no field for it; rare, built on use)
        if (list == null || list.IsReadOnly || vs.Vp is not { } vp || fs.Gen == null || StaticEds || key.TileWord1 != 0) return;
        var rec = new PipelineRecipe(vs.ProgramHash, vs.HintKey, fs.ProgramHash, fs.HintKey, (int)key.ColorFormat, key.HasColor, key.Blend,
            key.ColorFunc, key.AlphaFunc, key.ColorSrc, key.ColorDst, key.AlphaSrc, key.AlphaDst, key.Mask,
            (byte)topology, (byte)key.Polygon, (byte)key.Samples, fs.Gen.UsesFragColor, (byte)fs.Gen.RawColorBits, vp.Attributes, vp.Streams).Encode();
        list.Add(PipelineRecipe.KeyOf(rec), rec);
    }

    /// <summary>Module of a recorded shader: in memory, being built (waited for / run here), or loaded from the pack;
    /// null when none of these has it.</summary>
    ModuleEntry? RecordedModule(ulong programHash, ulong hintKey, bool vertex)
    {
        var key = (programHash, hintKey);
        if (_modules.TryGetValue(key, out var e)) return e;
        if (_inflight.TryGetValue(key, out var job)) return job.RunOrWait(this, byGame: false);
        if (_modules.TryGetValue(key, out e)) return e;
        var loaded = LoadPackedShader(programHash, hintKey, vertex);
        if (loaded == null) return null;
        Interlocked.Increment(ref _statPrewarmModules);
        return PublishModule(key, loaded);
    }

    /// <summary>Builds a recorded pipeline into the shared table (background thread). The vertex layout is derived
    /// from the recorded guest attributes and the current vertex shader, exactly like the GPU thread derives it.</summary>
    SharedBuild BuildRecordedPipeline(PipelineRecipe r, out bool stale)
    {
        stale = false;
        if (StaticEds) return SharedBuild.Failed; // recipes carry no depth/stencil state (VulkanRenderer.StaticState.cs)
        var vsM = RecordedModule(r.VsHash, r.VsHint, true);
        var fsM = RecordedModule(r.FsHash, r.FsHint, false);
        if (vsM?.Gen == null || fsM?.Gen == null || vsM.Module.Handle == 0 || fsM.Module.Handle == 0 || !vsM.Gen.IsVertex || fsM.Gen.IsVertex)
            return SharedBuild.Failed;
        // the fixed-function state was resolved against these (blend off for framebuffer fetch, integer view for raw outputs)
        if (fsM.Gen.UsesFragColor != r.FsUsesFragColor || fsM.Gen.RawColorBits != r.FsRawColorBits) { stale = true; return SharedBuild.Failed; }
        var vs = new VsEntry { Module = vsM.Module, Gen = vsM.Gen, ProgramHash = r.VsHash, HintKey = r.VsHint };
        BuildVertexInput(r.Attributes, r.Streams, vsM.Gen, vs);
        var topology = (PrimitiveTopology)r.Topology;
        var key = new PipelineKey(vs.Module.Handle, fsM.Module.Handle, vs.LayoutHash, (Format)r.AttachFormat, r.HasColor, r.Blend,
            r.ColorFunc, r.AlphaFunc, r.ColorSrc, r.ColorDst, r.AlphaSrc, r.AlphaDst, r.Mask, TopologyClass(topology), (PolygonMode)r.Polygon,
            r.Samples == 0 ? SampleCountFlags.Count1Bit : (SampleCountFlags)r.Samples);
        return BuildSharedPipeline(key, vs, fsM.Module, topology);
    }

    // =================================================================== prewarm (shader pack up to date)

    /// <summary>
    /// Background thread: loads the modules and compiles the pipelines the title used last time. It only adds to the
    /// shared module/pipeline tables, so the GPU thread never waits for it: a draw that needs a pipeline the
    /// prewarm has not reached yet just compiles it itself (the loser of such a race destroys its copy).
    /// </summary>
    void Prewarm(string head)
    {
        var sw = Stopwatch.StartNew();
        int failed = 0, stale = 0;
        try
        {
            foreach (ulong recordKey in _pipelineList!.Keys())
            {
                if (_stopPrewarm || _deviceLost) break;
                var rec = _pipelineList.TryRead(recordKey);
                var r = rec == null ? null : PipelineRecipe.Decode(rec);
                if (r == null) { failed++; continue; }
                var res = BuildRecordedPipeline(r, out bool isStale);
                if (res == SharedBuild.Built) _statPrewarmed++;
                else if (isStale) stale++;
                else if (res == SharedBuild.Failed) failed++;
            }
            if (_legacyPipelines != null)
                foreach (var rec in _legacyPipelines)
                {
                    if (_stopPrewarm || _deviceLost) break;
                    if (!PrewarmLegacy(rec)) failed++;
                }
        }
        catch (Exception e) { Log.Warn($"[shadercache] prewarm aborted: {e.Message}"); }
        _legacyPipelines = null;
        Log.Info($"{head}; prewarmed {_statPrewarmed} pipelines ({_statPrewarmModules} shader modules) in {sw.ElapsedMilliseconds} ms"
            + (failed > 0 ? $", {failed} skipped" : "") + (stale > 0 ? $", {stale} stale" : "") + (_stopPrewarm ? " (stopped early)" : ""));
        if (!_stopPrewarm && _statPrewarmed > 0) ScheduleVkCacheSave();
    }

    // version 3 record: vs program u64, vs hints u64, fs program u64, fs hints u64, colour format i32, has colour, blend,
    // colour func, alpha func, colour src, colour dst, alpha src, alpha dst, mask, topology, polygon mode (u8 each),
    // binding count u16, attribute count u16, sample count u8, bindings (binding, stride, rate: u32), attributes
    // (location, binding, format, offset: u32)
    const int LegacyRecordHead = 32 + 4 + 11 + 4 + 1;

    /// <summary>One version 3 record (resolved Vulkan vertex layout): true when built or already there.</summary>
    bool PrewarmLegacy(byte[] rec)
    {
        if (rec.Length < LegacyRecordHead || StaticEds) return false;
        var r = rec.AsSpan();
        int nb = BinaryPrimitives.ReadUInt16LittleEndian(r[47..]), na = BinaryPrimitives.ReadUInt16LittleEndian(r[49..]);
        if (rec.Length != LegacyRecordHead + nb * 12 + na * 16) return false;
        var vs = RecordedModule(BinaryPrimitives.ReadUInt64LittleEndian(r), BinaryPrimitives.ReadUInt64LittleEndian(r[8..]), true);
        var fs = RecordedModule(BinaryPrimitives.ReadUInt64LittleEndian(r[16..]), BinaryPrimitives.ReadUInt64LittleEndian(r[24..]), false);
        if (vs?.Gen == null || fs?.Gen == null || vs.Module.Handle == 0 || fs.Module.Handle == 0) return false;
        var bindings = MemoryMarshal.Cast<byte, VertexInputBindingDescription>(r.Slice(LegacyRecordHead, nb * 12)).ToArray();
        var attrs = MemoryMarshal.Cast<byte, VertexInputAttributeDescription>(r.Slice(LegacyRecordHead + nb * 12, na * 16)).ToArray();
        var topology = (PrimitiveTopology)r[45];
        var key = new PipelineKey(vs.Module.Handle, fs.Module.Handle, LayoutHash(bindings, attrs), (Format)BinaryPrimitives.ReadInt32LittleEndian(r[32..]),
            r[36] != 0, r[37] != 0, r[38], r[39], r[40], r[41], r[42], r[43], r[44], TopologyClass(topology), (PolygonMode)r[46],
            r[51] == 0 ? SampleCountFlags.Count1Bit : (SampleCountFlags)r[51]);
        var entry = new VsEntry { Module = vs.Module, Gen = vs.Gen, Bindings = bindings, Attributes = attrs, LayoutHash = key.VertexLayout };
        var res = BuildSharedPipeline(key, entry, fs.Module, topology);
        if (res == SharedBuild.Built) _statPrewarmed++;
        return res != SharedBuild.Failed;
    }

    // =================================================================== rebuild (shader pack stale or partial)

    /// <summary>VITASHARP_SHADER_REBUILD=0 disables the boot rebuild (a stale shader pack then fills up during gameplay, as before).</summary>
    static readonly bool RebuildEnabled = Environment.GetEnvironmentVariable(EnvVars.ShaderRebuild) is not ("0" or "off" or "false");
    volatile bool _rebuildActive;
    /// <summary>Rebuild work for the shader workers (<see cref="ShaderJob"/> or <see cref="PipelineRecipe"/>), in
    /// recorded first-use order; served after the draw-driven queues.</summary>
    readonly ConcurrentQueue<object> _rebuildJobs = new();
    int _rebuildPending;
    readonly ManualResetEventSlim _rebuildDone = new();
    int _statRebuiltShaders, _statRebuiltPipelines, _statRebuildTakenByGame, _statRebuildFailed, _statRebuildStale, _statRecipesCorrupted;
    string? _rebuildSummary;

    /// <summary>
    /// Boot rebuild after a generator bump (or a partial shader pack): queues every recorded shader and pipeline to the
    /// shader workers in first-use order (each pipeline right after its shaders, then the shaders no pipeline
    /// referenced), waits for them and logs a summary. Gameplay never waits for the rebuild as a whole: a shader the game
    /// needs first is promoted by <see cref="Prefetch"/> or run by the GPU thread itself (the job sits in the
    /// in-flight table, so it is compiled once), and pipelines are claimed like prefetched ones.
    /// </summary>
    void RebuildFromRecipes(List<ShaderRecipe> recipes, int missing, bool sync)
    {
        var sw = Stopwatch.StartNew();
        int queuedShaders = 0, queuedPipelines = 0, noProgram = 0;
        try
        {
            if (!EnsureShaderWorkers()) return;
            var byKey = new Dictionary<(ulong, ulong), ShaderRecipe>();
            foreach (var r in recipes) byKey.TryAdd((r.ProgramHash, r.HintKey), r);
            var programs = new Dictionary<ulong, GxpProgram?>();
            var seen = new HashSet<(ulong, ulong)>();
            Volatile.Write(ref _rebuildPending, 1); // released after the last item is queued
            _rebuildDone.Reset();

            void QueueShader(ulong hash, ulong hintKey)
            {
                var key = (hash, hintKey);
                if (!seen.Add(key) || _modules.ContainsKey(key) || _inflight.ContainsKey(key)) return;
                if (!byKey.TryGetValue(key, out var r)) return; // no recipe: the pipeline loads it from the pack or is skipped
                if (!programs.TryGetValue(hash, out var prog))
                {
                    var bytes = _recipes!.ProgramBytes(hash);
                    try { prog = bytes == null ? null : new GxpProgram(bytes, 0); }
                    catch (Exception) { prog = null; } // (bytes verified against the hash, but stay defensive)
                    programs[hash] = prog;
                }
                if (prog == null || prog.IsVertex != r.Vertex) { noProgram++; return; }
                var job = new ShaderJob(prog, hintKey, r.Hints, rebuild: true);
                if (!_inflight.TryAdd(key, job)) return;
                queuedShaders++;
                EnqueueRebuild(job);
            }

            foreach (ulong recordKey in _pipelineList!.Keys())
            {
                if (_stopPrewarm || _stopShaderJobs || _deviceLost) break;
                var rec = _pipelineList.TryRead(recordKey);
                var p = rec == null ? null : PipelineRecipe.Decode(rec);
                if (p == null) { Interlocked.Increment(ref _statRebuildFailed); continue; }
                QueueShader(p.VsHash, p.VsHint);
                QueueShader(p.FsHash, p.FsHint);
                queuedPipelines++;
                EnqueueRebuild(p);
            }
            foreach (var r in recipes)
            {
                if (_stopPrewarm || _stopShaderJobs || _deviceLost) break;
                QueueShader(r.ProgramHash, r.HintKey);
            }
            if (Interlocked.Decrement(ref _rebuildPending) == 0) _rebuildDone.Set();
            while (!_rebuildDone.Wait(100))
                if (_stopPrewarm || _stopShaderJobs || _deviceLost) break;
        }
        catch (Exception e) { Log.Warn($"[shadercache] rebuild aborted: {e.Message}"); }
        finally { _rebuildActive = false; }
        bool stopped = !_rebuildDone.IsSet;
        _rebuildSummary = $"rebuilt {_statRebuiltShaders} shaders + {_statRebuiltPipelines} pipelines for generator v{ShaderGenerator.CacheVersion} in {sw.Elapsed.TotalSeconds:F1} s on {_shaderWorkers?.Length ?? 0} workers"
            + (sync ? " (before the game started)" : "")
            + $" ({queuedShaders} shaders and {queuedPipelines} pipelines queued, {missing} missing from the pack, {_statRebuildTakenByGame} reached by the game first"
            + (_statRebuildFailed + noProgram > 0 ? $", {_statRebuildFailed + noProgram} unusable" : "") + (_statRebuildStale > 0 ? $", {_statRebuildStale} stale" : "")
            + (stopped ? ", stopped early" : "") + ")";
        Log.Info("[shadercache] " + _rebuildSummary);
        if (!stopped) ScheduleVkCacheSave();
    }

    void EnqueueRebuild(object item)
    {
        Interlocked.Increment(ref _rebuildPending);
        _rebuildJobs.Enqueue(item);
        _workAvailable.Release();
    }

    /// <summary>Worker side of the rebuild queue.</summary>
    void RunRebuildItem(object item)
    {
        try
        {
            switch (item)
            {
                case ShaderJob sj:
                    if (_stopShaderJobs || _deviceLost) sj.Cancel(this);
                    else sj.TryRun(this); // (no-op when promoted and already run, or taken by the GPU thread)
                    break;
                case PipelineRecipe pr when !_stopShaderJobs && !_deviceLost:
                    var res = BuildRecordedPipeline(pr, out bool stale);
                    if (res == SharedBuild.Built) Interlocked.Increment(ref _statRebuiltPipelines);
                    else if (stale) Interlocked.Increment(ref _statRebuildStale);
                    else if (res == SharedBuild.Failed) Interlocked.Increment(ref _statRebuildFailed);
                    break;
            }
        }
        catch (Exception e) { Log.WarnOnce("rebuild-item", $"[shader worker] rebuild item failed: {e.Message}"); }
        finally
        {
            if (Interlocked.Decrement(ref _rebuildPending) == 0) _rebuildDone.Set();
        }
    }

    /// <summary>Shutdown: drops the queued rebuild work (shader jobs are cancelled, so nobody waits for them).</summary>
    void DrainRebuildQueue()
    {
        while (_rebuildJobs.TryDequeue(out var item))
        {
            if (item is ShaderJob sj) sj.Cancel(this);
            if (Interlocked.Decrement(ref _rebuildPending) == 0) _rebuildDone.Set();
        }
    }

    /// <summary>A draw needs a shader the rebuild has queued but not started: move it ahead of the rest.</summary>
    void PromoteRebuildJob(ulong programHash, ulong hintKey)
    {
        if (!_rebuildActive || !_inflight.TryGetValue((programHash, hintKey), out var job) || !job.TryPromote()) return;
        _shaderJobs.Enqueue(job);
        _workAvailable.Release();
    }

    void StopPrewarm()
    {
        _stopPrewarm = true;
        _prewarmThread?.Join();
        _prewarmThread = null;
    }

    // =================================================================== driver pipeline cache

    /// <summary>Merges the saved driver cache into <see cref="_vkPipelineCache"/> after checking that it was written
    /// by the same GPU and driver (Vulkan validates the header too, but some drivers misbehave on foreign blobs).</summary>
    string LoadVkPipelineCache(string path, out long bytes)
    {
        bytes = 0;
        byte[] file;
        try
        {
            if (!File.Exists(path)) return "empty";
            file = File.ReadAllBytes(path);
        }
        catch (Exception e) { return $"unreadable ({e.Message})"; }
        var props = _core.Properties;
        var f = file.AsSpan();
        if (f.Length < VkCacheHeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(f) != VkCacheMagic || BinaryPrimitives.ReadUInt32LittleEndian(f[4..]) != VkCacheFormatVersion)
            return "discarded (bad header)";
        if (BinaryPrimitives.ReadUInt32LittleEndian(f[8..]) != props.VendorID || BinaryPrimitives.ReadUInt32LittleEndian(f[12..]) != props.DeviceID)
            return "discarded (other GPU)";
        if (BinaryPrimitives.ReadUInt32LittleEndian(f[16..]) != props.DriverVersion || !f.Slice(20, 16).SequenceEqual(new ReadOnlySpan<byte>(props.PipelineCacheUuid, 16)))
            return "discarded (driver changed)";
        int len = BinaryPrimitives.ReadInt32LittleEndian(f[36..]);
        if (len <= 0 || len != f.Length - VkCacheHeaderSize || XxHash64.HashToUInt64(f[VkCacheHeaderSize..]) != BinaryPrimitives.ReadUInt64LittleEndian(f[40..]))
            return "discarded (corrupted)";
        var data = f[VkCacheHeaderSize..];
        // the driver's own header: length, version ONE, vendor, device, pipeline cache UUID
        if (data.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(data) < 32 || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != props.VendorID || BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != props.DeviceID
            || !data.Slice(16, 16).SequenceEqual(new ReadOnlySpan<byte>(props.PipelineCacheUuid, 16)))
            return "discarded (driver header mismatch)";
        fixed (byte* p = data)
        {
            var ci = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo, InitialDataSize = (nuint)data.Length, PInitialData = p };
            if (_vk.CreatePipelineCache(_dev, in ci, null, out var loaded) != Result.Success) return "rejected by the driver";
            // the title starts before its first GPU command: nothing else uses the cache yet
            var r = _vk.MergePipelineCaches(_dev, _vkPipelineCache, 1, in loaded);
            _vk.DestroyPipelineCache(_dev, loaded, null);
            if (r != Result.Success) return $"merge failed ({r})";
        }
        bytes = data.Length;
        return "loaded";
    }

    void NotePipelineCreated()
    {
        if (Interlocked.Increment(ref _newPipelines) >= SaveEveryPipelines) ScheduleVkCacheSave();
    }

    void ScheduleVkCacheSave()
    {
        if (_vkCachePath == null || Interlocked.CompareExchange(ref _saveQueued, 1, 0) != 0) return;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { SaveVkPipelineCache(); }
            finally { Volatile.Write(ref _saveQueued, 0); }
        }, null);
    }

    /// <summary>Writes the driver cache atomically (temp file + rename). Thread-safe; a no-op once closed.</summary>
    byte[] _vkCacheSaveBuf = [];

    void SaveVkPipelineCache()
    {
        lock (_vkCacheLock)
        {
            if (_vkCacheClosed || _vkCachePath == null) return;
            Interlocked.Exchange(ref _newPipelines, 0);
            try
            {
                // the prewarm thread may add pipelines between the size query and the copy: retry on VK_INCOMPLETE
                // (a truncated blob is not guaranteed to be a valid cache)
                byte[]? file = null;
                nuint size = 0;
                for (int attempt = 0; attempt < 4 && file == null; attempt++)
                {
                    size = 0;
                    if (_vk.GetPipelineCacheData(_dev, _vkPipelineCache, ref size, null) != Result.Success || size == 0 || size > int.MaxValue / 2) return;
                    size += size / 8; // headroom for concurrent additions
                    // reused between saves (guarded by _vkCacheLock): the cache grows to megabytes and is saved repeatedly
                    if (_vkCacheSaveBuf.Length < VkCacheHeaderSize + (int)size) _vkCacheSaveBuf = new byte[VkCacheHeaderSize + (int)size + (int)size / 4];
                    var buf = _vkCacheSaveBuf;
                    Result r;
                    fixed (byte* p = &buf[VkCacheHeaderSize]) r = _vk.GetPipelineCacheData(_dev, _vkPipelineCache, ref size, p);
                    if (r == Result.Success) file = buf;
                    else if (r != Result.Incomplete) return;
                }
                if (file == null) return;
                var props = _core.Properties;
                var f = file.AsSpan();
                BinaryPrimitives.WriteUInt32LittleEndian(f, VkCacheMagic);
                BinaryPrimitives.WriteUInt32LittleEndian(f[4..], VkCacheFormatVersion);
                BinaryPrimitives.WriteUInt32LittleEndian(f[8..], props.VendorID);
                BinaryPrimitives.WriteUInt32LittleEndian(f[12..], props.DeviceID);
                BinaryPrimitives.WriteUInt32LittleEndian(f[16..], props.DriverVersion);
                new ReadOnlySpan<byte>(props.PipelineCacheUuid, 16).CopyTo(f[20..]);
                var data = f.Slice(VkCacheHeaderSize, (int)size);
                BinaryPrimitives.WriteInt32LittleEndian(f[36..], data.Length);
                BinaryPrimitives.WriteUInt64LittleEndian(f[40..], XxHash64.HashToUInt64(data));
                string tmp = _vkCachePath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(f[..(VkCacheHeaderSize + data.Length)]);
                    fs.Flush(true);
                }
                File.Move(tmp, _vkCachePath, true);
            }
            catch (Exception e) { Log.WarnOnce("vkcache-save", $"[shadercache] saving the driver pipeline cache failed: {e.Message}"); }
        }
    }

    /// <summary>Title exit: stop the prewarm, persist the driver cache, close the packs and log the session summary.</summary>
    void CloseTitleCaches()
    {
        StopPrewarm();
        SaveVkPipelineCache();
        lock (_vkCacheLock) _vkCacheClosed = true;
        _shaderPack?.Dispose();
        _pipelineList?.Dispose();
        _recipes?.Dispose();
        if (_titleId != null)
            Log.Info($"[shadercache] session {_titleId}: shaders {_statShaderPackHits} from cache, {_statShaderMisses} translated+compiled, {_statShaderMemHits} prewarmed/shared"
                + $" ({Ms(_statShaderTicks):F0} ms on the GPU thread); pipelines {_statPrewarmed} prewarmed ({_statPrewarmUsed} used),"
                + $" {_statGpuPipelines} compiled on the GPU thread ({Ms(_statGpuPipelineTicks):F0} ms, worst {Ms(_statGpuPipelineMaxTicks):F1} ms);"
                + $" frames stalled >= {HitchMs} ms by shader/pipeline work: {_statHitchFrames}, >= {LongHitchMs} ms: {_statLongHitchFrames} (worst {Ms(_statWorstFrameStallTicks):F1} ms);"
                + $" shaderc ({_compiler.OptLevel}): {ShaderCompiler.Compiled} compiled in {Ms(ShaderCompiler.CompileTicks):F0} ms, {ShaderCompiler.DiskHits} .spv disk hits; prefetch: {_statAsyncQueued} shaders queued to {(_shaderWorkers?.Length ?? 0)} workers, {_statAsyncWaited} waited for by the GPU thread; {_statPipelinesPrefetched} pipelines prefetched, {_statPipelineWaits} waited for ({Ms(_statPipelineWaitTicks):F0} ms)"
                + $"; rebuild: {_rebuildSummary ?? (_rebuildActive ? "still running" : "not needed")}");
        if (_titleId != null) Log.Info(StallReport());
        _stalls.Clear();
    }
}
