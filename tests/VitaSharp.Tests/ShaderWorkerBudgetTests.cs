using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The shader workers' CPU budget (<see cref="ShaderWorkerBudget"/>): the whole process at most 90% of the logical
/// processors, two cores left to the guest CPU thread and the GPU thread while the GPU thread works, all of the budget
/// while it waits for the workers. The automatic count used to be half of the processors in every state (8 logical
/// processors: 4, also during a stall with 3 cores idle), and the same on a phone.
/// </summary>
public sealed class ShaderWorkerBudgetTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 1)]
    [InlineData(4, 2, 3)] // the emulator pinned to half of an 8-thread machine
    [InlineData(6, 3, 5)]
    [InlineData(8, 5, 7)]
    [InlineData(12, 8, 10)]
    [InlineData(16, 8, 12)]
    [InlineData(64, 8, 12)]
    public void DesktopWorkersFollowTheCpuBudget(int processors, int steady, int stall)
    {
        ShaderWorkerBudget budget = ShaderWorkerBudget.For(processors, mobile: false, littleCores: 0);

        Assert.Equal(new ShaderWorkerBudget(steady, stall), budget);
        // the whole pool never takes more than 90% of the processors (one at least)
        Assert.True(budget.Stall <= Math.Max(1, processors * 9 / 10), $"{budget.Stall} of {processors}");
    }

    [Theory]
    [InlineData(8, 6, 2, 4)] // 2 big + 6 LITTLE (Redmi Note 14)
    [InlineData(8, 4, 1, 2)] // 4 big + 4 LITTLE
    [InlineData(8, 0, 1, 2)] // clusters not known: half the cores taken as LITTLE
    [InlineData(4, 0, 1, 1)]
    [InlineData(10, 8, 2, 4)]
    public void PhoneWorkersRunOnTheLittleCoresAndFewerThanOnADesktop(int processors, int littleCores, int steady, int stall)
    {
        ShaderWorkerBudget budget = ShaderWorkerBudget.For(processors, mobile: true, littleCores);

        Assert.Equal(new ShaderWorkerBudget(steady, stall), budget);
        Assert.True(budget.Stall <= ShaderWorkerBudget.For(processors, mobile: false, littleCores: 0).Stall);
    }

    [Fact]
    public void LittleCoresAreTheCoresOfTheSlowestCluster()
    {
        Assert.Equal(6, ShaderWorkerBudget.LittleCores([2_000_000, 2_000_000, 2_000_000, 2_000_000, 2_000_000, 2_000_000, 2_600_000, 2_600_000]));
        Assert.Equal(3, ShaderWorkerBudget.LittleCores([1_800_000, 1_800_000, 1_800_000, 2_400_000, 2_400_000, 3_000_000]));
        Assert.Equal(0, ShaderWorkerBudget.LittleCores([2_400_000, 2_400_000, 2_400_000, 2_400_000])); // one cluster
        Assert.Equal(0, ShaderWorkerBudget.LittleCores([2_000_000, 0, 2_600_000])); // a core not known
        Assert.Equal(0, ShaderWorkerBudget.LittleCores([]));
    }

    [Fact]
    public void AFixedCountIsTheSameInEveryState()
    {
        Assert.Equal(new ShaderWorkerBudget(3, 3), ShaderWorkerBudget.Fixed(3));
        Assert.Equal(new ShaderWorkerBudget(64, 64), ShaderWorkerBudget.Fixed(1000));
        Assert.Equal(new ShaderWorkerBudget(1, 1), ShaderWorkerBudget.Fixed(0));
    }
}
