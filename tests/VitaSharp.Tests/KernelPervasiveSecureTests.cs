using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// ScePervasiveForDriver's secure-monitor registers, clocks and DSI pixel clocks. (FW 3.74)
/// </summary>
[Collection("guest")]
public sealed class KernelPervasiveSecureTests(GuestMemoryFixture fixture) : IDisposable
{

    private readonly HleHarness _harness = new(fixture, "pervasives");

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
    public void SecurePervasiveRegistersAndClocksAreKept()
    {
        _harness.OnGuest(t =>
        {
            uint reset = R0(t, 0x81A155F1) + 0x1000; // the misc page, then the reset page
            Assert.Equal(0u, R0(t, 0x377126CD));
            Assert.Equal(1u, GuestMemory.R32(reset + 0x20) & 1);
            Assert.Equal(0u, R0(t, 0x6E11EB97));
            Assert.Equal(0u, GuestMemory.R32(reset + 0x20) & 1);
            Assert.Equal(0u, R0(t, 0xF345FB53, 2));
            Assert.Equal(1u, GuestMemory.R32(reset + 0x48) & 1);
            uint gate = reset + 0x1000;
            Assert.Equal(0u, R0(t, 0xA10583FF, 1));
            Assert.Equal(1u, GuestMemory.R32(gate + 0x54) & 1);

            uint clock = _harness.Word(0xFFFF);
            Assert.Equal(0x803F0000u, R0(t, 0x91C80C41, 0, 12345));
            Assert.Equal(0u, R0(t, 0x91C80C41, 0, 0x1E8480));
            Assert.Equal(0u, R0(t, 0x7E813FC5, 0, clock));
            Assert.Equal(0x1E8480u, GuestMemory.R32(clock));
            Assert.Equal(0x803F0000u, R0(t, 0x7E813FC5, 2, clock));
            uint byteClock = _harness.Alloc(8);
            uint pixelClock = _harness.Alloc(8);
            Assert.Equal(0u, R0(t, 0x243D0E78, 0x1E8480, byteClock, pixelClock));
            Assert.Equal(200.0, GuestMemory.Read<double>(byteClock));
            Assert.Equal(40.0, GuestMemory.Read<double>(pixelClock));

            Assert.Equal(0x803F0000u, R0(t, 0xE9D95643, 2, 10));
            Assert.Equal(0x803F0000u, R0(t, 0xE9D95643, 1, 7));
            Assert.Equal(0u, R0(t, 0xE9D95643, 1, 16));
        });
    }
}
