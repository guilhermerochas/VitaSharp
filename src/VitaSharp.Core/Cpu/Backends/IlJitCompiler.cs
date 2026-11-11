using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace VitaSharp.Core.Cpu.Backends;

/// <summary>
/// "Call-threaded" block JIT: emits one IL method per guest block that calls each instruction handler with the
/// instruction word and address as constants. RyuJIT inlines the handlers and folds the decoding, turning the block
/// into straight-line native code. A DynamicMethod costs ~1-2 ms to create and JIT, so only blocks that will run
/// millions of instructions pay it back (see <see cref="HotInstructions"/>). Needs Reflection.Emit (not Native AOT).
/// </summary>
public sealed unsafe class IlJitCompiler : IBlockCompiler
{
    public static readonly IlJitCompiler Instance = new();

    static readonly MethodInfo CondPassed = typeof(Arm).GetMethod(nameof(Arm.CondPassed), BindingFlags.Static | BindingFlags.Public)!;
    const int PcOffset = 15 * 4; // offsetof(CpuState, R[15])

    public string Name => "il";
    public bool IsSupported => RuntimeFeature.IsDynamicCodeSupported;
    public string? UnsupportedReason => IsSupported ? null : "needs dynamic code (Reflection.Emit), which Native AOT builds do not have";
    public BackendStats Stats { get; } = new();

    /// <summary>
    /// A block is compiled once it has executed this many guest instructions in total (executions x length): a
    /// DynamicMethod costs ~1 ms to create and JIT, so only code that will run millions of instructions pays it back.
    /// </summary>
    public long HotInstructions { get; set; } = 400_000;

    /// <summary>Null when dynamic code is unavailable (Native AOT): the dispatcher then leaves the block to the next backend.</summary>
    public CompiledBlock? Compile(Block b)
    {
        if (RuntimeFeature.IsDynamicCodeSupported) return CompileDynamic(b);
        BackendStats.RejectReason = "no dynamic code";
        return null;
    }

    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("The IL backend emits a DynamicMethod per block (Reflection.Emit); it is skipped when dynamic code is not supported.")]
    CompiledBlock CompileDynamic(Block b)
    {
        var dm = new DynamicMethod(IlDump.MethodName(b), typeof(void), [typeof(CpuState*)], typeof(IlJitCompiler).Module, skipVisibility: true);
        EmitBlock(dm.GetILGenerator(), b);
        if (CountExecutions)
        {
            lock (DumpLock) _dump ??= IlDump.Create(Environment.GetEnvironmentVariable(Util.EnvVars.IlDump));
            _dump?.Add(b, il => EmitBlock(il, b));
        }
        var fn = dm.CreateDelegate<JitBlockFn>();
        RuntimeHelpers.PrepareDelegate(fn); // compile to native here, off the guest thread
        return new CompiledBlock(this, fn);
    }

    /// <summary>IL dump mode (VITASHARP_IL_DUMP=dir): the compiled blocks are also written to assemblies there.</summary>
    static IlDump? _dump; // created by the first compile
    static readonly Lock DumpLock = new();

    /// <summary>True in IL dump mode: the dispatcher counts the executions of IL blocks for the dump's index.</summary>
    public static readonly bool CountExecutions = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Util.EnvVars.IlDump));

    /// <summary>The block's body: one handler call per instruction, guarded by its condition, then return.</summary>
    static void EmitBlock(ILGenerator il, Block b)
    {
        var insts = b.Insts;
        for (int i = 0; i < insts.Length; i++)
        {
            ref readonly var d = ref insts[i];
            bool last = i == insts.Length - 1;
            if (d.EndsBlock || last) EmitStorePc(il, d.Address + d.Size);
            if (d.Op is Op.Thumb_It or Op.A32_Nop or Op.T16_Nop or Op.T32_Nop) continue;

            Label skip = default;
            bool cond = (d.Flags & InstFlags.Conditional) != 0;
            if (cond)
            {
                skip = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, (int)d.Cond);
                il.Emit(OpCodes.Call, CondPassed);
                il.Emit(OpCodes.Brfalse, skip);
            }
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, (int)d.Raw);
            il.Emit(OpCodes.Ldc_I4, (int)d.Address);
            il.Emit(OpCodes.Call, HandlerTable.Methods[(int)d.Op]);
            if (cond) il.MarkLabel(skip);
        }
        il.Emit(OpCodes.Ret);
    }

    static void EmitStorePc(ILGenerator il, uint pc)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, PcOffset);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, (int)pc);
        il.Emit(OpCodes.Stind_I4);
    }
}
