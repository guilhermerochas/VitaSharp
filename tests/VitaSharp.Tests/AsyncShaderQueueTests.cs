using System.Collections.Concurrent;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The shader workers' pipeline queues (VulkanRenderer.TryNextPipelineJob): the pipelines of draws already submitted
/// go before the ones predicted when the game created a program, and the predicted ones wait while the GPU thread
/// builds a pipeline itself. In one FIFO, a new Killzone area's program loads queued their predictions ahead of the
/// pipelines its draws needed; the GPU thread built those itself, slowed by the workers' speculative compiles (3.6 s).
/// </summary>
public sealed class AsyncShaderQueueTests
{
    [Fact]
    public void ThePipelineOfASubmittedDrawRunsBeforeTheSpeculativeOnesQueuedEarlier()
    {
        var needed = new ConcurrentQueue<string>();
        var speculative = new ConcurrentQueue<string>();
        speculative.Enqueue("predicted 1");
        speculative.Enqueue("predicted 2");
        needed.Enqueue("draw");

        Assert.True(VulkanRenderer.TryNextPipelineJob(needed, speculative, gpuThreadBuilding: false, out string? first));
        Assert.True(VulkanRenderer.TryNextPipelineJob(needed, speculative, gpuThreadBuilding: false, out string? second));

        Assert.Equal("draw", first);
        Assert.Equal("predicted 1", second);
    }

    [Fact]
    public void SpeculativeJobsWaitWhileTheGpuThreadBuildsAPipeline()
    {
        var needed = new ConcurrentQueue<string>();
        var speculative = new ConcurrentQueue<string>();
        speculative.Enqueue("predicted");

        Assert.False(VulkanRenderer.TryNextPipelineJob(needed, speculative, gpuThreadBuilding: true, out string? held));
        Assert.Null(held);
        Assert.Single(speculative);

        needed.Enqueue("draw");
        Assert.True(VulkanRenderer.TryNextPipelineJob(needed, speculative, gpuThreadBuilding: true, out string? draw));
        Assert.Equal("draw", draw);
        Assert.True(VulkanRenderer.TryNextPipelineJob(needed, speculative, gpuThreadBuilding: false, out string? later));
        Assert.Equal("predicted", later);
    }
}
