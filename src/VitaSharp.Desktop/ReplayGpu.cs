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
        Console.WriteLine("  replay-gpu <dump.vsgd> [options]     replay a GPU dump (Ctrl+F8 / --gpu-dump-at=F1:F2) without the game");
        Console.WriteLine("    --out=<dir>            frames, sheets, log (default <dump>_replay next to the dump)");
        Console.WriteLine("    --frame=N              stop after frame N (renderer present number, as in frame_N.png)");
        Console.WriteLine("    --dump-every=K --dump-from=F   which frames to write as PNG (default every frame)");
        Console.WriteLine("    --trace-draws=N        full capture of the draws behind frame_N.png into <out>/trace_N: draws.txt, shaders/, targets/, textures/");
        Console.WriteLine("    --draw-limit=N[@F]     execute only the first N draws behind frame_F.png (default --frame), to bisect a bad draw");
        Console.WriteLine("                           (frames_index.txt: which draws of the dump each present shows; games that run ahead of their");
        Console.WriteLine("                           display queue issue them one or two presents earlier)");
        Console.WriteLine("    --msaa=native|ss|off   VITASHARP_MSAA; --debug-fs=<hash>:<expr> VITASHARP_DEBUG_FS; --debug-tex0=<mode>");
        Console.WriteLine("    --env=KEY=VALUE        any renderer/shader environment switch (repeatable); --no-recorded-env ignores the recorded ones");
        Console.WriteLine("    --res-scale=2|3|4|0.75|0.5  internal resolution (VITASHARP_RES_SCALE), as in run/test");
        Console.WriteLine("    --compare=<dir>        compare the written frames with frame_N.png of another run (compare.txt, diff_N.png)");
        Console.WriteLine("    --window [--hold] [--speed=X|--fast]  show it in a window at the recorded pace (X times), keep it open at the end");
        Console.WriteLine("    --shader-cache=<dir> --async-shaders=on|off (default off) --no-idle --no-targets --debug-mode");
        Console.WriteLine("  png-diff <dirA> <dirB> [pattern]     compare same-named PNGs of two folders (e.g. two traces' targets/): <dirA>/compare.txt, diff_*.png");
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


    static void ApplyEnvironment(GpuDumpHeader header, string[] options)
    {
        var set = options.Contains("--no-recorded-env") ? new Dictionary<string, string>() : GpuDumpPlayer.RecordedEnvironment(header);
        if (Opt(options, "--msaa") is { } msaa) set[EnvVars.Msaa] = msaa;
        if (Opt(options, "--gpu-profile") is { } gp) set[EnvVars.GpuProfile] = gp;
        if (Opt(options, "--debug-fs") is { } dfs) set[EnvVars.DebugFs] = dfs;
        if (Opt(options, "--debug-tex0") is { } dt) set[EnvVars.DebugTex0] = dt;
        // --env= is first-match-wins like every other option (and wins over the recorded switches)
        var given = new HashSet<string>();
        foreach (var e in options.Where(o => o.StartsWith("--env=")))
        {
            var kv = e["--env=".Length..];
            int eq = kv.IndexOf('=');
            if (eq > 0 && given.Add(kv[..eq])) set[kv[..eq]] = kv[(eq + 1)..];
        }
        // --res-scale= as in run/test, where the option also wins over VITASHARP_RES_SCALE
        if (Opt(options, "--res-scale") is { } rs) set[EnvVars.ResScale] = rs;
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
        /// <summary>Which draws produce each present (built when --trace-draws or --draw-limit is given).</summary>
        public GpuDumpFrameIndex? Index;
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
        if (s.TraceFrame >= 0 || s.DrawLimit >= 0)
        {
            // which draws of the dump each present shows: a game running ahead of its display queue draws a frame one
            // or two presents before it is shown, and the limit / trace must act on the draws behind frame_F.png
            s.Index = GpuDumpFrameIndex.Build(path);
            File.WriteAllText(Path.Combine(s.Out, "frames_index.txt"), s.Index.Describe());
            Log.Info($"[replay] frame index: {s.Index.Frames.Count} presents, draws ahead of their display: {s.Index.LagSummary()} ({s.Index.Seconds:F1} s) -> frames_index.txt");
        }

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
            long firstMicros = -1;
            Task<DiagnosticCaptureResult>? trace = null;
            // the draws behind the limited / traced present (GpuDumpFrameIndex): skipped past the limit, traced from
            // their first scene's begin to their last scene's end, wherever the presents fall around them
            var limit = s.DrawLimit >= 0 ? FrameDraws(s, s.LimitFrame, "--draw-limit") : null;
            var traced = s.TraceFrame >= 0 ? FrameDraws(s, s.TraceFrame, "--trace-draws") : null;
            long skipFrom = limit != null ? limit.FirstDraw + s.DrawLimit : long.MaxValue, skipTo = limit?.LastDraw ?? -1;
            bool tracing = false;
            void BeginTrace()
            {
                if (traced == null || tracing || trace != null) return;
                var tcs = new TaskCompletionSource<DiagnosticCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                string where = traced.Mapped ? $"issued between presents #{traced.ScenePresent} and #{traced.ScenePresent + 1}" : $"between presents #{s.TraceFrame - 1} and #{s.TraceFrame}";
                string title = $"the {traced.Draws} draws behind present #{s.TraceFrame} (draws {traced.FirstDraw}-{traced.LastDraw} of the dump, {where})";
                tracing = renderer.BeginDrawTrace(Path.Combine(s.Out, $"trace_{s.TraceFrame}"), title, s.TraceFrame, r => tcs.TrySetResult(r));
                if (tracing) trace = tcs.Task;
            }
            void EndTrace()
            {
                if (!tracing) return;
                renderer.EndDrawTrace();
                tracing = false;
            }
            var st = GpuDumpPlayer.Play(reader, renderer, s.Idle, s.ImportState,
                executeDraw: (draw, _) =>
                {
                    if (traced != null && draw == traced.FirstDraw) BeginTrace(); // no scene begin before it (dump cut)
                    return draw < skipFrom || draw > skipTo;
                },
                beforeCommand: (cmd, draws) =>
                {
                    if (traced != null && cmd is BeginSceneCommand && draws + 1 == traced.FirstDraw) BeginTrace();
                },
                afterCommand: (cmd, draws) =>
                {
                    if (tracing && traced != null && draws >= traced.LastDraw && cmd is EndSceneCommand or PresentCommand or FinishCommand) EndTrace();
                },
                presented: frameDraws =>
            {
                if (s.Speed > 0)
                {
                    // recorded pace: present N at its recorded time (relative to the first present)
                    if (firstMicros < 0) { firstMicros = reader.PresentInfo.Micros; wall.Restart(); }
                    double due = (reader.PresentInfo.Micros - firstMicros) / 1e6 / s.Speed;
                    double ahead = due - wall.Elapsed.TotalSeconds;
                    if (ahead > 0.001) Thread.Sleep(TimeSpan.FromSeconds(Math.Min(ahead, 1)));
                }
                if (renderer.PresentCount >= s.StopFrame) return false;
                return true;
            }, cancel: () => closing?.IsSet == true);
            long frames = st.Frames;
            EndTrace(); // a trace still open (the dump ends inside the traced scene)
            // retire every frame in flight: the last dumps are written when their frame completes
            renderer.Execute(new FinishCommand());
            if (limit != null) Log.Info($"[replay] present #{s.LimitFrame}: executed {Math.Min(limit.Draws, s.DrawLimit)} of {limit.Draws} draws behind it (--draw-limit: {st.Skipped} skipped)");
            if (trace != null)
            {
                if (trace.Wait(120000)) Log.Info($"[replay] traced present #{s.TraceFrame}: {trace.Result.Draws} draws, {trace.Result.Targets} targets, {trace.Result.Textures} textures -> {trace.Result.Dir}");
                else Log.Warn("[replay] the trace did not complete");
            }
            else if (s.TraceFrame >= 0) Log.Warn($"[replay] present #{s.TraceFrame} was not traced (its draws {traced?.FirstDraw}-{traced?.LastDraw} were not replayed: --frame too low, or not in the dump)");
            double sec = wall.Elapsed.TotalSeconds;
            Log.Info($"[replay] done: {frames} frames (presents {ro.FirstPresent + 1}-{renderer.PresentCount}), {st.Commands} commands, {st.Draws} draws ({st.Skipped} skipped), " +
                     $"{st.MemoryBytes / 1048576.0:F1} MB guest memory written, {sec:F1} s ({frames / Math.Max(sec, 1e-3):F1} fps)");
            if (renderer.Report() is { } rep) foreach (var line in rep.Split('\n')) if (line.Length > 0) Log.Info(line);
            return 0;
        }
        finally
        {
            _renderer = null;
            renderer.Dispose();
        }
    }

    /// <summary>The draws behind present <paramref name="present"/> (<see cref="GpuDumpFrameIndex"/>), logged; the draws
    /// between the previous present and it when no scene rendered the shown buffer (a CPU framebuffer, or drawn before
    /// the dump starts). Null when the present is not in the dump.</summary>
    static GpuDumpFrameIndex.Frame? FrameDraws(Settings s, long present, string option)
    {
        var f = s.Index?[present];
        if (f == null)
        {
            Log.Warn($"[replay] {option}: present #{present} is not in the dump (presents #{s.Index?.FirstPresent}-#{s.Index?.LastPresent})");
            return null;
        }
        if (f.Mapped)
        {
            Log.Info($"[replay] {option}: present #{present} shows draws {f.FirstDraw}-{f.LastDraw} of the dump ({f.Draws} draws; the last scene into 0x{f.Address:X8} " +
                     $"was issued between presents #{f.ScenePresent} and #{f.ScenePresent + 1}, {f.Lag} present(s) before it was shown)");
            return f;
        }
        Log.Warn($"[replay] {option}: no scene in the dump rendered the buffer 0x{f.Address:X8} shown at present #{present} (drawn by the CPU, or before the dump " +
                 $"starts): using the {Math.Max(0, f.IntervalLast - f.IntervalFirst + 1)} draws between presents #{present - 1} and #{present}");
        return new GpuDumpFrameIndex.Frame { Present = f.Present, Address = f.Address, FirstDraw = f.IntervalFirst, LastDraw = f.IntervalLast, IntervalFirst = f.IntervalFirst, IntervalLast = f.IntervalLast };
    }

    // =========================================================== frame comparison

    /// <summary>Compares frame_N.png of the replay with the same frames of a reference run: compare.txt (one line per
    /// frame: identical pixels, pixels off by more than 8, mean and max channel difference) and diff_N.png (differences x8).</summary>
    internal static void Compare(string outDir, string refDir, string pattern = "frame_*.png")
    {
        var sb = new StringBuilder("# frame identical% off>8% meanAbs maxAbs\n");
        int n = 0, identical = 0;
        double worstMean = 0;
        string worst = "-";
        foreach (var f in Directory.GetFiles(outDir, pattern).Order())
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
            else Png.Write(Path.Combine(outDir, "diff_" + (name.StartsWith("frame_") ? name[6..] : name)), diff, aw, ah);
            if (mean > worstMean) { worstMean = mean; worst = name; }
            sb.AppendLine(FormattableString.Invariant($"{name} {100.0 * same / px:F3} {100.0 * off / px:F3} {mean:F4} {max}"));
        }
        string summary = FormattableString.Invariant($"COMPARE {n} frames against {refDir}: {identical} pixel-identical, worst mean difference {worstMean:F4} ({worst})");
        sb.Insert(0, "# " + summary + "\n");
        File.WriteAllText(Path.Combine(outDir, "compare.txt"), sb.ToString());
        Log.Info(summary);
        Console.WriteLine(summary);
    }
}
