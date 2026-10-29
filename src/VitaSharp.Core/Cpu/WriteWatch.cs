using VitaSharp.Core.Util;
namespace VitaSharp.Core.Cpu;

/// <summary>
/// Debug watchpoints for guest memory accessed by the CPU (interpreter and JIT share the access helpers): stores into
/// [Lo, Lo + Span) and 32/64-bit loads from [ReadLo, ReadLo + ReadSpan). Only active when the process starts with
/// VITASHARP_WATCH=1: the gate is a static readonly field, so the JIT removes the checks entirely otherwise.
/// </summary>
public static unsafe class WriteWatch
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable(EnvVars.Watch) == "1";

    public static uint Lo, Span, ReadLo, ReadSpan;
    /// <summary>Called on the storing thread with (address, size, value) before the store.</summary>
    public static Action<uint, int, ulong>? OnHit;
    /// <summary>Called on the loading thread with (address, size).</summary>
    public static Action<uint, int>? OnRead;

    public static void Set(uint addr, uint length) { Lo = addr; Span = length; }
    public static void SetRead(uint addr, uint length) { ReadLo = addr; ReadSpan = length; }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static void Hit(uint addr, int size, ulong value) => OnHit?.Invoke(addr, size, value);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static void ReadHit(uint addr, int size) => OnRead?.Invoke(addr, size);
}
