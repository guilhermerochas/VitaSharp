using VitaSharp.Core.Host;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceDisplay (framebuffer set/get and their checks, vblank waits, the internal head/slot variants), SceCtrl (the
/// buffer API families: the standard ones report the shoulders as L/R TRIGGER, the "2" ones as L1/R1; negative logic;
/// digital mode centres the sticks) and SceTouch (panel info, sampling state, report ids per contact) on an emulator
/// with a scripted host input.
/// </summary>
[Collection("guest")]
public sealed class DisplayCtrlTouchTests(GuestMemoryFixture fx) : IDisposable
{
    const int DISP_INVALID_HEAD = unchecked((int)0x80290000), DISP_INVALID_VALUE = unchecked((int)0x80290001),
        DISP_INVALID_ADDR = unchecked((int)0x80290002), DISP_INVALID_TIMING = unchecked((int)0x80290006),
        CTRL_INVALID_ARG = unchecked((int)0x80340001), TOUCH_INVALID_ARG = unchecked((int)0x80350001);

    sealed class ScriptedInput : IInputBackend
    {
        public ControllerState Pad = ControllerState.Neutral;
        public List<TouchPoint> Front = [];
        public ControllerState GetController(int port) => Pad;
        public IReadOnlyList<TouchPoint> GetTouches(bool back) => back ? [] : Front;
    }

    readonly HleHarness _h = new(fx, "dispctrl");
    readonly ScriptedInput _in = new();

    public void Dispose() => _h.Dispose();

    uint FrameBuf(uint size, uint baseAddr, uint pitch, uint fmt, uint w, uint h)
    {
        uint p = _h.Alloc(0x20);
        GuestMemory.W32(p, size); GuestMemory.W32(p + 4, baseAddr); GuestMemory.W32(p + 8, pitch);
        GuestMemory.W32(p + 12, fmt); GuestMemory.W32(p + 16, w); GuestMemory.W32(p + 20, h);
        return p;
    }

    [Fact]
    public void FramebufferSetGetAndWaits()
    {
        var d = _h.Module<Display>();
        uint fbMem = _h.Alloc(960 * 544 * 4);
        Assert.Equal(DISP_INVALID_VALUE, Call(d, "sceDisplaySetFrameBuf", FrameBuf(0x10, fbMem, 960, 0, 960, 544), 1u));
        Assert.Equal(0, Call(d, "sceDisplaySetFrameBuf", FrameBuf(0x18, fbMem, 1024, 0, 960, 544), 1u));
        uint get = FrameBuf(0x1C, 0, 0, 0, 0, 0);
        Assert.Equal(0, Call(d, "sceDisplayGetFrameBuf", get, 1u));
        Assert.Equal(fbMem, GuestMemory.R32(get + 4));
        Assert.Equal(1024u, GuestMemory.R32(get + 8));
        Assert.Equal(960u, GuestMemory.R32(get + 16));
        Assert.Equal(544u, GuestMemory.R32(get + 20));
        Assert.Equal(0x00100000u, GuestMemory.R32(get + 0x18)); // SceDisplayFrameBufExt flags
        Assert.Equal(DISP_INVALID_ADDR, Call(d, "sceDisplayGetFrameBuf", 0u, 1u));
        Assert.Equal(DISP_INVALID_VALUE, Call(d, "sceDisplayGetFrameBuf", FrameBuf(0x20, 0, 0, 0, 0, 0), 1u));
        Assert.Equal(0, Call(d, "sceDisplaySetFrameBufForCompat", FrameBuf(0x18, fbMem, 960, 0, 960, 544), 0u));
        Assert.Equal(0, Call(d, "sceDisplaySetFrameBuf", 0u, 1u)); // NULL: blank

        uint rate = _h.Alloc(4), w = _h.Alloc(4), hgt = _h.Alloc(4);
        Assert.Equal(0, Call(d, "sceDisplayGetRefreshRate", new Ptr<float>(rate)));
        Assert.Equal(59.94005f, GuestMemory.Read<float>(rate));
        Assert.Equal(0, Call(d, "sceDisplayGetMaximumFrameBufResolution", new Ptr<int>(w), new Ptr<int>(hgt)));
        Assert.Equal((960u, 544u), (GuestMemory.R32(w), GuestMemory.R32(hgt)));
        Assert.Equal(0, Call(d, "sceDisplayGetPrimaryHead"));

        _h.OnGuest(_ =>
        {
            uint v0 = (uint)Invoke(d, "sceDisplayGetVcount")!;
            Assert.Equal(0, Call(d, "sceDisplayWaitVblankStart"));
            Assert.Equal(0, Call(d, "sceDisplayWaitVblankStartMulti", 2u));
            Assert.Equal(0, Call(d, "sceDisplayWaitVblankStartCB"));
            Assert.Equal(0, Call(d, "sceDisplayWaitVblankStartMultiCB", 1u));
            Assert.True((uint)Invoke(d, "sceDisplayGetVcount")! >= v0 + 4, "four vblanks waited for");
            Assert.Equal(0, Call(d, "sceDisplayWaitSetFrameBuf"));
            Assert.Equal(0, Call(d, "sceDisplayWaitSetFrameBufCB"));
            Assert.Equal(0, Call(d, "sceDisplayWaitSetFrameBufMulti", 1u));
            Assert.Equal(0, Call(d, "sceDisplayWaitSetFrameBufMultiCB", 1u));
        });

        // the internal variants: head 0 slot 0 is the game's framebuffer, head 1 (HDMI) is checked and not shown
        Assert.Equal(DISP_INVALID_HEAD, Call(d, "sceDisplayGetVcountInternal", 1));
        Assert.True(Call(d, "sceDisplayGetVcountInternal", 0) >= 0);
        Assert.Equal(0, Call(d, "sceDisplaySetFrameBufInternal", 0, 0, FrameBuf(0x18, fbMem, 960, 0, 960, 544), 1u));
        Assert.Equal(0, Call(d, "sceDisplaySetFrameBufInternal", 1, 0, FrameBuf(0x18, fbMem, 960, 0, 960, 544), 1u));
        Assert.Equal(DISP_INVALID_VALUE, Call(d, "sceDisplaySetFrameBufInternal", 1, 0, FrameBuf(0x14, fbMem, 960, 0, 960, 544), 1u));
        Assert.Equal(DISP_INVALID_HEAD, Call(d, "sceDisplaySetFrameBufInternal", 2, 0, 0u, 1u));
        Assert.Equal(DISP_INVALID_TIMING, Call(d, "sceDisplaySetFrameBufInternal", 0, 0, 0u, 2u));
        uint g2 = FrameBuf(0x18, 1, 1, 1, 1, 1);
        Assert.Equal(0, Call(d, "sceDisplayGetFrameBufInternal", 0, 0, g2, 1u));
        Assert.Equal(fbMem, GuestMemory.R32(g2 + 4));
        Assert.Equal(0, Call(d, "sceDisplayGetFrameBufInternal", 0, 1, g2, 1u)); // the LiveArea slot: empty
        Assert.Equal(0u, GuestMemory.R32(g2 + 4));
        Assert.Equal(DISP_INVALID_HEAD, Call(d, "sceDisplayGetFrameBufInternal", 1, 0, g2, 1u));
        Assert.Equal(DISP_INVALID_ADDR, Call(d, "sceDisplayGetFrameBufInternal", 0, 0, 0u, 1u));
        Assert.Equal(DISP_INVALID_TIMING, Call(d, "sceDisplayGetFrameBufInternal", 0, 0, g2, 3u));
        Assert.Equal(DISP_INVALID_VALUE, Call(d, "sceDisplayGetFrameBufInternal", 0, 1, FrameBuf(0x30, 0, 0, 0, 0, 0), 1u));
        Assert.Equal(DISP_INVALID_HEAD, Call(d, "sceDisplayGetResolutionInfoInternal", 1, _h.Alloc(0x1C)));
        Assert.Equal(DISP_INVALID_ADDR, Call(d, "sceDisplayGetResolutionInfoInternal", 0, 0u));
    }

    [Fact]
    public void ControllerBufferFamiliesMapTheShoulders()
    {
        _h.Emu.Host.Input = _in;
        var c = _h.Module<Ctrl>();
        _in.Pad = new ControllerState { Buttons = VitaButtons.Cross | VitaButtons.LTrigger, Lx = 10, Ly = 20, Rx = 30, Ry = 40 };
        uint buf = _h.Alloc(32 * 4);
        Assert.Equal(CTRL_INVALID_ARG, Call(c, "sceCtrlPeekBufferPositive", 0, 0u, 1u));
        Assert.Equal(0, Call(c, "sceCtrlPeekBufferPositive", 0, buf, 0u));
        // digital sampling (mode 0): the sticks read centred
        Assert.Equal(1, Call(c, "sceCtrlPeekBufferPositive", 0, buf, 1u));
        uint buttons = GuestMemory.R32(buf + 8);
        Assert.Equal((uint)(VitaButtons.Cross | VitaButtons.LTrigger), buttons);
        Assert.Equal((byte)128, GuestMemory.Read<byte>(buf + 12));
        // analog: the sticks; the old mode comes back
        Assert.Equal(0, Call(c, "sceCtrlSetSamplingMode", 1u));
        uint mode = _h.Word(9);
        Assert.Equal(0, Call(c, "sceCtrlGetSamplingMode", new Ptr<uint>(mode)));
        Assert.Equal(1u, GuestMemory.R32(mode));
        Assert.Equal(3, Call(c, "sceCtrlPeekBufferPositive", 0, buf, 3u)); // every sample requested
        Assert.Equal((10, 20, 30, 40), (GuestMemory.Read<byte>(buf + 12), GuestMemory.Read<byte>(buf + 13), GuestMemory.Read<byte>(buf + 14), GuestMemory.Read<byte>(buf + 15)));
        Assert.True(GuestMemory.Read<ulong>(buf) >= GuestMemory.Read<ulong>(buf + 32)); // the extra samples are older
        // negative logic
        Assert.Equal(1, Call(c, "sceCtrlPeekBufferNegative", 0, buf, 1u));
        Assert.Equal(~buttons, GuestMemory.R32(buf + 8));
        // the "2" family: L TRIGGER reads as L1 (0x400)
        Assert.Equal(1, Call(c, "sceCtrlPeekBufferPositive2", 0, buf, 1u));
        Assert.Equal((uint)VitaButtons.Cross | 0x400u, GuestMemory.R32(buf + 8));
        Assert.Equal(1, Call(c, "sceCtrlPeekBufferPositiveExt2", 0, buf, 1u));
        Assert.Equal(1, Call(c, "sceCtrlPeekBufferNegative2", 0, buf, 1u));
        Assert.Equal(1, Call(c, "sceCtrlPeekBufferPositiveExt", 0, buf, 1u));
        Assert.Equal((uint)(VitaButtons.Cross | VitaButtons.LTrigger), GuestMemory.R32(buf + 8));
        Assert.Equal(1u, Ctrl.MapButtons(1, Ctrl.Api.Standard));
        Assert.Equal(0x800u, Ctrl.MapButtons((uint)VitaButtons.RTrigger, Ctrl.Api.Ext | Ctrl.Api.V2));
        // the PS button never reaches an application
        _in.Pad = new ControllerState { Buttons = VitaButtons.PsButton | VitaButtons.Circle, Lx = 128, Ly = 128, Rx = 128, Ry = 128 };
        Call(c, "sceCtrlPeekBufferPositiveExt", 0, buf, 1u);
        Assert.Equal((uint)VitaButtons.Circle, GuestMemory.R32(buf + 8));
        _h.OnGuest(_ =>
        {
            // reads wait for the next sample: two reads are at least a vblank apart
            foreach (var fn in new[] { "sceCtrlReadBufferPositive", "sceCtrlReadBufferNegative", "sceCtrlReadBufferPositiveExt", "sceCtrlReadBufferPositive2", "sceCtrlReadBufferPositiveExt2", "sceCtrlReadBufferNegative2" })
                Assert.Equal(1, Call(c, fn, 0, buf, 1u));
        });

        Assert.Equal(1, Call(c, "sceCtrlSetSamplingModeExt", 0u));
        Assert.Equal(0, Call(c, "sceCtrlGetSamplingModeExt", new Ptr<uint>(mode)));
        Assert.Equal(0u, GuestMemory.R32(mode));
        Assert.Equal(0, Call(c, "sceCtrlSetAnalogStickCheckMode", 2));
        Assert.Equal(0, Call(c, "sceCtrlGetAnalogStickCheckMode", new Ptr<int>(mode)));
        Assert.Equal(2u, GuestMemory.R32(mode));
        Assert.Equal(CTRL_INVALID_ARG, Call(c, "sceCtrlGetAnalogStickCheckMode", new Ptr<int>(0)));
        Assert.Equal(0, Call(c, "sceCtrlSetAnalogStickCheckTarget", 3));
        Assert.Equal(0, Call(c, "sceCtrlGetAnalogStickCheckTarget", new Ptr<int>(mode)));
        Assert.Equal(3u, GuestMemory.R32(mode));
        Assert.Equal(CTRL_INVALID_ARG, Call(c, "sceCtrlGetAnalogStickCheckTarget", new Ptr<int>(0)));
        Assert.Equal(0, Call(c, "sceCtrlSetButtonIntercept", 1u));
        Assert.Equal(0, Call(c, "sceCtrlGetButtonIntercept", new Ptr<uint>(mode)));
        // rapid fire rules: index 0-15, ports 0-1, a rule pointer
        Assert.Equal(CTRL_INVALID_ARG, Call(c, "sceCtrlSetRapidFire", 0, 16, _h.Alloc(24)));
        Assert.Equal(CTRL_INVALID_ARG, Call(c, "sceCtrlSetRapidFire", 0, 0, 0u));
        Assert.Equal(CTRL_INVALID_ARG, Call(c, "sceCtrlClearRapidFire", 2, 0));
        Assert.Equal(0, Call(c, "sceCtrlClearRapidFire", 0, 0));
        Assert.Equal(0, Call(c, "sceCtrlSetActuator", 0, 0u));
        Assert.Equal(0, Call(c, "sceCtrlSetLightBar", 0, (byte)1, (byte)2, (byte)3));
        Assert.Equal(0, Call(c, "sceCtrlResetLightBar", 0));
        Assert.Equal(0, Call(c, "sceCtrlIsMultiControllerSupported"));
        Assert.Equal(0, Call(c, "sceCtrlSingleControllerMode", 1));
        uint level = _h.Alloc(4);
        Assert.Equal(0, Call(c, "sceCtrlGetBatteryInfo", 0, new Ptr<byte>(level)));
        Assert.Equal((byte)5, GuestMemory.Read<byte>(level));
    }

    [Fact]
    public void TouchPanelsSamplingAndContactIds()
    {
        _h.Emu.Host.Input = _in;
        var t = _h.Module<Touch>();
        uint info = _h.Alloc(0x30), data = _h.Alloc(0x90 * 2);
        Assert.Equal(TOUCH_INVALID_ARG, Call(t, "sceTouchGetPanelInfo", 2, info));
        Assert.Equal(0, Call(t, "sceTouchGetPanelInfo", 0, info));
        Assert.Equal((short)1919, GuestMemory.Read<short>(info + 4));
        Assert.Equal((short)1087, GuestMemory.Read<short>(info + 6));
        Assert.Equal(0, Call(t, "sceTouchGetPanelInfo", 1, info)); // the rear panel's active area is y 108..889
        Assert.Equal((short)108, GuestMemory.Read<short>(info + 2));
        Assert.Equal((short)889, GuestMemory.Read<short>(info + 6));

        _in.Front = [new TouchPoint(1, 0.5f, 0.5f)];
        // sampling stopped: the read succeeds with no reports
        Assert.Equal(1, Call(t, "sceTouchPeek", 0, data, 1u));
        Assert.Equal(0u, GuestMemory.R32(data + 12));
        Assert.Equal(TOUCH_INVALID_ARG, Call(t, "sceTouchPeek", 0, 0u, 1u));
        Assert.Equal(TOUCH_INVALID_ARG, Call(t, "sceTouchPeek", 0, data, 65u));
        Assert.Equal(0, Call(t, "sceTouchPeek", 0, data, 0u));
        Assert.Equal(0, Call(t, "sceTouchSetSamplingState", 0, 1u));
        uint st = _h.Word(9);
        Assert.Equal(0, Call(t, "sceTouchGetSamplingState", 0, new Ptr<uint>(st)));
        Assert.Equal(1u, GuestMemory.R32(st));
        Call(t, "sceTouchPeek", 0, data, 1u);
        Assert.Equal(1u, GuestMemory.R32(data + 12));
        byte id = GuestMemory.Read<byte>(data + 16);
        Assert.InRange(id, (byte)1, (byte)127);
        Assert.Equal((ushort)959, GuestMemory.Read<ushort>(data + 18)); // 0.5 of 0..1919
        Assert.Equal((ushort)543, GuestMemory.Read<ushort>(data + 20));
        // the same contact keeps its id; a new contact gets another one
        Call(t, "sceTouchPeek", 0, data, 1u);
        Assert.Equal(id, GuestMemory.Read<byte>(data + 16));
        _in.Front = [];
        Call(t, "sceTouchPeek", 0, data, 1u);
        Assert.Equal(0u, GuestMemory.R32(data + 12));
        _in.Front = [new TouchPoint(2, 0f, 1f)];
        Call(t, "sceTouchPeek", 0, data, 1u);
        Assert.NotEqual(id, GuestMemory.Read<byte>(data + 16));
        Assert.Equal((ushort)1087, GuestMemory.Read<ushort>(data + 20));
        // force reports only once enabled
        Assert.Equal(0, GuestMemory.Read<byte>(data + 17));
        Assert.Equal(0, Call(t, "sceTouchEnableTouchForce", 0));
        Call(t, "sceTouchPeek", 0, data, 1u);
        Assert.Equal(128, GuestMemory.Read<byte>(data + 17));
        Assert.Equal(0, Call(t, "sceTouchDisableTouchForce", 0));
        _h.OnGuest(_ => Assert.True(Call(t, "sceTouchRead", 0, data, 2u) >= 0));
        Assert.Equal(TOUCH_INVALID_ARG, Call(t, "sceTouchPeekRegion", 0, data, 1u, 0x100));
        Assert.True(Call(t, "sceTouchPeekRegion", 0, data, 1u, 1) >= 0);
        // the rear panel maps y into its active area
        Assert.Equal(0, Call(t, "sceTouchSetSamplingState", 1, 1u));
        Assert.Equal(1, Call(t, "sceTouchPeek", 1, data, 1u));
        Assert.Equal(0u, GuestMemory.R32(data + 12)); // the scripted input has no rear touches
    }
}
