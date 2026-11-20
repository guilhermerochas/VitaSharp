using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// SceAudioForDriver's per-process port settings, monitor, gain, routing and capture. (FW 3.74)
/// </summary>
[Collection("guest")]
public sealed class KernelAudioDriverTests(GuestMemoryFixture fixture) : IDisposable
{

    private readonly HleHarness _harness = new(fixture, "audiodrive");

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
    public void AudioDriverSettingsAreCheckedAndKept()
    {
        _harness.OnGuest(t =>
        {
            uint pid = (uint)t.Pid;
            Assert.Equal(0x8000u, R0(t, 0xB05D5B28, pid, 0));
            Assert.Equal(0xC026000Au, R0(t, 0xB05D5B28, pid, 3));
            Assert.Equal(0xFFFFFFFFu, R0(t, 0x629261CD, pid, 2, 0x101, 0));
            Assert.Equal(0u, R0(t, 0x629261CD, pid, 2 | 8, 0x40, 0));
            Assert.Equal(0x40u, R0(t, 0xB05D5B28, pid, 0));
            Assert.Equal(0x8000u, R0(t, 0xB05D5B28, pid, 1));
            Assert.Equal(0x40u, R0(t, 0xB05D5B28, pid, 2));
            Assert.Equal(0x80260009u, R0(t, 0x15FB30DF, 0x8001));
            Assert.Equal(0u, R0(t, 0x15FB30DF, 0x8000));
            Assert.Equal(0x80260007u, R0(t, 0xFC375BAC, 3));
            Assert.Equal(0x80260008u, R0(t, 0x134C96C1, 44100));
            Assert.Equal(0u, R0(t, 0x134C96C1, 8000));
            Assert.Equal(0xFFFFFFFFu, R0(t, 0xA8C38998, 2));
            Assert.Equal(0u, R0(t, 0xA8C38998, 7));
            Assert.Equal(0xFFFFFFFFu, R0(t, 0x7E0E1331, 3));
            Assert.Equal(0x80260102u, R0(t, 0x37517CC9, 0x81000080, 0x30));
            Assert.Equal(0x80260102u, R0(t, 0x37517CC9, 0x81000040, 0x40));
            Assert.Equal(0u, R0(t, 0x37517CC9, 0x81000080, 0x40));
        });
    }
}
