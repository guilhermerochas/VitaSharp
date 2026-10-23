using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>Result of a runtime diagnostic capture (renderer part).</summary>
public sealed class DiagnosticCaptureResult
{
    public string Dir = "";
    /// <summary>Present number at which the trace was armed (the traced frame is the one after it).</summary>
    public long Present;
    public int Draws, Targets, Textures;
    public bool Screenshot;
    public uint FrameWidth, FrameHeight;
    public string? Error;
    public double Milliseconds;
}

/// <summary>
/// Runtime diagnostics: the F10 capture (draw trace of one frame, render-target dumps, sampled textures, screenshot),
/// the rolling per-frame summary ring, and the debug-mode checks (anomaly detection on the presented image, first-use
/// logging). Nothing here runs per draw unless a capture is armed or debug mode is on. See docs/notes/debug-mode.md.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    // ================================================================ capture

    sealed class DiagRequest
    {
        public required string Dir;
        public required Action<DiagnosticCaptureResult> Done;
        public readonly StringBuilder Draws = new();
        public readonly HashSet<(ulong, ulong)> ShadersWritten = new();
        public int DrawCount;
        public long Present;
        public long T0;
    }

    DiagRequest? _diagRequest;
    DiagRequest? _diagActive;
    /// <summary>Draws issued while <c>_presentCount == _traceFrame</c> are traced; the targets are dumped at the present
    /// that makes <c>_presentCount == _traceDumpFrame</c>. --trace-draws=N sets both to N (historic behaviour: the targets
    /// of the frame before the traced draws); a capture traces one frame and dumps the targets at its end.</summary>
    long _traceFrame = -1, _traceDumpFrame = -1;
    string? _traceDir;

    void InitTrace()
    {
        _traceFrame = _traceDumpFrame = _opt.TraceDrawsFrame;
        _traceDir = _opt.TraceDrawsFrame >= 0 && _opt.ShaderCacheDir != null ? Path.Combine(_opt.ShaderCacheDir, "trace") : null;
    }

    /// <summary>
    /// Asks for a capture of the next full frame into <paramref name="dir"/> (any thread). <paramref name="done"/> runs on
    /// a thread-pool thread once the files are written. False when a capture is already pending.
    /// </summary>
    public bool RequestDiagnosticCapture(string dir, Action<DiagnosticCaptureResult> done) =>
        Interlocked.CompareExchange(ref _diagRequest, new DiagRequest { Dir = dir, Done = done }, null) == null;

    /// <summary>Withdraws a request the renderer has not started yet (no frame was presented since): true when withdrawn.</summary>
    public bool CancelDiagnosticCapture() => Interlocked.Exchange(ref _diagRequest, null) != null;

    /// <summary>Debug mode: an anomaly was confirmed on the presented image (kind, details). Raised on a thread-pool thread.</summary>
    public event Action<string, string>? AnomalyDetected;

    void TraceLine(string line)
    {
        if (_diagActive is { } capture) capture.Draws.AppendLine(line);
        else Log.Info(line);
    }

    void ArmCapture()
    {
        var req = Interlocked.Exchange(ref _diagRequest, null);
        if (req == null) return;
        try { Directory.CreateDirectory(req.Dir); }
        catch (Exception e) { req.Done(new DiagnosticCaptureResult { Dir = req.Dir, Error = e.Message }); return; }
        _diagActive = req;
        req.Present = _presentCount;
        req.T0 = Stopwatch.GetTimestamp();
        _traceFrame = _presentCount;
        _traceDumpFrame = _presentCount + 1;
        _traceDir = req.Dir;
        _traceTextureFrame = -1;
        req.Draws.AppendLine($"# VitaSharp draw trace: the frame between present #{_presentCount} and #{_presentCount + 1} (guest frame {DebugMode.Frame})");
        req.Draws.AppendLine("# [scene] begin = a GXM scene (render target, MSAA mode, depth); [draw] = one draw: vs/fs = GXP program hashes,");
        req.Draws.AppendLine("# rt = colour surface address:format, tN = textures (address:format:size:type...), msaa = how the scene is rendered");
        Log.Info($"[diag] capture armed at present #{_presentCount}: tracing the next frame into {req.Dir}");
    }

    void CompleteCapture(DiagRequest dc, RenderTarget src, DisplayFrame frame)
    {
        var res = new DiagnosticCaptureResult { Dir = dc.Dir, Present = dc.Present, FrameWidth = frame.Width, FrameHeight = frame.Height };
        try
        {
            string shot = Path.Combine(dc.Dir, "screenshot.png");
            res.Targets = DumpAllTargets(Path.Combine(dc.Dir, "targets"), (src, frame.Width, frame.Height, shot));
            res.Screenshot = File.Exists(shot);
            File.WriteAllText(Path.Combine(dc.Dir, "draws.txt"), dc.Draws.ToString());
            File.WriteAllText(Path.Combine(dc.Dir, "frames.txt"), RecentFrames(FrameRingSize));
            File.WriteAllText(Path.Combine(dc.Dir, "renderer.txt"),
                DescribeForDiagnostics() + "\n\n# state\n" + DebugState() + "\n\n# statistics so far\n" + (((IGpuBackend)this).Report() ?? "") + "\n");
        }
        catch (Exception e)
        {
            res.Error = e.ToString();
            Log.Warn($"[diag] capture failed: {e.Message}");
        }
        finally
        {
            res.Draws = dc.DrawCount;
            res.Textures = _tracedTextures.Count;
            res.Milliseconds = Stopwatch.GetElapsedTime(dc.T0).TotalMilliseconds;
            _diagActive = null;
            InitTrace(); // back to normal rendering (or to a --trace-draws frame still ahead)
            _tracedTextures.Clear();
            Log.Info($"[diag] frame traced: {res.Draws} draws, {res.Targets} render targets, {res.Textures} textures ({res.Milliseconds:F0} ms)");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { dc.Done(res); }
                catch (Exception e) { Log.Warn($"[diag] completion failed: {e}"); }
            });
        }
    }

    // ================================================================ rolling per-frame summaries

    struct FrameSummary
    {
        public long Present, GuestFrame;
        public int Draws, Scenes, NewShaders, NewPipelines, NewTargets, NewTextures;
        public float IntervalMs, TopCauseMs;
        public int TopCause;
        public uint Address, Width, Height;
    }

    const int FrameRingSize = 120;
    readonly FrameSummary[] _frameRing = new FrameSummary[FrameRingSize];
    int _frameRingCount;
    int _fScenes, _fNewShaders, _fNewPipelines, _fNewTargets, _fNewTextures;
    long _lastPresentTicks;
    readonly long[] _ledgerPrev = new long[(int)FrameLedger.Cat.Count];
    uint _lastFrameW, _lastFrameH;

    // causes that stall the GPU thread or the guest within a frame (background work and idle time excluded)
    static readonly FrameLedger.Cat[] HitchCauses =
    [
        FrameLedger.Cat.ShaderTranslate, FrameLedger.Cat.ShaderCompile, FrameLedger.Cat.ShaderPackLoad, FrameLedger.Cat.PipelineCreate,
        FrameLedger.Cat.TextureUpload, FrameLedger.Cat.FenceWait, FrameLedger.Cat.Writeback, FrameLedger.Cat.GuestGpuWait,
        FrameLedger.Cat.FileIo, FrameLedger.Cat.ShaderWait,
    ];

    /// <summary>At each present (always on: a few field writes and ten counter reads per frame).</summary>
    void RecordFrameSummary(DisplayFrame frame)
    {
        long now = Stopwatch.GetTimestamp();
        int top = -1;
        long topTicks = 0;
        foreach (var c in HitchCauses)
        {
            long t = FrameLedger.Total(c), d = t - _ledgerPrev[(int)c];
            _ledgerPrev[(int)c] = t;
            if (d > topTicks) { topTicks = d; top = (int)c; }
        }
        ref var s = ref _frameRing[_frameRingCount++ % FrameRingSize];
        s = new FrameSummary
        {
            Present = _presentCount + 1, GuestFrame = DebugMode.Frame, Draws = _drawSerial, Scenes = _fScenes,
            NewShaders = _fNewShaders, NewPipelines = _fNewPipelines, NewTargets = _fNewTargets, NewTextures = _fNewTextures,
            IntervalMs = _lastPresentTicks == 0 ? 0 : (float)((now - _lastPresentTicks) * 1000.0 / Stopwatch.Frequency),
            TopCause = top, TopCauseMs = (float)(topTicks * 1000.0 / Stopwatch.Frequency),
            Address = frame.Address, Width = frame.Width, Height = frame.Height,
        };
        _lastPresentTicks = now;
        _lastFrameW = frame.Width;
        _lastFrameH = frame.Height;
        _fScenes = _fNewShaders = _fNewPipelines = _fNewTargets = _fNewTextures = 0;
    }

    /// <summary>The last <paramref name="count"/> frames, oldest first (GPU thread, or any thread for a hang report:
    /// the values may then be torn, which is acceptable for a diagnostic).</summary>
    public string RecentFrames(int count = FrameRingSize)
    {
        var sb = new StringBuilder("# present guestFrame intervalMs draws scenes newShaders newPipelines newTargets newTextures topHitchCause(ms) framebuffer\n");
        int n = Math.Min(Math.Min(count, FrameRingSize), _frameRingCount);
        for (int i = _frameRingCount - n; i < _frameRingCount; i++)
        {
            var s = _frameRing[i % FrameRingSize];
            string cause = s.TopCause >= 0 && s.TopCauseMs >= 0.05f ? $"{FrameLedger.Names[s.TopCause]}({s.TopCauseMs:F1})" : "-";
            sb.Append($"p{s.Present} f{s.GuestFrame} {s.IntervalMs:F1}ms draws={s.Draws} scenes={s.Scenes} newSh={s.NewShaders} newPipe={s.NewPipelines} newRT={s.NewTargets} newTex={s.NewTextures} hitch={cause} fb=0x{s.Address:X8} {s.Width}x{s.Height}\n");
        }
        return sb.ToString();
    }

    // ================================================================ descriptions

    /// <summary>GPU, driver and renderer settings for info.txt / renderer.txt.</summary>
    public string DescribeForDiagnostics()
    {
        var p = _core.Properties;
        var sb = new StringBuilder();
        sb.AppendLine($"GPU: {_core.DeviceName} (vendor 0x{p.VendorID:X4}, device 0x{p.DeviceID:X4}, type {p.DeviceType})");
        sb.AppendLine($"Vulkan API: {p.ApiVersion >> 22}.{(p.ApiVersion >> 12) & 0x3FF}.{p.ApiVersion & 0xFFF}");
        sb.AppendLine($"Driver: {DriverDescription()}");
        sb.AppendLine($"MSAA: {MsaaSetting} (VITASHARP_MSAA), programmable blending in MSAA scenes: {(MsaaInputFetch ? "input attachment" : "copy")} (VITASHARP_MSAA_FETCH), sample-rate shading {_core.SampleRateShading}");
        sb.AppendLine($"Shaders: shaderc {_compiler.OptLevel} (setting {_opt.ShaderOptimization ?? "default"}), async shaders {_asyncShaders}, async pipelines {_asyncPipelines}, workers setting {_opt.ShaderWorkers}, generator v{ShaderGenerator.GeneratorVersion}, debug overrides {ShaderGenerator.DebugOverridesActive}");
        sb.AppendLine($"Shader cache: {_opt.ShaderCacheDir ?? "(none)"}, title {_titleId ?? "-"}, pack {(_shaderPack != null ? "in use" : "not used")}");
        sb.AppendLine($"VSync {_opt.VSync}, validation {_opt.Validation}, presented frame {_lastFrameW}x{_lastFrameH}, swapchain {(_swapchain.Handle != 0 ? "yes" : "no (headless)")}");
        return sb.ToString().TrimEnd();
    }

    string DriverDescription()
    {
        var p = _core.Properties;
        uint v = p.DriverVersion;
        // NVIDIA packs 10.8.8.6 bits; Intel on Windows 18.14; the others follow the Vulkan version encoding
        string ver = p.VendorID switch
        {
            0x10DE => $"{v >> 22}.{(v >> 14) & 0xFF}.{(v >> 6) & 0xFF}.{v & 0x3F}",
            0x8086 when OperatingSystem.IsWindows() => $"{v >> 14}.{v & 0x3FFF}",
            _ => $"{v >> 22}.{(v >> 12) & 0x3FF}.{v & 0xFFF}",
        };
        try
        {
            var drv = new PhysicalDeviceDriverProperties { SType = StructureType.PhysicalDeviceDriverProperties };
            var p2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &drv };
            _vk.GetPhysicalDeviceProperties2(_core.Physical, &p2);
            string name = Marshal.PtrToStringAnsi((nint)drv.DriverName) ?? "";
            string info = Marshal.PtrToStringAnsi((nint)drv.DriverInfo) ?? "";
            return $"{name} {info} ({drv.DriverID}), version {ver} (raw 0x{v:X8})".Trim();
        }
        catch { return $"version {ver} (raw 0x{v:X8})"; }
    }

    /// <summary>Renderer state for hang reports (read from any thread without locking: values may be slightly stale).</summary>
    public string DebugState()
    {
        var sb = new StringBuilder();
        sb.Append($"renderer: present #{_presentCount}, GPU frames {FrameCount}, frame slot {_frameIndex}, in scene {_inScene}, rt 0x{_rt?.Address ?? 0:X8}, draws this frame {_drawSerial}, device lost {_deviceLost}, shader jobs in flight {_inflight.Count}");
        for (int i = 0; i < _frames.Length; i++)
        {
            var f = _frames[i];
            sb.Append($"\n  frame slot {i}: recording {f.Recording}, submitted {f.Submitted} (seq {f.SubmitSeq}), retired {f.Retired}, writebacks {f.Writebacks.Count}, signals {f.Signals.Count}");
        }
        return sb.ToString();
    }

    bool _deviceLostReported;

    void ReportDeviceLost(string where)
    {
        if (_deviceLostReported) return;
        _deviceLostReported = true;
        Log.Error($"[gpu] VULKAN DEVICE LOST in {where} at present #{_presentCount} (guest frame {DebugMode.Frame})\n{DebugState()}\nlast frames:\n{RecentFrames(20)}");
        Log.Flush();
    }

    // ================================================================ debug mode: first-use logging

    readonly HashSet<string> _dbgSeen = new();
    readonly HashSet<(uint, uint, int)> _dbgOverlapSeen = new();

    void Dbg(string msg) => Log.Info($"[dbg] f{DebugMode.Frame} p{_presentCount} {msg}");

    string MsaaPath => _ms ? $"native {(int)_msSamples}x" : _ss ? "supersampled 2x" : "off";

    void DbgVertexShader(VsEntry e, DrawCommand d)
    {
        if (!_dbgSeen.Add($"vs{e.ProgramHash:X16}{e.HintKey:X16}")) return;
        Dbg($"new vertex shader {e.ProgramHash:X16} hint {e.HintKey:X16}: {d.VertexProgram.Attributes.Length} attributes, {d.VertexProgram.Streams.Length} streams, " +
            $"{(e.Failed ? "FAILED" : string.IsNullOrEmpty(e.Gen?.Glsl) ? "from the shader pack" : "translated")}{(e.Gen?.AttributeFallback == true ? ", attribute fallback" : "")}");
    }

    void DbgFragmentShader(FsEntry e, DrawCommand d, bool fetchInput)
    {
        if (!_dbgSeen.Add($"fs{e.ProgramHash:X16}{e.HintKey:X16}")) return;
        var fp = d.FragmentProgram;
        var prog = fp.Info.Program;
        Dbg($"new fragment shader {e.ProgramHash:X16} hint {e.HintKey:X16}: output fmt 0x{fp.OutputFormat:X} param {prog.OutputParamType}x{prog.OutputCompCount} start {prog.FragmentOutputStart}, " +
            $"program msaa mode {fp.MultisampleMode}, raw colour bits {e.Gen?.RawColorBits ?? 0}, framebuffer fetch {e.Gen?.UsesFragColor ?? false}{(fetchInput ? " (input attachment)" : "")}, " +
            $"writes depth {e.Gen?.WritesDepth ?? false}, textures 0x{fp.Info.TexturesUsed:X}, rt 0x{_rt?.Address ?? 0:X8} fmt 0x{_rt?.ColorFormat ?? 0:X8} ({_rt?.Format}), scene msaa {MsaaPath}, " +
            $"{(e.Failed ? "FAILED" : string.IsNullOrEmpty(e.Gen?.Glsl) ? "from the shader pack" : "translated")}");
    }

    void DbgPipeline(in PipelineKey k, VsEntry vs, FsEntry fs, PrimitiveTopology topology)
    {
        // GXP program hashes (the key's Vs/Fs are shader module handles, meaningless in a log)
        Dbg($"new pipeline vs {vs.ProgramHash:X16} fs {fs.ProgramHash:X16} {topology} colour {(k.HasColor ? k.ColorFormat.ToString() : "none")} samples {k.Samples} " +
            $"blend {(k.Blend ? $"{k.ColorFunc}/{k.ColorSrc}/{k.ColorDst} a {k.AlphaFunc}/{k.AlphaSrc}/{k.AlphaDst}" : "off")} mask 0x{k.Mask:X} polygon {k.Polygon} layout {k.VertexLayout:X16}");
    }

    void DbgRenderTarget(RenderTarget rt, Format format, List<KeyValuePair<(uint, uint, uint, uint), RenderTarget>> replaced)
    {
        var sb = new StringBuilder($"new render target 0x{rt.Address:X8} {rt.Width}x{rt.Height} stride {rt.Stride} fmt 0x{rt.ColorFormat:X8} -> {format}");
        foreach (var (_, old) in replaced) sb.Append($"; replaces 0x{old.Address:X8} {old.Width}x{old.Height} fmt 0x{old.ColorFormat:X8} ({old.Format})");
        uint bytes = rt.Stride * (uint)Formats.ColorBytesPerPixel(rt.ColorFormat) * rt.Height;
        foreach (var o in _renderTargets.Values)
        {
            if (o == rt) continue;
            uint ob = o.Stride * (uint)Formats.ColorBytesPerPixel(o.ColorFormat) * o.Height;
            if (o.Address < rt.Address + bytes && rt.Address < o.Address + ob)
                sb.Append($"; overlaps 0x{o.Address:X8} {o.Width}x{o.Height} fmt 0x{o.ColorFormat:X8}");
        }
        Dbg(sb.ToString());
    }

    void DbgScene(BeginSceneCommand b)
    {
        if (!_dbgSeen.Add($"sc{_rt?.Address ?? 0:X8}{_rt?.ColorFormat ?? 0:X8}{b.Width}x{b.Height}{b.Multisample}{b.Color?.Downscale}{b.Depth?.DepthData ?? 0:X8}{MsaaPath}")) return;
        Dbg($"new scene setup: rt 0x{_rt?.Address ?? 0:X8} {_rt?.Width}x{_rt?.Height} fmt 0x{_rt?.ColorFormat ?? 0:X8} ({_rt?.Format}) scene {b.Width}x{b.Height} gxm msaa {b.Multisample} downscale {b.Color?.Downscale} -> {MsaaPath}" +
            $", depth 0x{b.Depth?.DepthData ?? 0:X8} stencil 0x{b.Depth?.StencilData ?? 0:X8} fmt 0x{b.Depth?.Format ?? 0:X8}, gamma {b.Color?.GammaMode}");
    }

    void DbgTextureFormat(GxmTextureDesc d, uint w, uint h)
    {
        if (!_dbgSeen.Add($"tf{d.Format:X8}{d.TextureType:X8}")) return;
        Dbg($"new texture format 0x{d.Format:X8} ({Core.Gxm.GxmFormats.BaseOf(d.Format)}, swizzle bits 0x{d.Format & 0x7000:X}) type 0x{d.TextureType:X8} first seen at 0x{d.DataAddress:X8} {w}x{h} mips {d.MipCount} gamma {d.GammaMode}");
    }

    /// <summary>Several colour targets contain a sampled address: which ones (the first compatible one in this order is used).</summary>
    void DbgTextureOverlap(GxmTextureDesc d)
    {
        uint addr = d.DataAddress;
        var candidates = TargetsContaining(addr);
        int n = candidates.Count;
        if (n < 2 || !_dbgOverlapSeen.Add((addr, d.Format, n))) return;
        var sb = new StringBuilder();
        foreach (var rt in candidates)
            sb.Append($" [0x{rt.Address:X8} {rt.Width}x{rt.Height} fmt 0x{rt.ColorFormat:X8} lastScene {rt.LastScene}]");
        string set = sb.ToString();
        Dbg($"texture 0x{addr:X8} fmt 0x{d.Format:X8} {d.Width}x{d.Height} lies in {n} render targets, tried in this order:{set}");
    }

    void DbgDepthOverlap(uint addr, DepthTarget chosen, bool stencil)
    {
        int n = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            var sb = pass == 1 ? new StringBuilder() : null;
            foreach (var dt in _depthTargets.Values)
            {
                bool inDepth = dt.Address != 0 && addr >= dt.Address && addr < dt.Address + dt.StrideBytes * dt.Height;
                bool inStencil = dt.StencilAddress != 0 && addr >= dt.StencilAddress && addr < dt.StencilAddress + dt.StencilStrideBytes * dt.Height;
                if (!inDepth && !inStencil) continue;
                if (sb == null) n++;
                else sb.Append($" [0x{dt.Address:X8}/0x{dt.StencilAddress:X8} {dt.Width}x{dt.Height} lastUsed {dt.LastUsedFrame}]");
            }
            // the list is only built for a combination not logged yet
            if (sb == null && (n < 2 || !_dbgOverlapSeen.Add((chosen.Address, stencil ? 1u : 0u, -n)))) return;
            if (sb != null) Dbg($"address 0x{addr:X8} lies in {n} depth/stencil targets:{sb}; picked 0x{chosen.Address:X8} ({(stencil ? "stencil" : "depth")}, most recently used)");
        }
    }

    // ================================================================ debug mode: anomaly checks on the presented image

    const int SampleEvery = 30;
    int _inspectBudget;
    float _prevMean = -1;
    bool _prevBlack;
    ulong _prevHash;
    int _sameCount, _samples;
    readonly Dictionary<string, int> _streak = new();
    static readonly (string Kind, int From)? Inject = ParseInject(Environment.GetEnvironmentVariable("VITASHARP_DEBUG_INJECT"));
    int _injected;

    /// <summary>Test switch VITASHARP_DEBUG_INJECT=magenta|green@PRESENT: the anomaly check sees that colour over half of
    /// the image for a few samples from that present on (the rendered frame itself is untouched).</summary>
    static (string, int)? ParseInject(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Split('@');
        return (p[0].Trim().ToLowerInvariant(), p.Length > 1 && int.TryParse(p[1].TrimStart('f', 'p'), out var f) ? f : 0);
    }

    void DebugPresent(RenderTarget src, DisplayFrame frame)
    {
        // texture inspection (TEXTURE SUSPECT): one upload every 8 presents
        if (_presentCount % 8 == 0) _inspectBudget = 1;
        var f = F;
        if (_presentCount % SampleEvery != 0 || f.SamplePending) return;
        if (src.Format is not (Format.R8G8B8A8Unorm or Format.B8G8R8A8Unorm))
        {
            if (_dbgSeen.Add($"nosample{src.Format}")) Dbg($"anomaly checks skipped: presented surface is {src.Format}");
            return;
        }
        uint w = Math.Min(frame.Width, src.Width), h = Math.Min(frame.Height, src.Height);
        if (f.Sample is not { } sb || sb.W != w || sb.H != h)
        {
            if (f.Sample is { } old) f.Deferred.Add(() => _core.DestroyBuffer(old.Buf, old.Mem));
            var (b, m, p) = _core.CreateHostBuffer(w * h * 4, BufferUsageFlags.TransferDstBit);
            f.Sample = (b, m, p, w, h);
        }
        var region = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new Extent3D(w, h, 1) };
        _vk.CmdCopyImageToBuffer(Cmd, src.Image, ImageLayout.General, f.Sample!.Value.Buf, 1, in region);
        f.SamplePending = true;
        f.SampleBgra = src.Format == Format.B8G8R8A8Unorm;
        f.SampleIndex = _presentCount;
    }

    /// <summary>
    /// Cheap checks of the presented image (every 2nd pixel of every 2nd row), the same thresholds as the test
    /// harness's frame metrics plus pure green/blue areas: magenta (missing textures), saturated green/blue blocks,
    /// blown-out, flicker (mean brightness swing between samples), black and frozen output.
    /// </summary>
    void AnalyzeSample(Frame f)
    {
        f.SamplePending = false;
        if (f.Sample is not { } s) return;
        byte* px = (byte*)s.Ptr;
        int ri = f.SampleBgra ? 2 : 0, bi = f.SampleBgra ? 0 : 2;
        long sum = 0;
        int n = 0, white = 0, dark = 0, pink = 0, green = 0, blue = 0;
        ulong hash = 14695981039346656037;
        for (uint y = 0; y < s.H; y += 2)
            for (uint x = 0; x < s.W; x += 2)
            {
                byte* q = px + (y * s.W + x) * 4;
                int r = q[ri], g = q[1], b = q[bi];
                sum += (r * 54 + g * 183 + b * 19) >> 8;
                if (r > 235 && g > 235 && b > 235) white++;
                if (r < 10 && g < 10 && b < 10) dark++;
                if (r > 200 && b > 200 && g < 80) pink++;
                if (g > 180 && r < 60 && b < 60) green++;
                if (b > 180 && r < 60 && g < 60) blue++;
                hash = (hash ^ (ulong)((r >> 2) | (g >> 2) << 8 | (b >> 2) << 16)) * 1099511628211;
                n++;
            }
        if (n == 0) return;
        if (Inject is { } inj && f.SampleIndex >= inj.From && _injected < 4)
        {
            _injected++;
            if (inj.Kind == "green") green = Math.Max(green, n / 2); else pink = Math.Max(pink, n / 2);
            Log.Info($"[anomaly] test injection ({inj.Kind}) into the check of present #{f.SampleIndex}");
        }
        float mean = sum / (255f * n), fw = white / (float)n, fb = dark / (float)n, fm = pink / (float)n, fg = green / (float)n, fbl = blue / (float)n;
        bool black = fb > 0.97f;
        _samples++;
        if (_samples % 10 == 0)
            Log.Info($"[frame-metrics] p{f.SampleIndex} mean {mean:F3} white {fw:F3} black {fb:F3} magenta {fm:F3} green {fg:F3} blue {fbl:F3}");

        Check("magenta", fm > 0.05f, 2, $"{fm:P0} of the image is magenta (missing/garbage textures)");
        Check("saturated", fg + fbl > 0.08f, 2, $"{fg:P0} pure green and {fbl:P0} pure blue pixels (garbage render target / effect)");
        Check("blown-out", fw > 0.35f || mean > 0.85f, 2, $"{fw:P0} saturated white, mean brightness {mean:F2}");
        Check("flicker", _prevMean >= 0 && !black && !_prevBlack && MathF.Abs(mean - _prevMean) > 0.35f, 1, $"mean brightness {_prevMean:F2} -> {mean:F2} between checks {SampleEvery} frames apart");
        Check("black", black, 1, "more than 97% black pixels", capture: false);
        _prevMean = mean;
        _prevBlack = black;
        _sameCount = hash == _prevHash ? _sameCount + 1 : 0;
        _prevHash = hash;
        Check("frozen", _sameCount >= 20, 1, $"identical image for {_sameCount + 1} checks ({(_sameCount + 1) * SampleEvery} presents)", capture: false);

        void Check(string kind, bool hit, int need, string detail, bool capture = true)
        {
            int streak = hit ? _streak.GetValueOrDefault(kind) + 1 : 0;
            int before = _streak.GetValueOrDefault(kind);
            _streak[kind] = streak;
            if (!hit)
            {
                if (before >= need) Log.Info($"[anomaly] p{f.SampleIndex} f{DebugMode.Frame} {kind} cleared after {before} checks");
                return;
            }
            if (streak != need) return;
            Log.Info($"[anomaly] p{f.SampleIndex} f{DebugMode.Frame} {kind}: {detail}");
            if (capture && AnomalyDetected is { } handler)
                ThreadPool.QueueUserWorkItem(_ => { try { handler(kind, detail); } catch (Exception e) { Log.Warn($"[anomaly] handler failed: {e.Message}"); } });
        }
    }
}
