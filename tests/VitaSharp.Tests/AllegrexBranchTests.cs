using VitaSharp.Core.Compat.Allegrex;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>
/// Allegrex branches and jumps: the delay slot runs whether or not the branch is taken, branch-likely annuls it when
/// not taken, links are branch + 8, exceptions in a slot report the branch; plus a small program run to a break.
/// </summary>
public sealed class AllegrexBranchTests
{
    private const uint Base = AllegrexRig.CodeBase;

    // branch at Base, slot at Base + 4 (addiu t1, t1, 1), fall-through at Base + 8, target at Base + 16
    private static AllegrexRig RunBranch(uint branch, uint rsValue, uint rtValue = 0)
    {
        AllegrexRig rig = new(branch, Addiu(T1, T1, 1), Nop, Nop, Nop);
        rig[T0] = rsValue;
        rig[T2] = rtValue;
        rig.Step(2);
        return rig;
    }

    [Theory]
    [InlineData(0x04, 5u, 5u, true)]   // beq
    [InlineData(0x04, 5u, 6u, false)]
    [InlineData(0x05, 5u, 6u, true)]   // bne
    [InlineData(0x05, 5u, 5u, false)]
    [InlineData(0x06, 0u, 0u, true)]   // blez
    [InlineData(0x06, 0x80000000u, 0u, true)]
    [InlineData(0x06, 1u, 0u, false)]
    [InlineData(0x07, 1u, 0u, true)]   // bgtz
    [InlineData(0x07, 0u, 0u, false)]
    [InlineData(0x07, 0xFFFFFFFFu, 0u, false)]
    public void BranchesAlwaysRunTheDelaySlot(int opcode, uint left, uint right, bool taken)
    {
        AllegrexRig rig = RunBranch(IType(opcode, T2, T0, 3), left, right);
        Assert.Equal(1u, rig[T1]);
        Assert.Equal(taken ? Base + 16 : Base + 8, rig.Core.Pc);
    }

    [Theory]
    [InlineData(0x14, 5u, 5u, true)]   // beql
    [InlineData(0x14, 5u, 6u, false)]
    [InlineData(0x15, 5u, 6u, true)]   // bnel
    [InlineData(0x15, 5u, 5u, false)]
    [InlineData(0x16, 0u, 0u, true)]   // blezl
    [InlineData(0x16, 1u, 0u, false)]
    [InlineData(0x17, 1u, 0u, true)]   // bgtzl
    [InlineData(0x17, 0u, 0u, false)]
    public void BranchLikelyAnnulsTheSlotWhenNotTaken(int opcode, uint left, uint right, bool taken)
    {
        AllegrexRig rig = new(IType(opcode, T2, T0, 3), Addiu(T1, T1, 1), Addiu(A0, A0, 1), Nop, Nop);
        rig[T0] = left;
        rig[T2] = right;
        rig.Step(2);
        if (taken)
        {
            Assert.Equal(1u, rig[T1]);
            Assert.Equal(0u, rig[A0]);
            Assert.Equal(Base + 16, rig.Core.Pc);
        }
        else
        {
            Assert.Equal(0u, rig[T1]);    // the slot never ran
            Assert.Equal(1u, rig[A0]);    // the second instruction was the one after the slot
            Assert.Equal(Base + 12, rig.Core.Pc);
        }
    }

    [Theory]
    [InlineData(0x00, 0xFFFFFFFFu, true, false)]  // bltz
    [InlineData(0x00, 0u, false, false)]
    [InlineData(0x01, 0u, true, false)]           // bgez
    [InlineData(0x01, 0xFFFFFFFFu, false, false)]
    [InlineData(0x10, 0xFFFFFFFFu, true, true)]   // bltzal
    [InlineData(0x10, 0u, false, true)]           // links even when not taken
    [InlineData(0x11, 0u, true, true)]            // bgezal
    [InlineData(0x11, 0xFFFFFFFFu, false, true)]
    public void RegImmBranchesAndLinks(int rtField, uint value, bool taken, bool links)
    {
        AllegrexRig rig = new(IType(0x01, rtField, T0, 3), Addiu(T1, T1, 1), Nop, Nop, Nop);
        rig[T0] = value;
        rig[Ra] = 0x1234;
        rig.Step(2);
        Assert.Equal(1u, rig[T1]);
        Assert.Equal(taken ? Base + 16 : Base + 8, rig.Core.Pc);
        Assert.Equal(links ? Base + 8 : 0x1234u, rig[Ra]);
    }

    [Theory]
    [InlineData(0x02, 0u, false)]          // bltzl not taken: slot annulled
    [InlineData(0x03, 0u, true)]           // bgezl taken
    [InlineData(0x12, 0u, false)]          // bltzall: links, annuls
    [InlineData(0x13, 0u, true)]           // bgezall
    public void RegImmLikelyForms(int rtField, uint value, bool taken)
    {
        AllegrexRig rig = new(IType(0x01, rtField, T0, 3), Addiu(T1, T1, 1), Nop, Nop, Nop);
        rig[T0] = value;
        rig.Step(2);
        Assert.Equal(taken ? 1u : 0u, rig[T1]);
        Assert.Equal(taken ? Base + 16 : Base + 12, rig.Core.Pc);
        Assert.Equal(rtField >= 0x12 ? Base + 8 : 0u, rig[Ra]);
    }

    [Fact]
    public void JumpAndLinkAndReturn()
    {
        // jal Base + 0x100 ; slot ; (return point) addiu a0, a0, 1
        uint jal = (0x03u << 26) | (((Base + 0x100) >> 2) & 0x03FFFFFF);
        AllegrexRig rig = new(jal, Addiu(T1, T1, 1), Addiu(A0, A0, 1));
        rig.Bus.Load(Base + 0x100, [RType(0x08, 0, Ra, 0), Addiu(T2, T2, 1)]); // jr ra ; slot
        rig.Step(5);
        Assert.Equal(Base + 8, rig[Ra]);
        Assert.Equal(1u, rig[T1]);
        Assert.Equal(1u, rig[T2]);
        Assert.Equal(1u, rig[A0]);
        Assert.Equal(Base + 12, rig.Core.Pc);
    }

    [Fact]
    public void JumpKeepsTheTopBitsOfTheDelaySlotAddress()
    {
        uint j = (0x02u << 26) | 0x40;  // index 0x40 -> 0x100 within the 256 MB region
        AllegrexRig rig = new(j, Nop);
        rig.Step(2);
        Assert.Equal(0x80000100u, rig.Core.Pc);
    }

    [Fact]
    public void JalrReadsTheTargetBeforeWritingTheLink()
    {
        AllegrexRig rig = new(RType(0x09, T0, T0, 0), Nop); // jalr t0, t0
        rig[T0] = Base + 0x40;
        rig.Step(2);
        Assert.Equal(Base + 0x40, rig.Core.Pc);
        Assert.Equal(Base + 8, rig[T0]);
    }

    [Fact]
    public void ExceptionInADelaySlotReportsTheBranchWithBd()
    {
        AllegrexRig rig = new(Beq(Zero, Zero, 3), Break(), Nop, Nop, Nop);
        rig.Step(2);
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc);
        Assert.Equal(Base, rig.Core.Epc);
        Assert.NotEqual(0u, rig.Core.Cause & AllegrexCore.CauseBranchDelay);

        AllegrexRig plain = new(Break());
        plain.Core.WriteCop0(AllegrexCore.Cop0Cause, 0);
        plain.Step();
        Assert.Equal(0u, plain.Core.Cause & AllegrexCore.CauseBranchDelay);
    }

    [Fact]
    public void JumpToAMisalignedAddressRaisesAnAddressErrorOnFetch()
    {
        AllegrexRig rig = new(RType(0x08, 0, T0, 0), Nop);
        rig[T0] = Base + 0x42;
        rig.Step(3);
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc);
        Assert.Equal(Base + 0x42, rig.Core.ReadCop0(AllegrexCore.Cop0BadVAddr));
        Assert.Equal(Base + 0x42, rig.Core.Epc);
        Assert.Equal((uint)AllegrexExceptionCode.AddressErrorLoad << 2, rig.Core.Cause & AllegrexCore.CauseExceptionCodeMask);
    }

    /// <summary>
    /// A small program: sums 1..10 into v0 with a counted loop (bne with the decrement in its delay slot), stores the
    /// result, calls a subroutine that doubles it, and stops at a break the host handles.
    /// </summary>
    [Fact]
    public void ProgramSumsALoopAndStopsAtBreak()
    {
        uint[] program =
        [
            Addiu(V0, Zero, 0),           // 0x00 v0 = 0
            Addiu(T0, Zero, 10),          // 0x04 t0 = 10
            Addu(V0, V0, T0),             // 0x08 loop: v0 += t0
            Bne(T0, At, -2),              // 0x0C if t0 != 1 goto loop (at = 1)
            Addiu(T0, T0, -1),            // 0x10 slot: t0--
            Lui(A0, 0x8801),              // 0x14 a0 = 0x88010000
            Sw(V0, A0, 0),                // 0x18 [a0] = v0
            (0x03u << 26) | (((Base + 0x40) >> 2) & 0x03FFFFFF), // 0x1C jal double
            Lw(A1, A0, 0),                // 0x20 slot: a1 = [a0]
            Break(0x7F),                  // 0x24
            Nop,
            Nop,
            Nop,
            Nop,
            Nop,
            Nop,
            Addu(V0, V0, V0),             // 0x40 double: v0 += v0
            RType(0x08, 0, Ra, 0),        // 0x44 jr ra
            Nop,                          // 0x48
        ];
        AllegrexRig rig = new(program);
        rig[At] = 1;
        uint breakCode = 0;
        rig.Core.TrapHandler = (core, kind, code, address) =>
        {
            Assert.Equal(AllegrexTrapKind.Break, kind);
            breakCode = code;
            core.Stop();
            return true;
        };

        int executed = rig.Core.Run(1000);

        Assert.True(rig.Core.Stopped);
        Assert.Equal(0x7Fu, breakCode);
        Assert.Equal(110u, rig[V0]);
        Assert.Equal(55u, rig[A1]);
        Assert.Equal(55u, rig.Bus.Read32(0x08010000));
        Assert.Equal(2 + 30 + 4 + 3 + 1, executed);
    }
}
