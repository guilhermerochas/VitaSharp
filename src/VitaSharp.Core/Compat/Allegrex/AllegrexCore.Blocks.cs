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

    private struct DecodedInstruction
    {
        public uint Word;

        // the extended immediate, the shift amount, the branch offset in bytes, or the jump's target bits
        public uint Immediate;
        public byte Op;

        // destination (rd, or rt for the immediate forms and loads), rs, rt
        public byte D;
        public byte S;
        public byte T;
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
    }

    private readonly Block?[]?[] _pages = new Block?[]?[PageCount];
    private readonly bool[] _codePages = new bool[PageCount];
    private int _generation;

    // set by whatever must end the running block (control leaves the straight line, or an interrupt may be due)
    private bool _endBlock;

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

        // a block of the page before may run into this one (a delay slot past the page end)
        if (page > 0)
        {
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
                block?.Generation = -1;
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

    /// <summary>A store by the core: drops the decoded code of the written page (and of the page before, whose last
    /// block may end in it).</summary>
    private void NoteStore(uint physical)
    {
        uint page = (physical & PhysicalMask) >> PageShift;
        if (_codePages[page] || (page > 0 && _codePages[page - 1]))
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
        Block block = new() { Generation = _generation, Start = pc, Instructions = new DecodedInstruction[MaxBlockInstructions] };
        uint address = pc;
        bool delaySlotNext = false;
        int count = 0;
        while (count < MaxBlockInstructions)
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

            if (ends || (address & ((1u << PageShift) - 1)) == 0)
            {
                break;
            }
        }

        block.Count = count;
        if (count < MaxBlockInstructions)
        {
            Array.Resize(ref block.Instructions, count);
        }

        return block;
    }

    // how RunChain stopped
    private const int ChainEnd = 0;
    private const int ChainOther = 1;
    private const int ChainBranched = 2;

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
            if (c.Index >= c.Count || !NeedsRunBlock(c.Block.Instructions[c.Index].Op))
            {
                stop = RunChain(ref r, ref c, _generation);
            }
            if (stop == ChainBranched)
            {
                _pc = c.Target;
                _nextPc = c.Target + 4;
                _executedInstructions = baseCount + c.Done;
                entry = c.Block;
                return c.Done;
            }

            if (stop == ChainEnd)
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
        Execute(c.Block.Instructions[i].Word);
        _gpr[0] = 0;
        c.Index = i + 1;
        c.Synced = i + 1;
    }

    /// <summary>
    /// The hot path of a run: plain instructions, and branches and jumps whose delay slot is plain, on into the block a
    /// branch last continued to. Stops at the end of the block's share of the run (<see cref="ChainEnd"/>), at any
    /// other instruction (<see cref="ChainOther"/>, <see cref="Chain.Index"/> at it), or after a branch it cannot
    /// follow (<see cref="ChainBranched"/>, <see cref="Chain.Target"/>). A method of its own, with no calls, so that
    /// its loop keeps everything in registers.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int RunChain(ref uint r, ref Chain c, int generation)
    {
        Block block = c.Block;
        ref DecodedInstruction first = ref MemoryMarshal.GetArrayDataReference(block.Instructions);
        uint start = c.Start;
        int count = c.Count;
        int i = c.Index;
        int done = c.Done;
        int limit = c.Limit;
        int stop;
        while (true)
        {
            if (i >= count)
            {
                stop = ChainEnd;
                break;
            }

            ref DecodedInstruction d = ref Unsafe.Add(ref first, i);
            if (ExecutePlain(ref r, ref d))
            {
                i++;
                continue;
            }

            byte op = d.Op;
            if (op < OpBeq || op > OpJalr || i + 1 >= count || !IsPlain(Unsafe.Add(ref first, i + 1).Op))
            {
                stop = ChainOther;
                break;
            }

            // the branch (its registers are read before the slot runs), then its plain delay slot
            uint target = BranchTarget(ref r, ref d, op, start + (uint)(i * 4) + 4);
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
        return op == OpGeneral || op == OpLw || op == OpSw;
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
            case 0x12:
                // bc1f/bc1t, bvf/bvt
                branches = ((word >> 21) & 0x1F) == 0x08;
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
