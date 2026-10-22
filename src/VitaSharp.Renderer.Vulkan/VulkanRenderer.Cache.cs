using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Per-title persistent caches, under &lt;ShaderCacheDir&gt;/titles/&lt;TITLEID&gt;/:
/// <list type="bullet">
/// <item>shaders.pack: (GXP program hash, shader hints, generator version) → translation metadata + SPIR-V, so a
/// known program skips GLSL generation and compilation entirely;</item>
/// <item>pipelines.list: every pipeline the title created (shaders + fixed-function state), in first-use order.
/// On the next boot a background thread compiles them all before/while the game starts, so the first frames of a
/// scene do not hitch on pipeline compilation;</item>
/// <item>vk_&lt;vendor&gt;_&lt;device&gt;.cache: the driver's VkPipelineCache blob (driver-side compiled code), only
/// reused on the same GPU and driver.</item>
/// </list>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>Bump when the renderer changes what it puts in pipelines (vertex input layout, fixed-function state).</summary>
    /// <remarks>2: version 1 lists could hold vertex layouts built from shader-pack metadata that had lost the raw
    /// (register-format) attribute sizes, i.e. normalised/float fetches where the shader expects raw integers.
    /// 3: records carry the rasterization sample count (real MSAA).</remarks>
    const uint PipelineFormatVersion = 3;
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
    long _frameStallTicks, _statWorstFrameStallTicks;
    int _statHitchFrames;

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

            var props = _core.Properties;
            _vkCachePath = Path.Combine(dir, $"vk_{props.VendorID:x4}_{props.DeviceID:x4}.cache");
            string vkStatus = LoadVkPipelineCache(_vkCachePath, out long vkBytes);

            // debug shader overrides and draw traces need freshly generated GLSL: only the driver cache is used then
            if (ShaderGenerator.DebugOverridesActive || _opt.TraceDrawsFrame >= 0)
            {
                Log.Info($"[shadercache] {titleId}: shader/pipeline caches bypassed (debug shader overrides or draw trace active); driver cache {vkStatus}");
                return;
            }
            _shaderPack = BlobPack.Open(Path.Combine(dir, "shaders.pack"), ShaderPackFormatVersion << 16 | (uint)ShaderGenerator.GeneratorVersion);
            _pipelineList = BlobPack.Open(Path.Combine(dir, "pipelines.list"), PipelineFormatVersion << 16 | (uint)ShaderGenerator.GeneratorVersion);
            int shaders = _shaderPack?.Count ?? 0, pipelines = _pipelineList?.Count ?? 0;
            string head = $"[shadercache] {titleId}: driver cache {vkStatus} ({vkBytes / 1024} KB), {shaders} shaders and {pipelines} pipelines on record";
            if (_shaderPack == null || _pipelineList == null || pipelines == 0)
            {
                Log.Info(head + (pipelines == 0 ? " - first run, nothing to prewarm" : ""));
                return;
            }
            _prewarmThread = new Thread(() => Prewarm(head)) { Name = "Shader prewarm", IsBackground = true, Priority = ThreadPriority.BelowNormal };
            _prewarmThread.Start();
        }
    }

    // =================================================================== shader pack

    static ulong ShaderPackKey(ulong programHash, ulong hintKey)
    {
        // the per-sample framebuffer fetch choice changes the GLSL of programmable-blending shaders
        ulong gen = (ulong)ShaderGenerator.GeneratorVersion | (ShaderGenerator.PerSampleFramebufferFetch ? 0 : 1ul << 32);
        Span<ulong> k = stackalloc ulong[3] { programHash, hintKey, gen };
        return XxHash64.HashToUInt64(MemoryMarshal.AsBytes(k));
    }

    // record: flags u8 (vertex, uses frag colour, writes depth, attribute fallback, bits 4-5 raw colour output size:
    // 0 none, 1 = 8, 2 = 16, 3 = 32 bits), input count u16,
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
            | (gen.RawColorBits switch { 8 => 1, 16 => 2, 32 => 3, _ => 0 }) << 4);
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

    // record: vs program u64, vs hints u64, fs program u64, fs hints u64, colour format i32, has colour, blend,
    // colour func, alpha func, colour src, colour dst, alpha src, alpha dst, mask, topology, polygon mode (u8 each),
    // binding count u16, attribute count u16, sample count u8, bindings (binding, stride, rate: u32), attributes (location, binding,
    // format, offset: u32)
    const int PipelineRecordHead = 32 + 4 + 11 + 4 + 1;

    void RecordPipeline(in PipelineKey key, VsEntry vs, FsEntry fs, PrimitiveTopology topology)
    {
        var list = _pipelineList;
        if (list == null) return;
        int nb = vs.Bindings.Length, na = vs.Attributes.Length;
        Span<byte> buf = stackalloc byte[PipelineRecordHead + nb * 12 + na * 16];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, vs.ProgramHash);
        BinaryPrimitives.WriteUInt64LittleEndian(buf[8..], vs.HintKey);
        BinaryPrimitives.WriteUInt64LittleEndian(buf[16..], fs.ProgramHash);
        BinaryPrimitives.WriteUInt64LittleEndian(buf[24..], fs.HintKey);
        BinaryPrimitives.WriteInt32LittleEndian(buf[32..], (int)key.ColorFormat);
        buf[36] = key.HasColor ? (byte)1 : (byte)0;
        buf[37] = key.Blend ? (byte)1 : (byte)0;
        buf[38] = key.ColorFunc; buf[39] = key.AlphaFunc; buf[40] = key.ColorSrc; buf[41] = key.ColorDst;
        buf[42] = key.AlphaSrc; buf[43] = key.AlphaDst; buf[44] = key.Mask;
        buf[45] = (byte)topology;
        buf[46] = (byte)key.Polygon;
        BinaryPrimitives.WriteUInt16LittleEndian(buf[47..], (ushort)nb);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[49..], (ushort)na);
        buf[51] = (byte)key.Samples;
        MemoryMarshal.AsBytes(vs.Bindings.AsSpan()).CopyTo(buf[PipelineRecordHead..]);
        MemoryMarshal.AsBytes(vs.Attributes.AsSpan()).CopyTo(buf[(PipelineRecordHead + nb * 12)..]);
        list.Add(XxHash64.HashToUInt64(buf), buf);
    }

    // =================================================================== prewarm

    /// <summary>
    /// Background thread: loads the modules and compiles the pipelines the title used last time. It only adds to the
    /// shared module/pipeline tables, so the GPU thread never waits for it: a draw that needs a pipeline the
    /// prewarm has not reached yet just compiles it itself (the loser of such a race destroys its copy).
    /// </summary>
    void Prewarm(string head)
    {
        var sw = Stopwatch.StartNew();
        int failed = 0;
        try
        {
            foreach (ulong recordKey in _pipelineList!.Keys())
            {
                if (_stopPrewarm) break;
                var rec = _pipelineList.TryRead(recordKey);
                if (rec == null || rec.Length < PipelineRecordHead) { failed++; continue; }
                var r = rec.AsSpan();
                int nb = BinaryPrimitives.ReadUInt16LittleEndian(r[47..]), na = BinaryPrimitives.ReadUInt16LittleEndian(r[49..]);
                if (rec.Length != PipelineRecordHead + nb * 12 + na * 16) { failed++; continue; }
                var vs = PrewarmModule(BinaryPrimitives.ReadUInt64LittleEndian(r), BinaryPrimitives.ReadUInt64LittleEndian(r[8..]), true);
                var fs = PrewarmModule(BinaryPrimitives.ReadUInt64LittleEndian(r[16..]), BinaryPrimitives.ReadUInt64LittleEndian(r[24..]), false);
                if (vs == null || fs == null) { failed++; continue; }
                var bindings = MemoryMarshal.Cast<byte, VertexInputBindingDescription>(r.Slice(PipelineRecordHead, nb * 12)).ToArray();
                var attrs = MemoryMarshal.Cast<byte, VertexInputAttributeDescription>(r.Slice(PipelineRecordHead + nb * 12, na * 16)).ToArray();
                var topology = (PrimitiveTopology)r[45];
                var key = new PipelineKey(vs.Module.Handle, fs.Module.Handle, LayoutHash(bindings, attrs), (Format)BinaryPrimitives.ReadInt32LittleEndian(r[32..]),
                    r[36] != 0, r[37] != 0, r[38], r[39], r[40], r[41], r[42], r[43], r[44], TopologyClass(topology), (PolygonMode)r[46],
                    r[51] == 0 ? SampleCountFlags.Count1Bit : (SampleCountFlags)r[51]);
                if (_sharedPipelines.ContainsKey(key)) continue;
                var p = CreatePipeline(key, vs.Module, fs.Module, bindings, attrs, topology);
                if (p.Handle == 0) { failed++; continue; }
                if (_sharedPipelines.TryAdd(key, p)) { _statPrewarmed++; NotePipelineCreated(); }
                else _vk.DestroyPipeline(_dev, p, null);
            }
        }
        catch (Exception e) { Log.Warn($"[shadercache] prewarm aborted: {e.Message}"); }
        Log.Info($"{head}; prewarmed {_statPrewarmed} pipelines ({_statPrewarmModules} shader modules) in {sw.ElapsedMilliseconds} ms"
            + (failed > 0 ? $", {failed} skipped" : "") + (_stopPrewarm ? " (stopped early)" : ""));
        if (!_stopPrewarm && _statPrewarmed > 0) ScheduleVkCacheSave();
    }

    ModuleEntry? PrewarmModule(ulong programHash, ulong hintKey, bool vertex)
    {
        var key = (programHash, hintKey);
        if (_modules.TryGetValue(key, out var e)) return e.Module.Handle != 0 && e.Gen != null ? e : null;
        var loaded = LoadPackedShader(programHash, hintKey, vertex);
        if (loaded == null) return null;
        Interlocked.Increment(ref _statPrewarmModules);
        e = PublishModule(key, loaded);
        return e.Module.Handle != 0 && e.Gen != null ? e : null;
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
        if (_titleId != null)
            Log.Info($"[shadercache] session {_titleId}: shaders {_statShaderPackHits} from cache, {_statShaderMisses} translated+compiled, {_statShaderMemHits} prewarmed/shared"
                + $" ({Ms(_statShaderTicks):F0} ms on the GPU thread); pipelines {_statPrewarmed} prewarmed ({_statPrewarmUsed} used),"
                + $" {_statGpuPipelines} compiled on the GPU thread ({Ms(_statGpuPipelineTicks):F0} ms, worst {Ms(_statGpuPipelineMaxTicks):F1} ms);"
                + $" frames stalled >= {HitchMs} ms by shader/pipeline work: {_statHitchFrames} (worst {Ms(_statWorstFrameStallTicks):F1} ms)");
    }
}
