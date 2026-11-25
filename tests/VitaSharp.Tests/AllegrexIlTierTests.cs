using System.Reflection;
using VitaSharp.Core.Compat.Allegrex;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>
/// The IL tier keeps the interpreter's behaviour: a hot loop compiles and ends with the same state, a misaligned
/// access inside compiled code raises its address error at the exact PC, an interrupt a store raises is taken at the
/// next instruction, and every other Allegrex test passes again with the tier forced on (every compilable block
/// compiled at its first run).
/// </summary>
public sealed class AllegrexIlTierTests
{
    private const uint Base = AllegrexRig.CodeBase;

    private static uint[] Loop()
    {
        // t0 += t1; t2 ^= t0; t1 += 1; lw/sw through a0; bne t1, a1, start; slot: addu v0, v0, t0
        return
        [
            Addu(T0, T0, T1),
            RType(0x26, T2, T2, T0),
            Addiu(T1, T1, 1),
            Lw(V1, A0, 0),
            Addu(V1, V1, T2),
            Sw(V1, A0, 0),
            Bne(T1, A1, -7),
            Addu(V0, V0, T0),
            Nop,
        ];
    }

    private static AllegrexRig RunLoop(long hotInstructions, out int compiled)
    {
        using IDisposable tier = AllegrexCore.ForceIlTier(hotInstructions);
        AllegrexRig rig = new(Loop());
        rig[A0] = Base + 0x3000;
        rig[A1] = 5000;
        rig.Core.Run(5000 * 7);
        compiled = rig.Core.CompiledBlocks;
        return rig;
    }

    [Fact]
    public void AHotLoopCompilesAndEndsWithTheInterpretersState()
    {
        if (!AllegrexCore.IlTierSupported)
        {
            return;
        }

        AllegrexRig interpreted = RunLoop(long.MaxValue, out int none);
        AllegrexRig compiled = RunLoop(0, out int blocks);
        Assert.Equal(0, none);
        Assert.True(blocks > 0);
        for (int register = 0; register < 32; register++)
        {
            Assert.Equal(interpreted[register], compiled[register]);
        }

        Assert.Equal(interpreted.Core.Pc, compiled.Core.Pc);
        Assert.Equal(interpreted.Bus.Read32(Base + 0x3000), compiled.Bus.Read32(Base + 0x3000));
    }

    /// <summary>
    /// A two-block loop with FPU and VFPU arithmetic (one region: the blocks jump to each other in compiled code),
    /// with the Count/Compare timer interrupt arriving inside it, and optionally the inexact exception enabled.
    /// </summary>
    private static AllegrexRig RunFloatLoop(long hotInstructions, bool trapInexact, out int compiled)
    {
        uint addS = 0x46000000u | (1u << 16) | (0u << 11) | (2u << 6);   // add.s f2, f0, f1
        uint mulS = 0x46000002u | (1u << 16) | (2u << 11) | (0u << 6);   // mul.s f0, f2, f1
        uint vadd = 0x60000000u | (2u << 16) | (1u << 8) | 0u | 0x8080u; // vadd.q C000, C010, C020
        uint vmul = 0x64000000u | (2u << 16) | (0u << 8) | 1u | 0x8080u; // vmul.q C010, C000, C020
        uint vdot = 0x64800000u | (1u << 16) | (0u << 8) | 3u | 0x8080u; // vdot.q S030, C000, C010
        uint jump = (0x02u << 26) | ((Base >> 2) & 0x03FFFFFF);
        using IDisposable tier = AllegrexCore.ForceIlTier(hotInstructions);
        AllegrexRig rig = new(addS, vadd, Beq(T0, Zero, 3), Addiu(T1, T1, 1), Nop, Nop, mulS, vdot, jump, vmul);
        rig.Core.Fpr[0] = BitConverter.SingleToUInt32Bits(1.5f);
        rig.Core.Fpr[1] = BitConverter.SingleToUInt32Bits(1.0001f);
        for (int i = 0; i < 128; i++)
        {
            rig.Core.WriteVfpuRegister(i, BitConverter.SingleToUInt32Bits(0.25f + (i * 0.125f)));
        }

        rig.Core.Fcr31 = trapInexact ? 0x80u : 0u;
        rig.Core.Status = AllegrexCore.StatusIe | AllegrexCore.CauseTimerInterrupt;
        rig.Core.WriteCop0(AllegrexCore.Cop0Compare, 1003);
        rig.Core.Run(500);
        rig.Core.Run(1000);
        compiled = rig.Core.CompiledBlocks;
        return rig;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFloatLoopRegionEndsWithTheInterpretersState(bool trapInexact)
    {
        if (!AllegrexCore.IlTierSupported)
        {
            return;
        }

        AllegrexRig interpreted = RunFloatLoop(long.MaxValue, trapInexact, out _);
        AllegrexRig compiled = RunFloatLoop(0, trapInexact, out int regions);
        Assert.True(regions > 0);
        for (int register = 0; register < 32; register++)
        {
            Assert.Equal(interpreted[register], compiled[register]);
            Assert.Equal(interpreted.Core.Fpr[register], compiled.Core.Fpr[register]);
        }

        for (int register = 0; register < 128; register++)
        {
            Assert.Equal(interpreted.Core.ReadVfpuRegister(register), compiled.Core.ReadVfpuRegister(register));
        }

        Assert.Equal(interpreted.Core.Fcr31, compiled.Core.Fcr31);
        Assert.Equal(interpreted.Core.Pc, compiled.Core.Pc);
        Assert.Equal(interpreted.Core.Epc, compiled.Core.Epc);
        Assert.Equal(interpreted.Core.Cause, compiled.Core.Cause);
        Assert.Equal(interpreted.Core.ReadCop0(AllegrexCore.Cop0Count), compiled.Core.ReadCop0(AllegrexCore.Cop0Count));
    }

    [Fact]
    public void AMisalignedLoadInCompiledCodeRaisesAtItsPc()
    {
        if (!AllegrexCore.IlTierSupported)
        {
            return;
        }

        using IDisposable tier = AllegrexCore.ForceIlTier();
        AllegrexRig rig = new(Addiu(T0, T0, 1), Lw(V1, A0, 2), Addiu(T0, T0, 1), Bne(T0, Zero, -4), Nop);
        rig[A0] = Base + 0x3000;

        // the whole block's budget, so the compiled code runs: it leaves at the load, the decoder raises the error,
        // and the remaining three instructions run at the (empty) exception vector
        rig.Core.Run(5);
        Assert.Equal(AllegrexRig.ExceptionBase + 12, rig.Core.Pc);
        Assert.Equal(Base + 4, rig.Core.Epc);
        Assert.Equal(1u, rig[T0]);
        Assert.True(rig.Core.CompiledBlocks >= 1);
    }

    /// <summary>The Allegrex tests to run again with the tier forced: (class, method, InlineData row).</summary>
    public static TheoryData<string, string, int> AllegrexTests()
    {
        TheoryData<string, string, int> data = [];
        foreach (Type type in typeof(AllegrexIlTierTests).Assembly.GetTypes())
        {
            if (!type.Name.StartsWith("Allegrex", StringComparison.Ordinal) || !type.Name.EndsWith("Tests", StringComparison.Ordinal) || type == typeof(AllegrexIlTierTests))
            {
                continue;
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<TheoryAttribute>() != null)
                {
                    int rows = method.GetCustomAttributes<InlineDataAttribute>().Count();
                    for (int row = 0; row < rows; row++)
                    {
                        data.Add(type.FullName!, method.Name, row);
                    }
                }
                else if (method.GetCustomAttribute<FactAttribute>() != null)
                {
                    data.Add(type.FullName!, method.Name, -1);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllegrexTests))]
    public void PassesWithTheTierForced(string typeName, string methodName, int row)
    {
        Type type = typeof(AllegrexIlTierTests).Assembly.GetType(typeName)!;
        MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)!;
        object?[]? arguments = null;
        if (row >= 0)
        {
            arguments = method.GetCustomAttributes<InlineDataAttribute>().ElementAt(row).GetData(method).Single();
        }

        object? instance = method.IsStatic ? null : Activator.CreateInstance(type);

        // single-instruction blocks: a test that runs one instruction runs it in compiled code
        using IDisposable tier = AllegrexCore.ForceIlTier(0, 1);
        try
        {
            method.Invoke(instance, arguments);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }
}
