using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// The IL tier: a block that has run <see cref="IlHotInstructions"/> instructions is compiled to a DynamicMethod
/// (Reflection.Emit) that does its whole work in straight-line code, the guest registers it uses in locals RyuJIT can
/// keep in host registers. Only blocks made of what the block cache runs inline compile: the plain ALU instructions,
/// lw/sw, add.s/sub.s/mul.s and vadd/vsub/vmul/vdot (calls to their handlers with the decoded fields as constants),
/// and a closing branch or jump whose delay slot is plain or one of those FPU/VFPU instructions. A misaligned lw/sw
/// leaves the compiled code before it (registers written back) for the interpreter to raise the address error at the
/// exact PC; a store that ends the run (an interrupt, decoded code overwritten) or an FPU/VFPU exception leaves it
/// after the instruction. The compiled code lives in its block, so
/// invalidation drops both. Native AOT builds (Android) have no Reflection.Emit: there
/// <see cref="IlTierSupported"/> is false and the block cache is the fast path.
/// </summary>
public sealed partial class AllegrexCore
{
    /// <summary>The default hotness: a compile costs about a millisecond, which a block repays after a few hundred
    /// thousand instructions.</summary>
    public const long DefaultIlHotInstructions = 200_000;

    private static readonly AsyncLocal<long?> s_forcedIlHotInstructions = new();

    private static readonly FieldInfo s_busField = typeof(AllegrexCore).GetField(nameof(_bus), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo s_endBlockField = typeof(AllegrexCore).GetField(nameof(_endBlock), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_store32 = typeof(AllegrexCore).GetMethod(nameof(Store32), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_read32 = typeof(IAllegrexBus).GetMethod(nameof(IAllegrexBus.Read32))!;
    private static readonly FieldInfo s_currentPcField = typeof(AllegrexCore).GetField(nameof(_currentPc), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo s_currentInDelaySlotField = typeof(AllegrexCore).GetField(nameof(_currentInDelaySlot), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_fpuArithmetic = typeof(AllegrexCore).GetMethod(nameof(FpuArithmeticCompiled), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_vfpuLanes = typeof(AllegrexCore).GetMethod(nameof(VfpuLanesCompiled), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_vfpuDot = typeof(AllegrexCore).GetMethod(nameof(VfpuDotCompiled), BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Compiled block: runs it with the registers at <paramref name="registers"/>; returns the next PC and,
    /// in <paramref name="executed"/>, the instructions completed (fewer than the block's when it left early).</summary>
    private delegate uint BlockCode(AllegrexCore core, ref uint registers, out int executed);

    /// <summary>Whether this runtime can compile (false under Native AOT).</summary>
    public static bool IlTierSupported => RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>Instructions a block runs before it is compiled, for cores created from now on (long.MaxValue: never).</summary>
    public static long IlHotInstructions { get; set; } = DefaultIlHotInstructions;

    /// <summary>Tests: cores created in this async flow compile every compilable block at once (or never, with
    /// long.MaxValue); dispose to restore.</summary>
    internal static IDisposable ForceIlTier(long hotInstructions = 0)
    {
        long? previous = s_forcedIlHotInstructions.Value;
        s_forcedIlHotInstructions.Value = hotInstructions;
        return new IlTierScope(previous);
    }

    private sealed class IlTierScope(long? previous) : IDisposable
    {
        public void Dispose()
        {
            s_forcedIlHotInstructions.Value = previous;
        }
    }

    private static long CurrentIlHotInstructions()
    {
        if (!IlTierSupported)
        {
            return long.MaxValue;
        }

        return s_forcedIlHotInstructions.Value ?? IlHotInstructions;
    }

    private readonly long _ilHotInstructions = CurrentIlHotInstructions();
    private int _compiledBlocks;

    /// <summary>Blocks compiled by this core (statistics, tests).</summary>
    public int CompiledBlocks => _compiledBlocks;

    /// <summary>Counts a run of <paramref name="block"/> and compiles it once it is hot.</summary>
    private void NoteBlockRun(Block block)
    {
        block.Executed += block.Count;
        if (block.Executed >= _ilHotInstructions && !block.CompileTried)
        {
            block.CompileTried = true;
            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                block.Code = CompileBlock(block);
            }
            if (block.Code != null)
            {
                _compiledBlocks++;
            }
        }
    }

    /// <summary>The FPU/VFPU instructions the IL tier compiles as calls to their handlers.</summary>
    private static bool IsCompiledCall(byte op)
    {
        return op == OpFpuArithmetic || op == OpVfpuLanes || op == OpVfpuDot;
    }

    private static bool IsBranchOp(byte op)
    {
        return op >= OpBeq && op <= OpJalr;
    }

    /// <summary>The block as a DynamicMethod, or null when it holds anything the tier does not compile.</summary>
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("The IL tier emits a DynamicMethod per block (Reflection.Emit); it is skipped when dynamic code is not supported.")]
    private static BlockCode? CompileBlock(Block block)
    {
        DecodedInstruction[] instructions = block.Instructions;
        int count = block.Count;
        int body = count;
        bool branch = false;
        if (count >= 2 && IsBranchOp(instructions[count - 2].Op) && (IsPlain(instructions[count - 1].Op) || IsCompiledCall(instructions[count - 1].Op)))
        {
            body = count - 2;
            branch = true;
        }

        for (int k = 0; k < body; k++)
        {
            byte op = instructions[k].Op;
            if (!IsPlain(op) && op != OpLw && op != OpSw && !IsCompiledCall(op))
            {
                return null;
            }
        }

        DynamicMethod method = new($"allegrex_{block.Start:X8}", typeof(uint), [typeof(AllegrexCore), typeof(uint).MakeByRefType(), typeof(int).MakeByRefType()], typeof(AllegrexCore), skipVisibility: true);
        BlockEmitter emitter = new(method.GetILGenerator(), instructions, count, block.Start);
        emitter.Emit(block.Start, body, branch);
        block.EndsInBranch = branch;
        return (BlockCode)method.CreateDelegate(typeof(BlockCode));
    }

    /// <summary>Emits one block: the guest registers it touches in locals, loaded at entry and written back at every exit.</summary>
    private sealed class BlockEmitter
    {
        private readonly ILGenerator _il;
        private readonly DecodedInstruction[] _instructions;
        private readonly int _count;
        private readonly LocalBuilder?[] _registers = new LocalBuilder?[32];
        private readonly bool[] _written = new bool[32];
        private readonly List<(Label Label, int Executed)> _exits = [];
        private readonly LocalBuilder _address;
        private readonly uint _start;

        public BlockEmitter(ILGenerator il, DecodedInstruction[] instructions, int count, uint start)
        {
            _il = il;
            _instructions = instructions;
            _count = count;
            _start = start;
            _address = il.DeclareLocal(typeof(uint));
        }

        public void Emit(uint start, int body, bool branch)
        {
            for (int k = 0; k < _count; k++)
            {
                ref DecodedInstruction d = ref _instructions[k];
                if (IsCompiledCall(d.Op))
                {
                    // the FPU/VFPU register numbers are not general registers
                    continue;
                }

                Use(d.S);
                Use(d.T);
                Use(d.D);
                if (d.Op == OpJal)
                {
                    Use(31);
                }
            }

            for (int k = 1; k < 32; k++)
            {
                if (_registers[k] != null)
                {
                    RegisterAddress(k);
                    _il.Emit(OpCodes.Ldind_U4);
                    _il.Emit(OpCodes.Stloc, _registers[k]!);
                }
            }

            for (int k = 0; k < body; k++)
            {
                EmitInstruction(ref _instructions[k], k);
            }

            if (branch)
            {
                LocalBuilder target = _il.DeclareLocal(typeof(uint));
                uint slot = start + (uint)((body + 1) * 4);
                EmitBranchTarget(ref _instructions[body], slot, target);
                EmitInstruction(ref _instructions[body + 1], body + 1, true);
                WriteBack();
                SetExecuted(_count);
                _il.Emit(OpCodes.Ldloc, target);
                _il.Emit(OpCodes.Ret);
            }
            else
            {
                WriteBack();
                SetExecuted(_count);
                _il.Emit(OpCodes.Ldc_I4, (int)(start + (uint)(_count * 4)));
                _il.Emit(OpCodes.Ret);
            }

            foreach ((Label label, int executed) in _exits)
            {
                _il.MarkLabel(label);
                WriteBack();
                SetExecuted(executed);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ret);
            }
        }

        private void Use(int register)
        {
            if (register != 0 && _registers[register] == null)
            {
                _registers[register] = _il.DeclareLocal(typeof(uint));
            }
        }

        private void RegisterAddress(int register)
        {
            _il.Emit(OpCodes.Ldarg_1);
            _il.Emit(OpCodes.Ldc_I4, register * 4);
            _il.Emit(OpCodes.Add);
        }

        private void Load(int register)
        {
            if (register == 0)
            {
                _il.Emit(OpCodes.Ldc_I4_0);
            }
            else
            {
                _il.Emit(OpCodes.Ldloc, _registers[register]!);
            }
        }

        private void Store(int register)
        {
            // the value is on the stack; r0 is never a destination here (those decode as nops or go to the decoder)
            _il.Emit(OpCodes.Stloc, _registers[register]!);
            _written[register] = true;
        }

        private void WriteBack()
        {
            for (int k = 1; k < 32; k++)
            {
                if (_written[k])
                {
                    RegisterAddress(k);
                    _il.Emit(OpCodes.Ldloc, _registers[k]!);
                    _il.Emit(OpCodes.Stind_I4);
                }
            }
        }

        private void SetExecuted(int executed)
        {
            _il.Emit(OpCodes.Ldarg_2);
            _il.Emit(OpCodes.Ldc_I4, executed);
            _il.Emit(OpCodes.Stind_I4);
        }

        /// <summary>A branch out of the compiled code that reports <paramref name="executed"/> instructions done.</summary>
        private Label Exit(int executed)
        {
            Label label = _il.DefineLabel();
            _exits.Add((label, executed));
            return label;
        }

        private void Binary(ref DecodedInstruction d, OpCode operation)
        {
            Load(d.S);
            Load(d.T);
            _il.Emit(operation);
            Store(d.D);
        }

        private void WithImmediate(ref DecodedInstruction d, OpCode operation)
        {
            Load(d.S);
            _il.Emit(OpCodes.Ldc_I4, (int)d.Immediate);
            _il.Emit(operation);
            Store(d.D);
        }

        private void Shift(ref DecodedInstruction d, OpCode operation)
        {
            Load(d.T);
            _il.Emit(OpCodes.Ldc_I4, (int)d.Immediate);
            _il.Emit(operation);
            Store(d.D);
        }

        private void ConditionalMove(ref DecodedInstruction d, bool whenZero)
        {
            Label skip = _il.DefineLabel();
            Load(d.T);
            _il.Emit(whenZero ? OpCodes.Brtrue : OpCodes.Brfalse, skip);
            Load(d.S);
            Store(d.D);
            _il.MarkLabel(skip);
        }

        /// <summary>The effective address into the address local; a misaligned one leaves before instruction <paramref name="k"/>.</summary>
        private void Address(ref DecodedInstruction d, int k)
        {
            Load(d.S);
            _il.Emit(OpCodes.Ldc_I4, (int)d.Immediate);
            _il.Emit(OpCodes.Add);
            _il.Emit(OpCodes.Stloc, _address);
            _il.Emit(OpCodes.Ldloc, _address);
            _il.Emit(OpCodes.Ldc_I4_3);
            _il.Emit(OpCodes.And);
            _il.Emit(OpCodes.Brtrue, Exit(k));
            _il.Emit(OpCodes.Ldloc, _address);
            _il.Emit(OpCodes.Ldc_I4, (int)PhysicalMask);
            _il.Emit(OpCodes.And);
        }

        private void EmitInstruction(ref DecodedInstruction d, int k, bool delaySlot = false)
        {
            if (IsCompiledCall(d.Op))
            {
                EmitCall(ref d, k, delaySlot);
                return;
            }

            switch (d.Op)
            {
                case OpNop:
                    break;
                case OpSll:
                    Shift(ref d, OpCodes.Shl);
                    break;
                case OpSrl:
                    Shift(ref d, OpCodes.Shr_Un);
                    break;
                case OpSra:
                    Shift(ref d, OpCodes.Shr);
                    break;
                case OpSllv:
                    Load(d.T);
                    Load(d.S);
                    _il.Emit(OpCodes.Ldc_I4, 0x1F);
                    _il.Emit(OpCodes.And);
                    _il.Emit(OpCodes.Shl);
                    Store(d.D);
                    break;
                case OpMovz:
                    ConditionalMove(ref d, true);
                    break;
                case OpMovn:
                    ConditionalMove(ref d, false);
                    break;
                case OpAddu:
                    Binary(ref d, OpCodes.Add);
                    break;
                case OpSubu:
                    Binary(ref d, OpCodes.Sub);
                    break;
                case OpAnd:
                    Binary(ref d, OpCodes.And);
                    break;
                case OpOr:
                    Binary(ref d, OpCodes.Or);
                    break;
                case OpXor:
                    Binary(ref d, OpCodes.Xor);
                    break;
                case OpNor:
                    Load(d.S);
                    Load(d.T);
                    _il.Emit(OpCodes.Or);
                    _il.Emit(OpCodes.Not);
                    Store(d.D);
                    break;
                case OpSlt:
                    Binary(ref d, OpCodes.Clt);
                    break;
                case OpSltu:
                    Binary(ref d, OpCodes.Clt_Un);
                    break;
                case OpAddiu:
                    WithImmediate(ref d, OpCodes.Add);
                    break;
                case OpSlti:
                    WithImmediate(ref d, OpCodes.Clt);
                    break;
                case OpSltiu:
                    WithImmediate(ref d, OpCodes.Clt_Un);
                    break;
                case OpAndi:
                    WithImmediate(ref d, OpCodes.And);
                    break;
                case OpOri:
                    WithImmediate(ref d, OpCodes.Or);
                    break;
                case OpXori:
                    WithImmediate(ref d, OpCodes.Xor);
                    break;
                case OpLui:
                    _il.Emit(OpCodes.Ldc_I4, (int)d.Immediate);
                    Store(d.D);
                    break;
                case OpLw:
                {
                    LocalBuilder physical = _il.DeclareLocal(typeof(uint));
                    Address(ref d, k);
                    _il.Emit(OpCodes.Stloc, physical);
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldfld, s_busField);
                    _il.Emit(OpCodes.Ldloc, physical);
                    _il.Emit(OpCodes.Callvirt, s_read32);
                    Store(d.D);
                    break;
                }

                case OpSw:
                {
                    LocalBuilder physical = _il.DeclareLocal(typeof(uint));
                    Address(ref d, k);
                    _il.Emit(OpCodes.Stloc, physical);
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldloc, physical);
                    Load(d.T);
                    _il.Emit(OpCodes.Call, s_store32);

                    // the store ended the run (an interrupt, or decoded code overwritten): leave after it
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldfld, s_endBlockField);
                    _il.Emit(OpCodes.Brtrue, Exit(k + 1));
                    break;
                }

                default:
                    throw new InvalidOperationException($"the IL tier does not compile operation {d.Op}");
            }
        }

        /// <summary>An FPU/VFPU instruction: its exception PC, then its handler with the decoded fields; an exception
        /// leaves after it.</summary>
        private void EmitCall(ref DecodedInstruction d, int k, bool delaySlot)
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4, (int)(_start + (uint)(k * 4)));
            _il.Emit(OpCodes.Stfld, s_currentPcField);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(delaySlot ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Stfld, s_currentInDelaySlotField);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4, (int)d.Word);
            switch (d.Op)
            {
                case OpFpuArithmetic:
                    _il.Emit(OpCodes.Ldc_I4, (int)d.D);
                    _il.Emit(OpCodes.Ldc_I4, (int)d.S);
                    _il.Emit(OpCodes.Ldc_I4, (int)d.T);
                    _il.Emit(OpCodes.Ldc_I4, (int)d.Immediate);
                    _il.Emit(OpCodes.Call, s_fpuArithmetic);
                    break;
                case OpVfpuLanes:
                    _il.Emit(OpCodes.Ldc_I4, (int)d.D);
                    _il.Emit(OpCodes.Ldc_I4, (int)d.Immediate);
                    _il.Emit(OpCodes.Ldc_I4, (int)d.LanesS);
                    _il.Emit(OpCodes.Ldc_I4, (int)d.LanesT);
                    _il.Emit(OpCodes.Ldc_I4, (int)d.LanesD);
                    _il.Emit(OpCodes.Call, s_vfpuLanes);
                    break;
                default:
                    _il.Emit(OpCodes.Ldc_I4, (int)d.D);
                    _il.Emit(OpCodes.Ldc_I4, (int)d.LanesS);
                    _il.Emit(OpCodes.Ldc_I4, (int)d.LanesT);
                    _il.Emit(OpCodes.Ldc_I4, (int)d.LanesD);
                    _il.Emit(OpCodes.Call, s_vfpuDot);
                    break;
            }

            // only an exception ends the run here
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, s_endBlockField);
            _il.Emit(OpCodes.Brtrue, Exit(k + 1));
        }

        /// <summary>The branch's target into <paramref name="target"/> (its registers read before the delay slot runs, the link written).</summary>
        private void EmitBranchTarget(ref DecodedInstruction d, uint slot, LocalBuilder target)
        {
            switch (d.Op)
            {
                case OpBeq:
                case OpBne:
                {
                    Label notTaken = _il.DefineLabel();
                    Label done = _il.DefineLabel();
                    Load(d.S);
                    Load(d.T);
                    _il.Emit(d.Op == OpBeq ? OpCodes.Bne_Un : OpCodes.Beq, notTaken);
                    _il.Emit(OpCodes.Ldc_I4, (int)(slot + d.Immediate));
                    _il.Emit(OpCodes.Stloc, target);
                    _il.Emit(OpCodes.Br, done);
                    _il.MarkLabel(notTaken);
                    _il.Emit(OpCodes.Ldc_I4, (int)(slot + 4));
                    _il.Emit(OpCodes.Stloc, target);
                    _il.MarkLabel(done);
                    break;
                }

                case OpJ:
                case OpJal:
                    _il.Emit(OpCodes.Ldc_I4, (int)((slot & 0xF0000000) | d.Immediate));
                    _il.Emit(OpCodes.Stloc, target);
                    if (d.Op == OpJal)
                    {
                        _il.Emit(OpCodes.Ldc_I4, (int)(slot + 4));
                        Store(31);
                    }

                    break;
                case OpJr:
                    Load(d.S);
                    _il.Emit(OpCodes.Stloc, target);
                    break;
                default:
                    // jalr: the target before the link (rd may be rs)
                    Load(d.S);
                    _il.Emit(OpCodes.Stloc, target);
                    _il.Emit(OpCodes.Ldc_I4, (int)(slot + 4));
                    Store(d.D);
                    break;
            }
        }
    }
}
