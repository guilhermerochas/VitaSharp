using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The Wi-Fi driver's callbacks as FW 3.74 SceWlanBt keeps them: sceWlanRegisterCallback takes one of two slots (the
/// same process again: 0x80418002; a third: 0x80418005) and returns 0; the driver notifies the registered callbacks
/// with its state word when a configuration (sceWlanSetConfiguration) has been applied. Airplane mode in Settings and
/// in the shell's quick menu (shell.self 3.74 0x8115A488) turns the Wi-Fi off or on and waits up to 20 s for that
/// report: before, the registration only counted and no report ever came, so Settings stayed on "Please wait..." for
/// 20 s and then failed.
/// </summary>
[Collection("guest")]
public sealed class WlanCallbackTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int AlreadyRegistered = unchecked((int)0x80418002);
    private const uint SyntheticEntry = 0x81000001;
    private const int WifiOff = 2, WifiOn = 3;

    private readonly HleHarness _harness = new(fixture, "wlancb");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Wlan => _harness.Module<SystemServicesVsh>();

    [Fact]
    public void AnAppliedConfigurationIsReportedToTheRegisteredCallback()
    {
        ThreadMgr threads = _harness.Module<ThreadMgr>();
        _harness.OnGuest(_ =>
        {
            int callback = Call(threads, "sceKernelCreateCallback", "wlan", 0u, SyntheticEntry, 0u);
            Assert.Equal(0, Call(Wlan, "sceWlanRegisterCallback", callback));
            Assert.Equal(AlreadyRegistered, Call(Wlan, "sceWlanRegisterCallback", callback));

            // airplane mode on: the Wi-Fi goes off, and the callback hears about it with the driver's state
            Assert.Equal(0, Call(Wlan, "sceWlanSetConfiguration", WifiOff));
            Assert.Equal(1, Call(threads, "sceKernelGetCallbackCount", callback));
            Callback notified = _harness.Emu.Kernel.Objects.Get<Callback>(callback)!;
            Assert.Equal(Call(Wlan, "sceWlanGetConfiguration"), notified.NotifyArg);
            Assert.Equal(0, notified.NotifyArg & 0x40); // not the driver's error bit: the shell's handler reads it as a failure

            // and back on
            Assert.Equal(0, Call(Wlan, "sceWlanSetConfiguration", WifiOn));
            Assert.Equal(2, Call(threads, "sceKernelGetCallbackCount", callback));

            // once unregistered, nothing more is reported, and the slot can be taken again
            Assert.Equal(0, Call(Wlan, "sceWlanUnregisterCallback"));
            Assert.Equal(0, Call(Wlan, "sceWlanSetConfiguration", WifiOff));
            Assert.Equal(2, Call(threads, "sceKernelGetCallbackCount", callback));
            Assert.Equal(0, Call(Wlan, "sceWlanRegisterCallback", callback));
        });
    }

    [Fact]
    public void WithoutACallbackAConfigurationIsStillApplied()
    {
        Assert.Equal(0, Call(Wlan, "sceWlanSetConfiguration", WifiOff));
        Assert.Equal(WifiOff, Call(Wlan, "sceWlanGetConfiguration"));
    }
}
