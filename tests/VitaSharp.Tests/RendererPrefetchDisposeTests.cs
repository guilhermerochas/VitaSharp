using System.Reflection;
using VitaSharp.Core.Gpu;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The GPU queue calls <see cref="IGpuBackend.Prefetch"/> on the submitting thread without its backend lock (it is on
/// every command's path), so a renderer detached or replaced (a live resolution change, a state load, a test's
/// teardown) can be disposed while a guest thread is inside its Prefetch. The renderer guards that itself: Prefetch
/// announces itself in <c>_prefetchActive</c> and re-reads the stop flag; Dispose raises the flag and waits for the
/// announced calls before it touches the device. On the software Vulkan device: Dispose does not get past a Prefetch
/// in progress, and a Prefetch after Dispose does nothing.
/// </summary>
[Collection("guest")]
public sealed class RendererPrefetchDisposeTests(GuestMemoryFixture fx)
{
    private static readonly FieldInfo s_prefetchActive = typeof(VulkanRenderer).GetField("_prefetchActive", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_stopShaderJobs = typeof(VulkanRenderer).GetField("_stopShaderJobs", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [SoftwareVulkanFact]
    public void DisposeWaitsForAPrefetchInProgressAndALaterPrefetchIsANoOp()
    {
        _ = fx;
        Assert.NotNull(s_prefetchActive);
        Assert.NotNull(s_stopShaderJobs);
        using IDisposable driver = SoftwareVulkan.Use();
        var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = true, AsyncPipelines = true });
        // a guest thread inside Prefetch, past its re-check of the stop flag
        s_prefetchActive.SetValue(renderer, 1);
        Exception? failure = null;
        var dispose = new Thread(() =>
        {
            try
            {
                renderer.Dispose();
            }
            catch (Exception e)
            {
                failure = e;
            }
        })
        {
            IsBackground = true,
            Name = "dispose",
        };
        dispose.Start();
        // Dispose raised the stop flag and now waits for the announced call: it cannot return while the count is 1.
        // The bounded join only decides how long a broken guard has to show itself (Dispose of the software device
        // takes well under it): with the guard the join can never succeed, so a slow machine cannot fail this test.
        SpinWait.SpinUntil(() => (bool)s_stopShaderJobs.GetValue(renderer)! || !dispose.IsAlive);
        Assert.False(dispose.Join(3000), "Dispose went on while a Prefetch was in progress");
        Assert.Equal(1, (int)s_prefetchActive.GetValue(renderer)!);
        // the Prefetch leaves
        s_prefetchActive.SetValue(renderer, 0);
        Assert.True(dispose.Join(120_000), "Dispose never returned after the Prefetch left");
        Assert.Null(failure);
        // a guest thread that read the old backend before the swap and calls it now: nothing happens
        renderer.Prefetch(new BeginSceneCommand { Width = 64, Height = 64 });
        Assert.Equal(0, (int)s_prefetchActive.GetValue(renderer)!);
    }
}
