using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// SceGpuEs4ForDriver's services layer: devices, init server, per-process data, misc info, MMU, core dump. (FW 3.74)
/// </summary>
[Collection("guest")]
public sealed class KernelGpuServicesTests(GuestMemoryFixture fixture) : IDisposable
{

    private readonly HleHarness _harness = new(fixture, "gpuservice");

    public void Dispose() => _harness.Dispose();

    private unsafe uint R0(GuestThread thread, uint nid, params uint[] args)
    {
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not implemented");
        for (int i = 0; i < 4; i++)
        {
            thread.Core.State->R[i] = i < args.Length ? args[i] : 0xA5A5A5A5;
        }

        uint stack = thread.Core.State->R[13];
        thread.Core.State->R[13] = stack - 0x40;
        for (int i = 4; i < args.Length; i++)
        {
            GuestMemory.W32(stack - 0x40 + (uint)(i - 4) * 4, args[i]);
        }

        implementation(thread.Hle);
        thread.Core.State->R[13] = stack;
        return thread.Core.State->R[0];
    }

    [Fact]
    public void GpuServicesListTheSgxAndKeepPerProcessData()
    {
        _harness.OnGuest(t =>
        {
            uint count = _harness.Word(0);
            uint ids = _harness.Alloc(0x10);
            Assert.Equal(3u, R0(t, 0xEC5163B0, 0, ids));
            Assert.Equal(0u, R0(t, 0xEC5163B0, count, ids));
            Assert.Equal(1u, GuestMemory.R32(count));
            uint node = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0xE6B866FD, 0, 1, node));
            Assert.NotEqual(0u, GuestMemory.R32(node));
            Assert.Equal(4u, R0(t, 0xE6B866FD, 3, 1, node));

            Assert.Equal(0u, R0(t, 0x3AF6FE80, 2));
            Assert.Equal(0u, R0(t, 0xDCD78852, 2, 1));
            Assert.Equal(1u, R0(t, 0x3AF6FE80, 2));
            Assert.Equal(0xA2u, R0(t, 0xDCD78852, 5, 1));

            uint pid = (uint)t.Pid;
            Assert.Equal(0u, R0(t, 0x3DAB1F7F, pid));
            Assert.Equal(0u, R0(t, 0x1A53327D, pid));
            uint data = R0(t, 0x3DAB1F7F, pid);
            Assert.Equal(pid, GuestMemory.R32(data));
            uint owner = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0x7AA79658, data, owner));
            Assert.Equal(pid, GuestMemory.R32(owner));
            R0(t, 0xB4A2B60F, pid);
            Assert.Equal(0u, R0(t, 0x3DAB1F7F, pid));

            uint info = _harness.Alloc(0x100);
            uint text = _harness.Alloc(0x20);
            GuestMemory.W32(info, 0x10);
            GuestMemory.W32(info + 8, text);
            GuestMemory.W32(info + 0xC, 0x20);
            Assert.Equal(0u, R0(t, 0x8FB57186, info));
            Assert.Equal("1.7.17.5029", GuestMemory.ReadCString(text));
            GuestMemory.W32(info, 0x1);
            Assert.Equal(3u, R0(t, 0x8FB57186, info));

            Assert.Equal(0x804C0000u, R0(t, 0xEF4B16D3, 1, 0x1001, 0, 0x1000, 0));
            Assert.Equal(0u, R0(t, 0xEF4B16D3, 1, 0x1000, 0x81000000, 0x1000, 0));
            Assert.Equal(0u, R0(t, 0x9AAAD91C, 1, 0x1000, 0x1000));
            Assert.Equal(0x804C0000u, R0(t, 0x14ECCFCD, _harness.Alloc(0x810), 0x810));
            Assert.Equal(0x810u, R0(t, 0x395F7689));
        });
    }
}
