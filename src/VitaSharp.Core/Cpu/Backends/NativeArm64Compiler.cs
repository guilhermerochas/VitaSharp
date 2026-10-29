using VitaSharp.Core.Util;
using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu.Native;

namespace VitaSharp.Core.Cpu.Backends;

/// <summary>
/// Native AArch64 backend (Android, Apple silicon, Linux and Windows on ARM): the same IR as the x64 backend
/// (<see cref="ArmFrontend"/> + <see cref="IrPasses"/>), lowered by <see cref="Arm64Emitter"/> into the shared W^X code
/// arena and run through the same runtime (<see cref="NativeRuntime"/>: trampoline, callout bridge, link slots,
/// dispatch table). Unsupported instructions are interpreter callouts, exactly like on x64. It shares the command-line
/// name "native" with <see cref="NativeX64Compiler"/>; the chain keeps whichever the host supports.
/// </summary>
public sealed unsafe class NativeArm64Compiler : IBlockCompiler
{
    public static readonly NativeArm64Compiler Instance = new();

    [ThreadStatic] static Arm64Emitter? _emitter;
    static readonly bool[] NeonOp = Enumerable.Range(0, (int)Op.Count).Select(i => ((Op)i).ToString().StartsWith("Neon_", StringComparison.Ordinal)).ToArray();

    public string Name => "native";
    public bool IsSupported => UnsupportedReason == null;
    public string? UnsupportedReason =>
        RuntimeInformation.ProcessArchitecture != Architecture.Arm64 ? $"emits ARM64 code and this host is {RuntimeInformation.ProcessArchitecture}"
        : ExecutableMemory.UnsupportedReason is { } r ? r
        : WriteWatch.Enabled ? "is off while write watchpoints are active" : null;
    public BackendStats Stats { get; } = new();

    public long HotInstructions { get; set; } = 2_000;

    /// <summary>The frontend configured for this emitter (its SIMD coverage, no FZ tininess slow paths).</summary>
    public static ArmFrontend NewFrontend() => new(Arm64Emitter.SupportsVec, tinyCheck: false);

    public CompiledBlock? Compile(Block b) => Compile(b, NativeRuntime.Instance.Arm64Stubs);

    /// <summary>
    /// Compiles for the given runtime entry points. On an ARM64 host these are <see cref="NativeRuntime.Arm64Stubs"/>;
    /// tests on other hosts pass their own (the code then runs under an AArch64 emulator).
    /// </summary>
    public CompiledBlock? Compile(Block b, Arm64Emitter.Runtime stubs)
    {
        var ir = NewFrontend().Translate(b);
        IrPasses.DeadCode(ir);
        if (NativeX64Compiler.ValidateIr) IrPasses.Validate(ir);
        var rt = NativeRuntime.Instance;
        int exits = 0;
        foreach (var x in ir.Insts) if (x.Op == IrOp.ExitStatic) exits++;
        nint* slots = rt.AllocateSlots(exits);
        var em = _emitter ??= new Arm64Emitter();
        nint tierUp = 0;
        if (NativeX64Compiler.TierUp && Jit.Chain.Contains(IlJitCompiler.Instance))
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
        if (!em.Prepare(ir, stubs, k => (nint)(slots + k), tierUp)) return null;
        int size = 0;
        byte* code = rt.Code.Place(addr => { var bytes = em.Assemble(addr); size = bytes.Length; return bytes; });
        Interlocked.Add(ref NativeX64Compiler.CodeBytes, size); // one counter for both native backends (Report)
        rt.Register((nint)code, size, b);
        if (NativeX64Compiler.DumpCode) Console.Error.WriteLine($"[native-arm64] {b}\n{ir}\n{Arm64Dump.Dump((nint)code, size)}");
        bool unlogged = false;
        foreach (var x in ir.Insts) if (x.Op is IrOp.StrEx or IrOp.StoreD || (x.Op == IrOp.Callout && NativeVerifier.MayStore((Op)x.Imm))) unlogged = true;
        return new CompiledBlock(this, (nint)code, new NativeCodeInfo(size, exits, unlogged, Arm64: true));
    }
}
