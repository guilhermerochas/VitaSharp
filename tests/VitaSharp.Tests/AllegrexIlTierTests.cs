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
        using IDisposable tier = AllegrexCore.ForceIlTier();
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
