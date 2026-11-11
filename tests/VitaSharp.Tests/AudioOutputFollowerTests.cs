using VitaSharp.Core.Host;

namespace VitaSharp.Tests;

/// <summary>The host audio output choice ("Saida de audio", --audio-device): a chosen output is kept, unplugging it
/// falls back to the system default until it returns, and the system default never moves to a game controller's
/// audio output on its own (told by the device the output belongs to, or by name).</summary>
public class AudioOutputFollowerTests
{
    private const string Speakers = "OpenAL Soft on Alto-falantes (Realtek(R) Audio)";
    private const string Headset = "OpenAL Soft on Fones de ouvido (HyperX Cloud II)";
    private const string Ds4 = "OpenAL Soft on Fone de ouvido do headset (Wireless Controller)";

    [Theory]
    [InlineData("Fone de ouvido do headset (Wireless Controller)", true)]
    [InlineData("Headset Earphone (DualSense Wireless Controller)", true)]
    [InlineData("Speakers (DUALSHOCK 4 USB Wireless Adaptor)", true)]
    [InlineData("Headset (Xbox Controller)", true)]
    [InlineData("Headset (Xbox Wireless Adapter for Windows)", true)]
    [InlineData("Headphones (Xbox Wireless Headset)", false)]
    [InlineData("Speakers (8BitDo Ultimate)", true)]
    [InlineData("Fones de ouvido (HyperX Cloud II)", false)]
    [InlineData("Headphones (WH-1000XM4 Stereo)", false)]
    [InlineData("Alto-falantes (Realtek(R) Audio)", false)]
    [InlineData("Headset Earphone (Logitech G733 Gaming Headset)", false)]
    public void NameRuleTellsControllerOutputs(string name, bool controller) =>
        Assert.Equal(controller, AudioOutputFollower.LooksLikeController(name));

    [Fact]
    public void DeviceRuleMatchesOutputsInAControllersContainer()
    {
        Guid pad = Guid.NewGuid(), xbox = Guid.NewGuid(), headset = Guid.NewGuid(), pc = new("00000000-0000-0000-ffff-ffffffffffff");
        (string, Guid)[] outputs = new[] { ("Alto-falantes (Realtek)", pc), ("Fone (Wireless Controller)", pad), ("Headset (Xbox)", xbox), ("Fones (Brand X)", headset) };
        var devices = new (Guid, IReadOnlyList<string>, string, string)[]
        {
            (pad, ["HID_DEVICE_SYSTEM_GAME", "HID_DEVICE_UP:0001_U:0005", "HID_DEVICE"], "HIDClass", "HidUsb"),
            (xbox, ["USB\\MS_COMP_XUSB10"], "XnaComposite", "xusb22"),
            (headset, ["HID_DEVICE_SYSTEM_CONSUMER", "HID_DEVICE"], "HIDClass", "HidUsb"), // volume keys of a headset
            (pc, ["HID_DEVICE_SYSTEM_GAME"], "HIDClass", "HidUsb"), // the PC's own container is never a controller
        };
        HashSet<string> found = AudioOutputFollower.ControllerOutputs(outputs, devices);
        Assert.Equal(["Fone (Wireless Controller)", "Headset (Xbox)"], found.Order());
    }

    [Fact]
    public void DeviceRuleCatchesAControllerWithAPlainName()
    {
        // an output whose name says nothing of a controller still counts when the host's device check says so
        var f = new AudioOutputFollower(null, n => n.Contains("Generic USB Audio"));
        string first = f.Initial([Speakers, "OpenAL Soft on Speakers (Generic USB Audio)"], Speakers, out _);
        Assert.Equal(Speakers, first);
        Assert.Null(f.Check([Speakers, "OpenAL Soft on Speakers (Generic USB Audio)"], "OpenAL Soft on Speakers (Generic USB Audio)", true, out string? msg));
        Assert.Contains("game controller", msg);
    }

    [Fact]
    public void ChosenOutputIsKeptFallsBackAndReturns()
    {
        var f = new AudioOutputFollower("Wireless Controller");
        Assert.Equal(Ds4, f.Initial([Speakers, Ds4], Speakers, out _));
        // the default changing does not move a chosen output
        Assert.Null(f.Check([Speakers, Headset, Ds4], Headset, true, out _));
        Assert.Equal(Ds4, f.Current);
        // unplugged: the system default until it returns
        Assert.Equal(Headset, f.Check([Speakers, Headset], Headset, false, out string? gone));
        Assert.True(f.OnFallback);
        Assert.Contains("disconnected", gone);
        Assert.Null(f.Check([Speakers, Headset], Headset, true, out _));
        Assert.Equal(Ds4, f.Check([Speakers, Headset, Ds4], Headset, true, out string? back));
        Assert.False(f.OnFallback);
        Assert.Contains("back", back);
    }

    [Fact]
    public void MissingChosenOutputStartsOnTheDefaultAndSwitchesWhenPlugged()
    {
        var f = new AudioOutputFollower(Headset);
        Assert.Equal(Speakers, f.Initial([Speakers], Speakers, out _));
        Assert.True(f.OnFallback);
        Assert.Equal(Headset, f.Check([Speakers, Headset], Speakers, true, out _));
    }

    [Fact]
    public void SystemDefaultFollowsHeadphonesButNeverAController()
    {
        var f = new AudioOutputFollower(null);
        Assert.Equal(Speakers, f.Initial([Speakers, Headset, Ds4], Speakers, out _));
        Assert.Null(f.Check([Speakers, Headset, Ds4], Speakers, true, out string? none));
        Assert.Null(none);
        // headphones become the default: follow them, and log it
        Assert.Equal(Headset, f.Check([Speakers, Headset, Ds4], Headset, true, out string? moved));
        Assert.Contains("changed", moved);
        // the controller's headset jack becomes the default: stay
        Assert.Null(f.Check([Speakers, Headset, Ds4], Ds4, true, out string? stay));
        Assert.Contains("staying", stay);
        Assert.Equal(Headset, f.Current);
    }

    [Fact]
    public void ControllerDefaultAtStartPicksAnotherOutput()
    {
        var f = new AudioOutputFollower("");
        Assert.Equal(Speakers, f.Initial([Ds4, Speakers], Ds4, out string? why));
        Assert.Contains("game controller", why);
        // the only output left is the controller: it is used rather than nothing
        Assert.Equal(Ds4, new AudioOutputFollower(null).Initial([Ds4], Ds4, out _));
    }

    [Fact]
    public void LauncherNamesDropTheOpenAlPrefix() =>
        Assert.Equal("Fone de ouvido do headset (Wireless Controller)", AudioOutputFollower.FriendlyName(Ds4));
}
