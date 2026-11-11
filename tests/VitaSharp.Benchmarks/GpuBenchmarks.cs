using BenchmarkDotNet.Attributes;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Benchmarks;

/// <summary>
/// The GPU worker's CPU side on a real frame stream: <see cref="GpuDumpPlayer"/> feeds every command of a GPU dump
/// (<see cref="Core.Util.EnvVars.BenchDump"/>, e.g. a short Killzone dump) to a headless <see cref="VulkanRenderer"/>
/// on the host GPU, as replay-gpu does, then waits for the last frame (Finish). One operation = the whole dump; the
/// renderer, its shader and pipeline caches stay warm between operations (the first replay in setup fills them), so
/// the time is Execute: draw recording, uniforms, texture units and uploads, scene begin/end, presents. The dump's
/// recorded renderer switches are applied first, like replay-gpu (after the variant's: a switch set by both keeps the variant's).
/// </summary>
[WarmupCount(2), IterationCount(10)]
public class GpuWorkerBenchmarks
{
    /// <summary>"base" or a set of switches (<see cref="Core.Util.EnvVars.BenchVariants"/>): A/B on the same build.</summary>
    [ParamsSource(typeof(Fixtures), nameof(Fixtures.Variants))] public string Variant = "base";

    GuestMemory? _memory;
    VulkanRenderer? _renderer;
    byte[] _dump = [];
    string _cache = "";

    [GlobalSetup]
    public void Setup()
    {
        string path = Fixtures.RequireDump();
        _dump = File.ReadAllBytes(path);
        GpuDumpHeader header;
        using (var r = new GpuDumpReader(new MemoryStream(_dump)))
            header = r.Header;
        // renderer switches before the renderer and the shader generator types initialise
        Fixtures.Apply(Variant);
        foreach (var (k, v) in GpuDumpPlayer.RecordedEnvironment(header))
            Environment.SetEnvironmentVariable(k, v);
        _cache = Path.Combine(Path.GetTempPath(), "vitasharp-bench-shadercache");
        _memory = new GuestMemory();
        _renderer = new VulkanRenderer(new VulkanRendererOptions
        {
            ShaderCacheDir = _cache,
            VSync = false,
            AsyncShaders = false,
            AsyncPipelines = false,
            FirstPresent = header.Long("first-present"),
        });
        if (header["title-id"] is { Length: > 0 } title)
            _renderer.BeginTitle(title);
        ReplayDump();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _renderer?.Dispose();
        _memory?.Dispose();
    }

    [Benchmark]
    public long ReplayDump()
    {
        using var reader = new GpuDumpReader(new MemoryStream(_dump, writable: false));
        var st = GpuDumpPlayer.Play(reader, _renderer!);
        _renderer!.Execute(new FinishCommand());
        return st.Draws;
    }
}

/// <summary>
/// USSE -> GLSL translation (<see cref="ShaderGenerator.Generate"/>, before shaderc) of every distinct vertex and
/// fragment program a GPU dump draws with (<see cref="Core.Util.EnvVars.BenchDump"/>), with the default hints of the
/// shader-test command. One operation = the whole set (the cost of a cold shader cache's translation step).
/// </summary>
public class ShaderTranslationBenchmarks
{
    readonly List<GxpProgram> _programs = [];
    GuestMemory? _memory;
    ShaderHints _hints = null!;

    [GlobalSetup]
    public void Setup()
    {
        string path = Fixtures.RequireDump();
        _memory = new GuestMemory();
        var seen = new HashSet<ulong>();
        using (var reader = new GpuDumpReader(path))
            while (reader.Next())
                if (reader.Kind is not (GpuDumpRecord.Memory or GpuDumpRecord.Idle or GpuDumpRecord.BackendState) && reader.Command is DrawCommand d)
                    foreach (var p in new[] { d.VertexProgram.Info.Program, d.FragmentProgram.Info.Program })
                        if (seen.Add(p.Hash))
                            _programs.Add(p);
        if (_programs.Count == 0)
            throw new InvalidOperationException($"{path}: no draws");
        _hints = new ShaderHints(Enumerable.Repeat(0x0C000000u, 16).ToArray(), [], 0);
        Console.WriteLine($"// {_programs.Count} programs");
    }

    [GlobalCleanup] public void Cleanup() => _memory?.Dispose();

    [Benchmark]
    public int TranslateAll()
    {
        int chars = 0;
        foreach (var p in _programs)
            chars += ShaderGenerator.Generate(p, _hints).Glsl.Length;
        return chars;
    }
}
