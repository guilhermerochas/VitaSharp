using System.Diagnostics;
using VitaSharp.Core.Compat.Ge;
using VitaSharp.Renderer.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// A measurement, not a check (Category=Slow, no assertion on time or memory): what the GE's Vulkan device costs on
/// this computer's GPU (the system's Vulkan, not the software device). A "renderer" device is made first, as the GXM
/// renderer's; then the GE's rasterizer on a device of its own and on the renderer's (its second queue when it has
/// one), each with the time to make it, to draw its first list (pipelines compiled) and the memory it took: the
/// process's private bytes and the device-local heap's usage (VK_EXT_memory_budget, the driver's whole count for the
/// process). Run by hand: <c>dotnet test --filter FullyQualifiedName~GeDeviceCostBenchmarkTests</c>.
/// </summary>
[Trait("Category", "Slow")]
public sealed class GeDeviceCostBenchmarkTests(ITestOutputHelper output)
{
    private const uint ListAddress = 0x08001000, Vertices = 0x08010000;
    private const uint ThroughColorS16 = (1u << 23) | (7u << 2) | (2u << 7);

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    private static ulong DeviceHeapUsage(VkCore core)
    {
        ulong usage = 0;
        foreach (VkCore.HeapInfo heap in core.Heaps())
        {
            if (heap.DeviceLocal)
            {
                usage += heap.Usage;
            }
        }

        return usage;
    }

    private static void DrawOnce(IGeRasterizer rasterizer)
    {
        GeTestMemory memory = new();
        GeEngine engine = new(memory) { Rasterizer = rasterizer };
        memory.Write32(Vertices, 0xFF0000FF);
        memory.Write32(Vertices + 12, 0xFF0000FF);
        memory.Write16(Vertices + 16, 480);
        memory.Write16(Vertices + 18, 272);
        GeListBuilder list = new GeListBuilder(memory, ListAddress).RamBase()
            .Command(GeCommand.FrameBufferPointer, 0).Command(GeCommand.FrameBufferWidth, 512)
            .Command(GeCommand.FrameBufferPixelFormat, GePixels.Format8888)
            .Command(GeCommand.Scissor1, 0).Command(GeCommand.Scissor2, 479 | (271u << 10))
            .Command(GeCommand.VertexAddress, Vertices & 0xFFFFFF).Command(GeCommand.VertexType, ThroughColorS16)
            .Command(GeCommand.Primitive, (6u << 16) | 2).End();
        GeRegisters.Run(engine, list.Start);
    }

    private void Measure(string what, VkCore renderer, Func<GeVulkanRasterizerDevice?> make)
    {
        long bytesBefore = PrivateBytes();
        ulong heapBefore = DeviceHeapUsage(renderer);
        Stopwatch watch = Stopwatch.StartNew();
        GeVulkanRasterizerDevice? device = make();
        double createMs = watch.Elapsed.TotalMilliseconds;
        if (device == null)
        {
            output.WriteLine($"{what}: no device");
            return;
        }

        try
        {
            watch.Restart();
            DrawOnce(device);
            double firstMs = watch.Elapsed.TotalMilliseconds;
            long bytes = PrivateBytes() - bytesBefore;
            long heap = (long)DeviceHeapUsage(renderer) - (long)heapBefore;
            output.WriteLine($"{what}: created in {createMs:F1} ms, first list in {firstMs:F1} ms; private bytes +{bytes / 1048576.0:F1} MB, "
                + $"device-local heap usage +{heap / 1048576.0:F1} MB{(renderer.MemoryBudgetSupported ? "" : " (no VK_EXT_memory_budget)")}");
        }
        finally
        {
            device.Dispose();
        }
    }

    [Fact]
    public void TheGesDeviceOfItsOwnAgainstTheRenderersSecondQueue()
    {
        VkCore renderer;
        try
        {
            renderer = new VkCore(null, false);
        }
        catch (Exception failure) when (failure is InvalidOperationException or DllNotFoundException)
        {
            output.WriteLine($"no Vulkan: {failure.Message}");
            return;
        }

        try
        {
            output.WriteLine($"{renderer.DeviceName}: {(renderer.HasSecondaryQueue ? "a second graphics queue" : "one graphics queue")}");
            for (int round = 0; round < 2; round++)
            {
                Measure($"round {round} own device", renderer, () =>
                {
                    VkCore own = new(null, false);
                    return GeVulkanRasterizerDevice.CreateOn(own, own.Queue, own.SecondaryQueueLock, shared: false);
                });
                Measure($"round {round} shared device", renderer, () => GeVulkanRasterizerDevice.CreateOn(renderer.AddUser(),
                    renderer.HasSecondaryQueue ? renderer.SecondaryQueue : renderer.Queue, renderer.SecondaryQueueLock));
            }
        }
        finally
        {
            renderer.Dispose();
        }
    }
}
