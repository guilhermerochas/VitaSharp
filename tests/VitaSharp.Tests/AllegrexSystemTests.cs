using VitaSharp.Core.Compat.Allegrex;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>
/// Allegrex system side: syscall/break (host handler and exception), traps, COP0 moves, eret, interrupts (Status.IE or
/// mtic, masks, EXL), halt, reserved and not-yet-emulated instructions.
/// </summary>
public sealed class AllegrexSystemTests
{
    private static uint ExceptionCode(AllegrexCore core)
    {
        return (core.Cause & AllegrexCore.CauseExceptionCodeMask) >> 2;
    }

    [Fact]
    public void SyscallWithoutHandlerTakesTheExceptionWithItsCode()
    {
        AllegrexRig rig = new(Syscall(0x2104));
        rig.Step();
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc);
        Assert.Equal((uint)AllegrexExceptionCode.Syscall, ExceptionCode(rig.Core));
        Assert.Equal(AllegrexRig.CodeBase, rig.Core.Epc);
        Assert.Equal(0x2104u << 2, rig.Core.ReadCop0(AllegrexCore.Cop0SyscallCode));
        Assert.NotEqual(0u, rig.Core.Status & AllegrexCore.StatusExl);
    }

    [Fact]
    public void SyscallHandledByTheHostContinuesAfterIt()
    {
        AllegrexRig rig = new(Syscall(0x15), Addiu(T0, Zero, 1));
        uint seenCode = 0;
        uint seenAddress = 0;
        rig.Core.TrapHandler = (core, kind, code, address) =>
        {
            Assert.Equal(AllegrexTrapKind.Syscall, kind);
            seenCode = code;
            seenAddress = address;
            core.Gpr[V0] = 0x80020001;
            return true;
        };
        rig.Step(2);
        Assert.Equal(0x15u, seenCode);
        Assert.Equal(AllegrexRig.CodeBase, seenAddress);
        Assert.Equal(0x80020001u, rig[V0]);
        Assert.Equal(1u, rig[T0]);
        Assert.Equal(0u, rig.Core.Status & AllegrexCore.StatusExl);
    }

    [Fact]
    public void BreakTakesTheBreakpointException()
    {
        AllegrexRig rig = new(Break(3));
        rig.Step();
        Assert.Equal((uint)AllegrexExceptionCode.Breakpoint, ExceptionCode(rig.Core));
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc);
    }

    [Fact]
    public void BevSendsExceptionsToTheBootVector()
    {
        AllegrexRig rig = new(Break());
        rig.Core.Status = AllegrexCore.StatusBev;
        rig.Step();
        Assert.Equal(AllegrexCore.BootExceptionVector, rig.Core.Pc);
    }

    [Fact]
    public void KernelHandlerReturnsPastTheSyscallWithEret()
    {
        // handler at EBase: k0 = EPC + 4; EPC = k0; eret
        AllegrexRig rig = new(Syscall(1), Addiu(T0, Zero, 7));
        rig.Bus.Load(AllegrexRig.ExceptionBase, [Mfc0(K0, AllegrexCore.Cop0Epc), Addiu(K0, K0, 4), Mtc0(K0, AllegrexCore.Cop0Epc), Eret]);
        rig.Step(6);
        Assert.Equal(7u, rig[T0]);
        Assert.Equal(AllegrexRig.CodeBase + 8, rig.Core.Pc);
        Assert.Equal(0u, rig.Core.Status & AllegrexCore.StatusExl);
    }

    [Fact]
    public void EretPrefersErrorEpcWhileErlIsSet()
    {
        AllegrexRig rig = new(Eret);
        rig.Core.Status = AllegrexCore.StatusErl | AllegrexCore.StatusExl;
        rig.Core.ErrorEpc = 0x88000200;
        rig.Core.Epc = 0x88000300;
        rig.Step();
        Assert.Equal(0x88000200u, rig.Core.Pc);
        Assert.Equal(AllegrexCore.StatusExl, rig.Core.Status);
    }

    [Fact]
    public void NestedExceptionKeepsTheFirstEpc()
    {
        AllegrexRig rig = new(Break());
        rig.Core.Status = AllegrexCore.StatusExl;
        rig.Core.Epc = 0x88000444;
        rig.Step();
        Assert.Equal(0x88000444u, rig.Core.Epc);
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc);
    }

    [Theory]
    [InlineData(0x34, 5u, 5u, true)]           // teq
    [InlineData(0x34, 5u, 6u, false)]
    [InlineData(0x36, 5u, 6u, true)]           // tne
    [InlineData(0x30, 0xFFFFFFFFu, 0u, false)] // tge is signed
    [InlineData(0x31, 0xFFFFFFFFu, 0u, true)]  // tgeu is unsigned
    [InlineData(0x32, 0xFFFFFFFFu, 0u, true)]  // tlt
    [InlineData(0x33, 0xFFFFFFFFu, 0u, false)] // tltu
    public void RegisterTraps(int funct, uint left, uint right, bool traps)
    {
        AllegrexRig rig = new(RType(funct, 0, T0, T1));
        rig[T0] = left;
        rig[T1] = right;
        rig.Step();
        Assert.Equal(traps ? AllegrexRig.ExceptionBase : AllegrexRig.CodeBase + 4, rig.Core.Pc);
        if (traps)
        {
            Assert.Equal((uint)AllegrexExceptionCode.Trap, ExceptionCode(rig.Core));
        }
    }

    [Theory]
    [InlineData(0x0C, 5, 5u, true)]           // teqi
    [InlineData(0x0E, 5, 5u, false)]          // tnei
    [InlineData(0x08, -1, 0u, true)]          // tgei: 0 >= -1
    [InlineData(0x09, -1, 0u, false)]         // tgeiu: 0 >= 0xFFFFFFFF is false
    [InlineData(0x0A, 1, 0xFFFFFFFFu, true)]  // tlti
    [InlineData(0x0B, 1, 0xFFFFFFFFu, false)] // tltiu
    public void ImmediateTraps(int rtField, int immediate, uint value, bool traps)
    {
        AllegrexRig rig = new(IType(0x01, rtField, T0, immediate));
        rig[T0] = value;
        rig.Step();
        Assert.Equal(traps ? AllegrexRig.ExceptionBase : AllegrexRig.CodeBase + 4, rig.Core.Pc);
    }

    [Fact]
    public void Cop0MovesAndControlRegisters()
    {
        uint ctc0 = (0x10u << 26) | (6u << 21) | ((uint)T0 << 16) | (12u << 11);
        uint cfc0 = (0x10u << 26) | (2u << 21) | ((uint)T1 << 16) | (12u << 11);
        AllegrexRig rig = new(Mtc0(T0, AllegrexCore.Cop0EBase), Mfc0(T2, AllegrexCore.Cop0EBase), ctc0, cfc0, Mfc0(A0, AllegrexCore.Cop0CpuId), Mtc0(T0, AllegrexCore.Cop0CpuId), Mfc0(A1, AllegrexCore.Cop0CpuId));
        rig[T0] = 0x88004000;
        rig.Step(7);
        Assert.Equal(0x88004000u, rig.Core.EBase);
        Assert.Equal(0x88004000u, rig[T2]);
        Assert.Equal(0x88004000u, rig[T1]);
        Assert.Equal(0x88004000u, rig.Core.ReadCop0Control(12));
        Assert.Equal(0u, rig[A0]);   // main CPU
        Assert.Equal(0u, rig[A1]);   // read-only
    }

    [Fact]
    public void CauseWritesOnlyTheSoftwareInterruptBits()
    {
        AllegrexRig rig = new(Mtc0(T0, AllegrexCore.Cop0Cause), Mfc0(T1, AllegrexCore.Cop0Cause));
        rig[T0] = 0xFFFFFFFF;
        rig.Step(2);
        Assert.Equal(AllegrexCore.CauseSoftwareInterrupts, rig[T1]);
    }

    [Fact]
    public void CountFollowsExecutedInstructionsAndIsWritable()
    {
        AllegrexRig rig = new(Nop, Nop, Mfc0(T0, AllegrexCore.Cop0Count), Mtc0(T1, AllegrexCore.Cop0Count), Nop, Mfc0(T2, AllegrexCore.Cop0Count));
        rig[T1] = 1000;
        rig.Step(6);
        Assert.Equal(3u, rig[T0]);
        Assert.Equal(1002u, rig[T2]);
    }

    [Fact]
    public void InterruptIsTakenWithStatusIeAndUnmaskedLine()
    {
        AllegrexRig rig = new(Nop, Nop);
        rig.Core.Status = AllegrexCore.StatusIe | (1u << (8 + AllegrexCore.InterruptControllerLine));
        rig.Step();
        rig.Core.RaiseInterrupt(AllegrexCore.InterruptControllerLine);
        Assert.Equal(1, rig.Core.Run(1));
        Assert.Equal(AllegrexRig.ExceptionBase + 4, rig.Core.Pc); // the handler's first instruction ran
        Assert.Equal(AllegrexRig.CodeBase + 4, rig.Core.Epc);
        Assert.Equal((uint)AllegrexExceptionCode.Interrupt, ExceptionCode(rig.Core));
        Assert.Equal(1u << 10, rig.Core.Cause & AllegrexCore.CauseInterruptPending);
        Assert.NotEqual(0u, rig.Core.Status & AllegrexCore.StatusExl);
    }

    [Theory]
    [InlineData(0u, 0u, true, false)]                     // neither IE nor $ic
    [InlineData(AllegrexCore.StatusIe, 0u, true, true)]   // IE alone
    [InlineData(0u, 1u, true, true)]                      // mtic 1 alone (how the PSP kernel gates them)
    [InlineData(AllegrexCore.StatusIe, 1u, false, false)] // line masked in Status.IM
    [InlineData(AllegrexCore.StatusIe | AllegrexCore.StatusExl, 1u, true, false)] // already in an exception
    [InlineData(AllegrexCore.StatusIe | AllegrexCore.StatusErl, 1u, true, false)]
    public void InterruptGating(uint status, uint interruptControl, bool unmasked, bool taken)
    {
        AllegrexRig rig = new(Nop);
        uint mask = unmasked ? 1u << 10 : 1u << 11;
        rig.Core.Status = status | mask;
        rig.Core.InterruptControl = interruptControl;
        rig.Core.RaiseInterrupt(2);
        rig.Core.Run(1);
        Assert.Equal(taken ? AllegrexRig.ExceptionBase + 4 : AllegrexRig.CodeBase + 4, rig.Core.Pc);
    }

    [Fact]
    public void MticAndMficMoveOnlyBitZero()
    {
        AllegrexRig rig = new(Special2(0x26, 0, 0, T0), Special2(0x24, 0, 0, T1), Special2(0x26, 0, 0, T2), Special2(0x24, 0, 0, A0));
        rig[T0] = 3;
        rig[T2] = 2;
        rig.Step(4);
        Assert.Equal(1u, rig[T1]);
        Assert.Equal(0u, rig[A0]);
    }

    [Fact]
    public void ClearedInterruptIsNotTaken()
    {
        AllegrexRig rig = new(Nop);
        rig.Core.Status = AllegrexCore.StatusIe | 0xFF00;
        rig.Core.RaiseInterrupt(5);
        rig.Core.ClearInterrupt(5);
        rig.Step();
        Assert.Equal(AllegrexRig.CodeBase + 4, rig.Core.Pc);
    }

    [Fact]
    public void InterruptInADelaySlotReportsTheBranch()
    {
        // beq zero, zero, +2 ; nop (slot) ; ... the interrupt arrives with the slot next
        AllegrexRig rig = new(Beq(Zero, Zero, 2), Nop, Nop, Nop);
        rig.Core.Status = AllegrexCore.StatusIe | (1u << 10);
        rig.Step();
        rig.Core.RaiseInterrupt(2);
        rig.Core.Run(1);
        Assert.Equal(AllegrexRig.CodeBase, rig.Core.Epc);
        Assert.NotEqual(0u, rig.Core.Cause & AllegrexCore.CauseBranchDelay);
    }

    [Fact]
    public void HaltWaitsUntilAnUnmaskedLineEvenWithInterruptsDisabled()
    {
        AllegrexRig rig = new(Halt, Addiu(T0, Zero, 1));
        rig.Core.Status = 1u << 10;  // IM2, IE clear, $ic 0
        Assert.Equal(1, rig.Core.Run(10));
        Assert.True(rig.Core.Halted);
        Assert.Equal(0, rig.Core.Run(10));
        rig.Core.RaiseInterrupt(3);  // masked: still halted
        Assert.Equal(0, rig.Core.Run(10));
        rig.Core.RaiseInterrupt(2);
        Assert.Equal(1, rig.Core.Run(1));
        Assert.False(rig.Core.Halted);
        Assert.Equal(1u, rig[T0]);   // not taken (disabled): it goes on after halt
    }

    [Fact]
    public void StopEndsRunUntilReset()
    {
        AllegrexRig rig = new(Nop, Nop);
        rig.Core.Stop();
        Assert.Equal(0, rig.Core.Run(5));
        rig.Core.Reset(AllegrexRig.CodeBase);
        Assert.Equal(5, rig.Core.Run(5));
    }

    [Theory]
    [InlineData(0x68000000u)] // primary 0x1A
    [InlineData(0xB0000000u)] // me2c (Media Engine only)
    [InlineData(0x00000001u)] // SPECIAL 0x01
    [InlineData(0x7000003Fu)] // SPECIAL2 0x3F
    [InlineData(0x7C00003Fu)] // SPECIAL3 0x3F
    [InlineData(0x42000001u)] // tlbr: no TLB
    public void ReservedInstructionRaisesTheException(uint instruction)
    {
        AllegrexRig rig = new(instruction);
        rig.Step();
        Assert.Equal((uint)AllegrexExceptionCode.ReservedInstruction, ExceptionCode(rig.Core));
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc);
    }

    [Theory]
    [InlineData(0xF0000000u, "VFPU")] // vmmul
    public void VfpuIsReportedAsNotEmulated(uint instruction, string unit)
    {
        AllegrexRig rig = new(instruction);
        AllegrexUnimplementedException error = Assert.Throws<AllegrexUnimplementedException>(() => rig.Core.Run(1));
        Assert.Equal(instruction, error.Instruction);
        Assert.Equal(AllegrexRig.CodeBase, error.Address);
        Assert.StartsWith(unit, error.Unit);

        AllegrexRig handled = new(instruction, Addiu(T0, Zero, 1));
        handled.Core.TrapHandler = (core, kind, code, address) => kind == AllegrexTrapKind.Unimplemented && code == instruction;
        handled.Step(2);
        Assert.Equal(1u, handled[T0]);
    }

    [Fact]
    public void CacheAndSyncDoNothing()
    {
        AllegrexRig rig = new(IType(0x2F, 0x19, T0, 0x40), RType(0x0F, 0, 0, 0), Addiu(T1, Zero, 1));
        rig.Step(3);
        Assert.Equal(1u, rig[T1]);
        Assert.Equal(0, rig.Bus.Writes);
    }

    [Fact]
    public void ResetStateMatchesThePspCore()
    {
        AllegrexCore core = new(new AllegrexTestBus());
        core.Reset(0x88600000);
        Assert.Equal(0x88600000u, core.Pc);
        Assert.Equal(AllegrexCore.ResetStatus, core.Status);
        Assert.Equal(AllegrexCore.ResetConfig, core.ReadCop0(AllegrexCore.Cop0Config));
        Assert.False(core.Halted);
        Assert.Equal(0u, core.InterruptControl);
    }

    [Fact]
    public void CountReachingCompareRaisesTheTimerLineUntilCompareIsWritten()
    {
        AllegrexRig rig = new(Nop, Nop, Nop, Nop, Nop, Nop, Nop, Nop);
        rig.Core.WriteCop0(AllegrexCore.Cop0Compare, 5);
        rig.Step(4);
        Assert.Equal(0u, rig.Core.Cause & AllegrexCore.CauseTimerInterrupt);
        rig.Step();
        Assert.Equal(AllegrexCore.CauseTimerInterrupt, rig.Core.Cause & AllegrexCore.CauseTimerInterrupt);
        rig.Step();
        Assert.Equal(AllegrexCore.CauseTimerInterrupt, rig.Core.Cause & AllegrexCore.CauseTimerInterrupt); // level, stays
        rig.Core.WriteCop0(AllegrexCore.Cop0Compare, 100);
        Assert.Equal(0u, rig.Core.Cause & AllegrexCore.CauseTimerInterrupt);
    }

    [Fact]
    public void TimerInterruptIsTakenOnLineSeven()
    {
        AllegrexRig rig = new(Nop, Nop, Nop, Nop, Nop, Nop, Nop, Nop);
        rig.Core.Status = AllegrexCore.StatusIe | AllegrexCore.CauseTimerInterrupt; // IM7
        rig.Core.WriteCop0(AllegrexCore.Cop0Compare, 3);
        rig.Step(3);
        rig.Step();
        Assert.Equal(AllegrexRig.ExceptionBase + 4, rig.Core.Pc);
        Assert.Equal(AllegrexRig.CodeBase + 12, rig.Core.Epc);
        Assert.Equal((uint)AllegrexExceptionCode.Interrupt, ExceptionCode(rig.Core));
    }
}
