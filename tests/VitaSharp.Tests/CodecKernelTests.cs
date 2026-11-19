using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceCodecForDriver (SceCodec 3.74): no codec chip behind the host audio; every entry is a default that accepts the
/// setting (0), a register reads 0 and no headset microphone is detected.
/// </summary>
[Collection("guest")]
public sealed class CodecKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "codeck");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0xCCDE1B04u, "ksceCodecSetAudioOutMode", 1)]
    [InlineData(0x41CFFE48u, "ksceCodecGetOutputState", 0)]
    [InlineData(0xC1DB7BAFu, "ksceCodecSetAudioInputMode", 1)]
    [InlineData(0xCF398D8Au, "ksceCodecGetMicrophoneDetectStatus", 0)]
    [InlineData(0x762BF716u, "ksceCodecSetOutputRoute", 1)]
    [InlineData(0x4FA36937u, "ksceCodecReadRegister", 1)]
    [InlineData(0x8932F886u, "ksceCodecSetVolume", 1)]
    [InlineData(0xE483B663u, "ksceCodecSetMicrophoneDetectPower", 1)]
    [InlineData(0x23BF8884u, "ksceCodecWriteRegister", 2)]
    public void EveryEntryIsADefaultThatAccepts(uint nid, string name, int arguments)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceCodecForDriver").ToList();
        Assert.Single(bindings);
        Assert.True(bindings[0].Default);
        object[] args = name.Contains("Register", StringComparison.Ordinal)
            ? Enumerable.Repeat<object>(0x30u, arguments).ToArray()
            : Enumerable.Repeat<object>(1, arguments).ToArray();
        Assert.Equal(0, Call(Services, name, args));
    }
}
