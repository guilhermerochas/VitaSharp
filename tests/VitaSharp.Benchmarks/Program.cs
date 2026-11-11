using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using VitaSharp.Core.Util;

namespace VitaSharp.Benchmarks;

/// <summary>
/// Entry point: <c>compare BASE.md NEW.md</c> (<see cref="ResultCompare"/>), else BenchmarkDotNet's switcher (--filter, --list flat, --job short, --artifacts DIR, ...) with
/// <see cref="BenchConfig"/>: allocations (MemoryDiagnoser) and the JIT's code of the benchmarked method and what it
/// calls (DisassemblyDiagnoser, two levels), exported as GitHub Markdown.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "compare") return ResultCompare.Run(args[1], args[2]);
        foreach (var (name, path) in new[] { (EnvVars.BenchDump, Fixtures.DumpPath), (EnvVars.BenchAt9, Fixtures.At9Path) })
            Console.WriteLine(path != null ? $"{name}={path}" : $"{name} not set: its fixture benchmarks {(name == EnvVars.BenchDump ? "fail in setup" : "use generated data")}");
        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, BenchConfig.Create());
        return summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Any(r => !r.Success)) ? 1 : 0;
    }
}

/// <summary>The configuration of every run: allocations, two levels of disassembly, one joined GitHub Markdown table
/// (the default config's exporters). BenchmarkDotNet builds a generated project per run: its build uses two MSBuild nodes
/// and no shared compiler server (the server ignores -m and takes ~7 cores; docs/agents/regras.md). The machine's power
/// plan is left as it is (BenchmarkDotNet switches Windows to "High performance" by default).</summary>
public static class BenchConfig
{
    public static IConfig Create() => DefaultConfig.Instance
        .AddJob(Job.Default.WithArguments([new MsBuildArgument("-m:2"), new MsBuildArgument("-p:UseSharedCompilation=false"), new MsBuildArgument("-nr:false")])
            .WithPowerPlan(PowerPlan.UserPowerPlan).AsDefault())
        .AddDiagnoser(MemoryDiagnoser.Default)
        .AddDiagnoser(new DisassemblyDiagnoser(new DisassemblyDiagnoserConfig(maxDepth: 2, exportGithubMarkdown: true, exportHtml: false)))
        .WithOptions(ConfigOptions.JoinSummary)
        // the generated project rebuilds the referenced projects into its own folder: minutes on a busy machine
        .WithBuildTimeout(TimeSpan.FromMinutes(20));
}

/// <summary>
/// The benchmark processes run at below-normal priority: BenchmarkDotNet raises every benchmark process to High (and
/// its thread to Highest), which on a shared development machine takes the CPUs from everything else. A background
/// thread puts the process class back whenever it changes (the host raises it right after starting the process).
/// The thread's relative priority inside the process stays, so measurements are only less stable on a busy machine.
/// </summary>
static class PriorityKeeper
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Start()
    {
        var t = new Thread(() =>
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            while (true)
            {
                try
                {
                    me.Refresh();
                    if (me.PriorityClass != System.Diagnostics.ProcessPriorityClass.BelowNormal) me.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
                }
                catch (Exception) { } // not allowed on this system: keep what there is
                Thread.Sleep(250);
            }
        }) { IsBackground = true, Name = "bench priority" };
        t.Start();
    }
}

/// <summary>Fixtures with game data: paths from the environment, never in git.</summary>
public static class Fixtures
{
    public static string? DumpPath => Existing(Environment.GetEnvironmentVariable(EnvVars.BenchDump));
    public static string? At9Path => Existing(Environment.GetEnvironmentVariable(EnvVars.BenchAt9));

    static string? Existing(string? p) => p is { Length: > 0 } && File.Exists(p) ? Path.GetFullPath(p) : null;

    /// <summary>"base" plus the variants of <see cref="EnvVars.BenchVariants"/> ("K=V,K2=V2|K3=V3": one variant per
    /// '|'), for an A/B of renderer or core switches on the same build. Every variant runs in a process of its own.</summary>
    public static IEnumerable<string> Variants() =>
        new[] { "base" }.Concat((Environment.GetEnvironmentVariable(EnvVars.BenchVariants) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Sets the switches of <paramref name="variant"/> in this process (before the code reading them initialises).</summary>
    public static void Apply(string variant)
    {
        if (variant == "base") return;
        foreach (var kv in variant.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (kv.IndexOf('=') is var eq and > 0) Environment.SetEnvironmentVariable(kv[..eq], kv[(eq + 1)..]);
    }

    public static string RequireDump() => DumpPath
        ?? throw new InvalidOperationException($"set {EnvVars.BenchDump} to a GPU dump (.vsgd), e.g. a short Killzone dump (build/bench.* --dump)");
}
