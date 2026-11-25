using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// The decoded-block cache. <see cref="Run"/> executes straight-line runs of instructions (up to a branch or jump and
/// its delay slot, a system instruction, or a page end) that were fetched and decoded once. Each instruction keeps its
/// word and a small operation code with its fields already extracted; <see cref="RunBlock"/> dispatches on it with a
/// jump table, the common integer instructions (ALU, beq/bne, lw/sw) inline, everything else through the full decoder.
/// The inline instructions skip the per-instruction PC and Count bookkeeping, which is brought up to date before any
/// other instruction, before an exception and at the block end, so every instruction that can observe it (exceptions,
/// COP0 reads, the decoder's branches) sees the values of the one-at-a-time core.
/// Interrupts are checked between blocks; anything that may make one pending (RaiseInterrupt, the COP0 and
/// interrupt-control writes, eret, an exception, halt, Stop, a trap handler) ends the block at once, and a block never
/// runs past the instruction at which Count reaches Compare, so the order of events is the one of the
/// instruction-by-instruction core. Blocks are keyed by physical address in 4 KB pages: a store by the core into a page
/// with blocks drops that page's blocks, the cache instruction drops the page of its address (hit operations) or
/// everything (index operations, a whole I-cache flush), and <see cref="InvalidateCode"/> is for writers outside the
/// core (DMA, the ARM side). A block remembers the block it last continued to, so a loop does not look its blocks up.
/// </summary>
public sealed partial class AllegrexCore
{
    private const int PageShift = 12;
    private const int PageCount = 1 << (29 - PageShift);
    private const int WordsPerPage = 1 << (PageShift - 2);
    private const int MaxBlockInstructions = 128;

    // operation codes of decoded instructions: General goes through the decoder; Nop..Lui are plain (no exception,
    // no control flow, no PC or Count use, never write r0); the rest are inline but not plain
    private const byte OpGeneral = 0;
    private const byte OpNop = 1;
    private const byte OpSll = 2;
    private const byte OpSrl = 3;
    private const byte OpSra = 4;
    private const byte OpSllv = 5;
    private const byte OpMovz = 6;
    private const byte OpMovn = 7;
    private const byte OpAddu = 8;
    private const byte OpSubu = 9;
    private const byte OpAnd = 10;
    private const byte OpOr = 11;
    private const byte OpXor = 12;
    private const byte OpNor = 13;
    private const byte OpSlt = 14;
    private const byte OpSltu = 15;
    private const byte OpAddiu = 16;
    private const byte OpSlti = 17;
    private const byte OpSltiu = 18;
    private const byte OpAndi = 19;
    private const byte OpOri = 20;
    private const byte OpXori = 21;
    private const byte OpLui = 22;
    private const byte OpBeq = 23;
    private const byte OpBne = 24;
    private const byte OpJ = 25;
    private const byte OpJal = 26;
    private const byte OpJr = 27;
    private const byte OpJalr = 28;
    private const byte OpLw = 29;
    private const byte OpSw = 30;

    // through the decoder, but straight to their handler (AllegrexCore.FastPaths.cs)
    private const byte OpCop1 = 31;
    private const byte OpLwc1 = 32;
    private const byte OpSwc1 = 33;
    private const byte OpVfpu0 = 34;
    private const byte OpVfpu1 = 35;
    private const byte OpFpuArithmetic = 36;
    private const byte OpVfpuLanes = 37;
    private const byte OpVfpuDot = 38;

    private struct DecodedInstruction
    {
        public uint Word;

        // the extended immediate, the shift amount, the branch offset in bytes, or the jump's target bits
        public uint Immediate;
        public byte Op;

        // destination (rd, or rt for the immediate forms and loads), rs, rt; fd, fs, ft for the FPU; the lane count
        // for the VFPU
        public byte D;
        public byte S;
        public byte T;

        // the VFPU register file indices of the S, T and D vectors' lanes, a byte each
        public uint LanesS;
        public uint LanesT;
        public uint LanesD;
    }

    private sealed class Block
    {
        public DecodedInstruction[] Instructions = [];
        public int Count;

        // the cache generation it was decoded in; -1 once dropped
        public int Generation;
        public uint Start;

        // the block that last followed this one
        public Block? Next;

        // the IL tier: instructions run, whether a compile was tried, the compiled region it heads, the heads of the
        // regions it is part of
        public long Executed;
        public bool CompileTried;
        public BlockCode? Code;
        public List<Block>? Dependents;
    }

    private readonly Block?[]?[] _pages = new Block?[]?[PageCount];
    private readonly bool[] _codePages = new bool[PageCount];

    // pages a block of the page before runs into (a delay slot past its page end)
    private readonly bool[] _spillPages = new bool[PageCount];
    private int _generation;

    // set by whatever must end the running block (control leaves the straight line, or an interrupt may be due)
    private bool _endBlock;

    // exceptions taken so far: tells a direct handler's exception (the PC fields then hold the vector) from a store
    // that only ended the run
    private long _exceptionsTaken;

    /// <summary>Drops the decoded code of [physical, physical + length): for writes the core does not make (DMA, the ARM side).</summary>
    public void InvalidateCode(uint physical, int length)
    {
        uint first = (physical & PhysicalMask) >> PageShift;
        uint last = ((physical & PhysicalMask) + (uint)Math.Max(length, 1) - 1) >> PageShift;
        for (uint page = first; page <= last && page < PageCount; page++)
        {
            InvalidatePage(page);
        }
    }

    /// <summary>Drops every decoded block.</summary>
    public void InvalidateAllCode()
    {
        _generation++;
        _endBlock = true;
    }

    private void InvalidatePage(uint page)
    {
        DropPage(page);

        // a block of the page before that runs into this one (a delay slot past the page end)
        if (_spillPages[page])
        {
            _spillPages[page] = false;
            DropPage(page - 1);
        }
    }

    private void DropPage(uint page)
    {
        if (!_codePages[page])
        {
            return;
        }

        Block?[]? blocks = _pages[page];
        if (blocks != null)
        {
            foreach (Block? block in blocks)
            {
                if (block == null)
                {
                    continue;
                }

                block.Generation = -1;

                // the compiled regions it is part of go too (their heads may compile again)
                if (block.Dependents != null)
                {
                    foreach (Block head in block.Dependents)
                    {
                        head.Code = null;
                        head.CompileTried = false;
                        head.Executed = 0;
                    }

                    block.Dependents = null;
                }
            }
        }

        _pages[page] = null;
        _codePages[page] = false;
        _endBlock = true;
    }

    private void Store32(uint physical, uint value)
    {
        _bus.Write32(physical, value);
        NoteStore(physical);
    }

    private void Store16(uint physical, ushort value)
    {
        _bus.Write16(physical, value);
        NoteStore(physical);
    }

    private void Store8(uint physical, byte value)
    {
        _bus.Write8(physical, value);
        NoteStore(physical);
    }

    /// <summary>A store by the core: drops the decoded code of the written page (and of the page before when one of
    /// its blocks ends in it).</summary>
    private void NoteStore(uint physical)
    {
        uint page = (physical & PhysicalMask) >> PageShift;
        if (_codePages[page] || _spillPages[page])
        {
            InvalidatePage(page);
        }
    }

    /// <summary>The cache instruction: I-cache index operations (0x04-0x07) flush everything, hit operations (0x08-0x0F) the address's page.</summary>
    private void CacheOperation(uint instruction)
    {
        int operation = Rt(instruction);
        if (operation >= 0x04 && operation <= 0x07)
        {
            InvalidateAllCode();
        }
        else if (operation >= 0x08 && operation <= 0x0F)
        {
            InvalidateCode(EffectiveAddress(instruction), 4);
        }
    }

    /// <summary>The block at <paramref name="pc"/>: the one <paramref name="previous"/> last continued to, else from the cache, else decoded.</summary>
    private Block GetBlock(uint pc, Block? previous)
    {
        Block? next = previous?.Next;
        if (next != null && next.Start == pc && next.Generation == _generation)
        {
            return next;
        }

        uint physical = pc & PhysicalMask;
        uint page = physical >> PageShift;
        int word = (int)((physical >> 2) & (WordsPerPage - 1));
        Block?[]? blocks = _pages[page];
        Block? block = blocks?[word];
        if (block == null || block.Generation != _generation || block.Start != pc)
        {
            block = BuildBlock(pc);
            if (blocks == null)
            {
                blocks = new Block?[WordsPerPage];
                _pages[page] = blocks;
            }

            blocks[word] = block;
            _codePages[page] = true;
        }

        if (previous != null && previous.Generation == _generation)
        {
            previous.Next = block;
        }

        return block;
    }

    private Block BuildBlock(uint pc)
    {
        // a branch at the length limit still takes its delay slot: one more than the limit
        // without the IL tier (Native AOT, or never hot) a block never asks to be compiled
        Block block = new()
        {
            Generation = _generation,
            Start = pc,
            Instructions = new DecodedInstruction[_blockLimit + 1],
            CompileTried = _ilHotInstructions == long.MaxValue,
        };
        uint address = pc;
        bool delaySlotNext = false;
        int count = 0;
        while (true)
        {
            uint word = _bus.Read32(address & PhysicalMask);
            block.Instructions[count] = DecodeForBlock(word, out bool ends, out bool branches);
            count++;
            address += 4;
            if (delaySlotNext)
            {
                break;
            }

            if (branches)
            {
                // the delay slot goes with it
                delaySlotNext = true;
                continue;
            }

            if (ends || (address & ((1u << PageShift) - 1)) == 0 || count >= _blockLimit)
            {
                break;
            }
        }

        block.Count = count;
        uint lastPage = ((address - 4) & PhysicalMask) >> PageShift;
        if (lastPage != ((pc & PhysicalMask) >> PageShift))
        {
            _spillPages[lastPage] = true;
        }

        if (count < block.Instructions.Length)
        {
            Array.Resize(ref block.Instructions, count);
        }

        return block;
    }

    // how RunChain stopped
    private const int ChainEnd = 0;
    private const int ChainOther = 1;
    private const int ChainBranched = 2;
    private const int ChainStopped = 3;
    private const int ChainFinal = 4;

    /// <summary>Where a run is: the block, its start and how many of its instructions the run may execute, the
    /// instruction reached, the instructions of the earlier blocks of the run, what the run may still execute after
    /// them, the instruction the PC fields describe (inline instructions do not update them; -1: none of this block),
    /// and the target of a branch the run could not follow.</summary>
    private struct Chain
    {
        public Block Block;
        public uint Start;
        public int Count;
        public int Index;
        public int Done;
        public int Limit;
        public int Synced;
        public uint Target;
    }

    /// <summary>
    /// Runs up to <paramref name="limit"/> instructions from a block entered at its start with no delay slot pending,
    /// and on into the blocks it last continued to while nothing ends the run; returns how many ran, with
    /// <paramref name="entry"/> the last block. Each instruction has the effect <see cref="Step"/> gives it; the inline
    /// ones leave the PC and Count bookkeeping to the next instruction that needs it. <see cref="RunChain"/> runs the
    /// plain instructions and the branches with a plain delay slot; this method everything else.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int RunBlock(ref Block entry, int limit)
    {
        long baseCount = _executedInstructions;
        Chain c = new()
        {
            Block = entry,
            Start = entry.Start,
            Count = Math.Min(entry.Count, limit),
            Limit = limit,
        };
        ref uint r = ref MemoryMarshal.GetArrayDataReference(_gpr);
        _endBlock = false;
        while (true)
        {
            // an instruction that needs this method goes to it without the call
            int stop = ChainOther;
            if (c.Index == 0 || c.Index >= c.Count || !NeedsRunBlock(c.Block.Instructions[c.Index].Op))
            {
                stop = RunChain(ref r, ref c, _generation);
            }
            if (stop == ChainFinal)
            {
                // compiled code ran: the PC fields are final (the next PC, or an exception's vector)
                _executedInstructions = baseCount + c.Done;
                entry = c.Block;
                return c.Done;
            }

            if (stop == ChainBranched)
            {
                _pc = c.Target;
                _nextPc = c.Target + 4;
                _executedInstructions = baseCount + c.Done;
                entry = c.Block;
                return c.Done;
            }

            if (stop == ChainEnd || stop == ChainStopped)
            {
                break;
            }

            int i = c.Index;
            ref DecodedInstruction d = ref c.Block.Instructions[i];
            byte op = d.Op;
            if (op >= OpBeq && op <= OpJalr)
            {
                // a branch whose delay slot goes through the decoder (or the next run): the core's state after it
                uint slot = c.Start + (uint)(i * 4) + 4;
                uint target = BranchTarget(ref r, ref d, op, slot);
                if (i + 1 < c.Count && c.Block.Instructions[i + 1].Op >= OpCop1)
                {
                    // a delay slot that goes straight to its handler: only its exception PC is needed
                    ref DecodedInstruction delaySlot = ref c.Block.Instructions[i + 1];
                    _currentPc = slot;
                    _currentInDelaySlot = true;
                    long exceptions = _exceptionsTaken;
                    ExecuteDirect(delaySlot.Op, ref delaySlot);
                    _gpr[0] = 0;
                    c.Index = i + 2;
                    if (_exceptionsTaken != exceptions)
                    {
                        c.Synced = i + 2;
                        break;
                    }

                    // on to the target, into its block when it is known and nothing ended the run
                    if (!_endBlock && FollowBranch(ref c, target))
                    {
                        continue;
                    }

                    if (_endBlock)
                    {
                        c.Done += c.Index;
                        c.Index = 0;
                    }

                    _pc = target;
                    _nextPc = target + 4;
                    _executedInstructions = baseCount + c.Done;
                    entry = c.Block;
                    return c.Done;
                }

                _currentPc = slot - 4;
                _currentInDelaySlot = false;
                _pc = slot;
                _nextPc = target;
                _nextIsDelaySlot = true;
                _executedInstructions = baseCount + c.Done + i + 1;
                CheckTimer();
                c.Index = i + 1;
                c.Synced = i + 1;
                if (c.Index >= c.Count)
                {
                    break;
                }

                RunDelaySlot(ref c, baseCount);
                break;
            }

            if (op == OpLw)
            {
                uint address = Unsafe.Add(ref r, d.S) + d.Immediate;
                if ((address & 3) == 0)
                {
                    Unsafe.Add(ref r, d.D) = _bus.Read32(address & PhysicalMask);
                    c.Index = i + 1;
                    continue;
                }

                // misaligned: the address error, through the decoder with the exact PC
            }
            else if (op == OpSw)
            {
                uint address = Unsafe.Add(ref r, d.S) + d.Immediate;
                if ((address & 3) == 0)
                {
                    Store32(address & PhysicalMask, Unsafe.Add(ref r, d.T));
                    c.Index = i + 1;

                    // a device the store reached may have raised an interrupt, or the store hit decoded code
                    if (_endBlock)
                    {
                        break;
                    }

                    continue;
                }
            }

            if (op >= OpCop1)
            {
                // straight to the handler, and on through the ones that follow: these never branch nor read the PC
                // or Count, so only an exception's PC is needed
                DecodedInstruction[] instructions = c.Block.Instructions;
                uint start = c.Start;
                int count = c.Count;
                bool ended = false;
                while (true)
                {
                    _currentPc = start + (uint)(i * 4);
                    _currentInDelaySlot = false;
                    long exceptions = _exceptionsTaken;
                    ExecuteDirect(op, ref instructions[i]);
                    _gpr[0] = 0;
                    i++;
                    if (_endBlock)
                    {
                        if (_exceptionsTaken != exceptions)
                        {
                            c.Synced = i;
                        }

                        ended = true;
                        break;
                    }

                    if (i >= count || instructions[i].Op < OpCop1)
                    {
                        break;
                    }

                    op = instructions[i].Op;
                }

                c.Index = i;
                if (ended)
                {
                    break;
                }

                continue;
            }

            // through the decoder, with the bookkeeping of Step
            if (c.Synced != i)
            {
                _pc = c.Start + (uint)(i * 4);
                _nextPc = _pc + 4;
            }

            _currentPc = _pc;
            _currentInDelaySlot = false;
            _executedInstructions = baseCount + c.Done + i + 1;
            CheckTimer();
            _pc = _nextPc;
            _nextPc += 4;
            Execute(d.Word);
            _gpr[0] = 0;
            c.Index = i + 1;
            c.Synced = i + 1;
            if (_endBlock)
            {
                break;
            }

            if (_nextIsDelaySlot)
            {
                // the decoder's branch: its delay slot now (or in the next run), then the target
                if (c.Index < c.Count)
                {
                    RunDelaySlot(ref c, baseCount);
                }

                break;
            }
        }

        if (c.Synced != c.Index)
        {
            _pc = c.Start + (uint)(c.Index * 4);
            _nextPc = _pc + 4;
        }

        _executedInstructions = baseCount + c.Done + c.Index;
        entry = c.Block;
        return c.Done + c.Index;
    }

    /// <summary>An instruction the block decoded far enough to go straight to its handler.</summary>
    private void ExecuteDirect(byte op, ref DecodedInstruction d)
    {
        uint word = d.Word;
        switch (op)
        {
            case OpFpuArithmetic:
                FpuArithmeticDirect(ref d);
                break;
            case OpVfpuLanes:
                VfpuLanesDirect(ref d);
                break;
            case OpVfpuDot:
                VfpuDotDirect(ref d);
                break;
            case OpCop1:
                ExecuteCop1Direct(word);
                break;
            case OpLwc1:
                LoadWordToFpu(word);
                break;
            case OpSwc1:
                StoreWordFromFpu(word);
                break;
            case OpVfpu0:
                ExecuteVfpu0Direct(word);
                break;
            case OpVfpu1:
                ExecuteVfpu1Direct(word);
                break;
            default:
                Execute(word);
                break;
        }
    }

    /// <summary>After a branch and its delay slot: counts the block's instructions into the run and moves on into the
    /// block at <paramref name="target"/> when it is the one the block last continued to and the run has budget left.</summary>
    private bool FollowBranch(ref Chain c, uint target)
    {
        c.Done += c.Index;
        c.Limit -= c.Index;
        c.Index = 0;
        Block? next = c.Block.Next;
        if (c.Limit == 0 || next == null || next.Start != target || next.Generation != _generation)
        {
            return false;
        }

        c.Block = next;
        c.Start = target;
        c.Count = Math.Min(next.Count, c.Limit);
        c.Synced = -1;
        return true;
    }

    /// <summary>The delay slot of a branch, through the decoder: after it the core goes to the branch target.</summary>
    private void RunDelaySlot(ref Chain c, long baseCount)
    {
        int i = c.Index;
        _currentPc = _pc;
        _currentInDelaySlot = true;
        _nextIsDelaySlot = false;
        _executedInstructions = baseCount + c.Done + i + 1;
        CheckTimer();
        _pc = _nextPc;
        _nextPc += 4;
        ref DecodedInstruction d = ref c.Block.Instructions[i];
        if (d.Op >= OpCop1)
        {
            ExecuteDirect(d.Op, ref d);
        }
        else
        {
            Execute(d.Word);
        }

        _gpr[0] = 0;
        c.Index = i + 1;
        c.Synced = i + 1;
    }

    /// <summary>
    /// The hot path of a run: plain instructions, and branches and jumps whose delay slot is plain, on into the block a
    /// branch last continued to; a block the IL tier compiled runs as its compiled code. Stops at the end of the
    /// block's share of the run (<see cref="ChainEnd"/>), at any other instruction (<see cref="ChainOther"/>,
    /// <see cref="Chain.Index"/> at it), after a branch it cannot follow (<see cref="ChainBranched"/>,
    /// <see cref="Chain.Target"/>), or after compiled code a store ended (<see cref="ChainStopped"/>). A method of its
    /// own, so that its loop keeps everything in registers.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private int RunChain(ref uint r, ref Chain c, int generation)
    {
        Block block = c.Block;
        ref DecodedInstruction first = ref MemoryMarshal.GetArrayDataReference(block.Instructions);
        uint start = c.Start;
        int count = c.Count;
        int i = c.Index;
        int done = c.Done;
        int limit = c.Limit;
        int stop;
        uint target;
        while (true)
        {
            if (i == 0)
            {
                // a block entry: count it for the IL tier, and run its compiled region when the budget holds the block
                if (block.Code == null && !block.CompileTried)
                {
                    NoteBlockRun(block);
                }

                BlockCode? code = block.Code;
                if (code != null && limit >= block.Count && count == block.Count)
                {
                    long exceptions = _exceptionsTaken;
                    target = code(this, ref r, limit, out int executed);
                    if (executed > 0 || _exceptionsTaken != exceptions)
                    {
                        done += executed;
                        limit -= executed;
                        if (_exceptionsTaken == exceptions)
                        {
                            _pc = target;
                            _nextPc = target + 4;
                        }

                        i = 0;
                        stop = ChainFinal;
                        break;
                    }

                    // nothing ran (a misaligned access first, or prefixes not at rest): the interpreter takes it
                }
            }

            // the plain instructions back to back: the block entry work above happens once per block, not here
            while (i < count && ExecutePlain(ref r, ref Unsafe.Add(ref first, i)))
            {
                i++;
            }

            if (i >= count)
            {
                stop = ChainEnd;
                break;
            }

            ref DecodedInstruction d = ref Unsafe.Add(ref first, i);
            byte op = d.Op;
            if (op < OpBeq || op > OpJalr || i + 1 >= count || !IsPlain(Unsafe.Add(ref first, i + 1).Op))
            {
                stop = ChainOther;
                break;
            }

            // the branch (its registers are read before the slot runs), then its plain delay slot
            target = BranchTarget(ref r, ref d, op, start + (uint)(i * 4) + 4);
            ExecutePlain(ref r, ref Unsafe.Add(ref first, i + 1));
            i += 2;
            done += i;
            limit -= i;
            Block? next = block.Next;
            if (limit == 0 || next == null || next.Start != target || next.Generation != generation)
            {
                c.Target = target;
                i = 0;
                stop = ChainBranched;
                break;
            }

            block = next;
            start = target;
            count = Math.Min(next.Count, limit);
            first = ref MemoryMarshal.GetArrayDataReference(next.Instructions);
            c.Synced = -1;
            i = 0;
        }

        c.Block = block;
        c.Start = start;
        c.Count = count;
        c.Index = i;
        c.Done = done;
        c.Limit = limit;
        return stop;
    }

    private static bool NeedsRunBlock(byte op)
    {
        return op == OpGeneral || op >= OpCop1;
    }

    private static bool IsPlain(byte op)
    {
        return op >= OpNop && op <= OpLui;
    }

    /// <summary>Runs a plain instruction and returns true; false (and nothing done) for any other.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ExecutePlain(ref uint r, ref DecodedInstruction d)
    {
        switch (d.Op)
        {
            case OpNop:
                return true;
            case OpSll:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.T) << (int)d.Immediate;
                return true;
            case OpSrl:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.T) >> (int)d.Immediate;
                return true;
            case OpSra:
                Unsafe.Add(ref r, d.D) = (uint)((int)Unsafe.Add(ref r, d.T) >> (int)d.Immediate);
                return true;
            case OpSllv:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.T) << (int)(Unsafe.Add(ref r, d.S) & 0x1F);
                return true;
            case OpMovz:
                if (Unsafe.Add(ref r, d.T) == 0)
                {
                    Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.S);
                }

                return true;
            case OpMovn:
                if (Unsafe.Add(ref r, d.T) != 0)
                {
                    Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.S);
                }

                return true;
            case OpAddu:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.S) + Unsafe.Add(ref r, d.T);
                return true;
            case OpSubu:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.S) - Unsafe.Add(ref r, d.T);
                return true;
            case OpAnd:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.S) & Unsafe.Add(ref r, d.T);
                return true;
            case OpOr:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.S) | Unsafe.Add(ref r, d.T);
                return true;
            case OpXor:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.S) ^ Unsafe.Add(ref r, d.T);
                return true;
            case OpNor:
                Unsafe.Add(ref r, d.D) = ~(Unsafe.Add(ref r, d.S) | Unsafe.Add(ref r, d.T));
                return true;
            case OpSlt:
                Unsafe.Add(ref r, d.D) = (int)Unsafe.Add(ref r, d.S) < (int)Unsafe.Add(ref r, d.T) ? 1u : 0u;
                return true;
            case OpSltu:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.S) < Unsafe.Add(ref r, d.T) ? 1u : 0u;
                return true;
            case OpAddiu:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.S) + d.Immediate;
                return true;
            case OpSlti:
                Unsafe.Add(ref r, d.D) = (int)Unsafe.Add(ref r, d.S) < (int)d.Immediate ? 1u : 0u;
                return true;
            case OpSltiu:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.S) < d.Immediate ? 1u : 0u;
                return true;
            case OpAndi:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.S) & d.Immediate;
                return true;
            case OpOri:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.S) | d.Immediate;
                return true;
            case OpXori:
                Unsafe.Add(ref r, d.D) = Unsafe.Add(ref r, d.S) ^ d.Immediate;
                return true;
            case OpLui:
                Unsafe.Add(ref r, d.D) = d.Immediate;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Where an inline branch or jump goes (it reads its registers before it writes the link, as jalr does).</summary>
    private static uint BranchTarget(ref uint r, ref DecodedInstruction d, byte op, uint slot)
    {
        switch (op)
        {
            case OpBeq:
                return Unsafe.Add(ref r, d.S) == Unsafe.Add(ref r, d.T) ? slot + d.Immediate : slot + 4;
            case OpBne:
                return Unsafe.Add(ref r, d.S) != Unsafe.Add(ref r, d.T) ? slot + d.Immediate : slot + 4;
            case OpJ:
                return (slot & 0xF0000000) | d.Immediate;
            case OpJal:
                Unsafe.Add(ref r, 31) = slot + 4;
                return (slot & 0xF0000000) | d.Immediate;
            case OpJr:
                return Unsafe.Add(ref r, d.S);
            default:
            {
                // jalr
                uint target = Unsafe.Add(ref r, d.S);
                Unsafe.Add(ref r, d.D) = slot + 4;
                return target;
            }
        }
    }

    /// <summary>The instructions until Count reaches Compare (0: it never does within 2^32), for capping a block.</summary>
    private uint InstructionsToCompare()
    {
        return _cop0[Cop0Compare] - ((uint)_executedInstructions + _countOffset);
    }

    private void CheckTimer()
    {
        if ((uint)_executedInstructions + _countOffset == _cop0[Cop0Compare])
        {
            _timerInterrupt = true;
        }
    }

    /// <summary>The block form of an instruction; <paramref name="ends"/>: nothing after it in the block (it may change
    /// what is pending or the code), <paramref name="branches"/>: it has a delay slot.</summary>
    private static DecodedInstruction DecodeForBlock(uint word, out bool ends, out bool branches)
    {
        ends = false;
        branches = false;
        DecodedInstruction d = new()
        {
            Word = word,
            Op = OpGeneral,
            D = (byte)Rt(word),
            S = (byte)Rs(word),
            T = (byte)Rt(word),
            Immediate = SignedImmediate(word),
        };
        uint opcode = word >> 26;
        switch (opcode)
        {
            case 0x00:
                DecodeSpecialForBlock(ref d, ref ends, ref branches);
                return d;
            case 0x02:
                branches = true;
                d.Op = OpJ;
                d.Immediate = (word & 0x03FFFFFF) << 2;
                return d;
            case 0x03:
                branches = true;
                d.Op = OpJal;
                d.Immediate = (word & 0x03FFFFFF) << 2;
                return d;
            case 0x01:
            case 0x06:
            case 0x07:
            case 0x14:
            case 0x15:
            case 0x16:
            case 0x17:
                branches = true;
                return d;
            case 0x04:
                branches = true;
                d.Op = OpBeq;
                d.Immediate = SignedImmediate(word) << 2;
                return d;
            case 0x05:
                branches = true;
                d.Op = OpBne;
                d.Immediate = SignedImmediate(word) << 2;
                return d;
            case 0x09:
                d.Op = OpAddiu;
                break;
            case 0x0A:
                d.Op = OpSlti;
                break;
            case 0x0B:
                d.Op = OpSltiu;
                break;
            case 0x0C:
                d.Op = OpAndi;
                d.Immediate = UnsignedImmediate(word);
                break;
            case 0x0D:
                d.Op = OpOri;
                d.Immediate = UnsignedImmediate(word);
                break;
            case 0x0E:
                d.Op = OpXori;
                d.Immediate = UnsignedImmediate(word);
                break;
            case 0x0F:
                d.Op = OpLui;
                d.Immediate = word << 16;
                break;
            case 0x10:
            case 0x1C:
            case 0x2F:
                // COP0, halt/mfic/mtic, cache
                ends = true;
                return d;
            case 0x11:
                // bc1f/bc1t branch; add.s/sub.s/mul.s decode fully; the rest of COP1 goes straight to its handler
                branches = ((word >> 21) & 0x1F) == 0x08;
                d.Op = branches ? OpGeneral : OpCop1;
                if (((word >> 21) & 0x1F) == 0x10 && (word & 0x3F) <= 2)
                {
                    d.Op = OpFpuArithmetic;
                    d.D = (byte)Fd(word);
                    d.S = (byte)Fs(word);
                    d.T = (byte)Ft(word);
                    d.Immediate = word & 0x3F;
                }

                return d;
            case 0x12:
                // bvf/bvt
                branches = ((word >> 21) & 0x1F) == 0x08;
                return d;
            case 0x18:
                // vadd/vsub decode fully
                d.Op = ((word >> 23) & 7) <= 1 ? OpVfpuLanes : OpVfpu0;
                DecodeVfpuLanes(ref d, word);
                return d;
            case 0x19:
                // vmul/vdot decode fully
                d.Op = ((word >> 23) & 7) switch
                {
                    0 => OpVfpuLanes,
                    1 => OpVfpuDot,
                    _ => OpVfpu1,
                };
                DecodeVfpuLanes(ref d, word);
                return d;
            case 0x31:
                d.Op = OpLwc1;
                return d;
            case 0x39:
                d.Op = OpSwc1;
                return d;
            case 0x23:
                d.Op = OpLw;
                break;
            case 0x2B:
                d.Op = OpSw;
                return d;
            default:
                return d;
        }

        if (d.D == 0)
        {
            // writes r0 (a load to r0 still reads, through the decoder)
            d.Op = d.Op == OpLw ? OpGeneral : OpNop;
        }

        return d;
    }

    /// <summary>The lanes of a three-vector VFPU instruction, as <see cref="ReadVector"/> walks them; the operation
    /// (0 vadd, 1 vsub, 8 vmul) in the immediate.</summary>
    private static void DecodeVfpuLanes(ref DecodedInstruction d, uint word)
    {
        int size = VfpuSize(word);
        d.D = (byte)size;
        d.Immediate = (word >> 26) == 0x19 ? 8u : (word >> 23) & 7;
        d.LanesS = VectorLanes(VfpuS(word), size);
        d.LanesT = VectorLanes(VfpuT(word), size);
        d.LanesD = d.Op == OpVfpuDot ? (uint)VfpuIndex(VfpuD(word)) : VectorLanes(VfpuD(word), size);
    }

    private static uint VectorLanes(int register, int size)
    {
        if (size == 1)
        {
            return (uint)VfpuIndex(register);
        }

        int start = VectorStartLane(size, register);
        int matrixBase = ((register >> 2) & 7) * 16;
        int column = register & 3;
        bool transposed = ((register >> 5) & 1) != 0;
        uint lanes = 0;
        for (int i = 0; i < size; i++)
        {
            int lane = (start + i) & 3;
            int index = transposed ? matrixBase + column + (lane * 4) : matrixBase + (column * 4) + lane;
            lanes |= (uint)index << (i * 8);
        }

        return lanes;
    }

    private static void DecodeSpecialForBlock(ref DecodedInstruction d, ref bool ends, ref bool branches)
    {
        uint word = d.Word;
        d.D = (byte)Rd(word);
        d.Immediate = (uint)Sa(word);
        switch (word & 0x3F)
        {
            case 0x00:
                d.Op = OpSll;
                break;
            case 0x02:
                // srl; rs = 1 is rotr
                d.Op = Rs(word) == 0 ? OpSrl : OpGeneral;
                break;
            case 0x03:
                d.Op = OpSra;
                break;
            case 0x04:
                d.Op = OpSllv;
                break;
            case 0x08:
                branches = true;
                d.Op = OpJr;
                return;
            case 0x09:
                // jalr; with rd = 0 it is a plain jump
                branches = true;
                d.Op = d.D == 0 ? OpJr : OpJalr;
                return;
            case 0x0A:
                d.Op = OpMovz;
                break;
            case 0x0B:
                d.Op = OpMovn;
                break;
            case 0x0C:
            case 0x0D:
                ends = true;
                return;
            case 0x21:
                d.Op = OpAddu;
                break;
            case 0x23:
                d.Op = OpSubu;
                break;
            case 0x24:
                d.Op = OpAnd;
                break;
            case 0x25:
                d.Op = OpOr;
                break;
            case 0x26:
                d.Op = OpXor;
                break;
            case 0x27:
                d.Op = OpNor;
                break;
            case 0x2A:
                d.Op = OpSlt;
                break;
            case 0x2B:
                d.Op = OpSltu;
                break;
            default:
                return;
        }

        if (d.Op != OpGeneral && d.D == 0)
        {
            d.Op = OpNop;
        }
    }
}
