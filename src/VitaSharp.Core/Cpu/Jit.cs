using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace VitaSharp.Core.Cpu;

public unsafe delegate void JitBlockFn(CpuState* s);

/// <summary>
/// Block JIT: emits one IL method per guest block that calls each instruction handler with the
/// instruction word and address as constants. RyuJIT inlines the handlers and folds the decoding,
/// turning the block into straight-line native code. Compilation runs on background threads.
/// </summary>
public static unsafe class Jit
{
    static readonly MethodInfo CondPassed = typeof(Arm).GetMethod(nameof(Arm.CondPassed), BindingFlags.Static | BindingFlags.Public)!;
    const int PcOffset = 15 * 4; // offsetof(CpuState, R[15])

    /// <summary>
    /// A block is compiled once it has executed this many guest instructions in total (executions x length): a
    /// DynamicMethod costs ~1 ms to create and JIT, so only code that will run millions of instructions pays it back.
    /// </summary>
    public static long HotInstructions { get; set; } = 400_000;
    public static bool Enabled { get; set; } = true;
    public static long CompiledBlocks, CompiledInstructions, CompileTicks;

    static readonly BlockingCollection<Block> Queue = new(new ConcurrentQueue<Block>());
    static int _workersStarted;

    public static void Enqueue(Block b)
    {
        if (!Enabled || Interlocked.CompareExchange(ref b.JitState, 1, 0) != 0) return;
        EnsureWorkers();
        Queue.Add(b);
    }

    static void EnsureWorkers()
    {
        if (Interlocked.Exchange(ref _workersStarted, 1) != 0) return;
        int n = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
        for (int i = 0; i < n; i++)
        {
            var t = new Thread(() =>
            {
                Util.NativeThread.Register("JIT worker");
                foreach (var b in Queue.GetConsumingEnumerable())
                {
                    try { Compile(b); }
                    catch (Exception e) { Console.Error.WriteLine($"[JIT] {b}: {e.Message}"); b.JitState = 3; }
                }
            })
            { IsBackground = true, Name = $"JIT worker {i}", Priority = ThreadPriority.BelowNormal };
            t.Start();
        }
    }

    public static void Compile(Block b)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var dm = new DynamicMethod($"blk_{b.Start:X8}{(b.Thumb ? "t" : "a")}", typeof(void), [typeof(CpuState*)], typeof(Jit).Module, skipVisibility: true);
        var il = dm.GetILGenerator();
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

        var fn = dm.CreateDelegate<JitBlockFn>();
        RuntimeHelpers.PrepareDelegate(fn); // compile to native here, off the guest thread
        b.Compiled = fn;
        b.JitState = 2;
        Interlocked.Increment(ref CompiledBlocks);
        Interlocked.Add(ref CompiledInstructions, insts.Length);
        Interlocked.Add(ref CompileTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
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
