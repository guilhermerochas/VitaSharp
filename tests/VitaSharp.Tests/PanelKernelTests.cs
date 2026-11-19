using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceOledForDriver and SceLcdForDriver (oled.skprx, lcd.skprx 3.74): the PCH-1000's OLED panel answers (DDB id 0),
/// brightness needs the power-on routine and takes 0..0x10000, the 0/1 modes, one pending command sequence stepped by
/// the vertical blank (pauses counted one less on the OLED), the reset lines on the GPIO; no LCD panel answers.
/// </summary>
[Collection("guest")]
public sealed class PanelKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int OledInvalid = unchecked((int)0x803F0A02), OledBusy = unchecked((int)0x803F0A05);
    private const int LcdInvalid = unchecked((int)0x803F0C02), LcdNoPanel = unchecked((int)0x803F0C03);
    private const int LcdBusy = unchecked((int)0x803F0C05);

    private readonly HleHarness _harness = new(fixture, "panelk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    public static TheoryData<string, uint> Exports()
    {
        var data = new TheoryData<string, uint>();
        uint[] oled =
        [
            0x0CC6BCB4, 0x26F9EEA8, 0x2F0C4B67, 0x3C990F53, 0x3E6AE8A2, 0x43EF811A, 0x4891257F, 0x4C7836C7,
            0x4F8A1D4A, 0x5AF31932, 0x6B1E0B52, 0x9F4ABDDC, 0xAD6C7FB8, 0xBC84602E, 0xBF5C5624, 0xC9D5987C,
            0xDABBD9D3, 0xDDB1412B, 0xE30604CC, 0xED2D6F19, 0xF9624C47,
        ];
        uint[] lcd =
        [
            0x04E9269A, 0x0C7E03D8, 0x17F66722, 0x1A0A7519, 0x1D73D7F3, 0x223C8375, 0x3A6D6AC3, 0x3E7C2CCA,
            0x4735059C, 0x4B6B53C0, 0x4C277FEC, 0x4CC1E8C1, 0x5127FB5E, 0x581D3A87, 0x59CCDC97, 0x5F4124AB,
            0x6A08623C, 0x8C965153, 0x949D45E4, 0x9F1DB5CD, 0xC0A2A6EA, 0xD40968FB, 0xE03E120B,
        ];
        foreach (uint nid in oled)
        {
            data.Add("SceOledForDriver", nid);
        }

        foreach (uint nid in lcd)
        {
            data.Add("SceLcdForDriver", nid);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Exports))]
    public void EveryExportHasAnEntry(string library, uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == library).ToList();
        Assert.Single(bindings);
    }

    [Fact]
    public void TheOledPanelAnswersItsDdb()
    {
        uint id = _harness.Word(0xFFFF);
        uint revision = _harness.Word(0xFFFF);
        Assert.Equal(0, Call(Services, "ksceOledWaitReady"));
        Assert.Equal(0, Call(Services, "ksceOledGetDDB", id, revision));
        Assert.Equal(0, GuestMemory.Read<ushort>(id));
        Assert.Equal(0, GuestMemory.Read<ushort>(revision));
        Assert.Equal(0, Call(Services, "ksceOledGetDDB", 0u, 0u));
        uint buffer = _harness.Word(0xFFFFFFFF);
        Assert.Equal(0, Call(Services, "ksceOledReadRegister", 10u, buffer, 4u));
        Assert.Equal(0u, GuestMemory.R32(buffer));
        Assert.Equal(0, Call(Services, "ksceOledGetPanelState"));
    }

    [Fact]
    public void OledBrightnessNeedsThePowerOnRoutine()
    {
        Assert.Equal(OledBusy, Call(Services, "ksceOledSetBrightness", 0x8000u));
        Assert.Equal(0, Call(Services, "ksceOledSetDisplayColorSpaceMode", 1u));
        Assert.Equal(0, Call(Services, "ksceOledGetDisplayColorSpaceMode"));
        Assert.Equal(0, Call(Services, "ksceOledPowerOn"));
        Assert.Equal(0, Call(Services, "ksceOledIsResetLineLow"));
        Assert.Equal(OledInvalid, Call(Services, "ksceOledSetBrightness", 0x10001u));
        Assert.Equal(0, Call(Services, "ksceOledSetDisplayColorSpaceMode", 1u));
        Assert.Equal(1, Call(Services, "ksceOledGetDisplayColorSpaceMode"));
        Assert.Equal(0, Call(Services, "ksceOledSetBrightness", 0x10000u));
        Assert.Equal(0x10000, Call(Services, "ksceOledGetBrightness"));
        Assert.Equal(1, Call(Services, "ksceOledSequencePending", 1u));
        Assert.Equal(OledBusy, Call(Services, "ksceOledSetDisplayColorSpaceMode", 0u));
        Assert.Equal(0, Call(Services, "ksceOledVblankStep"));
        Assert.Equal(0, Call(Services, "ksceOledSequencePending", 1u));
        Assert.Equal(0, Call(Services, "ksceOledDisplayOff"));
        Assert.Equal(0, Call(Services, "ksceOledGetBrightness"));
        Assert.Equal(OledInvalid, Call(Services, "ksceOledSetDisplayColorSpaceMode", 2u));
    }

    [Fact]
    public void OledResetLineIsTheGpio()
    {
        Assert.Equal(0, Call(Services, "ksceOledResetLineSet"));
        Assert.Equal(0, Call(Services, "ksceOledIsResetLineLow"));
        Assert.Equal(1, Call(Services, "ksceGpioPortRead", 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceOledResetLineClear"));
        Assert.Equal(1, Call(Services, "ksceOledIsResetLineLow"));
    }

    /// <summary>A guest sequence: a command, a pause of <paramref name="pause"/> steps, another command, the end.</summary>
    private uint Sequence(byte pause)
    {
        uint sequence = _harness.Alloc(16);
        byte[] bytes = [0x11, 1, 0xAA, 0x0D, pause, 0x29, 0, 0xFF];
        for (int i = 0; i < bytes.Length; i++)
        {
            GuestMemory.Write(sequence + (uint)i, bytes[i]);
        }

        return sequence;
    }

    [Fact]
    public void OledQueuedSequenceIsSteppedByTheVblankWithItsPause()
    {
        uint sequence = Sequence(3);
        Assert.Equal(0, Call(Services, "ksceOledQueueSequence", sequence));
        Assert.Equal(OledBusy, Call(Services, "ksceOledQueueSequence", sequence));
        Assert.Equal(OledBusy, Call(Services, "ksceOledDisplayOn"));
        Assert.Equal(0, Call(Services, "ksceOledVblankStep"));
        Assert.Equal((sequence + 5, 2), Services.OledSequenceState);
        int steps = 0;
        while (Call(Services, "ksceOledSequencePending", 1u) == 1)
        {
            Call(Services, "ksceOledVblankStep");
            steps++;
        }

        Assert.Equal(3, steps);
        Assert.Equal(0, Call(Services, "ksceOledRunSequence", sequence));
        Assert.Equal((0u, 0), Services.OledSequenceState);
        Assert.Equal(0, Call(Services, "ksceOledSetSynchronous", 1u));
        Assert.Equal(0, Call(Services, "ksceOledDisplayOn"));
        Assert.Equal(0, Call(Services, "ksceOledSequencePending", 1u));
    }

    [Fact]
    public void NoLcdPanelAnswers()
    {
        Assert.Equal(0, Call(Services, "ksceLcdWaitReady"));
        Assert.Equal(LcdNoPanel, Call(Services, "ksceLcdGetDDB", _harness.Word(0), _harness.Word(0)));
        Assert.Equal(LcdNoPanel, Call(Services, "ksceLcdReadRegister", 10u, _harness.Word(0), 1u));
        Assert.Equal(LcdNoPanel, Call(Services, "ksceLcdWriteCommand", 0x11u, 0u, 0u));
        Assert.Equal(LcdNoPanel, Call(Services, "ksceLcdGetPanelState"));
    }

    [Fact]
    public void LcdBrightnessTurnsTheDisplayOnAndTheBacklightWaitsForThePowerOn()
    {
        Assert.Equal(LcdInvalid, Call(Services, "ksceLcdSetBrightness", 0x10001u));
        Assert.Equal(0, Call(Services, "ksceLcdSetBrightness", 0x4000u));
        Assert.Equal((true, 0x4000u, 0x4000u, 0u, 0xFFFFFFFFu), Services.LcdState);
        Assert.Equal(1, Call(Services, "ksceLcdSequencePending", 1u));
        Assert.Equal(0, Call(Services, "ksceLcdVblankStep"));
        Assert.Equal(1, Call(Services, "ksceLcdSequencePending", 1u));
        Assert.Equal(0, Call(Services, "ksceLcdPowerOn"));
        Assert.Equal((false, 0u, 0u, 0u, 0u), Services.LcdState);
        Assert.Equal(0, Call(Services, "ksceLcdSetBrightness", 0x8000u));
        Call(Services, "ksceLcdVblankStep");
        Assert.Equal(0, Call(Services, "ksceLcdSequencePending", 1u));
        Assert.Equal(0x8000, Call(Services, "ksceLcdGetBrightness"));
        Assert.Equal(0, Call(Services, "ksceLcdDisplayOn"));
        Assert.Equal(0, Call(Services, "ksceLcdDisplayOff"));
        Assert.Equal((false, 0u, 0u, 0u, 0xFFFFFFFFu), Services.LcdState);
        Assert.Equal(LcdBusy, Call(Services, "ksceLcdRunFixedSequence"));
    }

    [Fact]
    public void LcdModesAreKeptWhileOffAndQueuedWhileOn()
    {
        Assert.Equal(LcdInvalid, Call(Services, "ksceLcdSetPanelMode", 2u));
        Assert.Equal(0, Call(Services, "ksceLcdSetPanelMode", 1u));
        Assert.Equal(0, Call(Services, "ksceLcdSetDisplayColorSpaceMode", 1u));
        Assert.Equal(0u, Services.LcdState.Sequence);
        Assert.Equal(1, Call(Services, "ksceLcdGetPanelMode"));
        Assert.Equal(1, Call(Services, "ksceLcdGetDisplayColorSpaceMode"));
        Assert.Equal(0, Call(Services, "ksceLcdDisplayOn"));
        Assert.Equal(LcdBusy, Call(Services, "ksceLcdSetPanelMode", 0u));
        Assert.Equal(0, Call(Services, "ksceLcdSequencePending", 0u));
        Assert.Equal(0, Call(Services, "ksceLcdSetPanelMode", 0u));
        Assert.Equal(0, Call(Services, "ksceLcdGetPanelMode"));
    }

    [Fact]
    public void LcdPausesCountTheirFullLength()
    {
        uint sequence = Sequence(3);
        Assert.Equal(0, Call(Services, "ksceLcdQueueSequence", sequence));
        int steps = 0;
        while (Call(Services, "ksceLcdSequencePending", 1u) == 1)
        {
            Call(Services, "ksceLcdVblankStep");
            steps++;
        }

        Assert.Equal(5, steps);
        Assert.Equal(0, Call(Services, "ksceLcdRunSequence", sequence));
        Assert.Equal(0, Call(Services, "ksceLcdResetLinesSet"));
        Assert.Equal(1, Call(Services, "ksceGpioPortRead", 1u, 7u));
        Assert.Equal(0, Call(Services, "ksceLcdIsResetLineLow"));
        Assert.Equal(0, Call(Services, "ksceLcdResetLinesClear"));
        Assert.Equal(1, Call(Services, "ksceLcdIsResetLineLow"));
    }
}
