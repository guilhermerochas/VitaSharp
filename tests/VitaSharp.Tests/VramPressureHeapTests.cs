using VitaSharp.Renderer.Vulkan;
using HeapInfo = VitaSharp.Renderer.Vulkan.VkCore.HeapInfo;

namespace VitaSharp.Tests;

/// <summary>
/// The VRAM pressure that drives the renderer's evictions (<see cref="VkCore.HeapPressure"/>) counts only the
/// device-local heaps the renderer allocates in. A desktop GPU without resizable BAR has a small device-local,
/// host-visible heap (the BAR) that the driver and other processes fill; the renderer never allocates there, so its
/// caches cannot lower that heap's usage. Counting it held the pressure at 198% (MGS HD after switching games through
/// the shell, "[vram] pressure 198% ... evicting 0 MB") and the trim returned the image pool's empty pages every 4
/// presents while the main heap was far from full.
/// </summary>
public sealed class VramPressureHeapTests
{
    private const ulong Mb = 1ul << 20;

    // heap 0: main VRAM, 30% used; heap 1: system memory; heap 2: the BAR, 198% of a 72 MB budget
    private static readonly HeapInfo[] s_desktopNoRebar =
    [
        new(0, 8192 * Mb, DeviceLocal: true, Budget: 7000 * Mb, Usage: 2100 * Mb),
        new(1, 16384 * Mb, DeviceLocal: false, Budget: 12000 * Mb, Usage: 9000 * Mb),
        new(2, 256 * Mb, DeviceLocal: true, Budget: 72 * Mb, Usage: 143 * Mb),
    ];

    [Fact]
    public void HeapTheRendererDoesNotUse_DoesNotSetThePressure()
    {
        double pressure = VkCore.HeapPressure(s_desktopNoRebar, usedHeapMask: 1u << 0, out int heapIndex);

        Assert.Equal(0.30, pressure, 2);
        Assert.Equal(0, heapIndex);
    }

    [Fact]
    public void FullHeapTheRendererUses_SetsThePressure()
    {
        HeapInfo[] heaps =
        [
            new(0, 8192 * Mb, DeviceLocal: true, Budget: 7000 * Mb, Usage: 6650 * Mb),
            s_desktopNoRebar[1],
            s_desktopNoRebar[2],
        ];

        double pressure = VkCore.HeapPressure(heaps, usedHeapMask: 1u << 0, out int heapIndex);

        Assert.Equal(0.95, pressure, 2);
        Assert.Equal(0, heapIndex);
    }

    [Fact]
    public void RendererInSeveralHeaps_TheFullestOfThemCounts()
    {
        double pressure = VkCore.HeapPressure(s_desktopNoRebar, usedHeapMask: (1u << 0) | (1u << 2), out int heapIndex);

        Assert.Equal(143.0 / 72.0, pressure, 2);
        Assert.Equal(2, heapIndex);
    }

    [Fact]
    public void HostHeap_NeverCounts()
    {
        double pressure = VkCore.HeapPressure(s_desktopNoRebar, usedHeapMask: 1u << 1, out int heapIndex);

        Assert.Equal(0, pressure);
        Assert.Equal(-1, heapIndex);
    }

    [Fact]
    public void NothingAllocatedYet_EveryDeviceLocalHeapCounts()
    {
        double pressure = VkCore.HeapPressure(s_desktopNoRebar, usedHeapMask: 0, out int heapIndex);

        Assert.Equal(143.0 / 72.0, pressure, 2);
        Assert.Equal(2, heapIndex);
    }

    [Fact]
    public void UnifiedMemory_OneHeap_Counts()
    {
        HeapInfo[] heaps = [new(0, 8192 * Mb, DeviceLocal: true, Budget: 4096 * Mb, Usage: 3900 * Mb)];

        double pressure = VkCore.HeapPressure(heaps, usedHeapMask: 1u << 0, out int heapIndex);

        Assert.Equal(3900.0 / 4096.0, pressure, 2);
        Assert.Equal(0, heapIndex);
    }

    [Fact]
    public void HeapWithoutBudget_IsSkipped()
    {
        HeapInfo[] heaps = [new(0, 8192 * Mb, DeviceLocal: true, Budget: 0, Usage: 3900 * Mb)];

        double pressure = VkCore.HeapPressure(heaps, usedHeapMask: 1u << 0, out int heapIndex);

        Assert.Equal(0, pressure);
        Assert.Equal(-1, heapIndex);
    }
}
