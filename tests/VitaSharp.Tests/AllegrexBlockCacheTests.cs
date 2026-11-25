using VitaSharp.Core.Compat.Allegrex;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>
/// The decoded-block cache keeps the one-instruction core's behaviour: code the core rewrites runs as rewritten, code
/// written from outside needs <see cref="AllegrexCore.InvalidateCode"/> or an I-cache operation (as on the PSP, whose
/// I-cache does not snoop), an interrupt raised during a block and the Count/Compare timer are taken at the exact
/// instruction, and a long run allocates nothing.
/// </summary>
public sealed class AllegrexBlockCacheTests
{
    private const uint Base = AllegrexRig.CodeBase;

    [Fact]
    public void CodeTheCoreRewritesRunsAsRewritten()
    {
        // store addiu t2, t2, 5 over the instruction at +12, then run on into it
        AllegrexRig rig = new(Sw(T1, T0, 12), Nop, Nop, Addiu(T2, T2, 1));
        rig[T0] = Base;
        rig[T1] = Addiu(T2, T2, 5);
        rig.Step(4);
        Assert.Equal(5u, rig[T2]);
    }

    [Fact]
    public void CodeWrittenFromOutsideNeedsInvalidation()
    {
        AllegrexRig rig = new(Addiu(T2, T2, 1));
        rig.Step();
        Assert.Equal(1u, rig[T2]);

        rig.Bus.Load(Base, [Addiu(T2, T2, 7)]);
        rig.Core.Reset(Base);
        rig.Core.Status = 0;
        rig[T2] = 0;
        rig.Step();
        Assert.Equal(7u, rig[T2]); // Reset drops the decoded code

        // a DMA-like write the core does not see: the decoded block stays, as the PSP's I-cache would
        rig.Bus.Load(Base, [Addiu(T2, T2, 9)]);
        rig.Core.Pc = Base;
        rig[T2] = 0;
        rig.Step();
        Assert.Equal(7u, rig[T2]);

        rig.Core.InvalidateCode(AllegrexTestBus.RamBase, 4);
        rig.Core.Pc = Base;
        rig[T2] = 0;
        rig.Step();
        Assert.Equal(9u, rig[T2]);
    }

    [Fact]
    public void AnICacheIndexOperationDropsEveryDecodedBlock()
    {
        AllegrexRig rig = new(Addiu(T2, T2, 1));
        rig.Bus.Load(Base + 0x100, [IType(0x2F, 0x04, Zero, 0)]); // cache: I-cache index invalidate
        rig.Step();
        rig.Bus.Load(Base, [Addiu(T2, T2, 3)]);
        rig.Core.Pc = Base + 0x100;
        rig.Step();
        rig.Core.Pc = Base;
        rig[T2] = 0;
        rig.Step();
        Assert.Equal(3u, rig[T2]);
    }

    [Fact]
    public void AnICacheHitOperationDropsItsPage()
    {
        AllegrexRig rig = new(Addiu(T2, T2, 1));
        rig.Bus.Load(Base + 0x2000, [IType(0x2F, 0x08, T0, 0)]); // cache: I-cache hit invalidate of t0
        rig.Step();
        rig.Bus.Load(Base, [Addiu(T2, T2, 4)]);
        rig[T0] = Base;
        rig.Core.Pc = Base + 0x2000;
        rig.Step();
        rig.Core.Pc = Base;
        rig[T2] = 0;
        rig.Step();
        Assert.Equal(4u, rig[T2]);
    }

    private sealed class InterruptingBus(AllegrexTestBus inner, uint trigger) : IAllegrexBus
    {
        public AllegrexCore? Core { get; set; }

        public byte Read8(uint address)
        {
            return inner.Read8(address);
        }

        public ushort Read16(uint address)
        {
            return inner.Read16(address);
        }

        public uint Read32(uint address)
        {
            return inner.Read32(address);
        }

        public void Write8(uint address, byte value)
        {
            inner.Write8(address, value);
        }

        public void Write16(uint address, ushort value)
        {
            inner.Write16(address, value);
        }

        public void Write32(uint address, uint value)
        {
            inner.Write32(address, value);
            if (address == trigger)
            {
                // a device register whose write raises its interrupt at once
                Core!.RaiseInterrupt(AllegrexCore.InterruptControllerLine);
            }
        }
    }

    [Fact]
    public void AnInterruptRaisedDuringABlockIsTakenAtTheNextInstruction()
    {
        AllegrexTestBus memory = new();
        memory.Load(Base, [Sw(T1, T0, 0), Addiu(T2, T2, 1), Addiu(T2, T2, 1), Addiu(T2, T2, 1)]);
        InterruptingBus bus = new(memory, 0x08010000);
        AllegrexCore core = new(bus);
        bus.Core = core;
        core.Reset(Base);
        core.Status = AllegrexCore.StatusIe | (1u << 10);
        core.EBase = AllegrexRig.ExceptionBase;
        core.Gpr[T0] = 0x88010000;
        core.Run(2);
        Assert.Equal(Base + 4, core.Epc); // the store ran, the interrupt came before the next instruction
        Assert.Equal(0u, core.Gpr[T2]);
    }

    [Fact]
    public void TheTimerFiresAtItsInstructionInsideALongRun()
    {
        uint[] code = new uint[16];
        AllegrexRig rig = new(code);
        rig.Core.Status = AllegrexCore.StatusIe | AllegrexCore.CauseTimerInterrupt;
        rig.Core.WriteCop0(AllegrexCore.Cop0Compare, 5);
        rig.Core.Run(7);
        Assert.Equal(Base + 20, rig.Core.Epc);
        Assert.Equal(AllegrexRig.ExceptionBase + 8, rig.Core.Pc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALoopBranchesWhateverItsDelaySlot(bool decoderSlot)
    {
        // addiu t0, t0, 1; bne t0, t1, start; slot (a load: through the decoder, or an addu: inline)
        uint slot = decoderSlot ? Lw(V0, V1, 0) : Addu(V0, T0, Zero);
        AllegrexRig rig = new(Addiu(T0, T0, 1), Bne(T0, T1, -2), slot, Nop);
        rig[T1] = 5;
        rig[V1] = Base;
        rig.Core.Run(15);
        Assert.Equal(5u, rig[T0]);
        Assert.Equal(Base + 12, rig.Core.Pc);
        Assert.Equal(decoderSlot ? Addiu(T0, T0, 1) : 5u, rig[V0]);
    }

    [Fact]
    public void AnExceptionInADirectDelaySlotReportsTheBranchWithBd()
    {
        // beq zero, zero, +3; slot: swc1 f0, 2(t0), misaligned (an FPU store the block runs straight to its handler)
        AllegrexRig rig = new(Beq(Zero, Zero, 3), IType(0x39, 0, T0, 2), Nop, Nop, Nop);
        rig[T0] = Base + 0x3000;
        rig.Core.Run(2);
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc);
        Assert.Equal(Base, rig.Core.Epc);
        Assert.NotEqual(0u, rig.Core.Cause & AllegrexCore.CauseBranchDelay);
    }

    [Fact]
    public void ALongRunAllocatesNothing()
    {
        AllegrexRig rig = new(Addiu(T0, T0, 1), Bne(T0, Zero, -2), Addu(T1, T1, T0));
        rig.Core.Run(10_000); // decodes the blocks

        // the runtime's own tiering can allocate on the first runs: one clean run of the steady state is the proof
        long smallest = long.MaxValue;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            rig.Core.Run(100_000);
            smallest = Math.Min(smallest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, smallest);
    }
}
