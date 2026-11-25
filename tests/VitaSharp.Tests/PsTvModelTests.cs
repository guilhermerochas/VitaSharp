using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The console model option (<see cref="ConsoleModel"/>): a PlayStation TV is told apart by its identity (product sub
/// code bit 0x200, what FW 3.74's SceSblAIMgr tests: ksceSblAimgrIsDolce / IsGenuineDolce = 1, IsGenuineVITA = 0), its
/// SceCtrl supports several controllers (sceCtrlIsMultiControllerSupported = 1, ports 1-4 listed by
/// sceCtrlGetControllerPortInfo and read from the host's controllers one by one) and sceCtrlSetActuator reaches the
/// controller's motors. The handheld default keeps every answer it had. Synthetic: a host with scripted controllers and
/// a recording vibration sink.
/// </summary>
[Collection("guest")]
public sealed class PsTvModelTests(GuestMemoryFixture fixture)
{
    /// <summary>Host controllers the test sets, and the vibration they were asked for.</summary>
    private sealed class TestPads(int count) : IInputBackend, IMultiControllerInput, IControllerVibration
    {
        public readonly ControllerState[] Pads = [ControllerState.Neutral, ControllerState.Neutral, ControllerState.Neutral, ControllerState.Neutral];
        public readonly List<(int Port, byte Small, byte Large)> Vibrations = [];

        public int ControllerCount => count;

        public ControllerState GetPad(int index) => Pads[index];

        public ControllerState GetController(int port) => Pads[0];

        public IReadOnlyList<TouchPoint> GetTouches(bool back) => [];

        public void SetVibration(int port, byte small, byte large) => Vibrations.Add((port, small, large));
    }

    private const int CtrlInvalidArgument = unchecked((int)0x80340001);
    private const byte Virtual = 2, DualShock3 = 4, Unpaired = 0, Physical = 1;

    private (HleHarness Harness, TestPads Pads) Make(ConsoleModel model, int controllers)
    {
        var harness = new HleHarness(fixture, "pstv", model: model);
        var pads = new TestPads(controllers);
        harness.Emu.Host.Input = pads;
        harness.Emu.Host.Vibration = pads;
        return (harness, pads);
    }

    private static uint Buttons(HleHarness harness, int port)
    {
        uint data = harness.Alloc(32);
        Assert.Equal(1, Call(harness.Module<Ctrl>(), "sceCtrlPeekBufferPositive", port, data, 1u));
        return GuestMemory.R32(data + 8);
    }

    [Fact]
    public void APsTvIsDolceByItsIdentity()
    {
        (HleHarness harness, _) = Make(ConsoleModel.PsTv, 1);
        using (harness)
        {
            SystemServicesVsh services = harness.Module<SystemServicesVsh>();
            Assert.Equal(1, Call(services, "ksceSblAimgrIsDolce"));
            Assert.Equal(1, Call(services, "ksceSblAimgrIsGenuineDolce"));
            Assert.Equal(0, Call(services, "ksceSblAimgrIsGenuineVITA"));
            Assert.Equal(ConsoleIdentity.PsTvProductSubCode, Call(services, "ksceSblAimgrGetProductSubCode"));
            Assert.Equal(0x200, ConsoleIdentity.PsTvProductSubCode & 0x200);
        }
    }

    [Fact]
    public void APsTvReadsEachControllerOnItsPortAndListsThem()
    {
        (HleHarness harness, TestPads pads) = Make(ConsoleModel.PsTv, 2);
        using (harness)
        {
            Ctrl ctrl = harness.Module<Ctrl>();
            Assert.Equal(1, Call(ctrl, "sceCtrlIsMultiControllerSupported"));
            uint info = harness.Alloc(16);
            Assert.Equal(0, Call(ctrl, "sceCtrlGetControllerPortInfo", info));
            Assert.Equal(new byte[] { Virtual, DualShock3, DualShock3, Unpaired, Unpaired }, GuestMemory.Span(info, 5).ToArray());

            pads.Pads[0].Buttons = VitaButtons.Cross;
            pads.Pads[1].Buttons = VitaButtons.Circle;
            Assert.Equal((uint)VitaButtons.Cross, Buttons(harness, 1));
            Assert.Equal((uint)VitaButtons.Circle, Buttons(harness, 2));
            Assert.Equal(0u, Buttons(harness, 3)); // nobody on port 3
        }
    }

    [Fact]
    public void APsTvSendsTheActuatorToThePortsController()
    {
        (HleHarness harness, TestPads pads) = Make(ConsoleModel.PsTv, 2);
        using (harness)
        {
            Ctrl ctrl = harness.Module<Ctrl>();
            uint state = harness.Alloc(8);
            GuestMemory.Write(state, (byte)40);       // small motor
            GuestMemory.Write(state + 1, (byte)200);  // large motor
            Assert.Equal(0, Call(ctrl, "sceCtrlSetActuator", 2, state));
            GuestMemory.Write(state, (byte)0);
            GuestMemory.Write(state + 1, (byte)0);
            Assert.Equal(0, Call(ctrl, "sceCtrlSetActuator", 2, state));
            Assert.Equal(CtrlInvalidArgument, Call(ctrl, "sceCtrlSetActuator", 2, 0u));
            Assert.Equal(new (int, byte, byte)[] { (2, 40, 200), (2, 0, 0) }, pads.Vibrations);
        }
    }

    [Fact]
    public void TheHandheldKeepsItsSinglePadAndNoVibration()
    {
        (HleHarness harness, TestPads pads) = Make(ConsoleModel.Handheld, 2);
        using (harness)
        {
            Ctrl ctrl = harness.Module<Ctrl>();
            SystemServicesVsh services = harness.Module<SystemServicesVsh>();
            Assert.Equal(0, Call(services, "ksceSblAimgrIsDolce"));
            Assert.Equal(ConsoleIdentity.ProductSubCode, Call(services, "ksceSblAimgrGetProductSubCode"));
            Assert.Equal(0, Call(ctrl, "sceCtrlIsMultiControllerSupported"));
            uint info = harness.Alloc(16);
            Assert.Equal(0, Call(ctrl, "sceCtrlGetControllerPortInfo", info));
            Assert.Equal(new byte[] { Physical, Physical, Unpaired, Unpaired, Unpaired }, GuestMemory.Span(info, 5).ToArray());

            pads.Pads[0].Buttons = VitaButtons.Cross;
            pads.Pads[1].Buttons = VitaButtons.Circle;
            Assert.Equal((uint)VitaButtons.Cross, Buttons(harness, 0));
            Assert.Equal((uint)VitaButtons.Cross, Buttons(harness, 1)); // ports 0 and 1 are the built-in pad

            uint state = harness.Alloc(8);
            GuestMemory.Write(state + 1, (byte)200);
            Assert.Equal(0, Call(ctrl, "sceCtrlSetActuator", 1, state));
            Assert.Empty(pads.Vibrations);
        }
    }

    [Fact]
    public void TheModelOptionNamesThePsTv()
    {
        Assert.Equal(ConsoleModel.PsTv, ConsoleIdentity.ParseModel("pstv"));
        Assert.Equal(ConsoleModel.PsTv, ConsoleIdentity.ParseModel(" PSTV "));
        Assert.Equal(ConsoleModel.PsTv, ConsoleIdentity.ParseModel("vte-1000"));
        Assert.Equal(ConsoleModel.Handheld, ConsoleIdentity.ParseModel(null));
        Assert.Equal(ConsoleModel.Handheld, ConsoleIdentity.ParseModel("vita"));
        Assert.Equal(ConsoleModel.Handheld, ConsoleIdentity.ParseModel("pch-1000"));
    }
}
