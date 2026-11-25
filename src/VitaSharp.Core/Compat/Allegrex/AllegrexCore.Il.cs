using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// The IL tier: a block that has run <see cref="IlHotInstructions"/> instructions is compiled, with the decoded blocks
/// its branches reach (a region of up to <see cref="MaxRegionBlocks"/>), into one DynamicMethod (Reflection.Emit).
/// Inside it a branch to a block of the region is a jump, so a loop runs in compiled code until its instruction budget
/// is spent; a branch elsewhere (or a jr whose target is not in the region) leaves with the next PC. The guest
/// registers it touches (general, FPU, VFPU lanes, FCR31) live in locals RyuJIT keeps in host registers, loaded at
/// entry and written back at every exit. Compiled: the plain ALU instructions, lw/sw, add.s/sub.s/mul.s (their fast
/// path inline through <see cref="FpuFastResult"/>, the full handler when it does not apply) and vadd/vsub/vmul/vdot
/// with the prefixes at rest (checked at entry), and a closing branch or jump whose delay slot is one of those.
/// Exactness: a block is entered only when the budget holds all of it (the Count/Compare cap and the run's limit stay
/// exact); a misaligned lw/sw leaves before it, so the interpreter raises the address error at its PC; a store that
/// ends the run and an FPU exception (the PC and delay-slot flag stored before the handler) leave after it. The code
/// lives in its head block and every member keeps the head as a dependent, so invalidating any member drops it.
/// Native AOT builds (Android) have no Reflection.Emit: there <see cref="IlTierSupported"/> is false and the block
/// cache is the fast path.
/// </summary>
public sealed partial class AllegrexCore
{
    /// <summary>The default hotness: a compile costs about a millisecond, which a block repays after a few hundred
    /// thousand instructions.</summary>
    public const long DefaultIlHotInstructions = 200_000;

    /// <summary>The most blocks one compiled region takes.</summary>
    public const int MaxRegionBlocks = 16;

    private static readonly AsyncLocal<long?> s_forcedIlHotInstructions = new();
    private static readonly AsyncLocal<int?> s_forcedBlockLimit = new();

    private static readonly FieldInfo s_busField = typeof(AllegrexCore).GetField(nameof(_bus), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo s_endBlockField = typeof(AllegrexCore).GetField(nameof(_endBlock), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo s_currentPcField = typeof(AllegrexCore).GetField(nameof(_currentPc), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo s_currentInDelaySlotField = typeof(AllegrexCore).GetField(nameof(_currentInDelaySlot), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo s_fprField = typeof(AllegrexCore).GetField(nameof(_fpr), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo s_vfprField = typeof(AllegrexCore).GetField(nameof(_vfpr), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo s_fcr31Field = typeof(AllegrexCore).GetField(nameof(_fcr31), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_store32 = typeof(AllegrexCore).GetMethod(nameof(Store32), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_read32 = typeof(IAllegrexBus).GetMethod(nameof(IAllegrexBus.Read32))!;
    private static readonly MethodInfo s_fpuArithmetic = typeof(AllegrexCore).GetMethod(nameof(FpuArithmeticCompiled), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_fpuAdd = typeof(AllegrexCore).GetMethod(nameof(FpuAddFloat), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo s_fpuAddInexact = typeof(AllegrexCore).GetMethod(nameof(FpuAddInexact), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo s_fpuMultiply = typeof(AllegrexCore).GetMethod(nameof(FpuMultiplyFloat), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo s_fpuMultiplyInexact = typeof(AllegrexCore).GetMethod(nameof(FpuMultiplyInexact), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo s_bitsToSingle = typeof(BitConverter).GetMethod(nameof(BitConverter.UInt32BitsToSingle))!;
    private static readonly MethodInfo s_singleToBits = typeof(BitConverter).GetMethod(nameof(BitConverter.SingleToUInt32Bits))!;
    private static readonly MethodInfo s_prefixesAtRest = typeof(AllegrexCore).GetMethod(nameof(VfpuPrefixesAtRest), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_vfpuDot = typeof(AllegrexCore).GetMethod(nameof(VfpuDot4), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo s_vfpuAdd = typeof(AllegrexVfpuMath).GetMethod(nameof(AllegrexVfpuMath.Add))!;
    private static readonly MethodInfo s_vfpuSubtract = typeof(AllegrexVfpuMath).GetMethod(nameof(AllegrexVfpuMath.Subtract))!;
    private static readonly MethodInfo s_vfpuMultiply = typeof(AllegrexVfpuMath).GetMethod(nameof(AllegrexVfpuMath.Multiply))!;

    /// <summary>Compiled region: runs from its head block with the general registers at <paramref name="registers"/>
    /// and at most <paramref name="budget"/> instructions; returns the next PC and, in <paramref name="executed"/>,
    /// the instructions completed (after an exception the PC fields already hold the vector and the result is 0).</summary>
    private delegate uint BlockCode(AllegrexCore core, ref uint registers, int budget, out int executed);

    /// <summary>Whether this runtime can compile (false under Native AOT).</summary>
    public static bool IlTierSupported => RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>Instructions a block runs before it is compiled, for cores created from now on (long.MaxValue: never).</summary>
    public static long IlHotInstructions { get; set; } = DefaultIlHotInstructions;

    private readonly long _ilHotInstructions = CurrentIlHotInstructions();
    private int _compiledBlocks;

    /// <summary>Regions compiled by this core (statistics, tests).</summary>
    public int CompiledBlocks => _compiledBlocks;

    /// <summary>Tests: cores created in this async flow compile every compilable block at once (or never, with
    /// long.MaxValue), with blocks of at most <paramref name="blockLimit"/> instructions (one, a branch with its delay
    /// slot, puts every instruction in compiled code even when a test runs a single one); dispose to restore.</summary>
    internal static IDisposable ForceIlTier(long hotInstructions = 0, int blockLimit = MaxBlockInstructions)
    {
        long? previous = s_forcedIlHotInstructions.Value;
        int? previousLimit = s_forcedBlockLimit.Value;
        s_forcedIlHotInstructions.Value = hotInstructions;
        s_forcedBlockLimit.Value = blockLimit;
        return new IlTierScope(previous, previousLimit);
    }

    private sealed class IlTierScope(long? previous, int? previousLimit) : IDisposable
    {
        public void Dispose()
        {
            s_forcedIlHotInstructions.Value = previous;
            s_forcedBlockLimit.Value = previousLimit;
        }
    }

    // the most instructions a block takes (fewer only in tests, ForceIlTier)
    private readonly int _blockLimit = s_forcedBlockLimit.Value ?? MaxBlockInstructions;

    private static long CurrentIlHotInstructions()
    {
        if (!IlTierSupported)
        {
            return long.MaxValue;
        }

        return s_forcedIlHotInstructions.Value ?? IlHotInstructions;
    }

    /// <summary>Counts a run of <paramref name="block"/> and compiles its region once it is hot.</summary>
    private void NoteBlockRun(Block block)
    {
        block.Executed += block.Count;
        if (block.Executed >= _ilHotInstructions && !block.CompileTried)
        {
            block.CompileTried = true;
            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                block.Code = CompileRegion(block);
            }

            if (block.Code != null)
            {
                _compiledBlocks++;
            }
        }
    }

    /// <summary>The FPU/VFPU instructions the IL tier compiles.</summary>
    private static bool IsCompiledFloat(byte op)
    {
        return op == OpFpuArithmetic || op == OpVfpuLanes || op == OpVfpuDot;
    }

    private static bool IsBranchOp(byte op)
    {
        return op >= OpBeq && op <= OpJalr;
    }

    /// <summary>Whether the tier compiles <paramref name="block"/>: plain, lw/sw and FPU/VFPU instructions, closed by a
    /// branch with such a delay slot (not a load or store) or by the page end.</summary>
    private bool IsCompilable(Block block)
    {
        DecodedInstruction[] instructions = block.Instructions;
        int count = block.Count;
        int body = count;
        if (count >= 2 && IsBranchOp(instructions[count - 2].Op))
        {
            byte slot = instructions[count - 1].Op;
            if (!IsPlain(slot) && !IsCompiledFloat(slot))
            {
                return false;
            }

            body = count - 2;
        }

        for (int k = 0; k < body; k++)
        {
            byte op = instructions[k].Op;
            if (!IsPlain(op) && op != OpLw && op != OpSw && !IsCompiledFloat(op))
            {
                return false;
            }
        }

        // a block cut by a system instruction or one ending in a branch without its slot does not compile
        return body == count - 2 || ((block.Start + (uint)(count * 4)) & ((1u << PageShift) - 1)) == 0 || count >= _blockLimit;
    }

    /// <summary>The decoded block at <paramref name="pc"/> of the current generation, or null.</summary>
    private Block? DecodedBlockAt(uint pc)
    {
        uint physical = pc & PhysicalMask;
        Block?[]? blocks = _pages[physical >> PageShift];
        Block? block = blocks?[(int)((physical >> 2) & (WordsPerPage - 1))];
        return block != null && block.Generation == _generation && block.Start == pc ? block : null;
    }

    /// <summary>The PCs a block's closing branch or fall-through can go to that are known when it is compiled.</summary>
    private static void StaticSuccessors(Block block, List<uint> successors)
    {
        int count = block.Count;
        if (count >= 2 && IsBranchOp(block.Instructions[count - 2].Op))
        {
            ref DecodedInstruction branch = ref block.Instructions[count - 2];
            uint slot = block.Start + (uint)((count - 1) * 4);
            switch (branch.Op)
            {
                case OpBeq:
                case OpBne:
                    successors.Add(slot + branch.Immediate);
                    successors.Add(slot + 4);
                    break;
                case OpJ:
                case OpJal:
                    successors.Add((slot & 0xF0000000) | branch.Immediate);
                    break;
            }

            return;
        }

        successors.Add(block.Start + (uint)(count * 4));
    }

    /// <summary>The region of <paramref name="head"/> as a DynamicMethod, or null when the head does not compile.</summary>
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("The IL tier emits a DynamicMethod per region (Reflection.Emit); it is skipped when dynamic code is not supported.")]
    private BlockCode? CompileRegion(Block head)
    {
        if (!IsCompilable(head))
        {
            return null;
        }

        List<Block> region = [head];
        List<uint> successors = [];
        for (int b = 0; b < region.Count && region.Count < MaxRegionBlocks; b++)
        {
            successors.Clear();
            StaticSuccessors(region[b], successors);
            foreach (uint pc in successors)
            {
                Block? next = DecodedBlockAt(pc);
                if (next != null && !region.Contains(next) && IsCompilable(next) && region.Count < MaxRegionBlocks)
                {
                    region.Add(next);
                }
            }
        }

        DynamicMethod method = new($"allegrex_{head.Start:X8}", typeof(uint), [typeof(AllegrexCore), typeof(uint).MakeByRefType(), typeof(int), typeof(int).MakeByRefType()], typeof(AllegrexCore), skipVisibility: true);
        RegionEmitter emitter = new(method.GetILGenerator(), region);
        emitter.Emit();
        foreach (Block member in region)
        {
            member.Dependents ??= [];
            member.Dependents.Add(head);
        }

        return (BlockCode)method.CreateDelegate(typeof(BlockCode));
    }

    /// <summary>Emits one region: every guest register it touches in a local, loaded at entry and written back at
    /// every exit; a section per block.</summary>
    private sealed class RegionEmitter
    {
        private readonly ILGenerator _il;
        private readonly List<Block> _region;
        private readonly Label[] _sections;
        private readonly LocalBuilder?[] _gpr = new LocalBuilder?[32];
        private readonly bool[] _gprWritten = new bool[32];
        private readonly LocalBuilder?[] _fpr = new LocalBuilder?[32];
        private readonly bool[] _fprWritten = new bool[32];
        private readonly LocalBuilder?[] _vfpr = new LocalBuilder?[128];
        private readonly bool[] _vfprWritten = new bool[128];
        private LocalBuilder? _fcr31;
        private bool _fcr31Written;
        private readonly LocalBuilder _consumed;
        private readonly LocalBuilder _address;
        private readonly LocalBuilder _target;
        private readonly LocalBuilder _result;
        private readonly LocalBuilder[] _lanes = new LocalBuilder[4];
        private bool _usesVfpu;

        public RegionEmitter(ILGenerator il, List<Block> region)
        {
            _il = il;
            _region = region;
            _sections = new Label[region.Count];
            for (int b = 0; b < region.Count; b++)
            {
                _sections[b] = il.DefineLabel();
            }

            _consumed = il.DeclareLocal(typeof(int));
            _address = il.DeclareLocal(typeof(uint));
            _target = il.DeclareLocal(typeof(uint));
            _result = il.DeclareLocal(typeof(float));
            for (int i = 0; i < 4; i++)
            {
                _lanes[i] = il.DeclareLocal(typeof(uint));
            }
        }

        public void Emit()
        {
            // every register the region writes is written back at every exit (a loop reaches an early exit after
            // later instructions ran), so the sets are known before any code is emitted
            foreach (Block block in _region)
            {
                for (int k = 0; k < block.Count; k++)
                {
                    Declare(ref block.Instructions[k]);
                    MarkWritten(ref block.Instructions[k]);
                }
            }

            if (_fcr31 != null)
            {
                // the FPU instructions compile for round-to-nearest with no exception enabled (only ctc1, which does
                // not compile, changes that): otherwise the interpreter takes the region's first instruction
                Label nearest = _il.DefineLabel();
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, s_fcr31Field);
                _il.Emit(OpCodes.Ldc_I4, 0xF83);
                _il.Emit(OpCodes.And);
                _il.Emit(OpCodes.Brfalse, nearest);
                _il.Emit(OpCodes.Ldarg_3);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Stind_I4);
                _il.Emit(OpCodes.Ldc_I4, (int)_region[0].Start);
                _il.Emit(OpCodes.Ret);
                _il.MarkLabel(nearest);
            }

            if (_usesVfpu)
            {
                // the VFPU instructions compile for prefixes at rest: otherwise the interpreter takes the first one
                Label atRest = _il.DefineLabel();
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Call, s_prefixesAtRest);
                _il.Emit(OpCodes.Brtrue, atRest);
                _il.Emit(OpCodes.Ldarg_3);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Stind_I4);
                _il.Emit(OpCodes.Ldc_I4, (int)_region[0].Start);
                _il.Emit(OpCodes.Ret);
                _il.MarkLabel(atRest);
            }

            LoadAll();
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Stloc, _consumed);
            for (int b = 0; b < _region.Count; b++)
            {
                EmitBlock(b);
            }
        }

        private void Declare(ref DecodedInstruction d)
        {
            switch (d.Op)
            {
                case OpFpuArithmetic:
                    DeclareFpr(d.D);
                    DeclareFpr(d.S);
                    DeclareFpr(d.T);
                    _fcr31 ??= _il.DeclareLocal(typeof(uint));
                    return;
                case OpVfpuLanes:
                case OpVfpuDot:
                    _usesVfpu = true;
                    for (int i = 0; i < d.D; i++)
                    {
                        DeclareVfpr((int)((d.LanesS >> (i * 8)) & 0x7F));
                        DeclareVfpr((int)((d.LanesT >> (i * 8)) & 0x7F));
                        if (d.Op == OpVfpuLanes || i == 0)
                        {
                            DeclareVfpr((int)((d.LanesD >> (i * 8)) & 0x7F));
                        }
                    }

                    return;
                default:
                    DeclareGpr(d.S);
                    DeclareGpr(d.T);
                    DeclareGpr(d.D);
                    if (d.Op == OpJal)
                    {
                        DeclareGpr(31);
                    }

                    return;
            }
        }

        private void MarkWritten(ref DecodedInstruction d)
        {
            switch (d.Op)
            {
                case OpNop:
                case OpSw:
                case OpBeq:
                case OpBne:
                case OpJ:
                case OpJr:
                    return;
                case OpJal:
                    _gprWritten[31] = true;
                    return;
                case OpFpuArithmetic:
                    _fprWritten[d.D] = true;
                    _fcr31Written = true;
                    return;
                case OpVfpuLanes:
                    for (int i = 0; i < d.D; i++)
                    {
                        _vfprWritten[(d.LanesD >> (i * 8)) & 0x7F] = true;
                    }

                    return;
                case OpVfpuDot:
                    _vfprWritten[d.LanesD & 0x7F] = true;
                    return;
                default:
                    // the plain ALU instructions, lw and jalr write D (never r0 here)
                    if (d.D != 0)
                    {
                        _gprWritten[d.D] = true;
                    }

                    return;
            }
        }

        private void DeclareGpr(int register)
        {
            if (register != 0 && _gpr[register] == null)
            {
                _gpr[register] = _il.DeclareLocal(typeof(uint));
            }
        }

        private void DeclareFpr(int register)
        {
            // as floats, bit-cast at the boundaries: the arithmetic's dependency chain is then the float operation alone
            _fpr[register] ??= _il.DeclareLocal(typeof(float));
        }

        private void DeclareVfpr(int index)
        {
            _vfpr[index] ??= _il.DeclareLocal(typeof(uint));
        }

        // ---- loading and writing back the register locals

        private void LoadAll()
        {
            for (int k = 1; k < 32; k++)
            {
                if (_gpr[k] != null)
                {
                    GprAddress(k);
                    _il.Emit(OpCodes.Ldind_U4);
                    _il.Emit(OpCodes.Stloc, _gpr[k]!);
                }
            }

            LoadFloat();
        }

        /// <summary>The FPU/VFPU locals and FCR31 from the core (at entry, and after a full FPU handler ran).</summary>
        private void LoadFloat()
        {
            LoadArray(s_fprField, _fpr, true);
            LoadArray(s_vfprField, _vfpr);
            if (_fcr31 != null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, s_fcr31Field);
                _il.Emit(OpCodes.Stloc, _fcr31);
            }
        }

        private void LoadArray(FieldInfo field, LocalBuilder?[] locals, bool asFloat = false)
        {
            for (int k = 0; k < locals.Length; k++)
            {
                if (locals[k] != null)
                {
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldfld, field);
                    _il.Emit(OpCodes.Ldc_I4, k);
                    _il.Emit(OpCodes.Ldelem_U4);
                    if (asFloat)
                    {
                        _il.Emit(OpCodes.Call, s_bitsToSingle);
                    }

                    _il.Emit(OpCodes.Stloc, locals[k]!);
                }
            }
        }

        private void WriteBackAll()
        {
            for (int k = 1; k < 32; k++)
            {
                if (_gprWritten[k])
                {
                    GprAddress(k);
                    _il.Emit(OpCodes.Ldloc, _gpr[k]!);
                    _il.Emit(OpCodes.Stind_I4);
                }
            }

            WriteBackFloat();
        }

        private void WriteBackFloat()
        {
            WriteBackArray(s_fprField, _fpr, _fprWritten, true);
            WriteBackArray(s_vfprField, _vfpr, _vfprWritten);
            if (_fcr31Written)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldloc, _fcr31!);
                _il.Emit(OpCodes.Stfld, s_fcr31Field);
            }
        }

        private void WriteBackArray(FieldInfo field, LocalBuilder?[] locals, bool[] written, bool asFloat = false)
        {
            for (int k = 0; k < locals.Length; k++)
            {
                if (written[k])
                {
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldfld, field);
                    _il.Emit(OpCodes.Ldc_I4, k);
                    _il.Emit(OpCodes.Ldloc, locals[k]!);
                    if (asFloat)
                    {
                        _il.Emit(OpCodes.Call, s_singleToBits);
                    }

                    _il.Emit(OpCodes.Stelem_I4);
                }
            }
        }

        private void GprAddress(int register)
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
                _il.Emit(OpCodes.Ldloc, _gpr[register]!);
            }
        }

        private void Store(int register)
        {
            // the value is on the stack; r0 is never a destination here (those decode as nops or go to the decoder)
            _il.Emit(OpCodes.Stloc, _gpr[register]!);
        }

        // ---- exits

        /// <summary>Leaves with the next PC (on the stack is nothing; <paramref name="pc"/> pushes it) and the
        /// instructions done: the earlier blocks' plus <paramref name="offset"/>.</summary>
        private void Exit(Action pc, int offset)
        {
            WriteBackAll();
            _il.Emit(OpCodes.Ldarg_3);
            _il.Emit(OpCodes.Ldloc, _consumed);
            _il.Emit(OpCodes.Ldc_I4, offset);
            _il.Emit(OpCodes.Add);
            _il.Emit(OpCodes.Stind_I4);
            pc();
            _il.Emit(OpCodes.Ret);
        }

        private void ExitAt(uint pc, int offset)
        {
            Exit(() => _il.Emit(OpCodes.Ldc_I4, (int)pc), offset);
        }

        /// <summary>Goes to the block at <paramref name="pc"/>: its section when it is in the region, else out.</summary>
        private void GoTo(uint pc)
        {
            int index = _region.FindIndex(b => b.Start == pc);
            if (index >= 0)
            {
                _il.Emit(OpCodes.Br, _sections[index]);
            }
            else
            {
                ExitAt(pc, 0);
            }
        }

        // ---- blocks

        private void EmitBlock(int b)
        {
            Block block = _region[b];
            DecodedInstruction[] instructions = block.Instructions;
            int count = block.Count;
            _il.MarkLabel(_sections[b]);

            // entered only when the budget holds the whole block
            Label fits = _il.DefineLabel();
            _il.Emit(OpCodes.Ldarg_2);
            _il.Emit(OpCodes.Ldloc, _consumed);
            _il.Emit(OpCodes.Sub);
            _il.Emit(OpCodes.Ldc_I4, count);
            _il.Emit(OpCodes.Bge, fits);
            ExitAt(block.Start, 0);
            _il.MarkLabel(fits);

            bool branch = count >= 2 && IsBranchOp(instructions[count - 2].Op);
            int body = branch ? count - 2 : count;
            for (int k = 0; k < body; k++)
            {
                EmitInstruction(block, k, false);
            }

            if (!branch)
            {
                Consume(count);
                GoTo(block.Start + (uint)(count * 4));
                return;
            }

            ref DecodedInstruction d = ref instructions[body];
            uint slot = block.Start + (uint)((body + 1) * 4);
            EmitBranchTarget(ref d, slot);
            EmitInstruction(block, body + 1, true);
            Consume(count);
            switch (d.Op)
            {
                case OpBeq:
                case OpBne:
                    DispatchTarget([slot + d.Immediate, slot + 4]);
                    break;
                case OpJ:
                case OpJal:
                    GoTo((slot & 0xF0000000) | d.Immediate);
                    break;
                default:
                    // jr/jalr: any block of the region, else out
                    DispatchTarget(_region.ConvertAll(r => r.Start).ToArray());
                    break;
            }
        }

        private void Consume(int count)
        {
            _il.Emit(OpCodes.Ldloc, _consumed);
            _il.Emit(OpCodes.Ldc_I4, count);
            _il.Emit(OpCodes.Add);
            _il.Emit(OpCodes.Stloc, _consumed);
        }

        /// <summary>The target local against the candidates in the region: a jump to the one it is, else out with it.</summary>
        private void DispatchTarget(uint[] candidates)
        {
            foreach (uint pc in candidates.Distinct())
            {
                int index = _region.FindIndex(b => b.Start == pc);
                if (index >= 0)
                {
                    _il.Emit(OpCodes.Ldloc, _target);
                    _il.Emit(OpCodes.Ldc_I4, (int)pc);
                    _il.Emit(OpCodes.Beq, _sections[index]);
                }
            }

            Exit(() => _il.Emit(OpCodes.Ldloc, _target), 0);
        }

        // ---- instructions

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

        /// <summary>The physical address on the stack; a misaligned one leaves before instruction <paramref name="k"/>
        /// (whose PC the interpreter then raises the error at).</summary>
        private void Address(ref DecodedInstruction d, Block block, int k)
        {
            Label aligned = _il.DefineLabel();
            Load(d.S);
            _il.Emit(OpCodes.Ldc_I4, (int)d.Immediate);
            _il.Emit(OpCodes.Add);
            _il.Emit(OpCodes.Stloc, _address);
            _il.Emit(OpCodes.Ldloc, _address);
            _il.Emit(OpCodes.Ldc_I4_3);
            _il.Emit(OpCodes.And);
            _il.Emit(OpCodes.Brfalse, aligned);
            ExitAt(block.Start + (uint)(k * 4), k);
            _il.MarkLabel(aligned);
            _il.Emit(OpCodes.Ldloc, _address);
            _il.Emit(OpCodes.Ldc_I4, (int)PhysicalMask);
            _il.Emit(OpCodes.And);
        }

        private void EmitInstruction(Block block, int k, bool delaySlot)
        {
            ref DecodedInstruction d = ref block.Instructions[k];
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
                    Address(ref d, block, k);
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
                    Label goOn = _il.DefineLabel();
                    Address(ref d, block, k);
                    _il.Emit(OpCodes.Stloc, physical);
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldloc, physical);
                    Load(d.T);
                    _il.Emit(OpCodes.Call, s_store32);

                    // the store ended the run (an interrupt, or decoded code overwritten): leave after it
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldfld, s_endBlockField);
                    _il.Emit(OpCodes.Brfalse, goOn);
                    ExitAt(block.Start + (uint)((k + 1) * 4), k + 1);
                    _il.MarkLabel(goOn);
                    break;
                }

                case OpFpuArithmetic:
                    EmitFpuArithmetic(ref d, block, k, delaySlot);
                    break;
                case OpVfpuLanes:
                    EmitVfpuLanes(ref d);
                    break;
                case OpVfpuDot:
                    EmitVfpuDot(ref d);
                    break;
                default:
                    throw new InvalidOperationException($"the IL tier does not compile operation {d.Op}");
            }
        }

        /// <summary>
        /// add.s/sub.s/mul.s: the fast result from the locals; committed inline when no enabled exception comes of
        /// it, else (or when the fast path does not apply) the full handler on the written-back state, which may take
        /// the exception: then the code leaves after the instruction.
        /// </summary>
        private void EmitFpuArithmetic(ref DecodedInstruction d, Block block, int k, bool delaySlot)
        {
            Label slow = _il.DefineLabel();
            Label done = _il.DefineLabel();
            // the region entered in round-to-nearest with no exception enabled, so the fast result commits as is;
            // sub.s is add.s of the negated right operand (x - y is x + (-y) in IEEE, signed zeros included)
            bool multiply = d.Immediate == 2;
            _il.Emit(OpCodes.Ldloc, _fpr[d.S]!);
            _il.Emit(OpCodes.Ldloc, _fpr[d.T]!);
            if (d.Immediate == 1)
            {
                _il.Emit(OpCodes.Neg);
            }

            _il.Emit(OpCodes.Call, multiply ? s_fpuMultiply : s_fpuAdd);
            _il.Emit(OpCodes.Stloc, _result);

            // NaN (never a fast result) asks for the full handler
            _il.Emit(OpCodes.Ldloc, _result);
            _il.Emit(OpCodes.Ldloc, _result);
            _il.Emit(OpCodes.Bne_Un, slow);

            // fcr31 = (fcr31 & ~causes) | (e << 12) | (e << 2); fd = result
            _il.Emit(OpCodes.Ldloc, _fcr31!);
            _il.Emit(OpCodes.Ldc_I4, unchecked((int)~FpuCauseMask));
            _il.Emit(OpCodes.And);
            _il.Emit(OpCodes.Ldloc, _fpr[d.S]!);
            _il.Emit(OpCodes.Ldloc, _fpr[d.T]!);
            if (d.Immediate == 1)
            {
                _il.Emit(OpCodes.Neg);
            }

            _il.Emit(OpCodes.Ldloc, _result);
            _il.Emit(OpCodes.Call, multiply ? s_fpuMultiplyInexact : s_fpuAddInexact);
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Stloc, _address);
            _il.Emit(OpCodes.Ldc_I4, 12);
            _il.Emit(OpCodes.Shl);
            _il.Emit(OpCodes.Or);
            _il.Emit(OpCodes.Ldloc, _address);
            _il.Emit(OpCodes.Ldc_I4_2);
            _il.Emit(OpCodes.Shl);
            _il.Emit(OpCodes.Or);
            _il.Emit(OpCodes.Stloc, _fcr31!);
            _il.Emit(OpCodes.Ldloc, _result);
            _il.Emit(OpCodes.Stloc, _fpr[d.D]!);
            _il.Emit(OpCodes.Br, done);

            // the full handler, on the core's state
            _il.MarkLabel(slow);
            WriteBackFloat();
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4, (int)(block.Start + (uint)(k * 4)));
            _il.Emit(OpCodes.Stfld, s_currentPcField);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(delaySlot ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Stfld, s_currentInDelaySlotField);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4, (int)d.Word);
            _il.Emit(OpCodes.Ldc_I4, (int)d.D);
            _il.Emit(OpCodes.Ldc_I4, (int)d.S);
            _il.Emit(OpCodes.Ldc_I4, (int)d.T);
            _il.Emit(OpCodes.Ldc_I4, (int)d.Immediate);
            _il.Emit(OpCodes.Call, s_fpuArithmetic);
            LoadFloat();
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, s_endBlockField);
            _il.Emit(OpCodes.Brfalse, done);

            // the exception was taken: the PC fields hold the vector
            Exit(() => _il.Emit(OpCodes.Ldc_I4_0), k + 1);
            _il.MarkLabel(done);
        }

        private void LoadLane(uint lanes, int i)
        {
            _il.Emit(OpCodes.Ldloc, _vfpr[(lanes >> (i * 8)) & 0x7F]!);
        }

        /// <summary>vadd/vsub/vmul: every lane computed before any is written (the vectors may overlap).</summary>
        private void EmitVfpuLanes(ref DecodedInstruction d)
        {
            MethodInfo operation = d.Immediate switch
            {
                0 => s_vfpuAdd,
                1 => s_vfpuSubtract,
                _ => s_vfpuMultiply,
            };
            for (int i = 0; i < d.D; i++)
            {
                LoadLane(d.LanesS, i);
                LoadLane(d.LanesT, i);
                _il.Emit(OpCodes.Call, operation);
                _il.Emit(OpCodes.Stloc, _lanes[i]);
            }

            for (int i = 0; i < d.D; i++)
            {
                int index = (int)((d.LanesD >> (i * 8)) & 0x7F);
                _il.Emit(OpCodes.Ldloc, _lanes[i]);
                _il.Emit(OpCodes.Stloc, _vfpr[index]!);
            }
        }

        private void EmitVfpuDot(ref DecodedInstruction d)
        {
            foreach (uint lanes in new[] { d.LanesS, d.LanesT })
            {
                for (int i = 0; i < 4; i++)
                {
                    if (i < d.D)
                    {
                        LoadLane(lanes, i);
                    }
                    else
                    {
                        _il.Emit(OpCodes.Ldc_I4_0);
                    }
                }
            }

            _il.Emit(OpCodes.Call, s_vfpuDot);
            _il.Emit(OpCodes.Stloc, _vfpr[(int)(d.LanesD & 0x7F)]!);
        }

        /// <summary>The branch's target into the target local (its registers read before the delay slot runs, the
        /// link written).</summary>
        private void EmitBranchTarget(ref DecodedInstruction d, uint slot)
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
                    _il.Emit(OpCodes.Stloc, _target);
                    _il.Emit(OpCodes.Br, done);
                    _il.MarkLabel(notTaken);
                    _il.Emit(OpCodes.Ldc_I4, (int)(slot + 4));
                    _il.Emit(OpCodes.Stloc, _target);
                    _il.MarkLabel(done);
                    break;
                }

                case OpJ:
                case OpJal:
                    _il.Emit(OpCodes.Ldc_I4, (int)((slot & 0xF0000000) | d.Immediate));
                    _il.Emit(OpCodes.Stloc, _target);
                    if (d.Op == OpJal)
                    {
                        _il.Emit(OpCodes.Ldc_I4, (int)(slot + 4));
                        Store(31);
                    }

                    break;
                case OpJr:
                    Load(d.S);
                    _il.Emit(OpCodes.Stloc, _target);
                    break;
                default:
                    // jalr: the target before the link (rd may be rs)
                    Load(d.S);
                    _il.Emit(OpCodes.Stloc, _target);
                    _il.Emit(OpCodes.Ldc_I4, (int)(slot + 4));
                    Store(d.D);
                    break;
            }
        }
    }
}
