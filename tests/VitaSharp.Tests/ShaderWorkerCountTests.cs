using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The automatic shader worker pool (<c>VulkanRenderer.AutoShaderWorkers</c>, VulkanRenderer.AsyncShaders.cs): half
/// the logical processors at normal priority as before, plus spare-time workers at below-normal priority up to 90% of
/// the processors less one for the GPU thread, at most 8 in all; none on Android, where the thread placement owns the
/// cores.
/// </summary>
public sealed class ShaderWorkerCountTests
{
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 1, 0)]
    [InlineData(4, 2, 0)]
    [InlineData(6, 3, 1)]
    [InlineData(8, 4, 2)]
    [InlineData(12, 6, 2)]
    [InlineData(16, 6, 2)]
    [InlineData(32, 6, 2)]
    public void DesktopAddsSpareWorkersUpToTheCpuBudget(int processors, int normal, int spare)
    {
        (int Normal, int Spare) workers = VulkanRenderer.AutoShaderWorkers(processors, mobile: false);

        Assert.Equal((normal, spare), workers);
        // the pool never takes more than 90% of the processors less the GPU thread's core (one worker at least)
        Assert.True(workers.Normal + workers.Spare <= Math.Max(processors * 9 / 10 - 1, 1));
        Assert.True(workers.Normal + workers.Spare <= 8);
    }

    [Theory]
    [InlineData(4, 2)]
    [InlineData(8, 4)]
    [InlineData(12, 6)]
    public void MobileKeepsTheNormalPoolOnly(int processors, int normal)
    {
        Assert.Equal((normal, 0), VulkanRenderer.AutoShaderWorkers(processors, mobile: true));
    }
}
