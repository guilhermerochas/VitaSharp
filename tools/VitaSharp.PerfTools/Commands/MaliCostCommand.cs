using System.Diagnostics;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using VitaSharp.PerfTools.Mali;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.PerfTools.Commands;

/// <summary><c>vitasharp-perf mali-cost &lt;dump.vsgd&gt;</c>: replays a GPU dump headless and estimates its frames'
/// GPU time on a Mali GPU (<see cref="MaliCost"/>). See docs/notes/mobile-gpu.md ("Mali cost estimator").</summary>
sealed class MaliCostCommand : IPerfCommand
{
    public string Name => "mali-cost";
    public string Summary => "estimate a GPU dump's frame time on a Mali GPU with Arm's Mali Offline Compiler";

    public void PrintUsage()
    {
        Console.WriteLine("vitasharp-perf mali-cost <dump.vsgd> [options]");
        Console.WriteLine("  --gpu <core>           malioc core (default Mali-G57)");
        Console.WriteLine("  --frames a-b           present numbers to report (as frame_N.png of replay-gpu; the replay stops after b)");
        Console.WriteLine("  --fp16-whatif          also compile mediump variants (half registers; every fragment float as an upper bound)");
        Console.WriteLine("  --gpu-profile <p>      renderer device profile (default mali: the shaders the phone runs)");
        Console.WriteLine("  --shader-cache <dir>   shader cache (default <out>/shadercache); malioc results go to <dir>/malioc/<core>");
        Console.WriteLine("  --out <dir>            report folder (default <dump>_perf); mali_cost.txt");
        Console.WriteLine("  --malioc <exe>         Mali Offline Compiler (else VITASHARP_MALIOC, PATH, Arm Performance Studio)");
        Console.WriteLine("  --mhz 950 --cores 2 --gbps 8 --scale 1   GPU clock, cores, tile load/store bandwidth, calibration factor");
        Console.WriteLine("  --env KEY=VALUE        renderer/shader switch (repeat with --env=KEY=VALUE); --no-recorded-env");
    }

    public int Run(PerfArgs a)
    {
        if (a.Positional.Count != 1) throw new PerfArgsException("mali-cost needs one dump file");
        string path = Path.GetFullPath(a.Positional[0]);
        if (!File.Exists(path)) { Console.Error.WriteLine($"GPU dump {path} not found"); return 1; }
        string outDir = Path.GetFullPath(a.Get("--out") ?? Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_perf"));
        string cacheDir = Path.GetFullPath(a.Get("--shader-cache") ?? Path.Combine(outDir, "shadercache"));
        var (first, last) = a.Range("--frames");
        var settings = new MaliCost.Settings(
            Core: a.Get("--gpu") ?? "Mali-G57",
            Malioc: MaliCost.FindMalioc(a.Get("--malioc")) ?? throw new PerfArgsException("malioc not found: install Arm Performance Studio (mali_offline_compiler) or pass --malioc / VITASHARP_MALIOC"),
            CacheDir: cacheDir, OutDir: outDir,
            Mhz: a.Double("--mhz", 950), Cores: a.Double("--cores", 2), Gbps: a.Double("--gbps", 8), Scale: a.Double("--scale", 1),
            FirstFrame: first, LastFrame: last, Fp16WhatIf: a.Has("--fp16-whatif"));

        GpuDumpHeader header;
        using (var r = new GpuDumpReader(path)) header = r.Header;
        // environment switches must be in place before the renderer and shader generator types initialise
        var env = a.Has("--no-recorded-env") ? new Dictionary<string, string>() : GpuDumpPlayer.RecordedEnvironment(header);
        env[EnvVars.GpuProfile] = a.Get("--gpu-profile") ?? "mali";
        foreach (var e in Environment.GetCommandLineArgs().Where(x => x.StartsWith("--env=")).Select(x => x["--env=".Length..]).Append(a.Get("--env") ?? ""))
            if (e.IndexOf('=') is var eq and > 0) env[e[..eq]] = e[(eq + 1)..];
        foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v);
        Console.WriteLine($"{path}: {header["title-id"]} {header["title"]}; " + string.Join(' ', env.Select(kv => $"{kv.Key}={kv.Value}")));

        var cost = new MaliCost(settings);
        var ro = new VulkanRendererOptions
        {
            ShaderCacheDir = cacheDir,
            VSync = false,
            AsyncShaders = false,
            AsyncPipelines = false,
            FirstPresent = header.Long("first-present"),
            DrawCostSink = cost.Add,
        };
        var wall = Stopwatch.StartNew();
        using (var memory = new GuestMemory())
        using (var reader = new GpuDumpReader(path))
        {
            var renderer = new VulkanRenderer(ro);
            try
            {
                if (header["title-id"] is { Length: > 0 } title) renderer.BeginTitle(title);
                var st = GpuDumpPlayer.Play(reader, renderer, presented: _ => renderer.PresentCount < last);
                renderer.Execute(new FinishCommand());
                cost.Capture(renderer);
                Console.WriteLine($"replayed {st.Frames} frames, {st.Draws} draws in {wall.Elapsed.TotalSeconds:F1} s");
                if (renderer.Report() is { } rep)
                    foreach (var line in rep.Split('\n').Where(l => l.StartsWith("PERF gpu"))) Console.WriteLine(line);
            }
            finally { renderer.Dispose(); }
        }
        cost.Report();
        Log.Flush();
        return 0;
    }
}
