using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// Bulk memory copies for the hot paths that move megabytes per frame (GXM uniform buffers, upload rings, render-target
/// writebacks). On Mono (the Android app) <c>Span.CopyTo</c> runs Mono's own managed block copy, which measured about
/// 250 MB/s on a Cortex-A76 (F1 2011's 8 MB of uniforms and 13 MB of uploads per race frame took most of two cores);
/// there the copy goes to the C library's memmove (NEON, several GB/s), called without a GC transition (it neither
/// blocks nor calls back). CoreCLR on ARM64 keeps large copies in its managed <c>SpanHelpers.Memmove</c>: on the phone
/// that was 18.6% of the GPU worker in Killzone (libc's memcpy 1.6%, perf map + simpleperf, 09/11/2026), so ARM64
/// Linux/Android under CoreCLR uses libc too (copies of <see cref="LongCopy"/> bytes or more keep the GC
/// transition there, so a long copy never holds up a collection). The x64 desktop keeps CopyTo (its native threshold is
/// 2 KB). <see cref="EnvVars.NativeMemmove"/>=off keeps CopyTo everywhere.
/// </summary>
public static unsafe class FastCopy
{
    /// <summary>Copies at or above this size go to libc; smaller ones stay inline.</summary>
    private const int NativeThreshold = 128;

    /// <summary>CoreCLR: copies at or above this size call libc with the GC transition (a collection may run meanwhile).</summary>
    private const int LongCopy = 256 * 1024;

    private static readonly delegate* unmanaged[SuppressGCTransition]<void*, void*, nuint, void*> s_memmove;
    private static readonly delegate* unmanaged<void*, void*, nuint, void*> s_memmoveLong;

    static FastCopy()
    {
        bool mono = Type.GetType("Mono.RuntimeStructs") != null;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
        {
            return;
        }

        if (!mono && RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            return;
        }

        if (Environment.GetEnvironmentVariable(EnvVars.NativeMemmove) is "off" or "0")
        {
            return;
        }

        try
        {
            if (NativeLibrary.TryLoad("libc.so", out nint libc) && NativeLibrary.TryGetExport(libc, "memmove", out nint f))
            {
                s_memmove = (delegate* unmanaged[SuppressGCTransition]<void*, void*, nuint, void*>)f;
                // Mono's hybrid suspend does not wait for a thread in a native call: no transition needed at any size
                if (!mono)
                {
                    s_memmoveLong = (delegate* unmanaged<void*, void*, nuint, void*>)f;
                }
            }
        }
        catch (Exception) { }
    }

    /// <summary>Whether copies go to the C library.</summary>
    public static bool Native => s_memmove != null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (src.Length > dst.Length)
        {
            throw new ArgumentException("destination too short", nameof(dst));
        }

        if (s_memmove != null && src.Length >= NativeThreshold)
        {
            fixed (byte* s = src)
            fixed (byte* d = dst)
            {
                s_memmove(d, s, (nuint)src.Length);
            }
        }
        else
        {
            src.CopyTo(dst);
        }
    }
}
