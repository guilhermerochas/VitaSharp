using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// Bulk memory copies for the hot paths that move megabytes per frame (GXM uniform buffers, upload rings, render-target
/// writebacks). On Mono (the Android app) <c>Span.CopyTo</c> runs Mono's own managed block copy, which measured about
/// 250 MB/s on a Cortex-A76 (F1 2011's 8 MB of uniforms and 13 MB of uploads per race frame took most of two cores);
/// there the copy goes to the C library's memmove (NEON, several GB/s), called without a GC transition (it neither
/// blocks nor calls back). CoreCLR's CopyTo already ends in the native memmove for large sizes: the desktop keeps it.
/// </summary>
public static unsafe class FastCopy
{
    /// <summary>Copies at or above this size go to libc on Mono; smaller ones stay inline.</summary>
    const int NativeThreshold = 128;

    static readonly delegate* unmanaged[SuppressGCTransition]<void*, void*, nuint, void*> Memmove = Resolve();

    static delegate* unmanaged[SuppressGCTransition]<void*, void*, nuint, void*> Resolve()
    {
        if (Type.GetType("Mono.RuntimeStructs") == null || !OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()) return null;
        try
        {
            if (NativeLibrary.TryLoad("libc.so", out var libc) && NativeLibrary.TryGetExport(libc, "memmove", out var f))
                return (delegate* unmanaged[SuppressGCTransition]<void*, void*, nuint, void*>)f;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Whether copies go to the C library (Mono).</summary>
    public static bool Native => Memmove != null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (src.Length > dst.Length) throw new ArgumentException("destination too short", nameof(dst));
        if (Memmove != null && src.Length >= NativeThreshold)
            fixed (byte* s = src)
            fixed (byte* d = dst)
                Memmove(d, s, (nuint)src.Length);
        else src.CopyTo(dst);
    }
}
