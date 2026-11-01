#if VITASHARP_DEVELOPER
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan.Developer;

/// <summary>
/// One GPU bench run (developer builds): a fixture (a GPU dump cut after its target frame, <see cref="GpuBench.Cut"/>),
/// the variant to apply and the bench parameters. Text form: <c>key=value</c> pairs separated by ';' (the Android
/// launch extra <c>bench=...</c>, <c>vitasharp-perf bench --child</c>). See tools/dev/README.md.
/// </summary>
public sealed class GpuBenchSpec
{
    /// <summary>Folder of fixtures/, variants/, results/ and shadercache/ (Android: files/bench).</summary>
    public string Root = "";
    public string Fixture = "";
    public string Variant = "base";
    /// <summary>Timed repetitions of the target frame (after <see cref="Warm"/> untimed ones).</summary>
    public int Reps = 5, Warm = 2;
    /// <summary>Draw (1-based serial in the target frame) to measure: "without" alternates the frame with and without it,
    /// "only" alternates its scene with only that draw and with no draw.</summary>
    public int Draw;
    public string DrawMode = "without";
    /// <summary>Run id: results go to results/RUN.tsv (appended).</summary>
    public string Run = "bench";
    /// <summary>Everything else: render state (depthload, depthstore, barrier, msaa, fetchmerge, fetchsplit, gpuprofile, env.KEY).</summary>
    public readonly Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);

    public static GpuBenchSpec Parse(string text, string root)
    {
        var s = new GpuBenchSpec { Root = root };
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            string k = eq > 0 ? part[..eq].Trim() : part, v = eq > 0 ? part[(eq + 1)..].Trim() : "1";
            switch (k.ToLowerInvariant())
            {
                case "fixture": s.Fixture = v; break;
                case "variant": s.Variant = v; break;
                case "reps": s.Reps = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "warm": s.Warm = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "draw": s.Draw = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "drawmode": s.DrawMode = v; break;
                case "run": s.Run = v; break;
                case "root": s.Root = v; break;
                default: s.Params[k] = v; break;
            }
        }
        if (s.Fixture.Length == 0) throw new ArgumentException("bench: fixture=NAME missing");
        return s;
    }

    public string FixturePath => Path.IsPathRooted(Fixture) ? Fixture : Path.Combine(Root, "fixtures", Fixture.EndsWith(GpuDumpFormat.Extension) ? Fixture : Fixture + GpuDumpFormat.Extension);
    public string VariantDir => Path.Combine(Root, "variants", Variant);
    public string FixtureName => Path.GetFileNameWithoutExtension(FixturePath);
}

/// <summary>
/// GPU bench (developer builds): replays a fixture's target frame several times on a headless renderer and reports the
/// GPU time of the frame and of each of its scenes (render passes: tile traffic on Mali is per pass) from Vulkan
/// timestamps. A variant is a folder variants/NAME with replacement shaders (HASH_HINT.vert|frag, GLSL or .spv) and
/// optional params.txt lines (key=value render state); nothing in it needs a rebuild. Static renderer switches (msaa,
/// fetchmerge) are environment variables read once per process, so every run is its own process.
/// </summary>
public static class GpuBench
{
    /// <summary>One timed repetition: frame GPU ms (sum of scenes) and the ms of every scene.</summary>
    public sealed record Rep(string Config, int Index, double FrameMs, double[] SceneMs);

    public sealed class Result
    {
        public string Fixture = "", Variant = "";
        public readonly List<Rep> Reps = new();
        public readonly List<string> Scenes = new();
        public double WallSeconds;
    }

    /// <summary>Header keys of a fixture.</summary>
    public const string KeyFrame = "bench.frame", KeyName = "bench.name";

    /// <summary>Bench parameters that are renderer environment switches (one process per run).</summary>
    static readonly Dictionary<string, string> EnvParams = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msaa"] = EnvVars.Msaa, ["fetchmerge"] = EnvVars.FetchPassMerge, ["fetchsplit"] = EnvVars.FetchSplit,
        ["msaafetch"] = EnvVars.MsaaFetch, ["scenebreaks"] = EnvVars.SceneBreaks, ["gpuquirks"] = EnvVars.GpuQuirks,
    };

    /// <summary>A run of the spec's mode: mode=flicker (<see cref="Flicker"/>), else the timing bench (<see cref="Run"/>).</summary>
    public static void Execute(GpuBenchSpec spec, Action<string> log)
    {
        if (spec.Params.GetValueOrDefault("mode") == "flicker") Flicker(spec, log);
        else Run(spec, log);
    }

    /// <param name="done">called with the renderer after the last repetition, before it is disposed</param>

    public static Result Run(GpuBenchSpec spec, Action<string> log, Action<VulkanRenderer>? done = null)
    {
        var wall = Stopwatch.StartNew();
        string path = spec.FixturePath;
        Prepare(spec, log, out var header, out var ro);
        long target = header.Long(KeyFrame, -1);
        if (target < 0) using (var r = new GpuDumpReader(path)) { while (r.Next()) if (r.Kind == GpuDumpRecord.Present) target++; }
        var res = new Result { Fixture = spec.FixtureName, Variant = spec.Variant };
        using var memory = new GuestMemory();
        var renderer = new VulkanRenderer(ro);
        try
        {
            if (header["title-id"] is { Length: > 0 } title) renderer.BeginTitle(title);
            var sceneOfDraw = new List<int>();
            if (spec.Draw > 0) spec.Warm = Math.Max(1, spec.Warm); // the first pass maps draws to scenes
            int total = spec.Warm + spec.Reps * (spec.Draw > 0 ? 2 : 1);

            for (int rep = 0; rep < total; rep++)
            {
                bool timed = rep >= spec.Warm;
                // draw benches alternate: even = reference (whole frame / scene without draws), odd = with the change
                bool alt = spec.Draw > 0 && (rep - spec.Warm) % 2 == 1;
                string config = spec.Draw <= 0 ? "frame" : spec.DrawMode == "only" ? (alt ? $"only{spec.Draw}" : $"none{spec.Draw}") : (alt ? $"without{spec.Draw}" : "frame");
                Func<int, int, bool>? keep = spec.Draw <= 0 ? null : spec.DrawMode == "only"
                    ? (draw, scene) => sceneOfDraw.Count < spec.Draw || scene != sceneOfDraw[spec.Draw - 1] || (alt && draw == spec.Draw)
                    : (draw, _) => !(alt && draw == spec.Draw);
                var scenes = PlayTarget(path, renderer, target, rep == 0, keep, rep == 0 ? sceneOfDraw : null);
                if (rep == 0)
                {
                    foreach (var s in scenes)
                    {
                        int first = sceneOfDraw.IndexOf(s.Index) + 1, count = sceneOfDraw.Count(x => x == s.Index);
                        res.Scenes.Add($"s{s.Index} {s.Width}x{s.Height} rt 0x{s.RenderTarget:X8} draws {(count > 0 ? $"{first}-{first + count - 1}" : "-")}");
                    }
                    foreach (var line in res.Scenes) log($"[bench]   {line}");
                }
                if (!timed) continue;
                var ms = scenes.Select(s => s.Ms).ToArray();
                var repRes = new Rep(config, rep - spec.Warm, ms.Sum(), ms);
                res.Reps.Add(repRes);
                log(FormattableString.Invariant($"BENCH {spec.FixtureName} {spec.Variant} {config} rep {repRes.Index}: frame {repRes.FrameMs:F3} ms; scenes {string.Join(' ', ms.Select(m => m.ToString("F3", CultureInfo.InvariantCulture)))}"));
            }
            done?.Invoke(renderer);

        }
        finally { renderer.Dispose(); GpuBenchState.Reset(); }
        res.WallSeconds = wall.Elapsed.TotalSeconds;
        WriteResults(spec, res);
        log($"[bench] {spec.FixtureName} {spec.Variant}: {res.Reps.Count} timed reps in {res.WallSeconds:F1} s");
        return res;
    }

    /// <summary>
    /// Replays the fixture up to its target frame, executing the frames before it only on the first pass (later passes
    /// only restore the guest memory they wrote), then the target frame with scene timing. Returns its scenes.
    /// </summary>
    static List<VulkanRenderer.BenchScene> PlayTarget(string path, VulkanRenderer renderer, long target, bool first, Func<int, int, bool>? keep, List<int>? sceneOfDraw)
    {
        using var reader = new GpuDumpReader(path);
        long frame = 0;
        int draws = 0, scene = -1;
        GpuBenchState.Timing = target == 0;
        renderer.DevReadSceneTimes(); // drop anything left over
        while (reader.Next())
        {
            switch (reader.Kind)
            {
                case GpuDumpRecord.Memory:
                    reader.Data.CopyTo(GuestMemory.Span(reader.Address, reader.Data.Length));
                    continue;
                case GpuDumpRecord.Idle:
                    if (first || frame == target) renderer.Idle();
                    continue;
                case GpuDumpRecord.BackendState:
                    if (first) renderer.ImportDumpState(reader.Data);
                    continue;
            }
            var cmd = reader.Command!;
            bool inTarget = frame == target;
            if (!first && !inTarget)
            {
                if (cmd is PresentCommand && ++frame == target) GpuBenchState.Timing = true;
                continue;
            }
            switch (cmd)
            {
                case BeginSceneCommand:
                    if (inTarget) scene++;
                    renderer.Execute(cmd);
                    break;
                case DrawCommand:
                    if (inTarget)
                    {
                        draws++;
                        sceneOfDraw?.Add(scene);
                        if (keep != null && !keep(draws, scene)) continue;
                    }
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
            if (inTarget) break;
            if (++frame == target)
            {
                renderer.Execute(new FinishCommand()); // the frames before are not part of the measurement
                renderer.DevReadSceneTimes();
                GpuBenchState.Timing = true;
            }
        }
        renderer.Execute(new FinishCommand());
        GpuBenchState.Timing = false;
        return renderer.DevReadSceneTimes();
    }

    /// <summary>The run's parameters (the variant's params.txt, then the spec's), applied: renderer environment
    /// switches, <see cref="GpuBenchState"/>, and the renderer options.</summary>
    static Dictionary<string, string> Prepare(GpuBenchSpec spec, Action<string> log, out GpuDumpHeader header, out VulkanRendererOptions ro, bool async = false)
    {
        string path = spec.FixturePath;
        if (!File.Exists(path)) throw new FileNotFoundException($"bench fixture {path} not found");
        // the variant's params.txt, then the run's own parameters
        var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string paramsFile = Path.Combine(spec.VariantDir, "params.txt");
        if (File.Exists(paramsFile))
            foreach (var line in File.ReadAllLines(paramsFile))
                if (line.Trim() is { Length: > 0 } l && !l.StartsWith('#') && l.IndexOf('=') is var eq and > 0) p[l[..eq].Trim()] = l[(eq + 1)..].Trim();
        foreach (var (k, v) in spec.Params) p[k] = v;

        using (var r = new GpuDumpReader(path)) header = r.Header;
        var env = GpuDumpPlayer.RecordedEnvironment(header);
        env[EnvVars.GpuProfile] = p.GetValueOrDefault("gpuprofile") ?? Environment.GetEnvironmentVariable(EnvVars.GpuProfile) ?? env.GetValueOrDefault(EnvVars.GpuProfile) ?? "";
        foreach (var (k, v) in p)
        {
            if (EnvParams.TryGetValue(k, out var name)) env[name] = v;
            else if (k.StartsWith("env.", StringComparison.OrdinalIgnoreCase)) env[k[4..]] = v;
        }
        foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v.Length == 0 ? null : v);

        GpuBenchState.Reset();
        GpuBenchState.ShaderDir = Directory.Exists(spec.VariantDir) && Directory.EnumerateFiles(spec.VariantDir).Any(f => !f.EndsWith(".txt")) ? spec.VariantDir : null;
        GpuBenchState.DepthLoad = p.GetValueOrDefault("depthload") switch { "clear" => AttachmentLoadOp.Clear, "dontcare" => AttachmentLoadOp.DontCare, _ => null };
        GpuBenchState.DepthStore = p.GetValueOrDefault("depthstore") switch { "store" => AttachmentStoreOp.Store, "dontcare" => AttachmentStoreOp.DontCare, _ => null };
        GpuBenchState.FullBarriers = p.GetValueOrDefault("barrier") == "full";
        log($"[bench] {spec.FixtureName} variant {spec.Variant}: " + string.Join(' ', p.Select(kv => $"{kv.Key}={kv.Value}"))
            + (GpuBenchState.ShaderDir != null ? $" shaders from {GpuBenchState.ShaderDir}" : "") + "; env " + string.Join(' ', env.Select(kv => $"{kv.Key}={kv.Value}")));

        ro = new VulkanRendererOptions
        {
            ShaderCacheDir = Path.Combine(spec.Root, "shadercache"),
            VSync = false,
            AsyncShaders = (p.GetValueOrDefault("async") ?? (async ? "1" : "0")) == "1",
            AsyncPipelines = (p.GetValueOrDefault("async") ?? (async ? "1" : "0")) == "1",
            FirstPresent = header.Long("first-present"),
            GpuProfile = env[EnvVars.GpuProfile] is { Length: > 0 } gp ? gp : null,
        };
        return p;
    }

    /// <summary>
    /// Flicker test (spec mode=flicker): replays every frame of the fixture (a whole GPU dump) in a loop, reads back each
    /// presented frame as a small picture and runs <see cref="FlickerDetector"/> over them, to reproduce frames that only
    /// go wrong on the device at speed. Parameters: loops (default 3), speed=real (the recorded present times) or max
    /// (as fast as the GPU goes, default), threshold (6), skipscene=K (no draws in scene K of every frame), drawlimit=N
    /// (only the first N draws of every frame), plus the render-state parameters of <see cref="Run"/>. Flagged frames are
    /// saved with their neighbours to results/RUN_flicker/; one row per run goes to results/RUN.flicker.tsv.
    /// </summary>
    public static FlickerDetector Flicker(GpuBenchSpec spec, Action<string> log)
    {
        var wall = Stopwatch.StartNew();
        string path = spec.FixturePath;
        var p = Prepare(spec, log, out var header, out var ro, async: true);
        int loops = int.Parse(p.GetValueOrDefault("loops") ?? "3", CultureInfo.InvariantCulture);
        bool real = p.GetValueOrDefault("speed") == "real";
        int skipScene = int.Parse(p.GetValueOrDefault("skipscene") ?? "-1", CultureInfo.InvariantCulture);
        int drawLimit = int.Parse(p.GetValueOrDefault("drawlimit") ?? "0", CultureInfo.InvariantCulture);
        var det = new FlickerDetector(double.Parse(p.GetValueOrDefault("threshold") ?? "6", CultureInfo.InvariantCulture));
        string saveDir = Path.Combine(spec.Root, "results", spec.Run + "_flicker", $"{spec.FixtureName}_{spec.Variant}");
        var recent = new Queue<(long, byte[])>();
        int saved = 0;
        ro.FrameSink = (frame, rgba, w, h) =>
        {
            recent.Enqueue((frame, rgba));
            if (recent.Count > 3) recent.Dequeue();
            if (det.Add(frame, rgba, w, h) is not { } flag) return;
            log($"[bench] {det.Describe(flag)}");
            if (saved++ >= 12) return;
            Directory.CreateDirectory(saveDir);
            foreach (var (n, px) in recent) Png.Write(Path.Combine(saveDir, $"f{flag.Frame}_{n}.png"), px, w, h);
        };
        long frames = 0;
        using var memory = new GuestMemory();
        var renderer = new VulkanRenderer(ro);
        try
        {
            if (header["title-id"] is { Length: > 0 } title) renderer.BeginTitle(title);
            for (int loop = 0; loop < loops; loop++)
            {
                using var reader = new GpuDumpReader(path);
                var clock = Stopwatch.StartNew();
                long t0 = -1;
                int draws = 0, scene = -1;
                while (reader.Next())
                {
                    switch (reader.Kind)
                    {
                        case GpuDumpRecord.Memory: reader.Data.CopyTo(GuestMemory.Span(reader.Address, reader.Data.Length)); continue;
                        case GpuDumpRecord.Idle: renderer.Idle(); continue;
                        case GpuDumpRecord.BackendState: if (loop == 0) renderer.ImportDumpState(reader.Data); continue;
                    }
                    var cmd = reader.Command!;
                    switch (cmd)
                    {
                        case BeginSceneCommand: scene++; break;
                        case DrawCommand:
                            draws++;
                            if (scene == skipScene || (drawLimit > 0 && draws > drawLimit)) continue;
                            break;
                        case SignalCommand sig:
                            if (!renderer.DeferSignal(sig.Signal)) sig.Signal();
                            continue;
                        case PresentCommand when real:
                            long us = reader.PresentInfo.Micros;
                            if (t0 < 0) t0 = us;
                            long ahead = (us - t0) - (long)(clock.Elapsed.TotalMilliseconds * 1000);
                            if (ahead > 1000) Thread.Sleep((int)(ahead / 1000));
                            break;
                    }
                    renderer.Execute(cmd);
                    if (cmd is PresentCommand) { frames++; draws = 0; scene = -1; }
                }
            }
            renderer.Execute(new FinishCommand());
        }
        finally { renderer.Dispose(); GpuBenchState.Reset(); }
        double fps = frames / wall.Elapsed.TotalSeconds;
        log(FormattableString.Invariant($"BENCH {spec.FixtureName} {spec.Variant} flicker: {det.Summary}; {frames} frames at {fps:F1} fps ({(real ? "recorded pace" : "max speed")})"));
        Directory.CreateDirectory(Path.Combine(spec.Root, "results"));
        File.AppendAllText(Path.Combine(spec.Root, "results", spec.Run + ".flicker.tsv"),
            FormattableString.Invariant($"{spec.FixtureName}\t{spec.Variant}\t{(real ? "real" : "max")}\t{det.Frames}\t{det.Flagged.Count}\t{fps:F1}\t{string.Join(',', det.Flagged.Select(f => f.Frame))}\n"));
        return det;
    }

    /// <summary>results/RUN.tsv: fixture, variant, config, rep, frame ms, scene ms (comma separated); appended.</summary>

    static void WriteResults(GpuBenchSpec spec, Result res)
    {
        string dir = Path.Combine(spec.Root, "results");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        foreach (var r in res.Reps)
            sb.Append(CultureInfo.InvariantCulture, $"{res.Fixture}\t{res.Variant}\t{r.Config}\t{r.Index}\t{r.FrameMs:F4}\t{string.Join(',', r.SceneMs.Select(m => m.ToString("F4", CultureInfo.InvariantCulture)))}\n");
        File.AppendAllText(Path.Combine(dir, spec.Run + ".tsv"), sb.ToString());
    }

    /// <summary>
    /// Cuts a fixture out of a GPU dump: everything up to the end of frame <paramref name="frame"/> (0-based present
    /// index in the dump) with the frames before it kept as its state (guest memory, shader definitions, render
    /// targets carried by the dump's start), and the bench keys in the header. Chunks are copied as stored, so the
    /// last one may hold some records after the frame (the runner stops at the frame's present).
    /// </summary>
    public static (long Frames, long Bytes) Cut(string dump, string output, long frame, string name)
    {
        long end = -1, presents = 0;
        using (var r = new GpuDumpReader(dump))
            while (r.Next())
                if (r.Kind == GpuDumpRecord.Present && presents++ == frame) { end = r.CompressedBytesRead; break; }
        if (end < 0) throw new ArgumentException($"{dump} has {presents} frames, not {frame + 1}");
        using var inp = new FileStream(dump, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var header = GpuDumpCodec.ReadFileHeader(inp, out _);
        header[KeyFrame] = frame.ToString(CultureInfo.InvariantCulture);
        header[KeyName] = name;
        header["bench.source"] = Path.GetFileName(dump);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var outp = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        GpuDumpCodec.WriteFileHeader(outp, header);
        var buf = new byte[1 << 20];
        for (long left = end - inp.Position; left > 0;)
        {
            int n = inp.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (n <= 0) break;
            outp.Write(buf, 0, n);
            left -= n;
        }
        return (frame + 1, outp.Length);
    }
}
#endif
