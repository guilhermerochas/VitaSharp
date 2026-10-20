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

    public string Name => "native";
    public bool IsSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64 && !WriteWatch.Enabled;
    public BackendStats Stats { get; } = new();

    /// <summary>A native block costs tens of microseconds to compile: worth it after a few thousand instructions.</summary>
    public long HotInstructions { get; set; } = 2_000;

    /// <summary>Debug: check IR structure rules on every block (tests turn it on).</summary>
    public static bool ValidateIr = Environment.GetEnvironmentVariable("VITASHARP_NATIVE_VALIDATE") == "1";

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
        if (!em.Prepare(ir, rt.Stubs, k => (nint)(slots + k))) return null;
        int size = 0;
        byte* code = rt.Code.Place(rip => { var bytes = em.Assemble(rip); size = bytes.Length; return bytes; });
        Interlocked.Add(ref CodeBytes, size);
        if (DumpCode) Console.Error.WriteLine($"[native] {b}\n{ir}\n{X64Disassembler.Disassemble((nint)code, size)}");
        return new CompiledBlock(this, (nint)code, new NativeCodeInfo(size, exits));
    }

    /// <summary>Total generated machine code (diagnostics).</summary>
    public static long CodeBytes;

    /// <summary>One-line summary: code size, links, and the guest ops most often left to interpreter callouts.</summary>
    public static string Report()
    {
        long inl = ArmFrontend.InlineCount.Sum(), call = ArmFrontend.CalloutCount.Sum();
        var top = Enumerable.Range(0, (int)Op.Count).Where(i => ArmFrontend.CalloutCount[i] > 0)
            .OrderByDescending(i => ArmFrontend.CalloutCount[i]).Take(10)
            .Select(i => $"{(Op)i} {ArmFrontend.CalloutCount[i]}");
        return $"Native backend: {CodeBytes / 1024} KB code, {NativeRuntime.Links} links ({NativeRuntime.Unlinks} undone), " +
               $"static instructions inline {inl} / callout {call} ({100.0 * inl / Math.Max(1, inl + call):F1}% inline); top callouts: {string.Join(", ", top)}";
    }

    /// <summary>Debug: print IR and disassembly of every compiled block (VITASHARP_NATIVE_DUMP=1).</summary>
    public static bool DumpCode = Environment.GetEnvironmentVariable("VITASHARP_NATIVE_DUMP") == "1";
}

/// <summary>Size of a native block's code and its number of linkable exits.</summary>
public sealed record NativeCodeInfo(int Size, int Exits)
{
    public string Disassemble(nint entry) => X64Disassembler.Disassemble(entry, Size);
}
