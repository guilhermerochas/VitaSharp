using System.Diagnostics;
using System.Globalization;
#if VITASHARP_DEVELOPER
using VitaSharp.Renderer.Vulkan.Developer;
#endif

namespace VitaSharp.PerfTools.Commands;

/// <summary><c>vitasharp-perf bench</c>: the GPU bench of the developer flavour (tools/dev/README.md). cut and table work in
/// any build; run, shaders and --child need -p:VitaSharpDeveloper=true (the renderer's bench hooks).</summary>
sealed class BenchCommand : IPerfCommand
{
    public string Name => "bench";
    public string Summary => "GPU bench: cut fixtures from GPU dumps, replay them with variants A/B, tabulate GPU ms";

    public void PrintUsage()
    {
        Console.WriteLine("vitasharp-perf bench cut <dump.vsgd> --frame I [--name N] --root DIR   fixture DIR/fixtures/N.vsgd = dump up to frame I (0-based); --frame list prints the frames");
        Console.WriteLine("vitasharp-perf bench run --root DIR --fixtures a,b [--variants base,v1] [--rounds 4] [--reps 3] [--param k=v;k=v] [--run ID]");
        Console.WriteLine("    one process per (fixture, variant, round), interleaved A/B/A/B, affinity 0x0F, below normal priority; prints the table");
        Console.WriteLine("vitasharp-perf bench shaders --root DIR --fixture a [--out DIR]   writes the fixture's translated GLSL (HASH_HINT.vert|frag) to edit as a variant");
        Console.WriteLine("vitasharp-perf bench table <results.tsv>... [--base base] [--scenes N]   median, spread and delta vs the base variant");
        Console.WriteLine("vitasharp-perf bench --child \"fixture=a;variant=v;reps=3;...\" --root DIR   one run in this process (what run starts)");
        Console.WriteLine("  parameters: reps warm draw drawmode=without|only depthload=clear|dontcare depthstore=store|dontcare barrier=full");
        Console.WriteLine("              msaa=... fetchmerge=0|1 fetchsplit=0|1 msaafetch scenebreaks gpuquirks gpuprofile env.KEY=VALUE");
    }

    public int Run(PerfArgs a)
    {
        string sub = a.Positional.Count > 0 ? a.Positional[0] : a.Has("--child") ? "child" : throw new PerfArgsException("bench needs a subcommand");
        return sub switch
        {
            "cut" => Cut(a),
            "table" => Table(a.Positional.Skip(1).ToList(), a.Get("--base") ?? "base", (int)a.Double("--scenes", 0.0001)),
            "run" => RunRounds(a),
            "shaders" => Shaders(a),
            "child" => Child(a),
            _ => throw new PerfArgsException($"bench: unknown subcommand {sub}"),
        };
    }

    static string Root(PerfArgs a) => Path.GetFullPath(a.Get("--root") ?? throw new PerfArgsException("--root DIR missing"));

    static int Cut(PerfArgs a)
    {
        if (a.Positional.Count != 2) throw new PerfArgsException("bench cut needs one dump");
        string dump = Path.GetFullPath(a.Positional[1]);
        if (a.Get("--frame") is null or "list")
        {
            using var r = new Core.Gpu.GpuDumpReader(dump);
            long i = 0, draws = 0, scenes = 0;
            while (r.Next())
            {
                if (r.Kind == Core.Gpu.GpuDumpRecord.Draw) draws++;
                else if (r.Kind == Core.Gpu.GpuDumpRecord.BeginScene) scenes++;
                else if (r.Kind == Core.Gpu.GpuDumpRecord.Present)
                {
                    Console.WriteLine($"frame {i++}: present {r.PresentInfo.Present}, {scenes} scenes, {draws} draws, {r.CompressedBytesRead / 1024} KiB into the file");
                    draws = scenes = 0;
                }
            }
            return 0;
        }
        long frame = long.Parse(a.Get("--frame")!, CultureInfo.InvariantCulture);
        string name = a.Get("--name") ?? $"{Path.GetFileNameWithoutExtension(dump)}_f{frame}";
        string outPath = Path.Combine(Root(a), "fixtures", name + Core.Gpu.GpuDumpFormat.Extension);
#if VITASHARP_DEVELOPER
        var (frames, bytes) = GpuBench.Cut(dump, outPath, frame, name);
        Console.WriteLine($"{outPath}: {frames} frames, {bytes / 1024} KiB (target frame {frame})");
        return 0;
#else
        throw new PerfArgsException($"bench cut {outPath}: build vitasharp-perf with -p:VitaSharpDeveloper=true");
#endif
    }

    static int Child(PerfArgs a)
    {
#if VITASHARP_DEVELOPER
        var spec = GpuBenchSpec.Parse(a.Get("--child") ?? throw new PerfArgsException("--child SPEC"), Root(a));
        GpuBench.Run(spec, Console.WriteLine);
        Core.Util.Log.Flush();
        return 0;
#else
        throw new PerfArgsException("bench --child: build vitasharp-perf with -p:VitaSharpDeveloper=true");
#endif
    }

    static int Shaders(PerfArgs a)
    {
#if VITASHARP_DEVELOPER
        string root = Root(a);
        string fixture = a.Get("--fixture") ?? throw new PerfArgsException("--fixture NAME missing");
        string outDir = Path.GetFullPath(a.Get("--out") ?? Path.Combine(root, "variants", "_translated", fixture));
        // a fresh shader cache: every shader is translated, so its GLSL is known
        string tmp = Path.Combine(Path.GetTempPath(), "vitasharp-bench-" + Environment.ProcessId);
        var spec = GpuBenchSpec.Parse($"fixture={fixture};reps=0;warm=1;run=shaders", root);
        spec.Root = tmp;
        Directory.CreateDirectory(Path.Combine(tmp, "fixtures"));
        File.Copy(GpuBenchSpec.Parse($"fixture={fixture}", root).FixturePath, spec.FixturePath, true);
        int n = 0;
        GpuBench.Run(spec, Console.WriteLine, r => n = r.DevDumpShaders(outDir));
        Directory.Delete(tmp, true);
        Console.WriteLine($"{n} shaders in {outDir}: copy the ones to change into {Path.Combine(root, "variants", "NAME")}");
        return 0;
#else
        throw new PerfArgsException("bench shaders: build vitasharp-perf with -p:VitaSharpDeveloper=true");
#endif
    }

    /// <summary>Rounds of one child process per (fixture, variant), variants interleaved, then the table.</summary>
    static int RunRounds(PerfArgs a)
    {
        string root = Root(a);
        var fixtures = (a.Get("--fixtures") ?? throw new PerfArgsException("--fixtures a,b missing")).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var variants = (a.Get("--variants") ?? "base").Split(',', StringSplitOptions.RemoveEmptyEntries);
        int rounds = (int)a.Double("--rounds", 4), reps = (int)a.Double("--reps", 3);
        string run = a.Get("--run") ?? DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string extra = a.Get("--param") ?? "";
        string self = Environment.ProcessPath!;
        string prefix = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? $"\"{Environment.GetCommandLineArgs()[0]}\" " : "";
        var wall = Stopwatch.StartNew();
        for (int round = 0; round < rounds; round++)
            foreach (var f in fixtures)
                foreach (var v in round % 2 == 0 ? variants : variants.Reverse())
                {
                    string spec = $"fixture={f};variant={v};reps={reps};run={run};{extra}";
                    var psi = new ProcessStartInfo(self, $"{prefix}bench --child \"{spec}\" --root \"{root}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    using var p = Process.Start(psi)!;
                    try
                    {
                        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) p.ProcessorAffinity = Math.Min(0x0F, (1 << Environment.ProcessorCount) - 1);
                        p.PriorityClass = ProcessPriorityClass.BelowNormal;
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    var err = p.StandardError.ReadToEndAsync();
                    string? line;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                        if (line.StartsWith("BENCH ") || line.Contains("[Error")) Console.WriteLine($"  r{round} {line}");
                    p.WaitForExit();
                    if (p.ExitCode != 0) { Console.Error.WriteLine($"{spec}: exit {p.ExitCode}\n{err.Result}"); return 1; }
                }
        Console.WriteLine($"{rounds} rounds in {wall.Elapsed.TotalSeconds:F0} s");
        return Table([Path.Combine(root, "results", run + ".tsv")], variants[0], (int)a.Double("--scenes", 0.0001));
    }

    static int Table(List<string> files, string baseVariant, int scenes)
    {
#if VITASHARP_DEVELOPER
        var text = GpuBenchTable.Format(files.SelectMany(File.ReadAllLines), baseVariant, scenes);
        Console.Write(text);
        return text.Length > 0 ? 0 : 1;
#else
        throw new PerfArgsException("bench table: build vitasharp-perf with -p:VitaSharpDeveloper=true");
#endif
    }
}
