using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceHpremoteForDriver (SceHpremote 3.74): nothing plugged into the jack, sixteen callback slots (-1 when full or
/// not registered), the connect callback kept, reset and shutdown, the internal state default.
/// </summary>
[Collection("guest")]
public sealed class HpremoteKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "hprmk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x71B5FB67u)]
    [InlineData(0x7B038374u)]
    [InlineData(0x2229EF51u)]
    [InlineData(0x7E69EDA4u)]
    [InlineData(0x219C58F1u)]
    [InlineData(0x208DB1BDu)]
    [InlineData(0xC7154136u)]
    [InlineData(0x4D1E622Cu)]
    [InlineData(0xD22913DBu)]
    [InlineData(0x7735C403u)]
    [InlineData(0x68D64A0Fu)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceHpremoteForDriver").ToList();
        Assert.Single(bindings);
    }

    [Fact]
    public void NothingIsPluggedIn()
    {
        Assert.Equal(0, Call(Services, "ksceHprmGetHpDetect"));
        Assert.Equal(0, Call(Services, "ksceHprmIsAudioOutDockExist"));
        Assert.Equal(0, Call(Services, "ksceHprmIsHeadphoneExist"));
        Assert.Equal(0, Call(Services, "ksceHprmIsRemoteExist"));
        Assert.Equal(0, Call(Services, "ksceHprmIsMicrophoneExist"));
        Assert.Equal(0, Call(Services, "ksceHprmReset"));
        Assert.Equal(0, Call(Services, "ksceHprmShutdown"));
        uint context = _harness.Word(0xDEADBEEF);
        Assert.Equal(0, Call(Services, "ksceHprmGetInternalState", context));
        Assert.Equal(0u, GuestMemory.R32(context));
        Assert.Equal(0, Call(Services, "ksceHprmSetConnectCallback", 0x81000101u, 7u));
        Assert.Equal((0x81000101u, 7u), (Services.HprmKernelValues.Connect, Services.HprmKernelValues.Argument));
    }

    [Fact]
    public void SixteenCallbackSlots()
    {
        Assert.Equal(-1, Call(Services, "ksceHprmUnregisterCallback", 0x100));
        for (int slot = 0; slot < 16; slot++)
        {
            Assert.Equal(0, Call(Services, "ksceHprmRegisterCallback", 0x100 + slot));
        }

        Assert.Equal(-1, Call(Services, "ksceHprmRegisterCallback", 0x200));
        Assert.Equal(0, Call(Services, "ksceHprmUnregisterCallback", 0x105));
        Assert.Equal(-1, Call(Services, "ksceHprmUnregisterCallback", 0x105));
        Assert.Equal(0, Call(Services, "ksceHprmRegisterCallback", 0x200));
        Assert.Equal(16, Services.HprmKernelValues.Callbacks.Length);
    }
}
