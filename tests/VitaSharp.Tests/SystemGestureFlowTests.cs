using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSystemGesture driven frame by frame with SceTouchData (+0 timestamp, +12 report count, 16-byte reports {id, force,
/// x, y}): primitive touch events (BEGIN 1 / ACTIVE 2 / END 3, 96 bytes each) and the tap, drag, tap-and-hold, pinch and
/// rotation recognizers (144-byte events). The time comes from the touch data's timestamp, so the tests set it.
/// </summary>
[Collection("guest")]
public sealed class SystemGestureFlowTests(GuestMemoryFixture fx) : IDisposable
{
    const int INVALID_ARG = unchecked((int)0x80800001), NOT_INITIALIZED = unchecked((int)0x80800002);
    const uint BEGIN = 1, ACTIVE = 2, END = 3;

    readonly HleHarness _h = new(fx, "gesture");
    SystemGesture G => _h.Module<SystemGesture>();
    ulong _t = 1_000_000_000_000; // far past the process clock, so the touch timestamps rule

    public void Dispose() => _h.Dispose();

    uint Touch => _touchBuf ??= _h.Alloc(16 + 8 * 16);
    uint? _touchBuf;

    /// <summary>One frame of front touch reports, <paramref name="dtUs"/> after the previous one.</summary>
    void Frame(ulong dtUs, params (byte Id, short X, short Y)[] reports)
    {
        _t += dtUs;
        uint d = Touch;
        GuestMemory.Fill(d, 0, 16 + 8 * 16);
        GuestMemory.Write(d, _t);
        GuestMemory.W32(d + 12, (uint)reports.Length);
        for (int i = 0; i < reports.Length; i++)
        {
            uint r = d + 16 + (uint)i * 16;
            GuestMemory.Write(r, reports[i].Id);
            GuestMemory.Write(r + 1, (byte)64);
            GuestMemory.Write(r + 2, (ushort)reports[i].X);
            GuestMemory.Write(r + 4, (ushort)reports[i].Y);
        }
        Assert.Equal(0, Call(G, "sceSystemGestureUpdatePrimitiveTouchRecognizer", d, 0u));
    }

    uint Recognizer(uint type, short x = 0, short y = 0, short w = 1920, short h = 1088, uint param = 0)
    {
        uint rec = _h.Alloc(307 * 8), rect = _h.Alloc(8);
        GuestMemory.Write(rect, x);
        GuestMemory.Write(rect + 2, y);
        GuestMemory.Write(rect + 4, w);
        GuestMemory.Write(rect + 6, h);
        Assert.Equal(0, Call(G, "sceSystemGestureCreateTouchRecognizer", rec, type, 0u, rect, param));
        return rec;
    }

    List<byte[]> Events(uint rec)
    {
        Assert.Equal(0, Call(G, "sceSystemGestureUpdateTouchRecognizer", rec));
        uint buf = _h.Alloc(144 * 8), n = _h.Word(0);
        Assert.Equal(0, Call(G, "sceSystemGestureGetTouchEvents", rec, buf, 8u, new Ptr<uint>(n)));
        Assert.Equal((uint)Call(G, "sceSystemGestureGetTouchEventsCount", rec), GuestMemory.R32(n));
        return Enumerable.Range(0, (int)GuestMemory.R32(n)).Select(i => GuestMemory.ReadSpan(buf + (uint)i * 144, 144).ToArray()).ToList();
    }

    static uint State(byte[] e) => BitConverter.ToUInt32(e, 4);
    static short S16(byte[] e, int o) => BitConverter.ToInt16(e, o);

    [Fact]
    public void PrimitiveEventsFollowEachFinger()
    {
        Assert.Equal(NOT_INITIALIZED, Call(G, "sceSystemGestureUpdatePrimitiveTouchRecognizer", Touch, 0u));
        Assert.Equal(0, Call(G, "sceSystemGestureInitializePrimitiveTouchRecognizer", 0u));
        Frame(16_000, (1, 100, 200));
        Assert.Equal(1, Call(G, "sceSystemGestureGetPrimitiveTouchEventsCount"));
        uint ev = _h.Alloc(96 * 4), n = _h.Word(0);
        Assert.Equal(0, Call(G, "sceSystemGestureGetPrimitiveTouchEvents", ev, 4u, new Ptr<uint>(n)));
        Assert.Equal(1u, GuestMemory.R32(n));
        Assert.Equal(BEGIN, GuestMemory.R32(ev));
        ushort id = GuestMemory.Read<ushort>(ev + 4);
        Assert.Equal((short)100, GuestMemory.Read<short>(ev + 6));  // press x
        Assert.Equal((short)200, GuestMemory.Read<short>(ev + 8));  // press y

        // the same report id moves: ACTIVE, with the delta and the time since the last frame
        Frame(16_000, (1, 130, 190));
        Assert.Equal(0, Call(G, "sceSystemGestureGetPrimitiveTouchEventByIndex", 0u, ev));
        Assert.Equal(ACTIVE, GuestMemory.R32(ev));
        Assert.Equal(id, GuestMemory.Read<ushort>(ev + 4));
        Assert.Equal((short)130, GuestMemory.Read<short>(ev + 12));
        Assert.Equal((short)30, GuestMemory.Read<short>(ev + 18));
        Assert.Equal((short)-10, GuestMemory.Read<short>(ev + 20));
        Assert.Equal(16_000ul, GuestMemory.Read<ulong>(ev + 24));
        Assert.Equal(16_000ul, GuestMemory.Read<ulong>(ev + 32)); // since the press

        // a second finger is a new primitive
        Frame(16_000, (1, 130, 190), (2, 800, 600));
        Assert.Equal(2, Call(G, "sceSystemGestureGetPrimitiveTouchEventsCount"));
        // the first finger lifts: END for one frame, then gone
        Frame(16_000, (2, 800, 600));
        Assert.Equal(0, Call(G, "sceSystemGestureGetPrimitiveTouchEventByPrimitiveID", (uint)id, ev));
        Assert.Equal(END, GuestMemory.R32(ev));
        Frame(16_000, (2, 800, 600));
        Assert.Equal(1, Call(G, "sceSystemGestureGetPrimitiveTouchEventsCount"));
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureGetPrimitiveTouchEventByPrimitiveID", (uint)id, ev));
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureGetPrimitiveTouchEventByIndex", 5u, ev));
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureGetPrimitiveTouchEventByIndex", 0u, 0u));
        // capacity limits the copy; a NULL buffer only counts
        Assert.Equal(0, Call(G, "sceSystemGestureGetPrimitiveTouchEvents", 0u, 4u, new Ptr<uint>(n)));
        Assert.Equal(1u, GuestMemory.R32(n));
        Assert.Equal(0, Call(G, "sceSystemGestureResetPrimitiveTouchRecognizer"));
        Assert.Equal(0, Call(G, "sceSystemGestureGetPrimitiveTouchEventsCount"));
        Assert.Equal(0, Call(G, "sceSystemGestureFinalizePrimitiveTouchRecognizer"));
        Assert.Equal(NOT_INITIALIZED, Call(G, "sceSystemGestureUpdatePrimitiveTouchRecognizer", Touch, 0u));
    }

    [Fact]
    public void TapsAndDoubleTaps()
    {
        Call(G, "sceSystemGestureInitializePrimitiveTouchRecognizer", 0u);
        uint param = _h.Alloc(4);
        GuestMemory.Write(param, (byte)2); // up to double taps
        uint tap = Recognizer(1, 0, 0, 960, 1088, param);
        Frame(16_000, (1, 300, 300));
        Assert.Empty(Events(tap));
        Frame(100_000);
        var e = Assert.Single(Events(tap));
        Assert.Equal(END, State(e));
        Assert.Equal(1u, BitConverter.ToUInt32(e, 8));  // TAP
        Assert.Equal((short)300, S16(e, 26));
        Assert.Equal(1, e[30]);                          // tap count
        // a second tap soon after, nearby: a double tap
        Frame(100_000, (1, 310, 305));
        Events(tap);
        Frame(80_000);
        e = Assert.Single(Events(tap));
        Assert.Equal(2, e[30]);
        // a tap outside the rectangle, a long press and a slide are no taps
        Frame(500_000, (1, 1500, 300));
        Frame(50_000);
        Assert.Empty(Events(tap));
        Frame(500_000, (1, 300, 300));
        Frame(600_000);
        Assert.Empty(Events(tap));
        Frame(500_000, (1, 300, 300));
        Frame(50_000, (1, 400, 300));
        Frame(50_000);
        Events(tap); // the move frame
        Assert.Empty(Events(tap));
        // events by index / id
        Frame(500_000, (1, 100, 100));
        Frame(50_000);
        e = Assert.Single(Events(tap));
        uint dst = _h.Alloc(144);
        Assert.Equal(0, Call(G, "sceSystemGestureGetTouchEventByIndex", tap, 0u, dst));
        Assert.Equal(BitConverter.ToUInt32(e, 0), GuestMemory.R32(dst));
        Assert.Equal(0, Call(G, "sceSystemGestureGetTouchEventByEventID", tap, BitConverter.ToUInt32(e, 0), dst));
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureGetTouchEventByEventID", tap, 0xFFFFu, dst));
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureGetTouchEventByIndex", tap, 1u, dst));
        Assert.Equal(0, Call(G, "sceSystemGestureResetTouchRecognizer", tap));
        Assert.Equal(0, Call(G, "sceSystemGestureGetTouchEventsCount", tap));
    }

    [Fact]
    public void DragBeginsAtTouchDownAndEndsOnRelease()
    {
        Call(G, "sceSystemGestureInitializePrimitiveTouchRecognizer", 0u);
        uint drag = Recognizer(2);
        Frame(16_000, (1, 100, 100));
        var e = Assert.Single(Events(drag));
        Assert.Equal(BEGIN, State(e));
        uint id = BitConverter.ToUInt32(e, 0);
        Frame(16_000, (1, 150, 120));
        e = Assert.Single(Events(drag));
        Assert.Equal(ACTIVE, State(e));
        Assert.Equal(id, BitConverter.ToUInt32(e, 0));
        Assert.Equal((short)50, S16(e, 26));   // delta
        Assert.Equal((short)150, S16(e, 30));  // current
        Assert.Equal((short)100, S16(e, 34));  // pressed at
        Frame(16_000);
        e = Assert.Single(Events(drag));
        Assert.Equal(END, State(e));
        // pressed and released between two updates: begin and end together
        Frame(16_000, (2, 500, 500));
        Frame(16_000);
        var both = Events(drag);
        Assert.Equal([BEGIN, END], both.Select(State).ToArray());
        Assert.Equal(BitConverter.ToUInt32(both[0], 0), BitConverter.ToUInt32(both[1], 0));
    }

    [Fact]
    public void TapAndHoldNeedsHalfASecondStill()
    {
        Call(G, "sceSystemGestureInitializePrimitiveTouchRecognizer", 0u);
        uint hold = Recognizer(4);
        Frame(16_000, (1, 200, 200));
        Assert.Empty(Events(hold));
        Frame(300_000, (1, 202, 201));
        Assert.Empty(Events(hold));
        Frame(300_000, (1, 203, 201));
        Assert.Equal(BEGIN, State(Assert.Single(Events(hold))));
        Frame(16_000, (1, 203, 201));
        Assert.Equal(ACTIVE, State(Assert.Single(Events(hold))));
        Frame(16_000, (1, 400, 201)); // moved away: ends
        Assert.Equal(END, State(Assert.Single(Events(hold))));
    }

    [Fact]
    public void PinchAndRotation()
    {
        Call(G, "sceSystemGestureInitializePrimitiveTouchRecognizer", 0u);
        uint pinch = Recognizer(8), rot = Recognizer(0x10);
        Frame(16_000, (1, 500, 500), (2, 700, 500));
        Assert.Equal(BEGIN, State(Assert.Single(Events(pinch))));
        Assert.Equal(BEGIN, State(Assert.Single(Events(rot))));
        // spread to twice the distance; turn the second finger a quarter turn around the first
        Frame(16_000, (1, 500, 500), (2, 900, 500));
        var e = Assert.Single(Events(pinch));
        Assert.Equal(ACTIVE, State(e));
        Assert.Equal(2f, BitConverter.ToSingle(e, 24), 3);
        Frame(16_000, (1, 500, 500), (2, 500, 900));
        Events(pinch);
        e = Assert.Single(Events(rot));
        Assert.Equal(MathF.PI / 2, BitConverter.ToSingle(e, 24), 3);
        // a finger lifts: the gesture ends
        Frame(16_000, (1, 500, 500));
        Events(rot);
        var end = Events(pinch);
        Assert.Contains(end, x => State(x) == END);
    }

    [Fact]
    public void RecognizerInformationAndErrors()
    {
        uint rec = Recognizer(2, 10, 20, 300, 400), info = _h.Alloc(280);
        Assert.Equal(0, Call(G, "sceSystemGestureGetTouchRecognizerInformation", rec, info));
        Assert.Equal(2u, GuestMemory.R32(info));
        Assert.Equal((short)10, GuestMemory.Read<short>(info + 8));
        Assert.Equal((short)400, GuestMemory.Read<short>(info + 14));
        uint rect = _h.Alloc(8);
        GuestMemory.Write(rect + 4, (short)50);
        GuestMemory.Write(rect + 6, (short)60);
        Assert.Equal(0, Call(G, "sceSystemGestureUpdateTouchRecognizerRectangle", rec, rect));
        Call(G, "sceSystemGestureGetTouchRecognizerInformation", rec, info);
        Assert.Equal((short)0, GuestMemory.Read<short>(info + 8));
        Assert.Equal((short)60, GuestMemory.Read<short>(info + 14));
        // NULL rectangle: the whole front panel
        Assert.Equal(0, Call(G, "sceSystemGestureUpdateTouchRecognizerRectangle", rec, 0u));
        Call(G, "sceSystemGestureGetTouchRecognizerInformation", rec, info);
        Assert.Equal((short)1920, GuestMemory.Read<short>(info + 12));

        uint unknown = _h.Alloc(16), n = _h.Word(5);
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureGetTouchRecognizerInformation", unknown, info));
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureGetTouchRecognizerInformation", rec, 0u));
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureUpdateTouchRecognizerRectangle", unknown, rect));
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureResetTouchRecognizer", unknown));
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureUpdateTouchRecognizer", unknown));
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureGetTouchEvents", unknown, 0u, 1u, new Ptr<uint>(n)));
        Assert.Equal(0u, GuestMemory.R32(n));
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureGetTouchEventsCount", unknown));
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureGetTouchEventByIndex", unknown, 0u, info));
        Assert.Equal(INVALID_ARG, Call(G, "sceSystemGestureGetTouchEventByEventID", unknown, 0u, info));
    }
}
