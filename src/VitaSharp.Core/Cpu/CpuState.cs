using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VitaSharp.Core.Cpu;

public enum ExitReason : int
{
    None = 0,
    Svc = 1,
    Undefined = 2,
    Breakpoint = 3,
    BudgetExpired = 4,
    Halt = 5,
    Yield = 6,
}

/// <summary>
/// Architectural state of one Cortex-A9 core as seen by user code. Unmanaged and pinned in native
/// memory so both the interpreter and JIT-compiled blocks address fields through a raw pointer.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 16)]
public unsafe struct CpuState
{
    public fixed uint R[16];
    public uint N, Z, C, V;
    public uint Q;
    public uint GE;           // 4 bits, GE[3:0]
    public uint Thumb;        // CPSR.T
    public uint ITState;      // CPSR.IT (only meaningful across block boundaries)
    public uint Fpscr;        // includes NZCV in [31:28]
    public uint TpidrUro;     // CP15 c13 user read-only thread id (TLS pointer)
    public uint TpidrUrw;     // CP15 c13 user read/write thread id
    public uint ExclusiveAddr;
    public ulong ExclusiveValue;
    public uint ExclusiveValid;
    public ExitReason Exit;
    public uint SvcImm;
    public uint SvcPc;        // address of the SVC instruction
    public long Budget;       // remaining instruction budget before a reschedule
    public uint ExclusiveGen; // generation of the reserved granule at LDREX (see Arm.StoreExclusive*)
    public uint Pad1;
    public fixed ulong D[32]; // VFP/NEON register file (D0-D31 == Q0-Q15)

    public uint Cpsr
    {
        readonly get => (N << 31) | (Z << 30) | (C << 29) | (V << 28) | (Q << 27)
                        | ((ITState & 3) << 25) | ((GE & 0xF) << 16) | ((ITState >> 2) << 10)
                        | (Thumb << 5) | 0x10;
        set
        {
            N = (value >> 31) & 1; Z = (value >> 30) & 1; C = (value >> 29) & 1; V = (value >> 28) & 1;
            Q = (value >> 27) & 1; GE = (value >> 16) & 0xF; Thumb = (value >> 5) & 1;
            ITState = ((value >> 25) & 3) | (((value >> 10) & 0x3F) << 2);
        }
    }

    public uint Sp { readonly get => R[13]; set => R[13] = value; }
    public uint Lr { readonly get => R[14]; set => R[14] = value; }
    public uint Pc { readonly get => R[15]; set => R[15] = value; }

    public static CpuState* Allocate()
    {
        var p = (CpuState*)NativeMemory.AlignedAlloc((nuint)sizeof(CpuState), 64);
        Unsafe.InitBlockUnaligned(p, 0, (uint)sizeof(CpuState));
        p->Fpscr = 0x0300_0000; // default NaN + flush-to-zero as set by the Vita runtime
        return p;
    }

    public static void Free(CpuState* p) => NativeMemory.AlignedFree(p);

    // VFP single/double views
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref uint S(CpuState* s, int n) => ref ((uint*)s->D)[n];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref float F(CpuState* s, int n) => ref ((float*)s->D)[n];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref double Dbl(CpuState* s, int n) => ref ((double*)s->D)[n];
}
