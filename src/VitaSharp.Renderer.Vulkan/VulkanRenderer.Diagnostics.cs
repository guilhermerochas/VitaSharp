using Silk.NET.Vulkan;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
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
    /// <summary>Frames written to replay/ (0 = ring off: debug mode was off, -1 = failed).</summary>
    public int ReplayFrames;
    /// <summary>Frames written to drawring/ (0 = ring off: debug mode was off, -1 = failed).</summary>
    public int DrawRingFrames;
}

/// <summary>
/// Runtime diagnostics: the F10 capture (draw trace of one frame, render-target dumps, sampled textures, screenshot),
/// the rolling per-frame summary ring, and the debug-mode checks (anomaly detection on the presented image, first-use
/// logging). Nothing here runs per draw unless a capture is armed or debug mode is on. See docs/notes/debug-mode.md.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    // ================================================================ capture

    private sealed class DiagRequest
    {
        public required string Dir;
        public required Action<DiagnosticCaptureResult> Done;
        public readonly StringBuilder Draws = new();
        public readonly HashSet<(ulong, ulong)> ShadersWritten = new();
        public int DrawCount;
        public long Present;
        public long T0;
        /// <summary>Presents to keep collecting replay frames before the capture is armed (auto captures: about 1 s
        /// after the spike, so both sides of it are saved), the present that triggered it (-1 = when it arrived).</summary>
        public int PostFrames;
        public long TriggerPresent = -1;
        public long ArmAt = -1;
        /// <summary>The replay ring written in the background (VulkanRenderer.Replay.cs); awaited before completion.</summary>
        public Task<int>? Replay;
        /// <summary>The draw ring written in the background (VulkanRenderer.DrawRing.cs); awaited before completion.</summary>
        public Task<int>? DrawRing;

        public bool ReadyToArm(long present)
        {
            if (ArmAt < 0)
            {
                if (TriggerPresent < 0)
                {
                    TriggerPresent = present;
                }

                ArmAt = present + PostFrames;
            }
            return present >= ArmAt;
        }
    }

    private DiagRequest? _diagRequest;
    private DiagRequest? _diagActive;
    /// <summary>Draws issued while <c>_presentCount == _traceFrame</c> are traced; the targets are dumped at the present
    /// that makes <c>_presentCount == _traceDumpFrame</c>. --trace-draws=N sets both to N (historic behaviour: the targets
    /// of the frame before the traced draws); a capture traces one frame and dumps the targets at its end.</summary>
    private long _traceFrame = -1, _traceDumpFrame = -1;
    private string? _traceDir;
    /// <summary>A trace begun by <see cref="BeginDrawTrace"/>: every draw is traced until <see cref="EndDrawTrace"/>,
    /// across presents (replay-gpu traces the draws that produce one presented frame, which a game that runs ahead
    /// of its display queue issues one or two presents earlier).</summary>
    private bool _traceManual;
    /// <summary>Draw number within a manual trace (the N of --draw-limit=N@F); <see cref="_drawSerial"/> otherwise.</summary>
    private int _traceDrawId;
    private int NextDrawId => (_traceManual ? _traceDrawId : _drawSerial) + 1;

    private void InitTrace()
    {
        _traceFrame = _traceDumpFrame = _opt.TraceDrawsFrame;
        _traceDir = _opt.TraceDrawsFrame >= 0 && _opt.ShaderCacheDir != null ? Path.Combine(_opt.ShaderCacheDir, "trace") : null;
    }

    /// <summary>
    /// Asks for a capture of the next full frame into <paramref name="dir"/> (any thread). <paramref name="done"/> runs on
    /// a thread-pool thread once the files are written. False when a capture is already pending.
    /// </summary>
    /// <param name="postFrames">presents to wait (collecting replay frames) before the trace is armed</param>
    /// <param name="triggerPresent">the present that caused the capture (an anomaly), marked in replay.txt</param>
    public bool RequestDiagnosticCapture(string dir, Action<DiagnosticCaptureResult> done, int postFrames = 0, long triggerPresent = -1) =>
        Interlocked.CompareExchange(ref _diagRequest, new DiagRequest { Dir = dir, Done = done, PostFrames = Math.Max(0, postFrames), TriggerPresent = triggerPresent }, null) == null;

    /// <summary>
    /// Starts tracing every draw from now on into <paramref name="dir"/> (renderer thread; replay-gpu uses it for the
    /// draws that produce one presented frame, wherever they lie between presents). <see cref="EndDrawTrace"/>
    /// dumps the targets and completes it. False when a capture is already pending or active.
    /// </summary>
    /// <param name="title">first line of draws.txt (what the traced range is)</param>
    /// <param name="present">the present the traced draws produce, reported in the result</param>
    public bool BeginDrawTrace(string dir, string title, long present, Action<DiagnosticCaptureResult> done)
    {
        if (_diagActive != null || _diagRequest != null)
        {
            return false;
        }

        var req = new DiagRequest { Dir = dir, Done = done };
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception e) { done(new DiagnosticCaptureResult { Dir = dir, Error = e.Message }); return false; }
        _diagActive = req;
        req.Present = present;
        req.T0 = Stopwatch.GetTimestamp();
        _traceManual = true;
        _traceFrame = _traceDumpFrame = -1;
        _traceDir = dir;
        _traceDrawId = 0;
        _traceTextureFrame = _presentCount;
        _tracedTextures.Clear();
        _traceTextureStats = new StringBuilder();
        req.Draws.AppendLine("# VitaSharp draw trace: " + title);
        req.Draws.AppendLine("# [scene] begin = a GXM scene (render target, MSAA mode, depth); [draw] = one draw: vs/fs = GXP program hashes,");
        req.Draws.AppendLine("# rt = colour surface address:format, tN = textures (address:format:size:type...), msaa = how the scene is rendered");
        Log.Info($"[diag] draw trace begun at present #{_presentCount}: {title} -> {dir}");
        return true;
    }

    /// <summary>Ends a <see cref="BeginDrawTrace"/> trace: the work recorded so far is submitted, the render targets are
    /// dumped (with the last presented frame as the screenshot) and the result is delivered. Call it between scenes.</summary>
    public void EndDrawTrace()
    {
        if (!_traceManual || _diagActive is not { } dc)
        {
            return;
        }

        _traceManual = false;
        EndRendering();
        SubmitFrame(false, 0);
        CompleteCapture(dc, _lastPresented?.Src, _lastPresented?.Frame ?? default);
    }

    /// <summary>Withdraws a request the renderer has not started yet (no frame was presented since): true when withdrawn.</summary>
    public bool CancelDiagnosticCapture() => Interlocked.Exchange(ref _diagRequest, null) != null;

    /// <summary>Debug mode: an anomaly was confirmed on the presented frames (kind, details, present where it was
    /// confirmed). Raised on a thread-pool thread.</summary>
    public event Action<string, string, long>? AnomalyDetected;

    private void TraceLine(string line)
    {
        if (_diagActive is { } capture)
        {
            capture.Draws.AppendLine(line);
        }
        else
        {
            Log.Info(line);
        }
    }

    private void ArmCapture(DiagRequest req)
    {
        // a concurrent CancelDiagnosticCapture may have withdrawn it
        if (Interlocked.CompareExchange(ref _diagRequest, null, req) != req)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(req.Dir);
        }
        catch (Exception e) { req.Done(new DiagnosticCaptureResult { Dir = req.Dir, Error = e.Message }); return; }
        // the replay ring before the traced frame: snapshot now, PNGs written in the background
        req.Replay = SaveReplay(req.Dir, req.TriggerPresent);
        try
        {
            req.DrawRing = SaveDrawRing(req.Dir, req.TriggerPresent);
        }
        catch (Exception e)
        {
            Log.Warn($"[diag] draw ring snapshot failed: {e.Message}");
        }
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

    private void CompleteCapture(DiagRequest dc, RenderTarget? src, DisplayFrame frame)
    {
        var res = new DiagnosticCaptureResult { Dir = dc.Dir, Present = dc.Present, FrameWidth = frame.Width, FrameHeight = frame.Height };
        try
        {
            string shot = Path.Combine(dc.Dir, "screenshot.png");
            res.Targets = DumpAllTargets(Path.Combine(dc.Dir, "targets"), src != null ? (src, frame.Width, frame.Height, shot) : null);
            res.Screenshot = File.Exists(shot);
            File.WriteAllText(Path.Combine(dc.Dir, "draws.txt"), dc.Draws.ToString());
            File.WriteAllText(Path.Combine(dc.Dir, "frames.txt"), RecentFrames(FrameRingSize));
            if (_nanScans > 0 || Core.Gxm.Shader.ShaderGenerator.NanDraws)
            {
                File.WriteAllText(Path.Combine(dc.Dir, "nan.txt"), NanHistory());
            }

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
            _traceManual = false;
            InitTrace(); // back to normal rendering (or to a --trace-draws frame still ahead)
            _tracedTextures.Clear();
            Log.Info($"[diag] frame traced: {res.Draws} draws, {res.Targets} render targets, {res.Textures} textures ({res.Milliseconds:F0} ms)");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (dc.Replay is { } replay)
                    {
                        try
                        {
                            res.ReplayFrames = replay.Wait(TimeSpan.FromSeconds(60)) ? replay.Result : -1;
                        }
                        catch (Exception e)
                        {
                            res.ReplayFrames = -1;
                            Log.Warn($"[diag] replay write failed: {e.GetBaseException().Message}");
                        }
                    }
                    if (dc.DrawRing is { } ring)
                    {
                        try
                        {
                            res.DrawRingFrames = ring.Wait(TimeSpan.FromSeconds(60)) ? ring.Result : -1;
                        }
                        catch (Exception e)
                        {
                            res.DrawRingFrames = -1;
                            Log.Warn($"[diag] draw ring write failed: {e.GetBaseException().Message}");
                        }
                    }
                    dc.Done(res);
                }
                catch (Exception e)
                {
                    Log.Warn($"[diag] completion failed: {e}");
                }
            });
        }
    }

    // ================================================================ rolling per-frame summaries

    private struct FrameSummary
    {
        public long Present, GuestFrame;
        public int Draws, Scenes, NewShaders, NewPipelines, NewTargets, NewTextures;
        public float IntervalMs, TopCauseMs;
        public int TopCause;
        public uint Address, Width, Height;
    }

    private const int FrameRingSize = 120;
    private readonly FrameSummary[] _frameRing = new FrameSummary[FrameRingSize];
    private int _frameRingCount;
    private int _fScenes, _fNewShaders, _fNewPipelines, _fNewTargets, _fNewTextures;
    private long _lastPresentTicks;
    private readonly long[] _ledgerPrev = new long[(int)FrameLedger.Cat.Count];
    private uint _lastFrameW, _lastFrameH;

    // causes that stall the GPU thread or the guest within a frame (background work and idle time excluded)
    private static readonly FrameLedger.Cat[] s_hitchCauses =
    [
        FrameLedger.Cat.ShaderTranslate, FrameLedger.Cat.ShaderCompile, FrameLedger.Cat.ShaderPackLoad, FrameLedger.Cat.PipelineCreate,
        FrameLedger.Cat.TextureUpload, FrameLedger.Cat.FenceWait, FrameLedger.Cat.Writeback, FrameLedger.Cat.GuestGpuWait,
        FrameLedger.Cat.FileIo, FrameLedger.Cat.ShaderWait,
    ];

    /// <summary>At each present (always on: a few field writes and ten counter reads per frame).</summary>
    private void RecordFrameSummary(DisplayFrame frame)
    {
        long now = Stopwatch.GetTimestamp();
        int top = -1;
        long topTicks = 0;
        foreach (FrameLedger.Cat c in s_hitchCauses)
        {
            long t = FrameLedger.Total(c), d = t - _ledgerPrev[(int)c];
            _ledgerPrev[(int)c] = t;
            if (d > topTicks)
            {
                topTicks = d;
                top = (int)c;
            }
        }
        ref FrameSummary s = ref _frameRing[_frameRingCount++ % FrameRingSize];
        s = new FrameSummary
        {
            Present = _presentCount + 1,
            GuestFrame = DebugMode.Frame,
            Draws = _drawSerial,
            Scenes = _fScenes,
            NewShaders = _fNewShaders,
            NewPipelines = _fNewPipelines,
            NewTargets = _fNewTargets,
            NewTextures = _fNewTextures,
            IntervalMs = _lastPresentTicks == 0 ? 0 : (float)((now - _lastPresentTicks) * 1000.0 / Stopwatch.Frequency),
            TopCause = top,
            TopCauseMs = (float)(topTicks * 1000.0 / Stopwatch.Frequency),
            Address = frame.Address,
            Width = frame.Width,
            Height = frame.Height,
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
            FrameSummary s = _frameRing[i % FrameRingSize];
            string cause = s.TopCause >= 0 && s.TopCauseMs >= 0.05f ? $"{FrameLedger.Names[s.TopCause]}({s.TopCauseMs:F1})" : "-";
            sb.Append($"p{s.Present} f{s.GuestFrame} {s.IntervalMs:F1}ms draws={s.Draws} scenes={s.Scenes} newSh={s.NewShaders} newPipe={s.NewPipelines} newRT={s.NewTargets} newTex={s.NewTextures} hitch={cause} fb=0x{s.Address:X8} {s.Width}x{s.Height}\n");
        }
        return sb.ToString();
    }

    // ================================================================ descriptions

    /// <summary>GPU, driver and renderer settings for info.txt / renderer.txt.</summary>
    public string DescribeForDiagnostics()
    {
        PhysicalDeviceProperties p = _core.Properties;
        var sb = new StringBuilder();
        sb.AppendLine($"GPU: {_core.DeviceName} (vendor 0x{p.VendorID:X4}, device 0x{p.DeviceID:X4}, type {p.DeviceType})");
        sb.AppendLine($"Vulkan API: {p.ApiVersion >> 22}.{(p.ApiVersion >> 12) & 0x3FF}.{p.ApiVersion & 0xFFF}");
        sb.AppendLine($"Driver: {DriverDescription()}");
        sb.AppendLine($"MSAA: {_msaaSetting} (VITASHARP_MSAA), programmable blending in MSAA scenes: {(s_msaaInputFetch ? "input attachment" : "copy")} (VITASHARP_MSAA_FETCH), sample-rate shading {_core.SampleRateShading}");
        sb.AppendLine($"Shaders: shaderc {_compiler.OptLevel} (setting {_opt.ShaderOptimization ?? "default"}), async shaders {_asyncShaders}, async pipelines {_asyncPipelines}, workers setting {_opt.ShaderWorkers}, generator v{ShaderGenerator.GeneratorVersion}, debug overrides {ShaderGenerator.DebugOverridesActive}");
        sb.AppendLine($"Shader cache: {_opt.ShaderCacheDir ?? "(none)"}, title {_titleId ?? "-"}, pack {(_shaderPack != null ? "in use" : "not used")}");
        sb.AppendLine($"VSync {_opt.VSync}, validation {_opt.Validation}, presented frame {_lastFrameW}x{_lastFrameH}, swapchain {(_swapchain.Handle != 0 ? "yes" : "no (headless)")}");
        sb.AppendLine($"VRAM: {VramSummary()}");
        sb.AppendLine(_core.Caps.Describe());
        sb.AppendLine($"Mobile paths: {MobilePathsSummary()}");
        sb.AppendLine(LoadStoreReport());
        return sb.ToString().TrimEnd();
    }

    private string DriverDescription()
    {
        PhysicalDeviceProperties p = _core.Properties;
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
        catch
        {
            return $"version {ver} (raw 0x{v:X8})";
        }
    }

    /// <summary>Renderer state for hang reports (read from any thread without locking: values may be slightly stale).</summary>
    public string DebugState()
    {
        var sb = new StringBuilder();
        sb.Append($"renderer: present #{_presentCount}, GPU frames {FrameCount}, frame slot {_frameIndex}, in scene {_inScene}, rt 0x{_renderTarget?.Address ?? 0:X8}, draws this frame {_drawSerial}, device lost {_deviceLost}, shader jobs in flight {_inflight.Count}");
        for (int i = 0; i < _frames.Length; i++)
        {
            Frame f = _frames[i];
            sb.Append($"\n  frame slot {i}: recording {f.Recording}, submitted {f.Submitted} (seq {f.SubmitSeq}), retired {f.Retired}, writebacks {f.Writebacks.Count}, signals {f.Signals.Count}");
        }
        return sb.ToString();
    }

    private bool _deviceLostReported;

    /// <summary>Raised once when the GPU driver reports VK_ERROR_DEVICE_LOST (a driver reset, TDR): the front end tells
    /// the user; the renderer keeps returning instead of crashing, but draws nothing more. Argument: the Vulkan call.</summary>
    public event Action<string>? DeviceLost;

    private void ReportDeviceLost(string where)
    {
        if (_deviceLostReported)
        {
            return;
        }

        _deviceLostReported = true;
        Log.Error($"[gpu] VULKAN DEVICE LOST in {where} at present #{_presentCount} (guest frame {DebugMode.Frame})\n{DebugState()}\nlast frames:\n{RecentFrames(20)}");
        Log.Flush();
        try
        {
            DeviceLost?.Invoke(where);
        }
        catch (Exception e)
        {
            Log.Warn($"[gpu] device lost notice failed: {e.Message}");
        }
    }

    // ================================================================ debug mode: first-use logging

    private readonly HashSet<string> _dbgSeen = new();
    private readonly HashSet<(uint, uint, int)> _dbgOverlapSeen = new();

    private void Dbg(string msg) => Log.Info($"[dbg] f{DebugMode.Frame} p{_presentCount} {msg}");

    private string MsaaPath => _multisampled ? $"native {(int)_msSamples}x" : _supersampled ? "supersampled 2x" : "off";

    private void DbgVertexShader(VsEntry e, DrawCommand d)
    {
        if (!_dbgSeen.Add($"vs{e.ProgramHash:X16}{e.HintKey:X16}"))
        {
            return;
        }

        Dbg($"new vertex shader {e.ProgramHash:X16} hint {e.HintKey:X16}: {d.VertexProgram.Attributes.Length} attributes, {d.VertexProgram.Streams.Length} streams, " +
            $"{(e.Failed ? "FAILED" : string.IsNullOrEmpty(e.Gen?.Glsl) ? "from the shader pack" : "translated")}{(e.Gen?.AttributeFallback == true ? ", attribute fallback" : "")}");
    }

    private void DbgFragmentShader(FsEntry e, DrawCommand d, bool fetchInput)
    {
        if (!_dbgSeen.Add($"fs{e.ProgramHash:X16}{e.HintKey:X16}"))
        {
            return;
        }

        GxmFragmentProgram fp = d.FragmentProgram;
        GxpProgram prog = fp.Info.Program;
        Dbg($"new fragment shader {e.ProgramHash:X16} hint {e.HintKey:X16}: output fmt 0x{fp.OutputFormat:X} param {prog.OutputParamType}x{prog.OutputCompCount} start {prog.FragmentOutputStart}, " +
            $"program msaa mode {fp.MultisampleMode}, raw colour bits {e.Gen?.RawColorBits ?? 0}, framebuffer fetch {e.Gen?.UsesFragColor ?? false}{(fetchInput ? " (input attachment)" : "")}, " +
            $"writes depth {e.Gen?.WritesDepth ?? false}, textures 0x{fp.Info.TexturesUsed:X}, rt 0x{_renderTarget?.Address ?? 0:X8} fmt 0x{_renderTarget?.ColorFormat ?? 0:X8} ({_renderTarget?.Format}), scene msaa {MsaaPath}, " +
            $"{(e.Failed ? "FAILED" : string.IsNullOrEmpty(e.Gen?.Glsl) ? "from the shader pack" : "translated")}");
    }

    private void DbgPipeline(in PipelineKey k, VsEntry vs, FsEntry fs, PrimitiveTopology topology)
    {
        // GXP program hashes (the key's Vs/Fs are shader module handles, meaningless in a log)
        Dbg($"new pipeline vs {vs.ProgramHash:X16} fs {fs.ProgramHash:X16} {topology} colour {(k.HasColor ? k.ColorFormat.ToString() : "none")} samples {k.Samples} " +
            $"blend {(k.Blend ? $"{k.ColorFunc}/{k.ColorSrc}/{k.ColorDst} a {k.AlphaFunc}/{k.AlphaSrc}/{k.AlphaDst}" : "off")} mask 0x{k.Mask:X} polygon {k.Polygon} layout {k.VertexLayout:X16}");
    }

    private void DbgRenderTarget(RenderTarget rt, Format format, List<KeyValuePair<(uint, uint, uint, uint), RenderTarget>> replaced)
    {
        var sb = new StringBuilder($"new render target 0x{rt.Address:X8} {rt.Width}x{rt.Height} stride {rt.Stride} fmt 0x{rt.ColorFormat:X8} -> {format}");
        foreach (((uint, uint, uint, uint) _, RenderTarget? old) in replaced)
        {
            sb.Append($"; replaces 0x{old.Address:X8} {old.Width}x{old.Height} fmt 0x{old.ColorFormat:X8} ({old.Format})");
        }

        uint bytes = rt.Stride * (uint)Formats.ColorBytesPerPixel(rt.ColorFormat) * rt.Height;
        foreach (RenderTarget o in _renderTargets.Values)
        {
            if (o == rt)
            {
                continue;
            }

            uint ob = o.Stride * (uint)Formats.ColorBytesPerPixel(o.ColorFormat) * o.Height;
            if (o.Address < rt.Address + bytes && rt.Address < o.Address + ob)
            {
                sb.Append($"; overlaps 0x{o.Address:X8} {o.Width}x{o.Height} fmt 0x{o.ColorFormat:X8}");
            }
        }
        Dbg(sb.ToString());
    }

    private void DbgScene(BeginSceneCommand b)
    {
        if (!_dbgSeen.Add($"sc{_renderTarget?.Address ?? 0:X8}{_renderTarget?.ColorFormat ?? 0:X8}{b.Width}x{b.Height}{b.Multisample}{b.Color?.Downscale}{b.Depth?.DepthData ?? 0:X8}{MsaaPath}"))
        {
            return;
        }

        Dbg($"new scene setup: rt 0x{_renderTarget?.Address ?? 0:X8} {_renderTarget?.Width}x{_renderTarget?.Height} fmt 0x{_renderTarget?.ColorFormat ?? 0:X8} ({_renderTarget?.Format}) scene {b.Width}x{b.Height} gxm msaa {b.Multisample} downscale {b.Color?.Downscale} -> {MsaaPath}" +
            $", depth 0x{b.Depth?.DepthData ?? 0:X8} stencil 0x{b.Depth?.StencilData ?? 0:X8} fmt 0x{b.Depth?.Format ?? 0:X8}, gamma {b.Color?.GammaMode}");
    }

    private void DbgTextureFormat(GxmTextureDesc d, uint w, uint h)
    {
        if (!_dbgSeen.Add($"tf{d.Format:X8}{d.TextureType:X8}"))
        {
            return;
        }

        Dbg($"new texture format 0x{d.Format:X8} ({Core.Gxm.GxmFormats.BaseOf(d.Format)}, swizzle bits 0x{d.Format & 0x7000:X}) type 0x{d.TextureType:X8} first seen at 0x{d.DataAddress:X8} {w}x{h} mips {d.MipCount} gamma {d.GammaMode}");
    }

    /// <summary>Several colour targets contain a sampled address: which ones (the first compatible one in this order is used).</summary>
    private void DbgTextureOverlap(GxmTextureDesc d)
    {
        uint addr = d.DataAddress;
        List<RenderTarget> candidates = TargetsContaining(addr);
        int n = candidates.Count;
        if (n < 2 || !_dbgOverlapSeen.Add((addr, d.Format, n)))
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (RenderTarget rt in candidates)
        {
            sb.Append($" [0x{rt.Address:X8} {rt.Width}x{rt.Height} fmt 0x{rt.ColorFormat:X8} lastScene {rt.LastScene}]");
        }

        string set = sb.ToString();
        Dbg($"texture 0x{addr:X8} fmt 0x{d.Format:X8} {d.Width}x{d.Height} lies in {n} render targets, tried in this order:{set}");
    }

    private void DbgDepthOverlap(uint addr, DepthTarget chosen, bool stencil)
    {
        int n = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            StringBuilder? sb = pass == 1 ? new StringBuilder() : null;
            foreach (DepthTarget dt in _depthTargets.Values)
            {
                bool inDepth = dt.Address != 0 && addr >= dt.Address && addr < dt.Address + dt.StrideBytes * dt.Height;
                bool inStencil = dt.StencilAddress != 0 && addr >= dt.StencilAddress && addr < dt.StencilAddress + dt.StencilStrideBytes * dt.Height;
                if (!inDepth && !inStencil)
                {
                    continue;
                }

                if (sb == null)
                {
                    n++;
                }
                else
                {
                    sb.Append($" [0x{dt.Address:X8}/0x{dt.StencilAddress:X8} {dt.Width}x{dt.Height} lastUsed {dt.LastUsedFrame}]");
                }
            }
            // the list is only built for a combination not logged yet
            if (sb == null && (n < 2 || !_dbgOverlapSeen.Add((chosen.Address, stencil ? 1u : 0u, -n))))
            {
                return;
            }

            if (sb != null)
            {
                Dbg($"address 0x{addr:X8} lies in {n} depth/stencil targets:{sb}; picked 0x{chosen.Address:X8} ({(stencil ? "stencil" : "depth")}, most recently used)");
            }
        }
    }
}
