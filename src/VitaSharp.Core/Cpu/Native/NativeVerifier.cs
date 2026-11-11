using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// In-game differential mode (VITASHARP_NATIVE_VERIFY=1): every native block execution is checked against the
/// interpreter. The block runs natively from the current state while its generated code logs every guest store
/// (address, size, previous value); the stores are undone, the state restored, and the interpreter runs the block for
/// real - the guest always continues with the interpreter's result. Registers, flags, Thumb, the SIMD file, FPSCR,
/// the exit fields and the final contents of every address the native code stored to are compared; mismatches are
/// logged with the block's IR and disassembly. Blocks whose callouts store to memory (NEON stores, exclusives) cannot
/// be undone and are only interpreted. IL-backend blocks without stores are checked the same way (<see cref="RunCompiled"/>).
/// Debug only: roughly halves CPU speed.
/// </summary>
public static unsafe class NativeVerifier
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable(EnvVars.NativeVerify) == "1";
    public static long Verified, Mismatches, SimdMismatches, Skipped;
    public static Action<string>? Log;
    private const int MaxReports = 40;

    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static nint t_log;

#pragma warning disable CS0649 // written by generated code
    private struct Entry
    {
        public uint Addr, Size, Old, Pad;
    }
#pragma warning restore CS0649

    /// <summary>Ops whose interpreter handler may write guest memory (used to flag blocks with unlogged stores).</summary>
    public static bool MayStore(Op op) => op is Op.A32_LdStImm or Op.A32_LdStReg or Op.A32_LdStExtra or Op.A32_LdmStm
        or Op.A32_Exclusive or Op.A32_Swp or Op.T16_LdStReg or Op.T16_LdStImm or Op.T16_LdStH or Op.T16_LdStSp or Op.T16_Push
        or Op.T16_Stm or Op.T32_LdmStm or Op.T32_LdStDual or Op.T32_ExclusiveWord or Op.T32_ExclusiveOther or Op.T32_LdSt
        or Op.Vfp_LdrStrArm or Op.Vfp_LdrStrThumb or Op.Vfp_LdmStm or Op.Neon_LoadStore;

    /// <summary>Executes the block (interpreter semantics), verifying the native code when possible.</summary>
    public static void Run(CpuState* s, Block b)
    {
        if (b.Code?.Tag is not NativeCodeInfo info || info.UnloggedStores || b.NativeEntry == 0)
        {
            Interlocked.Increment(ref Skipped);
            CpuCore.Interpret(s, b);
            return;
        }
        if (t_log == 0)
        {
            t_log = (nint)NativeMemory.Alloc((nuint)(X64Emitter.StoreLogEntries * sizeof(Entry)));
        }

        CpuState s0 = *s;
        s->StoreLog = t_log;
        s->StoreLogCount = 0;
        s->CycleAcc = 0;
        s->CheckWindow = 0;
        s->LinkBreak = 1;
        s->NBlocks = 0;
        s->NInstr = 0;
        string? nativeFault = null;
        try
        {
            NativeRuntime.Instance.Run(s, b.NativeEntry);
            if (s->CalloutFault != 0)
            {
                NativeRuntime.ThrowPendingFault(s);
            }
        }
        catch (Exception e)
        {
            nativeFault = e.GetType().Name;
        }
        CpuState sn = *s;
        if (sn.TierUpRequest != 0)
        {
            // the block asked to be recompiled by the IL backend before running: nothing executed, nothing to compare
            *s = s0;
            Jit.TierUp(b);
            CpuCore.Interpret(s, b);
            return;
        }
        int n = (int)s->StoreLogCount;
        var log = (Entry*)t_log;
        uint[] finals = new uint[n];
        for (int i = 0; i < n; i++)
        {
            finals[i] = Read(log[i].Addr, log[i].Size);
        }

        for (int i = n - 1; i >= 0; i--)
        {
            Write(log[i].Addr, log[i].Size, log[i].Old);
        }

        *s = s0;
        string? interpFault = null;
        try
        {
            CpuCore.Interpret(s, b);
        }
        catch (Exception e)
        {
            interpFault = e.GetType().Name;
            Compare(s, &s0, &sn, b, n, finals, nativeFault, interpFault);
            throw;
        }
        Compare(s, &s0, &sn, b, n, finals, nativeFault, interpFault);
    }

    /// <summary>
    /// The IL backend's blocks (the fallback for blocks the native backend rejects, or the backend itself): the IL code
    /// cannot log its stores, so only blocks without memory stores are checked - the compiled code runs on a copy of the
    /// state, the interpreter on the real one, and registers, flags, FPSCR, the SIMD file and the exit fields are
    /// compared. Blocks that store are only interpreted.
    /// </summary>
    public static void RunCompiled(CpuState* s, Block b, JitBlockFn fn)
    {
        if (b.Insts.Any(i => MayStore(i.Op)))
        {
            Interlocked.Increment(ref Skipped);
            CpuCore.Interpret(s, b);
            return;
        }
        CpuState s0 = *s;
        string? compiledFault = null;
        try
        {
            fn(s);
        }
        catch (Exception e)
        {
            compiledFault = e.GetType().Name;
        }
        CpuState sc = *s;
        *s = s0;
        string? interpFault = null;
        try
        {
            CpuCore.Interpret(s, b);
        }
        catch (Exception e)
        {
            interpFault = e.GetType().Name;
            Compare(s, &s0, &sc, b, 0, [], compiledFault, interpFault);
            throw;
        }
        Compare(s, &s0, &sc, b, 0, [], compiledFault, interpFault);
    }

    private static uint Read(uint addr, uint size) => size switch
    {
        1 => GuestMemory.Read<byte>(addr),
        2 => GuestMemory.Read<ushort>(addr),
        _ => GuestMemory.Read<uint>(addr),
    };

    private static void Write(uint addr, uint size, uint v)
    {
        if (size == 1)
        {
            GuestMemory.Write(addr, (byte)v);
        }
        else if (size == 2)
        {
            GuestMemory.Write(addr, (ushort)v);
        }
        else
        {
            GuestMemory.Write(addr, v);
        }
    }

    private static void Compare(CpuState* si, CpuState* s0, CpuState* sn, Block b, int n, uint[] finals, string? nativeFault, string? interpFault)
    {
        Interlocked.Increment(ref Verified);
        var sb = new StringBuilder();
        if (nativeFault != interpFault)
        {
            sb.Append($" fault: native {nativeFault ?? "-"} interp {interpFault ?? "-"}");
        }

        for (int r = 0; r < 16; r++)
        {
            if (si->R[r] != sn->R[r])
            {
                sb.Append($" R{r}: interp {si->R[r]:X8} native {sn->R[r]:X8}");
            }
        }

        if (si->Cpsr != sn->Cpsr)
        {
            sb.Append($" CPSR: interp {si->Cpsr:X8} native {sn->Cpsr:X8}");
        }

        if (si->Fpscr != sn->Fpscr)
        {
            sb.Append($" FPSCR: interp {si->Fpscr:X8} native {sn->Fpscr:X8}");
        }

        if (si->Exit != sn->Exit)
        {
            sb.Append($" Exit: interp {si->Exit} native {sn->Exit}");
        }

        if (si->Exit != ExitReason.None && (si->SvcImm != sn->SvcImm || si->SvcPc != sn->SvcPc))
        {
            sb.Append(" svc fields");
        }

        if (si->TpidrUrw != sn->TpidrUrw)
        {
            sb.Append(" TPIDRURW");
        }

        for (int i = 0; i < 32; i++)
        {
            if (si->D[i] != sn->D[i])
            {
                sb.Append($" D{i}: interp {si->D[i]:X16} native {sn->D[i]:X16}");
            }
        }
        // memory: what the native code left at each address it stored to, against the interpreter's result
        var seen = new HashSet<uint>();
        var log = (Entry*)t_log;
        for (int i = n - 1; i >= 0; i--)
        {
            uint a = log[i].Addr;
            if (!seen.Add(a | (log[i].Size << 30)))
            {
                continue;
            }

            uint v = Read(a, log[i].Size);
            if (v != finals[i])
            {
                sb.Append($" MEM[{a:X8}/{log[i].Size}]: interp {v:X} native {finals[i]:X}");
            }
        }
        if (sb.Length == 0)
        {
            return;
        }

        long k = Interlocked.Increment(ref Mismatches);
        // Blocks of plain integer code that read memory another host thread writes concurrently (HLE, GPU worker,
        // other guest cores) can legitimately see different values in the two runs. Mismatches in blocks with
        // NEON/VFP instructions (exclusive loads race by design: other cores update those words) are counted and reported separately so they are never drowned by those.
        bool simd = b.Insts.Any(i => i.Op.ToString() is var n && (n.StartsWith("Neon_", StringComparison.Ordinal)
            || n.StartsWith("Vfp_", StringComparison.Ordinal)));
        string tag = "";
        if (simd)
        {
            long ks = Interlocked.Increment(ref SimdMismatches);
            if (ks > MaxReports)
            {
                return;
            }

            tag = $" (SIMD/FP #{ks})";
        }
        else if (k - SimdMismatches > MaxReports)
        {
            return;
        }

        string regs = string.Join(" ", Enumerable.Range(0, 16).Select(r => s0->R[r].ToString("X8")));
        string detail = $"NATIVE VERIFY MISMATCH #{k}{tag} {b}:{sb}\n  in: r=[{regs}] cpsr={s0->Cpsr:X8} fpscr={s0->Fpscr:X8}\n  " +
                     string.Join(" ", b.Insts.Select(i => $"{i.Address:X8}:{i.Raw:X8}:{i.Op}{(i.Cond != 14 ? $"/c{i.Cond}" : "")}"));
        if (k <= 8 && b.Code?.Tag is NativeCodeInfo info)
        {
            IrBlock ir = new ArmFrontend().Translate(b);
            IrPasses.DeadCode(ir);
            detail += $"\nIR:\n{ir}\nx64:\n{info.Disassemble(b.NativeEntry)}";
        }
        (Log ?? Util.Log.Warn)(detail);
    }
}
