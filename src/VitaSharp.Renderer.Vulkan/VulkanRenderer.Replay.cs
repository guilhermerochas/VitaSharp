using System.Diagnostics;
using System.Text;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Debug mode: replay ring of the presented frames and the per-frame anomaly checks. Every present blits the presented
/// image, downscaled to fit 480x272, into a device-local staging image and copies it into the next slot of a
/// host-visible ring with the frame's own command buffer (no wait). When the frame retires (non-blocking, see
/// VulkanRenderer.Sync.cs) its slot gets the colour metrics of <see cref="FrameMetrics"/> and goes through the
/// <see cref="FrameAnomalyDetector"/>; a capture (F10, --diag-at, an anomaly) writes the ring into replay/ before
/// the traced frame. The ring exists only while debug mode is on (F9 off frees it). See docs/notes/debug-mode.md.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    /// <summary>128 slots: 4.3 s at 30 fps, 2.1 s at 60 fps. 480x272 RGBA8 = 510 KB per slot, 64 MB host memory in all
    /// (full 960x544 would be 255 MB for the same time), plus one 510 KB device-local staging image.</summary>
    public const int ReplaySlots = 128, ReplayW = 480, ReplayH = 272;
    const int ReplaySlotBytes = ReplayW * ReplayH * 4;
    /// <summary>Auto captures keep collecting this many presents after the anomaly (about 1 s at 30 fps).</summary>
    public const int ReplayPostFrames = 30;

    struct ReplayEntry
    {
        public long Present, GuestFrame, Ticks;
        public uint W, H, SrcW, SrcH;
        public bool Ready;
        public FrameMetrics Metrics;
        public FrameMetrics.Hit Hits;
    }

    sealed class ReplayRing
    {
        public Buffer Buf;
        public DeviceMemory Mem;
        public byte* Ptr;
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation ImageMem;
        public bool ImageInitialized;
        public long Written;
        public readonly ReplayEntry[] Entries = new ReplayEntry[ReplaySlots];
    }

    ReplayRing? _replay;
    bool _replayFailed;
    FrameAnomalyDetector _detector = new();
    readonly Queue<FrameAnomalyDetector.Event> _recentEvents = new();
    int _inspectBudget;
    // cost accounting (debug mode only): GPU-worker time recording the copies and measuring the retired frames
    long _replayRecordTicks, _replayMeasureTicks, _replayRecorded, _replayMeasured;

    /// <summary>"\nPERF replay: ..." when the ring ran this session, else empty.</summary>
    string ReplayReport() => _replayRecorded == 0 ? "" :
        $"\nPERF replay: {_replayRecorded} frames copied ({_replayRecordTicks * 1e6 / Stopwatch.Frequency / _replayRecorded:F1} us/frame recording on the GPU worker), " +
        $"{_replayMeasured} measured ({_replayMeasureTicks * 1e6 / Stopwatch.Frequency / Math.Max(1, _replayMeasured):F1} us/frame metrics + rules)";

    /// <summary>Metrics sample every 4th pixel of every 4th row of the ring frame (a 120x68 grid for 960x544, ~8K
    /// samples): ~60 us per frame on the GPU worker, where every 2nd pixel/row cost ~190 us. A glitch over the 5-8%
    /// thresholds still covers hundreds of samples.</summary>
    const int MeasureStep = 4;
    Format _replayFormatChecked = Format.Undefined;
    bool _replayFormatOk;

    static readonly (string Kind, long From, int Count)? Inject = ParseInject(Environment.GetEnvironmentVariable("VITASHARP_DEBUG_INJECT"));

    /// <summary>
    /// Test switch VITASHARP_DEBUG_INJECT=KIND@PRESENT[:COUNT] (KIND = green, blue, magenta, black, white; COUNT frames,
    /// default 4): the replay copies of those presents are painted (green/blue/magenta over the top half, black/white
    /// over all of it) before they are measured, so the detector and the saved replay both see it. The rendered frame
    /// itself is untouched.
    /// </summary>
    static (string, long, int)? ParseInject(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Split('@');
        string kind = p[0].Trim().ToLowerInvariant();
        long from = 0;
        int count = 4;
        if (p.Length > 1)
        {
            var q = p[1].Split(':');
            if (long.TryParse(q[0].Trim().TrimStart('f', 'p'), out var f)) from = f;
            if (q.Length > 1 && int.TryParse(q[1].Trim(), out var c)) count = Math.Max(1, c);
        }
        return (kind, from, count);
    }

    ReplayRing? CreateReplay()
    {
        if (_replayFailed) return null;
        var ring = new ReplayRing();
        try
        {
            var (buf, mem, ptr) = _core.CreateHostBuffer((ulong)ReplaySlots * ReplaySlotBytes, BufferUsageFlags.TransferDstBit, MemCat.DebugRing);
            ring.Buf = buf; ring.Mem = mem; ring.Ptr = (byte*)ptr;
            (ring.Image, ring.View, ring.ImageMem) = CreateImage(MemCat.DebugRing, ReplayW, ReplayH, Format.R8G8B8A8Unorm,
                ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        }
        catch (Exception e)
        {
            _replayFailed = true;
            if (ring.Buf.Handle != 0) _core.DestroyBuffer(ring.Buf, ring.Mem);
            Log.Warn($"[dbg] replay ring unavailable ({e.Message}): anomaly checks and replay captures are off");
            return null;
        }
        _detector = new FrameAnomalyDetector();
        Dbg($"replay ring on: {ReplaySlots} frames at up to {ReplayW}x{ReplayH} ({((long)ReplaySlots * ReplaySlotBytes + (1 << 19)) >> 20} MB host memory)");
        return ring;
    }

    /// <summary>Debug mode turned off (F9): the ring is freed once the frames recording into it have completed.</summary>
    void ReleaseReplay()
    {
        var ring = _replay;
        if (ring == null) return;
        _replay = null;
        foreach (var f in _frames) f.ReplaySlot = -1;
        // the current frame's deferred list runs after its fence, and frames retire in submission order
        F.Deferred.Add(() => DestroyReplayRing(ring));
        Log.Info($"[dbg] replay ring released at present #{_presentCount}");
    }

    void DestroyReplay()
    {
        if (_replay is { } ring) DestroyReplayRing(ring);
        _replay = null;
    }

    void DestroyReplayRing(ReplayRing ring)
    {
        _core.DestroyBuffer(ring.Buf, ring.Mem);
        _vk.DestroyImageView(_dev, ring.View, null);
        _vk.DestroyImage(_dev, ring.Image, null);
        _imagePool.Free(ring.ImageMem);
    }

    /// <summary>Debug mode, every present (after EndRendering): records the replay copy into the frame's command buffer.</summary>
    void DebugPresent(RenderTarget src, DisplayFrame frame)
    {
        // texture inspection (TEXTURE SUSPECT): one upload every 8 presents
        if (_presentCount % 8 == 0) _inspectBudget = 1;
        var f = F;
        if (f.ReplaySlot >= 0) return;
        if (src.Format != _replayFormatChecked)
        {
            _replayFormatChecked = src.Format;
            // blits cannot convert integer formats; the presented surface is a colour format in practice
            string name = src.Format.ToString();
            _replayFormatOk = !name.Contains("int", StringComparison.Ordinal);
            if (!_replayFormatOk && _dbgSeen.Add($"noreplay{src.Format}")) Dbg($"replay/anomaly checks skipped: presented surface is {src.Format}");
        }
        if (!_replayFormatOk) return;
        var ring = _replay ??= CreateReplay();
        if (ring == null) return;
        uint w = Math.Min(frame.Width, src.Width), h = Math.Min(frame.Height, src.Height);
        if (w == 0 || h == 0) return;
        float scale = Math.Min(1f, Math.Min(ReplayW / (float)w, ReplayH / (float)h));
        uint dw = Math.Clamp((uint)MathF.Round(w * scale), 1, ReplayW), dh = Math.Clamp((uint)MathF.Round(h * scale), 1, ReplayH);

        long t0 = Stopwatch.GetTimestamp();
        int slot = (int)(ring.Written % ReplaySlots);
        ring.Written++;
        ring.Entries[slot] = new ReplayEntry
        {
            Present = _presentCount, GuestFrame = DebugMode.Frame, Ticks = t0, W = dw, H = dh, SrcW = w, SrcH = h,
        };
        // the previous copy out of the staging image must be done before the blit overwrites it
        Barrier(ring.Image, ring.ImageInitialized ? ImageLayout.General : ImageLayout.Undefined, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit, AccessFlags.TransferWriteBit);
        ring.ImageInitialized = true;
        Blit(src.Image, w, h, ring.Image, dw, dh, Filter.Linear);
        Barrier(ring.Image, ImageLayout.General, ImageLayout.General, ImageAspectFlags.ColorBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
        var region = new BufferImageCopy
        {
            BufferOffset = (ulong)slot * ReplaySlotBytes,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(dw, dh, 1),
        };
        _vk.CmdCopyImageToBuffer(Cmd, ring.Image, ImageLayout.General, ring.Buf, 1, in region);
        BufferBarrier(ring.Buf, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, AccessFlags.TransferWriteBit, AccessFlags.HostReadBit);
        f.ReplaySlot = slot;
        _replayRecordTicks += Stopwatch.GetTimestamp() - ring.Entries[slot].Ticks;
        _replayRecorded++;
    }

    /// <summary>The frame retired: its replay slot is complete. Measure it and run the anomaly rules.</summary>
    void CompleteReplayFrame(Frame f)
    {
        int slot = f.ReplaySlot;
        f.ReplaySlot = -1;
        if (_replay is not { } ring) return;
        ref var e = ref ring.Entries[slot];
        long t0 = Stopwatch.GetTimestamp();
        byte* px = ring.Ptr + (long)slot * ReplaySlotBytes;
        if (Inject is { } inj && e.Present >= inj.From && e.Present < inj.From + inj.Count)
        {
            InjectColour(px, (int)e.W, (int)e.H, inj.Kind);
            Log.Info($"[anomaly] test injection ({inj.Kind}) into present #{e.Present}");
        }
        var m = FrameMetrics.Measure(px, (int)e.W, (int)e.H, MeasureStep);
        e.Metrics = m;
        e.Hits = m.Hits;
        e.Ready = true;
        if (e.Present % 300 == 0) Log.Info($"[frame-metrics] p{e.Present} {m}");
        _replayMeasured++;

        foreach (var ev in _detector.Feed(e.Present, m))
        {
            _recentEvents.Enqueue(ev);
            while (_recentEvents.Count > 64) _recentEvents.Dequeue();
            Log.Info($"[anomaly] p{ev.Present} f{e.GuestFrame} {ev.Kind}{(ev.Detail == "cleared" ? " cleared" : ": " + ev.Detail)}");
            if (ev.Capture && AnomalyDetected is { } handler)
            {
                var (kind, detail, present) = (ev.Kind, ev.Detail, ev.Present);
                ThreadPool.QueueUserWorkItem(_ => { try { handler(kind, detail, present); } catch (Exception x) { Log.Warn($"[anomaly] handler failed: {x.Message}"); } });
            }
        }
        _replayMeasureTicks += Stopwatch.GetTimestamp() - t0;
    }

    static void InjectColour(byte* px, int w, int h, string kind)
    {
        (byte r, byte g, byte b, bool all) c = kind switch
        {
            "green" => (0, 255, 0, false),
            "blue" => (0, 0, 255, false),
            "black" => (0, 0, 0, true),
            "white" => (255, 255, 255, true),
            _ => (255, 0, 255, false), // magenta
        };
        int rows = c.all ? h : (h + 1) / 2;
        for (int y = 0; y < rows; y++)
        {
            byte* q = px + (long)y * w * 4;
            for (int x = 0; x < w; x++, q += 4) { q[0] = c.r; q[1] = c.g; q[2] = c.b; q[3] = 255; }
        }
    }

    sealed record ReplayShot(long Present, long GuestFrame, DateTime Time, uint W, uint H, uint SrcW, uint SrcH, FrameMetrics Metrics, FrameMetrics.Hit Hits, byte[] Pixels);

    /// <summary>
    /// Capture armed: retires the frames in flight (so every slot recorded so far is complete), copies the ready slots
    /// out of the ring and writes them into <paramref name="dir"/>/replay/ on a worker thread. Null when the ring is off.
    /// </summary>
    Task<int>? SaveReplay(string dir, long trigger)
    {
        if (_replay == null) return null;
        foreach (var o in _frames)
            if (o.Submitted && !o.Retired) RetireFrame(o, RetireCause.Finish);
        if (_replay is not { } ring) return null;
        long now = Stopwatch.GetTimestamp();
        DateTime wallNow = DateTime.Now;
        var shots = new List<ReplayShot>(ReplaySlots);
        for (long k = Math.Max(0, ring.Written - ReplaySlots); k < ring.Written; k++)
        {
            ref var e = ref ring.Entries[(int)(k % ReplaySlots)];
            if (!e.Ready) continue;
            var pixels = new byte[e.W * e.H * 4];
            new ReadOnlySpan<byte>(ring.Ptr + (k % ReplaySlots) * ReplaySlotBytes, pixels.Length).CopyTo(pixels);
            var time = wallNow - TimeSpan.FromSeconds((now - e.Ticks) / (double)Stopwatch.Frequency);
            shots.Add(new ReplayShot(e.Present, e.GuestFrame, time, e.W, e.H, e.SrcW, e.SrcH, e.Metrics, e.Hits, pixels));
        }
        var events = _recentEvents.ToArray();
        long armed = _presentCount;
        Log.Info($"[diag] replay: {shots.Count} frames (presents #{(shots.Count > 0 ? shots[0].Present : 0)}-#{(shots.Count > 0 ? shots[^1].Present : 0)}), trigger #{trigger}, writing in the background");
        return Task.Run(() => WriteReplay(Path.Combine(dir, "replay"), shots, events, trigger, armed));
    }

    const int SheetCols = 6, SheetRows = 5, SheetCellW = 240, SheetCellH = 136;

    static int WriteReplay(string dir, List<ReplayShot> shots, FrameAnomalyDetector.Event[] events, long trigger, long armed)
    {
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.AppendLine("# VitaSharp replay ring: the presented frames before the capture (debug mode), downscaled to fit 480x272");
        sb.AppendLine($"# {shots.Count} frames; trigger present #{trigger} (F10/--diag-at: when the request arrived; auto: the frame the anomaly was confirmed on); capture armed at present #{armed}");
        sb.AppendLine("# frame_<present>.png = one frame; sheet_<first>-<last>.png = 30 frames, 6 per row, oldest top left (red border = trigger, yellow = a per-frame threshold hit)");
        sb.AppendLine("# hits: M = magenta > 5%, S = saturated green+blue > 8%, B = blown-out (white > 35% or mean > 0.85), K = black > 97%");
        sb.AppendLine("# present guestFrame time(local) dt_to_trigger_ms size(src) mean white black magenta green blue hits");
        DateTime? triggerTime = shots.LastOrDefault(s => s.Present <= trigger)?.Time;
        foreach (var s in shots)
        {
            var m = s.Metrics;
            string dt = triggerTime is { } tt ? $"{(s.Time - tt).TotalMilliseconds:+0;-0;0}" : "-";
            sb.AppendLine(FormattableString.Invariant($"p{s.Present} f{s.GuestFrame} {s.Time:HH:mm:ss.fff} {dt} {s.W}x{s.H}({s.SrcW}x{s.SrcH}) {m.Mean:F3} {m.White:F3} {m.Black:F3} {m.Magenta:F3} {m.Green:F3} {m.Blue:F3} {HitString(s.Hits)}{(s.Present == trigger ? "  <== trigger" : "")}"));
        }
        sb.AppendLine();
        sb.AppendLine("# recent anomaly events (debug mode, newest last)");
        foreach (var e in events) sb.AppendLine($"p{e.Present} {e.Kind}{(e.Capture ? " [capture]" : "")}: {e.Detail}");
        if (events.Length == 0) sb.AppendLine("(none)");
        File.WriteAllText(Path.Combine(dir, "replay.txt"), sb.ToString());

        foreach (var s in shots)
        {
            for (int i = 3; i < s.Pixels.Length; i += 4) s.Pixels[i] = 255; // the display ignores alpha
            Png.Write(Path.Combine(dir, $"frame_{s.Present:D6}.png"), s.Pixels, (int)s.W, (int)s.H);
        }

        int per = SheetCols * SheetRows;
        for (int i = 0; i < shots.Count; i += per)
        {
            int n = Math.Min(per, shots.Count - i);
            int sw = SheetCols * SheetCellW, sh = ((n + SheetCols - 1) / SheetCols) * SheetCellH;
            var sheet = new byte[sw * sh * 4];
            for (int j = 0; j < n; j++)
            {
                var s = shots[i + j];
                var cell = Thumbnail(s.Pixels, (int)s.W, (int)s.H, SheetCellW, SheetCellH);
                int cx = j % SheetCols * SheetCellW, cy = j / SheetCols * SheetCellH;
                for (int y = 0; y < SheetCellH; y++)
                    cell.AsSpan(y * SheetCellW * 4, SheetCellW * 4).CopyTo(sheet.AsSpan(((cy + y) * sw + cx) * 4));
                if (s.Present == trigger) Border(sheet, sw, cx, cy, 255, 0, 0);
                else if ((s.Hits & ~FrameMetrics.Hit.Black) != 0) Border(sheet, sw, cx, cy, 255, 220, 0);
            }
            Png.Write(Path.Combine(dir, $"sheet_{shots[i].Present:D6}-{shots[i + n - 1].Present:D6}.png"), sheet, sw, sh);
        }
        Log.Info($"[diag] replay written: {shots.Count} frames into {dir}");
        return shots.Count;
    }

    static void Border(byte[] img, int stride, int x0, int y0, byte r, byte g, byte b)
    {
        for (int y = y0; y < y0 + SheetCellH; y++)
            for (int x = x0; x < x0 + SheetCellW; x++)
            {
                bool edge = y - y0 < 3 || y0 + SheetCellH - y <= 3 || x - x0 < 3 || x0 + SheetCellW - x <= 3;
                if (!edge) continue;
                int o = (y * stride + x) * 4;
                img[o] = r; img[o + 1] = g; img[o + 2] = b; img[o + 3] = 255;
            }
    }

    static string HitString(FrameMetrics.Hit h) => h == 0 ? "-" :
        $"{(h.HasFlag(FrameMetrics.Hit.Magenta) ? "M" : "")}{(h.HasFlag(FrameMetrics.Hit.Saturated) ? "S" : "")}{(h.HasFlag(FrameMetrics.Hit.Bright) ? "B" : "")}{(h.HasFlag(FrameMetrics.Hit.Black) ? "K" : "")}";
}
