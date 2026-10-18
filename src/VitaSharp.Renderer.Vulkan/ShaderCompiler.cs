using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Shaderc;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>GLSL → SPIR-V through shaderc, with an on-disk cache keyed by the source hash.</summary>
public sealed unsafe class ShaderCompiler : IDisposable
{
    readonly Shaderc _sc = Shaderc.GetApi();
    readonly Compiler* _compiler;
    readonly string? _cacheDir;
    readonly object _lock = new();

    public ShaderCompiler(string? cacheDir)
    {
        _compiler = _sc.CompilerInitialize();
        _cacheDir = cacheDir;
        if (cacheDir != null) Directory.CreateDirectory(cacheDir);
    }

    public byte[]? Compile(string glsl, bool vertex, out string? error)
    {
        error = null;
        ulong hash = System.IO.Hashing.XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(glsl));
        string? cachePath = _cacheDir != null ? Path.Combine(_cacheDir, $"{hash:x16}.spv") : null;
        if (cachePath != null && File.Exists(cachePath))
        {
            try { return File.ReadAllBytes(cachePath); } catch { }
        }

        byte[] src = Encoding.UTF8.GetBytes(glsl);
        byte[] name = Encoding.ASCII.GetBytes((vertex ? "shader.vert" : "shader.frag") + "\0");
        byte[] entry = "main\0"u8.ToArray();
        CompilationResult* res;
        lock (_lock)
        {
            var opts = _sc.CompileOptionsInitialize();
            _sc.CompileOptionsSetTargetEnv(opts, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan10);
            _sc.CompileOptionsSetOptimizationLevel(opts, OptimizationLevel.Performance);
            fixed (byte* s = src) fixed (byte* n = name) fixed (byte* e = entry)
                res = _sc.CompileIntoSpv(_compiler, s, (nuint)src.Length, vertex ? ShaderKind.VertexShader : ShaderKind.FragmentShader, n, e, opts);
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
        _sc.CompilerRelease(_compiler);
        _sc.Dispose();
    }
}
