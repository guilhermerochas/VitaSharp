using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceMotionDevForDriver (SceMotionDev 3.74): the firmware version answers of a version-0 sensor, AppMgr's word, the
/// active check, the update mode's state checks and the NULL checks.
/// </summary>
[Collection("guest")]
public sealed class MotionDevKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Null = unchecked((int)0x80360001), NotUpdating = unchecked((int)0x80360002);
    private const int NotReady = unchecked((int)0x80360003), State = unchecked((int)0x80360004);

    private readonly HleHarness _harness = new(fixture, "motionk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x031AF7A1u)]
    [InlineData(0x0808D77Du)]
    [InlineData(0x09918DAEu)]
    [InlineData(0x22CD6DCAu)]
    [InlineData(0x3CD0CE14u)]
    [InlineData(0x77BA3A04u)]
    [InlineData(0x85E3C678u)]
    [InlineData(0xA75976EEu)]
    [InlineData(0xD7DA3DA7u)]
    [InlineData(0xDB89D1BFu)]
    [InlineData(0xFD1C528Du)]
    [InlineData(0xFE6ECA41u)]
    [InlineData(0x11A17A96u)]
    [InlineData(0x7FD06731u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceMotionDevForDriver").ToList();
        Assert.Single(bindings);
    }

    [Fact]
    public void AVersionZeroSensorAndTheKeptWord()
    {
        Assert.Equal(0, Call(Services, "ksceMotionDevNoiseFilterIsAvailable"));
        Assert.Equal(1, Call(Services, "MotionDevIsOldFirmware"));
        Assert.Equal(0, Call(Services, "ksceMotionDevRegisterVirtualMotionDriver", 0u));
        uint output = _harness.Alloc(4);
        Assert.Equal(Null, Call(Services, "MotionDevGetWord", 0u));
        Assert.Equal(0, Call(Services, "MotionDevSetWord", 0x1234u));
        Assert.Equal(0, Call(Services, "MotionDevGetWord", output));
        Assert.Equal(0x1234u, GuestMemory.R32(output));
        Assert.Equal(0, Call(Services, "MotionDevIsActive", output));
        Assert.Equal(1u, GuestMemory.R32(output));
        Assert.Equal(0, Call(Services, "MotionDevSetAlarmMode", 3));
        Assert.Equal(3, Services.MotionKernelValues.Mode);
    }

    [Fact]
    public void TheUpdateModeGatesItsEntries()
    {
        uint buffer = _harness.Alloc(16);
        uint output = _harness.Alloc(4);
        Assert.Equal(0, Call(Services, "MotionDevStartWakeUp"));
        Assert.Equal(NotUpdating, Call(Services, "MotionDevLeaveUpdateMode"));
        Assert.Equal(NotUpdating, Call(Services, "MotionDevWriteFirmware", buffer, buffer, 16u, 0));
        Assert.Equal(Null, Call(Services, "MotionDevWriteFirmware", buffer, 0u, 16u, 0));
        Assert.Equal(0, Call(Services, "MotionDevWriteCalibration", buffer, buffer, buffer, 0));

        Assert.Equal(0, Call(Services, "MotionDevEnterUpdateMode"));
        Assert.Equal(4, Services.MotionKernelValues.State);
        Assert.Equal(NotReady, Call(Services, "MotionDevStartWakeUp"));
        Assert.Equal(NotReady, Call(Services, "MotionDevStopWakeUp"));
        Assert.Equal(NotReady, Call(Services, "MotionDevWriteCalibration", buffer, buffer, buffer, 0));
        Assert.Equal(State, Call(Services, "MotionDevIsActive", output));
        Assert.Equal(0, Call(Services, "MotionDevWriteFirmware", buffer, buffer, 16u, 0));
        Assert.Equal(0, Call(Services, "MotionDevLeaveUpdateMode"));
        Assert.Equal(0, Call(Services, "MotionDevEndUpdate"));
        Assert.Equal(2, Services.MotionKernelValues.State);
    }
}
