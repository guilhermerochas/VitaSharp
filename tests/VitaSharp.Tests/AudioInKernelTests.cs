using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceAudioInForDriver (SceAudioin 3.74): any port open, a process owning a port (pid 0: a free slot), the adopted
/// process (sceAudioInGetAdopt follows it: the foreground process until AppMgr adopts one), the open and release
/// callbacks called with the owner's pid, and Bluetooth PCM taken only while the input route is Bluetooth.
/// </summary>
[Collection("guest")]
public sealed class AudioInKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Port = 0x80;

    private readonly HleHarness _harness = new(fixture, "audioink");

    public void Dispose() => _harness.Dispose();

    private Audio Audio => _harness.Module<Audio>();

    private int Pid => _harness.Emu.Pid;

    [Theory]
    [InlineData(0x005A2A31u)]
    [InlineData(0xFB46FA10u)]
    [InlineData(0xE4042F6Fu)]
    [InlineData(0x3EC79053u)]
    [InlineData(0x197CAA1Bu)]
    [InlineData(0x3D164387u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceAudioInForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void ThePortsOwnerIsKnownToTheKernel()
    {
        Assert.Equal(0, Call(Audio, "ksceAudioInIsAnyPortOpen"));
        Assert.Equal(0, Call(Audio, "ksceAudioInIsProcessPortOwner", Pid));
        Assert.Equal(1, Call(Audio, "ksceAudioInIsProcessPortOwner", 0)); // a free slot's owner is 0
        Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 0, 256, 16000, 0));
        Assert.Equal(1, Call(Audio, "ksceAudioInIsAnyPortOpen"));
        Assert.Equal(1, Call(Audio, "ksceAudioInIsProcessPortOwner", Pid));
        Assert.Equal(0, Call(Audio, "ksceAudioInIsProcessPortOwner", Pid + 1));
        Assert.Equal(1, Call(Audio, "ksceAudioInIsProcessPortOwner", 0));
        Assert.Equal(0, Call(Audio, "sceAudioInReleasePort", Port));
        Assert.Equal(0, Call(Audio, "ksceAudioInIsAnyPortOpen"));
        Assert.Equal(0, Call(Audio, "ksceAudioInIsProcessPortOwner", Pid));
    }

    [Fact]
    public void GetAdoptFollowsTheAdoptedProcess()
    {
        Assert.Equal(unchecked((int)0x80260104), Call(Audio, "sceAudioInGetAdopt", 1));
        Assert.Equal(0, Call(Audio, "sceAudioInGetAdopt", 0)); // no port
        Call(Audio, "sceAudioInOpenPort", 2, 256, 16000, 0);
        Assert.Equal(1, Call(Audio, "sceAudioInGetAdopt", 0)); // the foreground process, until AppMgr adopts one
        Assert.Equal(1, Call(Audio, "sceAudioInGetAdopt", 2));
        Assert.Equal(0, Call(Audio, "ksceAudioInSetAdoptForPid", Pid + 1, 0u, 1u, 0));
        Assert.Equal(0, Call(Audio, "sceAudioInGetAdopt", 0));
        Assert.Equal(0, Call(Audio, "ksceAudioInSetAdoptForPid", Pid, 0u, 0u, 7));
        Assert.Equal(1, Call(Audio, "sceAudioInGetAdopt", 0));
        Assert.Equal(7, Audio.InKernelValues.Flag);
    }

    [Fact]
    public void TheOpenAndReleaseCallbacksGetTheOwnersPid()
    {
        uint seen = _harness.Alloc(8);
        uint callback = Recorder(seen);
        uint released = Recorder(seen + 4);
        Assert.Equal(0, Call(Audio, "ksceAudioInSetPortOpenCallback", callback));
        Assert.Equal(0, Call(Audio, "ksceAudioInSetPortReleaseCallback", released));
        _harness.OnGuest(_ =>
        {
            Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 0, 512, 16000, 0));
        });
        Assert.Equal((uint)Pid, GuestMemory.R32(seen));
        Assert.Equal(0u, GuestMemory.R32(seen + 4));
        _harness.OnGuest(_ =>
        {
            Assert.Equal(unchecked((int)0x80260107), Call(Audio, "sceAudioInOpenPort", 0, 512, 16000, 0));
            Assert.Equal(0, Call(Audio, "sceAudioInReleasePort", Port));
        });
        Assert.Equal((uint)Pid, GuestMemory.R32(seen + 4));

        // a refused open or release calls nothing; NULL removes the callback
        GuestMemory.W32(seen, 0);
        GuestMemory.W32(seen + 4, 0);
        Assert.Equal(0, Call(Audio, "ksceAudioInSetPortOpenCallback", 0u));
        _harness.OnGuest(_ =>
        {
            Assert.Equal(unchecked((int)0x80260109), Call(Audio, "sceAudioInReleasePort", Port));
            Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 0, 512, 16000, 0));
        });
        Assert.Equal(0u, GuestMemory.R32(seen));
        Assert.Equal(0u, GuestMemory.R32(seen + 4));
    }

    [Fact]
    public void BluetoothPcmIsTakenOnlyOnTheBluetoothRoute()
    {
        uint pcm = _harness.Alloc(240);
        Assert.Equal(-1, Call(Audio, "ksceAudioInSubmitBluetoothPcm", pcm, 120u));
        Assert.Equal(0, Call(Audio, "sceAudioInSelectInput", 0x10));
        Assert.Equal(0, Call(Audio, "ksceAudioInSubmitBluetoothPcm", pcm, 120u));
        Assert.Equal(120, Audio.InKernelValues.BluetoothSamples);
        Assert.Equal(0, Call(Audio, "sceAudioInSelectInput", 0));
        Assert.Equal(-1, Call(Audio, "ksceAudioInSubmitBluetoothPcm", pcm, 120u));
    }

    /// <summary>ARM code that stores its first argument at <paramref name="target"/> and returns.</summary>
    private uint Recorder(uint target)
    {
        uint code = _harness.Alloc(16);
        GuestMemory.W32(code, 0xE59F1004); // ldr r1, [pc, #4]  (the word at +12)
        GuestMemory.W32(code + 4, 0xE5810000); // str r0, [r1]
        GuestMemory.W32(code + 8, 0xE12FFF1E); // bx lr
        GuestMemory.W32(code + 12, target);
        return code;
    }
}
