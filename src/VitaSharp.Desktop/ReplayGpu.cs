using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using VitaSharp.Desktop.Host;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>
/// <c>replay-gpu &lt;dump.vsgd&gt; [options]</c>: replays a GPU dump (<see cref="GpuDumpRecorder"/>) on a fresh Vulkan
/// renderer, without the game: the recorded guest memory is written into an empty guest address space before each
/// command that read it, and the commands are executed in the recorded order. See docs/notes/gpu-dump.md.
/// </summary>
static class ReplayGpu
{
    static string? Opt(string[] o, string name) => o.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];

    public static void PrintUsage()
    {
        Console.WriteLine("  replay-gpu <dump.vsgd> [options]     replay a GPU dump (F8 / --gpu-dump-at=F1:F2) without the game");
        Console.WriteLine("    --out=<dir>            frames, sheets, log (default <dump>_replay next to the dump)");
        Console.WriteLine("    --frame=N              stop after frame N (renderer present number, as in frame_N.png)");
        Console.WriteLine("    --dump-every=K --dump-from=F   which frames to write as PNG (default every frame)");
        Console.WriteLine("    --trace-draws=N        full capture of frame N into <out>/trace_N: draws.txt, shaders/, targets/, textures/");
        Console.WriteLine("    --draw-limit=N[@F]     execute only the first N draws of frame F (default --frame), to bisect a bad draw");
        Console.WriteLine("    --msaa=native|ss|off   VITASHARP_MSAA; --debug-fs=<hash>:<expr> VITASHARP_DEBUG_FS; --debug-tex0=<mode>");
        Console.WriteLine("    --env=KEY=VALUE        any renderer/shader environment switch (repeatable); --no-recorded-env ignores the recorded ones");
        Console.WriteLine("    --compare=<dir>        compare the written frames with frame_N.png of another run (compare.txt, diff_N.png)");
        Console.WriteLine("    --window [--hold] [--speed=X|--fast]  show it in a window at the recorded pace (X times), keep it open at the end");
        Console.WriteLine("    --shader-cache=<dir> --async-shaders=on|off (default off) --no-idle --no-targets --debug-mode");
        Console.WriteLine("    --info                 header and statistics only; --recompress=<out.vsgd> [--quality=0-11] rewrites it smaller (default 9)");
    }

    public static int Run(string path, string[] options)
    {
        if (!File.Exists(path)) { Console.Error.WriteLine($"GPU dump {path} not found"); return 1; }
        GpuDumpHeader header;
        using (var r = new GpuDumpReader(path)) header = r.Header;
        if (options.Contains("--info")) return Info(path);
        if (Opt(options, "--recompress") is { } recompressed)
        {
            int q = int.TryParse(Opt(options, "--quality"), out var qq) ? Math.Clamp(qq, 0, 11) : 9;
            var (chunks, raw, sec) = GpuDumpReader.Recompress(path, recompressed, q);
            Console.WriteLine($"{chunks} chunks, {raw / 1048576.0:F1} MB raw -> {new FileInfo(recompressed).Length / 1048576.0:F1} MB at quality {q} " +
                              $"({new FileInfo(path).Length / 1048576.0:F1} MB before), {sec:F1} s compressing ({raw / 1048576.0 / Math.Max(sec, 1e-3):F0} MB/s)");
            return 0;
        }
        // environment switches must be in place before the renderer and shader generator types initialise
        ApplyEnvironment(header, options);
        return RunCore(path, header, options);
    }

    static readonly string[] NotReplayedEnv = ["VITASHARP_DATA", "VITASHARP_GPU_DUMP_AT", "VITASHARP_DEBUG_MODE", "VITASHARP_CPU", "VITASHARP_LOCKSTEP"];

    static void ApplyEnvironment(GpuDumpHeader header, string[] options)
    {
        var set = new Dictionary<string, string>();
        if (!options.Contains("--no-recorded-env"))
            foreach (var (k, v) in header.Entries)
                if (k.StartsWith("env.") && !NotReplayedEnv.Contains(k[4..]) && Environment.GetEnvironmentVariable(k[4..]) == null) set[k[4..]] = v;
        if (Opt(options, "--msaa") is { } msaa) set["VITASHARP_MSAA"] = msaa;
        if (Opt(options, "--debug-fs") is { } dfs) set["VITASHARP_DEBUG_FS"] = dfs;
        if (Opt(options, "--debug-tex0") is { } dt) set["VITASHARP_DEBUG_TEX0"] = dt;
        foreach (var e in options.Where(o => o.StartsWith("--env=")))
        {
            var kv = e["--env=".Length..];
            int eq = kv.IndexOf('=');
            if (eq > 0) set[kv[..eq]] = kv[(eq + 1)..];
        }
        foreach (var (k, v) in set) Environment.SetEnvironmentVariable(k, v);
        if (set.Count > 0) Console.WriteLine("Environment: " + string.Join(' ', set.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    static int Info(string path)
    {
        using var r = new GpuDumpReader(path);
        Console.WriteLine($"GPU dump {path} (format v{r.Version}, {new FileInfo(path).Length / 1048576.0:F1} MB)");
        Console.Write(r.Header.Text);
        long mem = 0, memBytes = 0, draws = 0, cmds = 0, frames = 0, idles = 0;
        long firstMicros = -1, lastMicros = 0;
        var regions = new SortedDictionary<uint, (long Updates, long Bytes)>(); // per 1 MB of guest address space
        long vub = 0, fub = 0, firstFrameBytes = 0;
        var uniq = new HashSet<ulong>();
        var uniqF = new HashSet<ulong>();
        while (r.Next())
        {
            switch (r.Kind)
            {
                case GpuDumpRecord.Memory:
                    mem++; memBytes += r.Data.Length;
                    var g = regions.GetValueOrDefault(r.Address >> 20);
                    regions[r.Address >> 20] = (g.Updates + 1, g.Bytes + r.Data.Length);
                    break;
                case GpuDumpRecord.Idle: idles++; break;
                case GpuDumpRecord.BackendState: Console.WriteLine($"backend state (render targets at the start): {r.Data.Length / 1048576.0:F1} MB"); break;
                default:
                    cmds++;
                    if (r.Command is DrawCommand d)
                    {
                        draws++;
                        vub += d.VertexUniformsLength; fub += d.FragmentUniformsLength;
                        uniq.Add(System.IO.Hashing.XxHash3.HashToUInt64(d.VertexUniformSpan));
                        uniqF.Add(System.IO.Hashing.XxHash3.HashToUInt64(d.FragmentUniformSpan));
                    }
                    if (r.Kind == GpuDumpRecord.Present)
                    {
                        frames++;
                        if (firstMicros < 0) firstMicros = r.PresentInfo.Micros;
                        lastMicros = r.PresentInfo.Micros;
                        // compressed bytes read so far (chunks end at presents): the first frame carries the initial state
                        if (frames == 1) firstFrameBytes = r.CompressedBytesRead;
                    }
                    break;
            }
        }
        Console.WriteLine($"records: {frames} frames, {cmds} commands ({draws} draws), {idles} idle points, {mem} memory updates ({memBytes / 1048576.0:F1} MB)");
        if (frames > 1) Console.WriteLine($"recorded time {lastMicros / 1e6:F2} s, {frames / Math.Max(lastMicros / 1e6, 1e-6):F1} fps; {new FileInfo(path).Length / 1048576.0 / Math.Max(lastMicros / 1e6, 1e-6):F1} MB per second of gameplay");
        long fileBytes = new FileInfo(path).Length;
        if (frames > 1) Console.WriteLine($"on disk: first frame (with the initial state) {firstFrameBytes / 1048576.0:F1} MB, then {(fileBytes - firstFrameBytes) / 1048576.0 / (frames - 1):F2} MB/frame " +
                                          $"({(fileBytes - firstFrameBytes) / 1048576.0 / Math.Max((lastMicros - firstMicros) / 1e6, 1e-6):F1} MB per recorded second)");
        Console.WriteLine($"draw uniforms: vertex {vub / 1048576.0:F1} MB ({uniq.Count} distinct blocks), fragment {fub / 1048576.0:F1} MB ({uniqF.Count} distinct)");
        Console.WriteLine("guest memory read by the GPU, per MB of address space (updates, KB):");
        foreach (var (mb, v) in regions) Console.WriteLine($"  0x{mb << 20:X8}  {v.Updates,7} {v.Bytes / 1024.0,10:F0}");
        if (r.Summary != null) Console.Write("summary:\n" + r.Summary.Text);
        else Console.WriteLine("summary: missing (the recording was cut short)");
        return 0;
    }

    sealed class Settings
    {
        public long StopFrame = long.MaxValue, TraceFrame = -1, LimitFrame = -1, DrawLimit = -1;
        public bool Idle = true, ImportState = true;
        public double Speed = 1; // 0 = as fast as possible
        public string Out = "";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunCore(string path, GpuDumpHeader header, string[] options)
    {
        var s = new Settings
        {
            Out = Path.GetFullPath(Opt(options, "--out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "_replay")),
            Idle = !options.Contains("--no-idle"),
            ImportState = !options.Contains("--no-targets"),
            Speed = options.Contains("--fast") ? 0 : double.TryParse(Opt(options, "--speed"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sp) ? sp : 1,
        };
        if (long.TryParse(Opt(options, "--frame"), out var fr)) s.StopFrame = fr;
        if (long.TryParse(Opt(options, "--trace-draws"), out var tf)) s.TraceFrame = tf;
        if (Opt(options, "--draw-limit") is { } dl)
        {
            var p = dl.Split('@');
            s.DrawLimit = long.Parse(p[0]);
            s.LimitFrame = p.Length > 1 ? long.Parse(p[1]) : s.StopFrame != long.MaxValue ? s.StopFrame : -1;
            if (s.LimitFrame < 0) { Console.Error.WriteLine("--draw-limit needs a frame: --draw-limit=N@F or --frame=F"); return 1; }
        }
        Directory.CreateDirectory(s.Out);
        foreach (var f in Directory.GetFiles(s.Out, "frame_*.png")) File.Delete(f);
        foreach (var f in Directory.GetFiles(s.Out, "diff_*.png")) File.Delete(f);
        Log.OpenFile(Path.Combine(s.Out, "log.txt"));
        if (options.Contains("--debug-mode")) DebugMode.Enable();
        long firstPresent = header.Long("first-present");
        Log.Info($"[replay] {path}: {header["title-id"]} {header["title"]}, recorded with {header["build"]} on {header["gpu"]}, presents after #{firstPresent}");

        var thumbs = new List<(long Frame, byte[] Rgba)>();
        var ro = new VulkanRendererOptions
        {
            ShaderCacheDir = Opt(options, "--shader-cache") ?? Path.Combine(Program.DataRoot, "shadercache"),
            Validation = options.Contains("--validation"),
            VSync = false,
            DumpFramesDir = s.Out,
            DumpEvery = int.TryParse(Opt(options, "--dump-every"), out var de) && de > 0 ? de : 1,
            DumpFrom = long.TryParse(Opt(options, "--dump-from"), out var dfrom) ? dfrom : 0,
            Thumbnails = thumbs,
            ShaderOptimization = Opt(options, "--shaderc-opt"),
            // synchronous by default: every draw finds its pipeline ready (the async path never skips a draw either,
            // but synchronous compilation keeps replay timings comparable)
            AsyncShaders = Opt(options, "--async-shaders") is "on" or "1" or "true",
            AsyncPipelines = Opt(options, "--async-shaders") is "on" or "1" or "true",
            FirstPresent = firstPresent,
        };

        int rc;
        if (options.Contains("--window"))
        {
            using var window = new WindowHost($"VitaSharp replay | {header["title-id"]} {Path.GetFileName(path)}", 1, false);
            bool hold = options.Contains("--hold");
            rc = 1;
            var closing = new ManualResetEventSlim();
            window.Closing += () => closing.Set();
            window.Loaded += () =>
            {
                ro.Surface = window;
                ro.VSync = true;
                new Thread(() =>
                {
                    try { rc = Replay(path, header, ro, s, closing); }
                    catch (Exception e) { Log.Error($"[replay] failed: {e}"); }
                    if (!hold) window.Close();
                }) { Name = "GPU replay", IsBackground = true }.Start();
            };
            window.Resized += () => _renderer?.NotifyResized();
            window.Run();
            closing.Set();
        }
        else
        {
            s.Speed = 0;
            rc = Replay(path, header, ro, s, null);
        }
        if (thumbs.Count > 0) Runner.WriteContactSheet(s.Out, thumbs);
        if (Opt(options, "--compare") is { } cmp) Compare(s.Out, cmp);
        Log.Flush();
        Console.WriteLine($"Results in {s.Out}");
        return rc;
    }

    static VulkanRenderer? _renderer;

    static int Replay(string path, GpuDumpHeader header, VulkanRendererOptions ro, Settings s, ManualResetEventSlim? closing)
    {
        using var memory = new GuestMemory();
        using var reader = new GpuDumpReader(path);
        var renderer = _renderer = new VulkanRenderer(ro);
        try
        {
            if (header["title-id"] is { Length: > 0 } title) renderer.BeginTitle(title);
            var wall = Stopwatch.StartNew();
            long frameDraws = 0, draws = 0, skipped = 0, commands = 0, memBytes = 0, frames = 0;
            long firstMicros = -1;
            Task<DiagnosticCaptureResult>? trace = null;
            void MaybeRequestTrace()
            {
                // the capture arms at the next present and traces the frame that follows it
                if (s.TraceFrame < 0 || trace != null || renderer.PresentCount != s.TraceFrame - 2) return;
                var tcs = new TaskCompletionSource<DiagnosticCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                string dir = Path.Combine(s.Out, $"trace_{s.TraceFrame}");
                Directory.CreateDirectory(dir);
                if (renderer.RequestDiagnosticCapture(dir, r => tcs.TrySetResult(r))) trace = tcs.Task;
            }
            if (s.TraceFrame >= 0 && s.TraceFrame - 2 < renderer.PresentCount)
                Log.Warn($"[replay] --trace-draws={s.TraceFrame}: the trace needs the frame before it in the dump (first replayable trace frame is {renderer.PresentCount + 2})");
            MaybeRequestTrace();
            while (reader.Next())
            {
                if (closing?.IsSet == true) break;
                switch (reader.Kind)
                {
                    case GpuDumpRecord.Memory:
                        reader.Data.CopyTo(GuestMemory.Span(reader.Address, reader.Data.Length)); // marks the pages written
                        memBytes += reader.Data.Length;
                        continue;
                    case GpuDumpRecord.Idle:
                        if (s.Idle) renderer.Idle();
                        continue;
                    case GpuDumpRecord.BackendState:
                        if (s.ImportState) renderer.ImportDumpState(reader.Data);
                        continue;
                }
                var cmd = reader.Command!;
                commands++;
                switch (cmd)
                {
                    case DrawCommand:
                        frameDraws++;
                        draws++;
                        if (s.DrawLimit >= 0 && renderer.PresentCount + 1 == s.LimitFrame && frameDraws > s.DrawLimit) { skipped++; continue; }
                        renderer.Execute(cmd);
                        break;
                    case SignalCommand sig:
                        if (!renderer.DeferSignal(sig.Signal)) sig.Signal();
                        break;
                    default:
                        renderer.Execute(cmd);
                        break;
                }
                if (cmd is not PresentCommand) continue;
                frames++;
                if (frameDraws > 0 && s.DrawLimit >= 0 && renderer.PresentCount == s.LimitFrame)
                    Log.Info($"[replay] frame {s.LimitFrame}: executed {Math.Min(frameDraws, s.DrawLimit)} of {frameDraws} draws (--draw-limit)");
                frameDraws = 0;
                if (s.Speed > 0)
                {
                    // recorded pace: present N at its recorded time (relative to the first present)
                    if (firstMicros < 0) { firstMicros = reader.PresentInfo.Micros; wall.Restart(); }
                    double due = (reader.PresentInfo.Micros - firstMicros) / 1e6 / s.Speed;
                    double ahead = due - wall.Elapsed.TotalSeconds;
                    if (ahead > 0.001) Thread.Sleep(TimeSpan.FromSeconds(Math.Min(ahead, 1)));
                }
                if (renderer.PresentCount >= s.StopFrame) break;
                MaybeRequestTrace();
            }
            // retire every frame in flight: the last dumps are written when their frame completes
            renderer.Execute(new FinishCommand());
            if (trace != null)
            {
                if (trace.Wait(120000)) Log.Info($"[replay] traced frame {s.TraceFrame}: {trace.Result.Draws} draws, {trace.Result.Targets} targets, {trace.Result.Textures} textures -> {trace.Result.Dir}");
                else Log.Warn("[replay] the trace did not complete");
            }
            else if (s.TraceFrame >= 0) Log.Warn($"[replay] frame {s.TraceFrame} was not traced (not in the dump?)");
            double sec = wall.Elapsed.TotalSeconds;
            Log.Info($"[replay] done: {frames} frames (presents {ro.FirstPresent + 1}-{renderer.PresentCount}), {commands} commands, {draws} draws ({skipped} skipped), " +
                     $"{memBytes / 1048576.0:F1} MB guest memory written, {sec:F1} s ({frames / Math.Max(sec, 1e-3):F1} fps)");
            if (renderer.Report() is { } rep) foreach (var line in rep.Split('\n')) if (line.Length > 0) Log.Info(line);
            return 0;
        }
        finally
        {
            _renderer = null;
            renderer.Dispose();
        }
    }

    // =========================================================== frame comparison

    /// <summary>Compares frame_N.png of the replay with the same frames of a reference run: compare.txt (one line per
    /// frame: identical pixels, pixels off by more than 8, mean and max channel difference) and diff_N.png (differences x8).</summary>
    static void Compare(string outDir, string refDir)
    {
        var sb = new StringBuilder("# frame identical% off>8% meanAbs maxAbs\n");
        int n = 0, identical = 0;
        double worstMean = 0;
        string worst = "-";
        foreach (var f in Directory.GetFiles(outDir, "frame_*.png").Order())
        {
            string name = Path.GetFileName(f), other = Path.Combine(refDir, name);
            if (!File.Exists(other)) continue;
            var (a, aw, ah) = PngReader.Read(f);
            var (b, bw, bh) = PngReader.Read(other);
            n++;
            if (aw != bw || ah != bh) { sb.AppendLine($"{name} size {aw}x{ah} vs {bw}x{bh}"); continue; }
            long same = 0, off = 0, sum = 0;
            int max = 0;
            var diff = new byte[a.Length];
            for (int i = 0; i < a.Length; i += 4)
            {
                int d = 0;
                for (int c = 0; c < 3; c++)
                {
                    int x = Math.Abs(a[i + c] - b[i + c]);
                    sum += x;
                    d = Math.Max(d, x);
                    diff[i + c] = (byte)Math.Min(255, x * 8);
                }
                diff[i + 3] = 255;
                if (d == 0) same++;
                if (d > 8) off++;
                max = Math.Max(max, d);
            }
            long px = a.Length / 4;
            double mean = sum / (3.0 * px);
            if (same == px) identical++;
            else Png.Write(Path.Combine(outDir, "diff_" + name[6..]), diff, aw, ah);
            if (mean > worstMean) { worstMean = mean; worst = name; }
            sb.AppendLine($"{name} {100.0 * same / px:F3} {100.0 * off / px:F3} {mean:F4} {max}");
        }
        string summary = $"COMPARE {n} frames against {refDir}: {identical} pixel-identical, worst mean difference {worstMean:F4} ({worst})";
        sb.Insert(0, "# " + summary + "\n");
        File.WriteAllText(Path.Combine(outDir, "compare.txt"), sb.ToString());
        Log.Info(summary);
        Console.WriteLine(summary);
    }
}

/// <summary>Minimal PNG reader for 8-bit RGB/RGBA non-interlaced images (the frame dumps).</summary>
static class PngReader
{
    public static (byte[] Rgba, int W, int H) Read(string path)
    {
        var data = File.ReadAllBytes(path);
        int p = 8, w = 0, h = 0, colorType = 6;
        using var idat = new MemoryStream();
        while (p + 8 <= data.Length)
        {
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p));
            string type = Encoding.ASCII.GetString(data, p + 4, 4);
            var body = data.AsSpan(p + 8, len);
            if (type == "IHDR")
            {
                w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(body);
                h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                if (body[8] != 8 || body[12] != 0 || body[9] is not (2 or 6)) throw new NotSupportedException($"{path}: only 8-bit RGB/RGBA PNGs");
                colorType = body[9];
            }
            else if (type == "IDAT") idat.Write(body);
            else if (type == "IEND") break;
            p += 12 + len;
        }
        int bpp = colorType == 6 ? 4 : 3, stride = w * bpp;
        var raw = new byte[(stride + 1) * h];
        idat.Position = 0;
        using (var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress)) z.ReadExactly(raw);
        var img = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            byte f = raw[y * (stride + 1)];
            var src = raw.AsSpan(y * (stride + 1) + 1, stride);
            var dst = img.AsSpan(y * stride, stride);
            for (int x = 0; x < stride; x++)
            {
                int a = x >= bpp ? dst[x - bpp] : 0, b = y > 0 ? img[(y - 1) * stride + x] : 0, c = x >= bpp && y > 0 ? img[(y - 1) * stride + x - bpp] : 0;
                int v = f switch
                {
                    0 => src[x],
                    1 => src[x] + a,
                    2 => src[x] + b,
                    3 => src[x] + (a + b) / 2,
                    4 => src[x] + Paeth(a, b, c),
                    _ => throw new InvalidDataException($"{path}: bad PNG filter {f}"),
                };
                dst[x] = (byte)v;
            }
        }
        if (bpp == 4) return (img, w, h);
        var rgba = new byte[w * h * 4];
        for (int i = 0, j = 0; i < img.Length; i += 3, j += 4) { rgba[j] = img[i]; rgba[j + 1] = img[i + 1]; rgba[j + 2] = img[i + 2]; rgba[j + 3] = 255; }
        return (rgba, w, h);

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}
