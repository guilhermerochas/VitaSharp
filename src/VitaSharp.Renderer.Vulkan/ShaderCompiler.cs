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

    /// <summary>shaderc optimisation level (default Zero = the launcher's "Compilação rápida de shaders"; --shaderc-opt=,
    /// and VITASHARP_SHADERC_OPT overrides both). spirv-opt was ~85% of the GLSL to SPIR-V time (Killzone: 44 ms -> 7 ms
    /// per shader) and the driver optimises the SPIR-V again when it builds the pipeline (no measurable GPU time
    /// difference on NVIDIA).</summary>
    public OptimizationLevel OptLevel { get; }

    /// <summary>Parses zero|size|perf (also performance, o0, os, o3); null for anything else.</summary>
    public static OptimizationLevel? ParseOptLevel(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "perf" or "performance" or "o3" => OptimizationLevel.Performance,
        "size" or "os" => OptimizationLevel.Size,
        "zero" or "o0" or "none" => OptimizationLevel.Zero,
        _ => null,
    };

    /// <summary>The level to use: VITASHARP_SHADERC_OPT, else <paramref name="configured"/>, else Zero.</summary>
    public static OptimizationLevel ResolveOptLevel(string? configured) =>
        ParseOptLevel(Environment.GetEnvironmentVariable(EnvVars.ShadercOpt)) ?? ParseOptLevel(configured) ?? OptimizationLevel.Zero;

    /// <summary>Shaders really compiled by shaderc, their total time (stopwatch ticks), and disk cache hits.</summary>
    public static long Compiled, CompileTicks, DiskHits;

    public ShaderCompiler(string? cacheDir, OptimizationLevel optLevel = OptimizationLevel.Zero)
    {
        OptLevel = optLevel;
        _compilers = new(() => (nint)_sc.CompilerInitialize(), trackAllValues: true);
        _cacheDir = cacheDir;
        if (cacheDir != null) Directory.CreateDirectory(cacheDir);
    }

    /// <summary>
    /// Pays glslang's one-time process initialisation (its built-in symbol tables: ~100 ms on a desktop CPU, measured
    /// 97-107 ms for the first shader of a process against 1-5 ms for the next ones) on a background thread at
    /// renderer start, so the first shader the game needs does not. Without it the first new shader of every session,
    /// even one with warm caches, stalled the frame that drew it by that much (FIFA 13: 2 new shaders, 211 ms).
    /// Uses a compiler of its own (the initialisation is process-wide; glslang guards it). Once per process.
    /// </summary>
    public static void WarmProcessInBackground()
    {
        if (Environment.GetEnvironmentVariable(EnvVars.ShadercWarm) is "0" or "off") return;
        if (Interlocked.Exchange(ref _warmStarted, 1) != 0) return;
        new Thread(() =>
        {
            try
            {
                var sc = Shaderc.GetApi();
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                // never released: glslang counts its users and frees the tables when the last compiler goes away (a
                // released warm-up compiler made the next shader pay the whole initialisation again)
                var compiler = sc.CompilerInitialize();
                WarmCompiler = (nint)compiler;
                WarmCompile(sc, compiler, "#version 450\nvoid main() { gl_Position = vec4(0.0); }\n", ShaderKind.VertexShader);
                WarmCompile(sc, compiler, "#version 450\nlayout(location = 0) out vec4 c;\nvoid main() { c = vec4(0.0); }\n", ShaderKind.FragmentShader);
                Log.Info($"[gpu] shaderc warmed up in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms (background)");
            }
            catch (Exception e) { Log.WarnOnce("shaderc-warm", $"[gpu] shaderc warm-up failed: {e.Message}"); }
        }) { IsBackground = true, Name = "shaderc warm-up", Priority = ThreadPriority.BelowNormal }.Start();
    }

    static int _warmStarted;
    /// <summary>The warm-up's compiler, kept for the process so glslang stays initialised.</summary>
    internal static nint WarmCompiler { get; private set; }

    static void WarmCompile(Shaderc sc, Compiler* compiler, string glsl, ShaderKind kind)
    {
        byte[] src = Encoding.UTF8.GetBytes(glsl);
        byte[] name = Encoding.ASCII.GetBytes((kind == ShaderKind.VertexShader ? "warm.vert" : "warm.frag") + "\0");
        byte[] entry = "main\0"u8.ToArray();
        var opts = sc.CompileOptionsInitialize();
        sc.CompileOptionsSetTargetEnv(opts, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan10);
        CompilationResult* res;
        fixed (byte* s = src) fixed (byte* n = name) fixed (byte* e = entry)
            res = sc.CompileIntoSpv(compiler, s, (nuint)src.Length, kind, n, e, opts);
        sc.CompileOptionsRelease(opts);
        sc.ResultRelease(res);
    }

    public byte[]? Compile(string glsl, bool vertex, out string? error) =>
        Compile(glsl, vertex ? ShaderKind.VertexShader : ShaderKind.FragmentShader, out error);

    /// <summary>Any stage (also the renderer's built-in compute shaders, e.g. the debug-mode NaN scan).</summary>
    public byte[]? Compile(string glsl, ShaderKind kind, out string? error)
    {
        error = null;
        ulong hash = System.IO.Hashing.XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(glsl));
        // the optimisation level is part of the key (performance keeps the historical file names)
        string suffix = OptLevel == OptimizationLevel.Performance ? "" : OptLevel == OptimizationLevel.Zero ? ".o0" : ".os";
        // simulated generator bump (test only): as if every GLSL had changed
        if (VitaSharp.Core.Gxm.Shader.ShaderGenerator.VersionSalt != 0) suffix += $".salt{VitaSharp.Core.Gxm.Shader.ShaderGenerator.VersionSalt}";
        string? cachePath = _cacheDir != null ? Path.Combine(_cacheDir, $"{hash:x16}{suffix}.spv") : null;
        if (cachePath != null && File.Exists(cachePath))
        {
            try { var cached = File.ReadAllBytes(cachePath); Interlocked.Increment(ref DiskHits); return cached; } catch { }
        }

        byte[] src = Encoding.UTF8.GetBytes(glsl);
        byte[] name = Encoding.ASCII.GetBytes(kind switch { ShaderKind.VertexShader => "shader.vert", ShaderKind.ComputeShader => "shader.comp", _ => "shader.frag" } + "\0");
        byte[] entry = "main\0"u8.ToArray();
        CompilationResult* res;
        Compiler* compiler = (Compiler*)_compilers.Value;
        {
            var opts = _sc.CompileOptionsInitialize();
            _sc.CompileOptionsSetTargetEnv(opts, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan10);
            _sc.CompileOptionsSetOptimizationLevel(opts, OptLevel);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            fixed (byte* s = src) fixed (byte* n = name) fixed (byte* e = entry)
                res = _sc.CompileIntoSpv(compiler, s, (nuint)src.Length, kind, n, e, opts);
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
                // several threads may compile the same GLSL at once (shader workers and the GPU thread, or two hint keys
                // that generate identical source): write a private temp file and rename it, so a reader never sees a
                // partially written .spv and concurrent writers do not fail on a sharing violation
                string tmp = $"{cachePath}.{Environment.CurrentManagedThreadId}.tmp";
                try { File.WriteAllBytes(tmp, bytes); File.Move(tmp, cachePath, true); }
                catch (Exception ex)
                {
                    try { File.Delete(tmp); } catch { }
                    if (!File.Exists(cachePath)) Log.WarnOnce("spv-cache-write", $"shader cache write failed: {ex.Message}");
                }
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
