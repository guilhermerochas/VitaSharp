using VitaSharp.Core.Util;
using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu.Native;

namespace VitaSharp.Core.Cpu.Backends;

/// <summary>
/// Native x86-64 backend: guest block -> IR (<see cref="ArmFrontend"/>) -> dead-code/lazy-flag pass ->
/// x64 machine code (<see cref="X64Emitter"/>, Iced encoder) in the W^X code arena (<see cref="NativeRuntime"/>).
/// No Reflection.Emit: blocks run through an unmanaged function pointer, so the backend also works under Native AOT.
/// <para>
/// Unsupported instructions do not reject the block: they become callouts into the interpreter handler for just that
/// instruction (the guest state is flushed to <see cref="CpuState"/> first), so every block compiles and the inline
/// coverage grows op by op. A block is only rejected (null: the IL backend or the interpreter take it) when it would
/// need more spill slots than the frame provides. Watchpoints (VITASHARP_WATCH) disable the backend: generated loads
/// and stores do not call the watch hooks.
/// </para>
/// </summary>
public sealed unsafe class NativeX64Compiler : IBlockCompiler
{
    public static readonly NativeX64Compiler Instance = new();

    [ThreadStatic] static X64Emitter? _emitter;
    static readonly bool[] NeonOp = Enumerable.Range(0, (int)Op.Count).Select(i => ((Op)i).ToString().StartsWith("Neon_", StringComparison.Ordinal)).ToArray();

    public string Name => "native";
    public bool IsSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64 && !WriteWatch.Enabled;
    public string? UnsupportedReason =>
        RuntimeInformation.ProcessArchitecture != Architecture.X64 ? $"emits x86-64 code and this host is {RuntimeInformation.ProcessArchitecture}"
        : WriteWatch.Enabled ? "is off while write watchpoints are active" : null;
    public BackendStats Stats { get; } = new();

    /// <summary>A native block costs tens of microseconds to compile: worth it after a few thousand instructions.</summary>
    public long HotInstructions { get; set; } = 2_000;

    /// <summary>Debug: check IR structure rules on every block (tests turn it on).</summary>
    public static bool ValidateIr = Environment.GetEnvironmentVariable(EnvVars.NativeValidate) == "1";

    public CompiledBlock? Compile(Block b)
    {
        var ir = new ArmFrontend().Translate(b);
        IrPasses.DeadCode(ir);
        if (ValidateIr) IrPasses.Validate(ir);
        var rt = NativeRuntime.Instance;
        int exits = 0;
        foreach (var x in ir.Insts) if (x.Op == IrOp.ExitStatic) exits++;
        nint* slots = rt.AllocateSlots(exits);
        var em = _emitter ??= new X64Emitter(); // per compile thread: the Iced assembler and buffers are reused
        // Blocks dominated by NEON callouts run better in the IL backend once hot (it inlines the handlers, a callout
        // is a call plus two GC transitions): give them an execution budget after which they are recompiled there.
        nint tierUp = 0;
        if (TierUp && Jit.Chain.Contains(IlJitCompiler.Instance))
        {
            int simd = 0;
            foreach (var x in ir.Insts)
                if (x.Op == IrOp.Callout && NeonOp[x.Imm]) simd++;
            if (simd >= 2 && simd * 4 >= b.Insts.Length)
            {
                tierUp = (nint)rt.AllocateSlots(1);
                *(int*)tierUp = (int)Math.Clamp(IlJitCompiler.Instance.HotInstructions / Math.Max(1, b.Insts.Length), 64, int.MaxValue);
            }
        }
        if (!em.Prepare(ir, rt.Stubs, k => (nint)(slots + k), tierUp)) return null;
        int size = 0;
        byte* code = rt.Code.Place(rip => { var bytes = em.Assemble(rip); size = bytes.Length; return bytes; });
        Interlocked.Add(ref CodeBytes, size);
        rt.Register((nint)code, size, b);
        if (DumpCode) Console.Error.WriteLine($"[native] {b}\n{ir}\n{X64Disassembler.Disassemble((nint)code, size)}");
        bool unlogged = false; // callouts that may store to memory: the verifier cannot undo them
        foreach (var x in ir.Insts) if (x.Op is IrOp.StrEx or IrOp.StoreD || (x.Op == IrOp.Callout && NativeVerifier.MayStore((Op)x.Imm))) unlogged = true;
        return new CompiledBlock(this, (nint)code, new NativeCodeInfo(size, exits, unlogged));
    }

    /// <summary>Recompile NEON-callout-heavy blocks with the IL backend once hot (VITASHARP_NATIVE_TIERUP=0 disables).</summary>
    public static bool TierUp = Environment.GetEnvironmentVariable(EnvVars.NativeTierup) != "0";

    /// <summary>Total generated machine code (diagnostics).</summary>
    public static long CodeBytes;

    /// <summary>One-line summary: code size, links, and the guest ops most often left to interpreter callouts.</summary>
    public static string Report()
    {
        long inl = ArmFrontend.InlineCount.Sum(), call = ArmFrontend.CalloutCount.Sum();
        var top = Enumerable.Range(0, (int)Op.Count).Where(i => ArmFrontend.CalloutCount[i] > 0)
            .OrderByDescending(i => ArmFrontend.CalloutCount[i]).Take(10)
            .Select(i => $"{(Op)i} {ArmFrontend.CalloutCount[i]}");
        var hits = NativeRuntime.CalloutHits;
        var hot = Enumerable.Range(0, (int)Op.Count).Where(i => hits[i] > 0).OrderByDescending(i => hits[i]).Take(10)
            .Select(i => $"{(Op)i} {hits[i] / 1000}K");
        return $"Native backend: {CodeBytes / 1024} KB code, {NativeRuntime.Links} links ({NativeRuntime.Unlinks} undone), {NativeRuntime.TieredUp} blocks tiered up to IL, " +
               $"static instructions inline {inl} / callout {call} ({100.0 * inl / Math.Max(1, inl + call):F1}% inline); top callouts: {string.Join(", ", top)}; " +
               $"executed callouts {hits.Sum() / 1000}K: {string.Join(", ", hot)}; by class: {string.Join(", ", CalloutClasses.Top(40))}";
    }

    /// <summary>Debug: print IR and disassembly of every compiled block (VITASHARP_NATIVE_DUMP=1).</summary>
    public static bool DumpCode = Environment.GetEnvironmentVariable(EnvVars.NativeDump) == "1";
}

/// <summary>Size of a native block's code and its number of linkable exits.</summary>
public sealed record NativeCodeInfo(int Size, int Exits, bool UnloggedStores = false)
{
    public string Disassemble(nint entry) => X64Disassembler.Disassemble(entry, Size);
}
