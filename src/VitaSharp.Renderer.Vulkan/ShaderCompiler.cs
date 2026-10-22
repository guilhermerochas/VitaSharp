using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Shaderc;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>GLSL → SPIR-V through shaderc, with an on-disk cache keyed by the source hash.</summary>
public sealed unsafe class ShaderCompiler : IDisposable
{
    readonly Shaderc _sc = Shaderc.GetApi();
    // one shaderc compiler per thread: the GPU thread and the shader workers compile concurrently
    readonly ThreadLocal<nint> _compilers;
    readonly string? _cacheDir;

    /// <summary>shaderc optimisation level: VITASHARP_SHADERC_OPT=zero (default), size or perf. spirv-opt was ~85% of the
    /// GLSL to SPIR-V time (Killzone: 44 ms -> 7 ms per shader) and the driver optimises the SPIR-V again when it builds
    /// the pipeline (no measurable GPU time difference on NVIDIA).</summary>
    public static readonly OptimizationLevel OptLevel = Environment.GetEnvironmentVariable("VITASHARP_SHADERC_OPT")?.ToLowerInvariant() switch
    {
        "perf" or "performance" => OptimizationLevel.Performance,
        "size" => OptimizationLevel.Size,
        _ => OptimizationLevel.Zero,
    };

    /// <summary>Shaders really compiled by shaderc, their total time (stopwatch ticks), and disk cache hits.</summary>
    public static long Compiled, CompileTicks, DiskHits;

    public ShaderCompiler(string? cacheDir)
    {
        _compilers = new(() => (nint)_sc.CompilerInitialize(), trackAllValues: true);
        _cacheDir = cacheDir;
        if (cacheDir != null) Directory.CreateDirectory(cacheDir);
    }

    public byte[]? Compile(string glsl, bool vertex, out string? error)
    {
        error = null;
        ulong hash = System.IO.Hashing.XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(glsl));
        // the optimisation level is part of the key (performance keeps the historical file names)
        string suffix = OptLevel == OptimizationLevel.Performance ? "" : OptLevel == OptimizationLevel.Zero ? ".o0" : ".os";
        string? cachePath = _cacheDir != null ? Path.Combine(_cacheDir, $"{hash:x16}{suffix}.spv") : null;
        if (cachePath != null && File.Exists(cachePath))
        {
            try { var cached = File.ReadAllBytes(cachePath); Interlocked.Increment(ref DiskHits); return cached; } catch { }
        }

        byte[] src = Encoding.UTF8.GetBytes(glsl);
        byte[] name = Encoding.ASCII.GetBytes((vertex ? "shader.vert" : "shader.frag") + "\0");
        byte[] entry = "main\0"u8.ToArray();
        CompilationResult* res;
        Compiler* compiler = (Compiler*)_compilers.Value;
        {
            var opts = _sc.CompileOptionsInitialize();
            _sc.CompileOptionsSetTargetEnv(opts, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan10);
            _sc.CompileOptionsSetOptimizationLevel(opts, OptLevel);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            fixed (byte* s = src) fixed (byte* n = name) fixed (byte* e = entry)
                res = _sc.CompileIntoSpv(compiler, s, (nuint)src.Length, vertex ? ShaderKind.VertexShader : ShaderKind.FragmentShader, n, e, opts);
            Interlocked.Add(ref CompileTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            Interlocked.Increment(ref Compiled);
            _sc.CompileOptionsRelease(opts);
        }
        try
        {
            if (_sc.ResultGetCompilationStatus(res) != CompilationStatus.Success)
            {
                error = Marshal.PtrToStringAnsi((nint)_sc.ResultGetErrorMessage(res));
                return null;
            }
            var len = (int)_sc.ResultGetLength(res);
            var bytes = new byte[len];
            new ReadOnlySpan<byte>(_sc.ResultGetBytes(res), len).CopyTo(bytes);
            if (cachePath != null)
            {
                try { File.WriteAllBytes(cachePath, bytes); } catch (Exception ex) { Log.Warn($"shader cache write failed: {ex.Message}"); }
            }
            return bytes;
        }
        finally { _sc.ResultRelease(res); }
    }

    public void Dispose()
    {
        foreach (var c in _compilers.Values) _sc.CompilerRelease((Compiler*)c);
        _compilers.Dispose();
        // The shaderc library is deliberately not unloaded (no _sc.Dispose()): it registers a process-exit handler
        // that stays behind after dlclose, so on Linux exit() jumped into the unmapped library (SIGSEGV after every
        // clean run). Keeping it mapped until the process ends costs nothing; a new renderer reuses it.
    }
}
