using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The spare shader workers (perf/stut1's below-normal "spare-time" workers, now part of the one CPU budget,
/// <see cref="ShaderWorkerBudget"/>): the pool is the budget's <see cref="ShaderWorkerBudget.Stall"/> workers, the
/// <see cref="ShaderWorkerBudget.Steady"/> share at normal priority and the ones above it the spare workers, at
/// below-normal priority, that run while the GPU thread waits for the workers. The whole pool never takes more than 90%
/// of the logical processors, and no second rule adds workers on top of it.
/// </summary>
public sealed class ShaderWorkerCountTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(4, 1)] // the emulator pinned to half of an 8-thread machine
    [InlineData(6, 2)]
    [InlineData(8, 2)]
    [InlineData(12, 2)]
    [InlineData(16, 4)]
    [InlineData(64, 4)]
    public void TheSpareWorkersAreTheBudgetAboveTheSteadyShare(int processors, int spare)
    {
        var budget = ShaderWorkerBudget.For(processors, mobile: false, littleCores: 0);

        Assert.Equal(spare, budget.Spare);
        Assert.Equal(budget.Stall, budget.Steady + budget.Spare);
        // steady and spare together stay within 90% of the processors (one worker at least)
        Assert.True(budget.Stall <= Math.Max(1, processors * 9 / 10), $"{budget.Stall} of {processors}");
    }

    [Theory]
    [InlineData(8, 6, 2)] // 2 big + 6 LITTLE (Redmi Note 14): 2 steady, 2 spare
    [InlineData(8, 4, 1)]
    [InlineData(4, 0, 0)]
    public void PhonesKeepTheirSpareWorkersOnTheLittleCoresBudget(int processors, int littleCores, int spare)
    {
        Assert.Equal(spare, ShaderWorkerBudget.For(processors, mobile: true, littleCores).Spare);
    }

    [Fact]
    public void AFixedPoolHasNoSpareWorkersUnlessTheyAreAsked()
    {
        Assert.Equal(0, ShaderWorkerBudget.Fixed(3).Spare);
        // VITASHARP_SHADER_SPARE_WORKERS: N spare workers above the same steady share
        Assert.Equal(new ShaderWorkerBudget(3, 5), ShaderWorkerBudget.Fixed(3).WithSpare(2));
        Assert.Equal(new ShaderWorkerBudget(5, 5), ShaderWorkerBudget.For(8, mobile: false, littleCores: 0).WithSpare(0));
        Assert.Equal(new ShaderWorkerBudget(5, 5), ShaderWorkerBudget.For(8, mobile: false, littleCores: 0).WithSpare(-1));
        Assert.Equal(64, ShaderWorkerBudget.Fixed(60).WithSpare(10).Stall);
    }
}
