using System.Reflection;
using System.Text;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>G1 misc media/voice libraries: SceIme, SceVoice, SceVoiceQoS, SceG729, SceAudioenc, SceVideoenc,
/// SceLibMp4Recorder, SceHandwriting.</summary>
[Collection("guest")]
public sealed class MediaMiscHleTests(GuestMemoryFixture fx)
{
    static object? Invoke<T>(T module, string name, params object[] args) =>
        typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);
    static int Call<T>(T module, string name, params object[] args) => (int)Invoke(module, name, args)!;

    static void WriteUtf16(uint addr, string s)
    {
        var b = Encoding.Unicode.GetBytes(s + "\0");
        b.CopyTo(GuestMemory.Span(addr, b.Length));
    }

    static string ReadUtf16(uint addr)
    {
        int n = 0;
        while (GuestMemory.Read<ushort>(addr + (uint)n * 2) != 0) n++;
        return Encoding.Unicode.GetString(GuestMemory.Span(addr, n * 2));
    }

    // ------------------------------------------------------------------ SceIme

    const uint ImeHandler = 0x1234_5679;

    (Ime ime, uint param, uint buffer, List<(uint id, uint p0, uint p2, string text, int edIdx, int edLen)> events) NewIme(string initial)
    {
        var ime = new Ime();
        uint param = fx.Memory.Alloc(0x1000, "ime param");
        GuestMemory.Fill(param, 0, 0x40);
        uint init = param + 0x100, buffer = param + 0x400, work = fx.Memory.Alloc(20 * 1024, "ime work");
        WriteUtf16(init, initial);
        GuestMemory.W32(param + 0x1C, work);
        GuestMemory.W32(param + 0x20, 0xA5A5);
        GuestMemory.W32(param + 0x24, ImeHandler);
        GuestMemory.W32(param + 0x2C, init);
        GuestMemory.W32(param + 0x30, 16);
        GuestMemory.W32(param + 0x34, buffer);
        var events = new List<(uint, uint, uint, string, int, int)>();
        ime.GuestCallOverride = (fn, a) =>
        {
            Assert.Equal(ImeHandler, fn);
            Assert.Equal(0xA5A5u, a[0]);
            uint e = a[1], id = GuestMemory.R32(e);
            string text = id == Ime.EVENT_UPDATE_TEXT ? ReadUtf16(GuestMemory.R32(e + 16)) : "";
            events.Add((id, GuestMemory.R32(e + 4), GuestMemory.R32(e + 12), text, (int)GuestMemory.R32(e + 20), (int)GuestMemory.R32(e + 24)));
            return 0;
        };
        return (ime, param, buffer, events);
    }

    [Fact]
    public void ImeOpenValidatesParam()
    {
        var (ime, param, _, _) = NewIme("");
        Assert.Equal(Ime.ERR_INVALID_POINTER, Call(ime, "sceImeOpen", 0u));
        GuestMemory.W32(param + 0x30, 0);
        Assert.Equal(Ime.ERR_INVALID_MAX_TEXT_LENGTH, Call(ime, "sceImeOpen", param));
        GuestMemory.W32(param + 0x30, 2049);
        Assert.Equal(Ime.ERR_INVALID_MAX_TEXT_LENGTH, Call(ime, "sceImeOpen", param));
        GuestMemory.W32(param + 0x30, 16);
        GuestMemory.W32(param + 0x14, 6);
        Assert.Equal(Ime.ERR_INVALID_TYPE, Call(ime, "sceImeOpen", param));
        GuestMemory.W32(param + 0x14, 0);
        GuestMemory.W32(param + 0x18, 8);
        Assert.Equal(Ime.ERR_INVALID_OPTION, Call(ime, "sceImeOpen", param));
        GuestMemory.W32(param + 0x18, 0);
        GuestMemory.Write<byte>(param + 0x3C, 1);
        Assert.Equal(Ime.ERR_INVALID_RESERVED, Call(ime, "sceImeOpen", param));
        GuestMemory.Write<byte>(param + 0x3C, 0);
        GuestMemory.Write<byte>(param + 0x38, 4);
        Assert.Equal(Ime.ERR_INVALID_ENTER_LABEL, Call(ime, "sceImeOpen", param));
        GuestMemory.Write<byte>(param + 0x38, 0);
        GuestMemory.W32(param + 0x24, 0);
        Assert.Equal(Ime.ERR_INVALID_HANDLER, Call(ime, "sceImeOpen", param));
        GuestMemory.W32(param + 0x24, ImeHandler);
        WriteUtf16(param + 0x100, new string('x', 17));
        Assert.Equal(Ime.ERR_INVALID_TEXT, Call(ime, "sceImeOpen", param));
        Assert.False(ime.IsOpen);
    }

    [Fact]
    public void ImeEventFlowOpenTextEnterClose()
    {
        string? saved = CommonDialog.ImeDefaultText;
        CommonDialog.ImeDefaultText = null;
        try
        {
            var (ime, param, buffer, events) = NewIme("ab");
            Assert.Equal(Ime.ERR_NOT_OPENED, Call(ime, "sceImeUpdate"));
            Assert.Equal(0, Call(ime, "sceImeOpen", param));
            Assert.Equal(Ime.ERR_ALREADY_OPENED, Call(ime, "sceImeOpen", param));
            Assert.Equal("ab", ReadUtf16(buffer));
            Assert.Empty(events); // OPEN is delivered by sceImeUpdate, not inside sceImeOpen
            Assert.Equal(0, Call(ime, "sceImeUpdate"));
            Assert.Single(events);
            Assert.Equal(Ime.EVENT_OPEN, events[0].id);
            Assert.Equal(960u, events[0].p2); // rect.width

            ime.HostTextChanged("abc", 3);
            ime.HostEnter();
            Assert.Equal(0, Call(ime, "sceImeUpdate"));
            Assert.Equal(3, events.Count);
            Assert.Equal(Ime.EVENT_UPDATE_TEXT, events[1].id);
            Assert.Equal("abc", events[1].text);
            Assert.Equal(3u, events[1].p2); // caretIndex
            Assert.Equal(2, events[1].edIdx);
            Assert.Equal(1, events[1].edLen);
            Assert.Equal("abc", ReadUtf16(buffer));
            Assert.Equal(Ime.EVENT_PRESS_ENTER, events[2].id);

            // deletion reports a negative length change at the edit position
            ime.HostTextChanged("c", 1);
            Assert.Equal(0, Call(ime, "sceImeUpdate"));
            Assert.Equal((0, -2), (events[3].edIdx, events[3].edLen));

            // caret moved by the game: UPDATE_CARET with the new index; out of range rejected
            uint caret = fx.Memory.Alloc(0x10, "ime caret");
            GuestMemory.W32(caret + 12, 0);
            Assert.Equal(0, Call(ime, "sceImeSetCaret", caret));
            GuestMemory.W32(caret + 12, 5);
            Assert.Equal(Ime.ERR_INVALID_ARG, Call(ime, "sceImeSetCaret", caret));
            Assert.Equal(Ime.ERR_INVALID_POINTER, Call(ime, "sceImeSetCaret", 0u));
            Assert.Equal(0, Call(ime, "sceImeUpdate"));
            Assert.Equal((Ime.EVENT_UPDATE_CARET, 0u), (events[4].id, events[4].p0));

            // SetText replaces buffer and state without an event
            uint txt = param + 0x800;
            WriteUtf16(txt, "hello");
            Assert.Equal(0, Call(ime, "sceImeSetText", txt, 5u));
            Assert.Equal("hello", ReadUtf16(buffer));
            Assert.Equal(5, ime.Caret);
            Assert.Equal(Ime.ERR_INVALID_TEXT, Call(ime, "sceImeSetText", txt, 17u));
            Assert.Equal(0, Call(ime, "sceImeSetPreeditGeometry", caret));
            Assert.Equal(Ime.ERR_INVALID_POINTER, Call(ime, "sceImeSetPreeditGeometry", 0u));

            // events queued before Close are discarded
            ime.HostEnter();
            Assert.Equal(0, Call(ime, "sceImeClose"));
            Assert.Equal(Ime.ERR_NOT_OPENED, Call(ime, "sceImeClose"));
            Assert.Equal(Ime.ERR_NOT_OPENED, Call(ime, "sceImeSetCaret", caret));
            Assert.Equal(Ime.ERR_NOT_OPENED, Call(ime, "sceImeSetPreeditGeometry", caret));
            Assert.Equal(0, Call(ime, "sceImeOpen", param));
            Assert.Equal(0, Call(ime, "sceImeUpdate"));
            Assert.Equal(Ime.EVENT_OPEN, events[^1].id);
            Assert.Equal(0, Call(ime, "sceImeClose"));
        }
        finally { CommonDialog.ImeDefaultText = saved; }
    }

    [Fact]
    public void ImeScriptedTextAndCloseFromHandler()
    {
        string? saved = CommonDialog.ImeDefaultText;
        CommonDialog.ImeDefaultText = "Hi";
        try
        {
            var (ime, param, buffer, events) = NewIme("");
            var inner = ime.GuestCallOverride!;
            ime.GuestCallOverride = (fn, a) =>
            {
                uint r = inner(fn, a);
                if (GuestMemory.R32(a[1]) == Ime.EVENT_UPDATE_TEXT) Assert.Equal(0, Call(ime, "sceImeClose"));
                return r;
            };
            Assert.Equal(0, Call(ime, "sceImeOpen", param));
            Assert.Equal(0, Call(ime, "sceImeUpdate"));
            // OPEN, UPDATE_TEXT; the handler closed the IME so PRESS_ENTER is not delivered
            Assert.Equal(new[] { Ime.EVENT_OPEN, Ime.EVENT_UPDATE_TEXT }, events.Select(e => e.id).ToArray());
            Assert.Equal("Hi", ReadUtf16(buffer));
            Assert.False(ime.IsOpen);
        }
        finally { CommonDialog.ImeDefaultText = saved; }
    }

    [Fact]
    public void ImeFilterRewritesText()
    {
        string? saved = CommonDialog.ImeDefaultText;
        CommonDialog.ImeDefaultText = null;
        try
        {
            var (ime, param, buffer, events) = NewIme("");
            const uint Filter = 0x2222_0001;
            GuestMemory.W32(param + 0x28, Filter);
            var inner = ime.GuestCallOverride!;
            ime.GuestCallOverride = (fn, a) =>
            {
                if (fn != Filter) return inner(fn, a);
                // filter(outText, outTextLength, srcText, srcTextLength): upper-case, reject "bad"
                string src = Encoding.Unicode.GetString(GuestMemory.Span(a[2], (int)a[3] * 2));
                if (src == "bad") return unchecked((uint)-1);
                WriteUtf16(a[0], src.ToUpperInvariant());
                GuestMemory.W32(a[1], (uint)src.Length);
                return 0;
            };
            Assert.Equal(0, Call(ime, "sceImeOpen", param));
            ime.HostTextChanged("abc", 3);
            ime.HostTextChanged("bad", 3);
            Assert.Equal(0, Call(ime, "sceImeUpdate"));
            Assert.Equal(2, events.Count); // OPEN + filtered UPDATE_TEXT; the rejected edit is dropped
            Assert.Equal("ABC", events[1].text);
            Assert.Equal("ABC", ReadUtf16(buffer));
            Assert.Equal(0, Call(ime, "sceImeClose"));
        }
        finally { CommonDialog.ImeDefaultText = saved; }
    }

    // ------------------------------------------------------------------ SceVoice

    sealed class FakeClock { public double Now; }

    (Voice v, FakeClock clock, uint scratch) NewVoice()
    {
        var clock = new FakeClock();
        var v = new Voice { Clock = () => clock.Now, MemBlockExists = uid => uid == 0x4242 };
        uint s = fx.Memory.Alloc(0x10000, "voice scratch");
        GuestMemory.Fill(s, 0, 0x10000);
        GuestMemory.W32(s, 0x20000000);
        Assert.Equal(0, Call(v, "sceVoiceInit", s, 100u));
        return (v, clock, s);
    }

    /// <summary>Writes a SceVoicePortParam at <paramref name="p"/> and creates the port; returns (result, id).</summary>
    static (int r, uint id) CreatePort(Voice v, uint p, uint type, uint data, ushort threshold = 0, float volume = 1f, uint rate = 16000)
    {
        GuestMemory.Fill(p, 0, 0x20);
        GuestMemory.W32(p, type);
        GuestMemory.Write(p + 4, threshold);
        GuestMemory.Write(p + 8, volume);
        GuestMemory.W32(p + 0xC, data);
        GuestMemory.W32(p + 0x14, rate);
        int r = Call(v, "sceVoiceCreatePort", p + 0x20, p);
        return (r, GuestMemory.R32(p + 0x20));
    }

    [Fact]
    public void VoiceInitStartStopEnd()
    {
        var (v, _, s) = NewVoice();
        Assert.Equal(Voice.ERR_ALREADY_INITIALIZED, Call(v, "sceVoiceInit", s, 100u));
        uint info = s + 0x100;
        Assert.Equal(0, Call(v, "sceVoiceGetResourceInfo", info));
        Assert.Equal(new ushort[] { 7, 7, 1, 1, 64 }, Enumerable.Range(0, 5).Select(i => GuestMemory.Read<ushort>(info + (uint)i * 2)).ToArray());
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, Call(v, "sceVoiceStart", 0u));
        uint start = s + 0x200;
        GuestMemory.W32(start, 99);
        Assert.Equal(Voice.ERR_INVALID_MEMBLOCK, Call(v, "sceVoiceStart", start));
        var (r, id) = CreatePort(v, s + 0x300, Voice.OUT_PCMAUDIO, 2048);
        Assert.Equal(0, r);
        GuestMemory.Fill(info, 0xEE, 0x18);
        Assert.Equal(Voice.ERR_NOT_STARTED, Call(v, "sceVoiceGetPortInfo", id, info));
        Assert.Equal(0u, GuestMemory.R32(info + 0x14)); // whole struct cleared
        GuestMemory.W32(start, 0x4242);
        Assert.Equal(0, Call(v, "sceVoiceStart", start));
        Assert.Equal(0, Call(v, "sceVoiceStart", start)); // already started: success
        GuestMemory.Fill(info, 0xEE, 0x18);
        Assert.Equal(0, Call(v, "sceVoiceGetPortInfo", id, info));
        Assert.Equal(Voice.OUT_PCMAUDIO, GuestMemory.R32(info));
        Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(info + 8)); // reserved untouched on success
        Assert.Equal(0, Call(v, "sceVoiceStop"));
        Assert.Equal(0, Call(v, "sceVoiceStop"));
        Assert.Equal(Voice.ERR_NOT_STARTED, Call(v, "sceVoiceGetPortInfo", id, info));
        Assert.Equal(0, Call(v, "sceVoiceEnd"));
        Assert.Equal(Voice.ERR_NOT_INITIALIZED, Call(v, "sceVoiceEnd"));
        Assert.Equal(Voice.ERR_NOT_INITIALIZED, CreatePort(v, s + 0x300, Voice.OUT_PCMAUDIO, 2048).r);
        Assert.Equal(0, Call(v, "sceVoiceInit", s, 100u));
    }

    [Fact]
    public void VoiceCreatePortValidationAndLimits()
    {
        var (v, _, s) = NewVoice();
        uint p = s + 0x400;
        var (r, id) = CreatePort(v, p, Voice.IN_PCMAUDIO, 1000);
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, r);
        Assert.Equal(0xFFu, id); // written before validation
        Assert.Equal(0, CreatePort(v, p, Voice.IN_PCMAUDIO, 1024).r);
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, CreatePort(v, p, Voice.IN_PCMAUDIO, 65024).r);
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, CreatePort(v, p, Voice.IN_PCMAUDIO, 4096, rate: 48000).r);
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, CreatePort(v, p, Voice.IN_PCMAUDIO, 1024, threshold: 32).r); // 1024 B = 32 ms
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, CreatePort(v, p, Voice.OUT_VOICE, 6000).r);
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, CreatePort(v, p, Voice.IN_DEVICE, 1).r);
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, CreatePort(v, p, 6, 0).r);
        Assert.Equal(0, CreatePort(v, p, Voice.IN_DEVICE, 0).r);
        Assert.Equal(Voice.ERR_RESOURCE_LIMIT, CreatePort(v, p, Voice.IN_DEVICE, 0).r);
        for (int i = 0; i < 7; i++) Assert.Equal(0, CreatePort(v, p, Voice.IN_VOICE, 12200).r);
        Assert.Equal(Voice.ERR_RESOURCE_LIMIT, CreatePort(v, p, Voice.IN_VOICE, 12200).r);
        // deleting frees the lowest slot for reuse
        Assert.Equal(0, Call(v, "sceVoiceDeletePort", 0u));
        Assert.Equal(Voice.ERR_INVALID_PORT_ID, Call(v, "sceVoiceDeletePort", 0u));
        Assert.Equal(Voice.ERR_INVALID_PORT_ID, Call(v, "sceVoiceDeletePort", 64u));
        var (r2, id2) = CreatePort(v, p, Voice.OUT_PCMAUDIO, 4096);
        Assert.Equal((0, 0u), (r2, id2));
    }

    [Fact]
    public void VoiceConnectDisconnectAndTopology()
    {
        var (v, _, s) = NewVoice();
        uint p = s + 0x400;
        uint mic = CreatePort(v, p, Voice.IN_DEVICE, 0).id;
        uint inV = CreatePort(v, p, Voice.IN_VOICE, 12200).id;
        uint outV = CreatePort(v, p, Voice.OUT_VOICE, 12200).id;
        uint spk = CreatePort(v, p, Voice.OUT_DEVICE, 0).id;
        Assert.Equal(Voice.ERR_INVALID_PORT, Call(v, "sceVoiceCheckTopology")); // unconnected ports
        Assert.Equal(Voice.ERR_INVALID_PORT, Call(v, "sceVoiceConnectIPortToOPort", inV, outV)); // IN_VOICE -> OUT_VOICE
        Assert.Equal(Voice.ERR_INVALID_PORT, Call(v, "sceVoiceConnectIPortToOPort", outV, mic)); // reversed
        Assert.Equal(Voice.ERR_INVALID_PORT_ID, Call(v, "sceVoiceConnectIPortToOPort", 40u, outV));
        Assert.Equal(0, Call(v, "sceVoiceConnectIPortToOPort", mic, outV));
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, Call(v, "sceVoiceConnectIPortToOPort", mic, outV)); // duplicate
        Assert.Equal(0, Call(v, "sceVoiceConnectIPortToOPort", inV, spk));
        Assert.Equal(0, Call(v, "sceVoiceCheckTopology"));
        // a second OUT_VOICE with another bitrate breaks the adjacency rule
        uint outV2 = CreatePort(v, p, Voice.OUT_VOICE, 5700).id;
        Assert.Equal(0, Call(v, "sceVoiceConnectIPortToOPort", mic, outV2));
        Assert.Equal(Voice.ERR_INVALID_PORT, Call(v, "sceVoiceCheckTopology"));
        Assert.Equal(0, Call(v, "sceVoiceSetBitRate", outV2, 12200u));
        Assert.Equal(0, Call(v, "sceVoiceCheckTopology"));
        Assert.Equal(0, Call(v, "sceVoiceDisconnectIPortFromOPort", mic, outV2));
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, Call(v, "sceVoiceDisconnectIPortFromOPort", mic, outV2));
        // deleting a port drops its connections
        Assert.Equal(0, Call(v, "sceVoiceDeletePort", outV2));
        Assert.Equal(0, Call(v, "sceVoiceDeletePort", mic));
        Assert.Equal(Voice.ERR_INVALID_PORT, Call(v, "sceVoiceCheckTopology")); // outV lost its only connection
    }

    [Fact]
    public void VoicePcmMixWithVolume()
    {
        var (v, clock, s) = NewVoice();
        uint p = s + 0x400;
        uint inP = CreatePort(v, p, Voice.IN_PCMAUDIO, 1024).id;
        uint outP = CreatePort(v, p, Voice.OUT_PCMAUDIO, 4096, volume: 0.5f).id;
        Assert.Equal(0, Call(v, "sceVoiceConnectIPortToOPort", inP, outP));
        GuestMemory.W32(s + 0x200, 0x4242);
        Assert.Equal(0, Call(v, "sceVoiceStart", s + 0x200));
        uint info = s + 0x100;
        Assert.Equal(0, Call(v, "sceVoiceGetPortInfo", inP, info));
        Assert.Equal((Voice.STATE_IDLE, 1024u, 320u), (GuestMemory.R32(info + 4), GuestMemory.R32(info + 0xC), GuestMemory.R32(info + 0x10)));

        // writing more than the ring holds accepts only the free space
        uint data = s + 0x1000, size = s + 0x80;
        for (uint i = 0; i < 1024; i++) GuestMemory.Write(data + i * 2, (short)1000);
        GuestMemory.W32(size, 2048);
        Assert.Equal(0, Call(v, "sceVoiceWriteToIPort", inP, data, size, 0u));
        Assert.Equal(1024u, GuestMemory.R32(size));
        Assert.Equal(0, Call(v, "sceVoiceGetPortInfo", inP, info));
        Assert.Equal((Voice.STATE_ACTIVE, 0u), (GuestMemory.R32(info + 4), GuestMemory.R32(info + 0xC)));

        clock.Now += 0.020; // two ticks: 640 bytes
        uint outBuf = s + 0x3000;
        GuestMemory.W32(size, 4096);
        Assert.Equal(0, Call(v, "sceVoiceReadFromOPort", outP, outBuf, size));
        Assert.Equal(640u, GuestMemory.R32(size));
        Assert.Equal((short)500, GuestMemory.Read<short>(outBuf));
        Assert.Equal((short)500, GuestMemory.Read<short>(outBuf + 638));
        // odd capacities are rounded down to whole samples
        clock.Now += 0.010;
        GuestMemory.W32(size, 101);
        Assert.Equal(0, Call(v, "sceVoiceReadFromOPort", outP, outBuf, size));
        Assert.Equal(100u, GuestMemory.R32(size));
        // wrong port kinds
        Assert.Equal(Voice.ERR_INVALID_PORT, Call(v, "sceVoiceReadFromOPort", inP, outBuf, size));
        Assert.Equal(Voice.ERR_INVALID_PORT, Call(v, "sceVoiceWriteToIPort", outP, data, size, 0u));
        // mute on the input silences the mix
        Assert.Equal(0, Call(v, "sceVoiceSetMuteFlag", inP, 1u));
        GuestMemory.W32(size, 1024);
        Assert.Equal(0, Call(v, "sceVoiceWriteToIPort", inP, data, size, 0u));
        clock.Now += 0.050;
        GuestMemory.W32(size, 4096);
        Assert.Equal(0, Call(v, "sceVoiceReadFromOPort", outP, outBuf, size));
        Assert.True(GuestMemory.R32(size) >= 320);
        Assert.Equal((short)0, GuestMemory.Read<short>(outBuf + GuestMemory.R32(size) - 2));
        // reset clears the ring
        Assert.Equal(0, Call(v, "sceVoiceResetPort", inP));
        Assert.Equal(0, Call(v, "sceVoiceGetPortInfo", inP, info));
        Assert.Equal(1024u, GuestMemory.R32(info + 0xC));
    }

    [Fact]
    public void VoiceOutVoiceProducesSizedFramesFromMic()
    {
        var (v, clock, s) = NewVoice();
        uint p = s + 0x400;
        uint mic = CreatePort(v, p, Voice.IN_DEVICE, 0).id;
        uint outV = CreatePort(v, p, Voice.OUT_VOICE, 5700).id; // 15 bytes / 20 ms
        Assert.Equal(0, Call(v, "sceVoiceConnectIPortToOPort", mic, outV));
        GuestMemory.W32(s + 0x200, 0x4242);
        Assert.Equal(0, Call(v, "sceVoiceStart", s + 0x200));
        clock.Now += 0.065; // 6 ticks = 3 frames
        uint info = s + 0x100;
        Assert.Equal(0, Call(v, "sceVoiceGetPortInfo", outV, info));
        Assert.Equal((45u, 15u), (GuestMemory.R32(info + 0xC), GuestMemory.R32(info + 0x10)));
        uint buf = s + 0x2000, size = s + 0x80;
        GuestMemory.W32(size, 40); // rounded down to whole frames
        Assert.Equal(0, Call(v, "sceVoiceReadFromOPort", outV, buf, size));
        Assert.Equal(30u, GuestMemory.R32(size));
        // paused output keeps reporting its old state and produces nothing
        Assert.Equal(0, Call(v, "sceVoicePausePort", outV));
        clock.Now += 0.1;
        Assert.Equal(0, Call(v, "sceVoiceGetPortInfo", outV, info));
        Assert.Equal((Voice.STATE_ACTIVE, 15u), (GuestMemory.R32(info + 4), GuestMemory.R32(info + 0xC)));
        Assert.Equal(0, Call(v, "sceVoiceResumePort", outV));
        // pause-all does not catch up the paused time
        Assert.Equal(0, Call(v, "sceVoicePausePortAll"));
        clock.Now += 1.0;
        Assert.Equal(0, Call(v, "sceVoiceResumePortAll"));
        Assert.Equal(0, Call(v, "sceVoiceGetPortInfo", outV, info));
        Assert.Equal(15u, GuestMemory.R32(info + 0xC));
    }

    [Fact]
    public void VoicePortAttributes()
    {
        var (v, _, s) = NewVoice();
        uint p = s + 0x400, outp = s + 0x80;
        uint mic = CreatePort(v, p, Voice.IN_DEVICE, 0).id;
        uint pcm = CreatePort(v, p, Voice.OUT_PCMAUDIO, 2048).id;
        uint vo = CreatePort(v, p, Voice.OUT_VOICE, 3850).id;
        Assert.Equal(0, Call(v, "sceVoiceSetVolume", pcm, 2.5f));
        Assert.Equal(0, Call(v, "sceVoiceGetVolume", pcm, outp));
        Assert.Equal(2.5f, GuestMemory.Read<float>(outp));
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, Call(v, "sceVoiceGetVolume", pcm, 0u));
        Assert.Equal(0, Call(v, "sceVoiceGetMuteFlag", pcm, outp));
        Assert.Equal((ushort)0, GuestMemory.Read<ushort>(outp));
        Assert.Equal(0, Call(v, "sceVoiceSetMuteFlagAll", 1u));
        Assert.Equal(0, Call(v, "sceVoiceGetMuteFlag", pcm, outp));
        Assert.Equal((ushort)1, GuestMemory.Read<ushort>(outp));
        Assert.Equal(Voice.ERR_INVALID_PORT, Call(v, "sceVoiceSetBitRate", pcm, 3850u));
        Assert.Equal(Voice.ERR_INVALID_PORT, Call(v, "sceVoiceGetBitRate", pcm, outp));
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, Call(v, "sceVoiceSetBitRate", vo, 1234u));
        Assert.Equal(0, Call(v, "sceVoiceSetBitRate", vo, 7300u));
        Assert.Equal(0, Call(v, "sceVoiceGetBitRate", vo, outp));
        Assert.Equal(7300u, GuestMemory.R32(outp));
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, Call(v, "sceVoiceSetPortAttr", mic, 1000u, outp));
        Assert.Equal(Voice.ERR_INVALID_PORT_ID, Call(v, "sceVoiceSetPortAttr", 50u, 1000u, outp));
        GuestMemory.W32(outp, 77);
        Assert.Equal(0, Call(v, "sceVoiceGetPortAttr", pcm, 1000u, outp));
        Assert.Equal(77u, GuestMemory.R32(outp)); // non-device: untouched
        Assert.Equal(0, Call(v, "sceVoiceGetPortAttr", mic, 1000u, outp));
        Assert.Equal(0u, GuestMemory.R32(outp));
        // UpdatePort: type cannot change, bitrate validated
        GuestMemory.W32(p, Voice.OUT_PCMAUDIO);
        Assert.Equal(Voice.ERR_INVALID_ARGUMENT, Call(v, "sceVoiceUpdatePort", vo, p));
        GuestMemory.W32(p, Voice.OUT_VOICE);
        GuestMemory.W32(p + 0xC, 12200);
        GuestMemory.Write(p + 8, 0.25f);
        Assert.Equal(0, Call(v, "sceVoiceUpdatePort", vo, p));
        Assert.Equal(0, Call(v, "sceVoiceGetBitRate", vo, outp));
        Assert.Equal(12200u, GuestMemory.R32(outp));
        Assert.Equal(0, Call(v, "sceVoiceGetVolume", vo, outp));
        Assert.Equal(0.25f, GuestMemory.Read<float>(outp));
    }

    // ------------------------------------------------------------------ SceVoiceQoS

    [Fact]
    public void VoiceQoSSessionFlow()
    {
        var q = new VoiceQoS();
        uint s = fx.Memory.Alloc(0x1000, "qos");
        Assert.Equal(VoiceQoS.ERR_GENERIC, Call(q, "sceVoiceQoSCreateLocalEndpoint", s));
        Assert.Equal(0, Call(q, "sceVoiceQoSInit", s + 0x800, 0x800u, 0x20000000u));
        Assert.Equal(VoiceQoS.ERR_GENERIC, Call(q, "sceVoiceQoSInit", s + 0x800, 0x800u, 0x20000000u));
        Assert.Equal(0, Call(q, "sceVoiceQoSCreateLocalEndpoint", s));
        Assert.Equal(0, Call(q, "sceVoiceQoSCreateRemoteEndpoint", s + 4));
        int local = (int)GuestMemory.R32(s), remote = (int)GuestMemory.R32(s + 4);
        Assert.NotEqual(local, remote);
        Assert.Equal(VoiceQoS.ERR_GENERIC, Call(q, "sceVoiceQoSConnect", s + 8, remote, local)); // swapped ids
        Assert.Equal(0, Call(q, "sceVoiceQoSConnect", s + 8, local, remote));
        int conn = (int)GuestMemory.R32(s + 8);
        Assert.Equal(0, Call(q, "sceVoiceQoSGetLocalEndpoint", conn, s + 12));
        Assert.Equal(0, Call(q, "sceVoiceQoSGetRemoteEndpoint", conn, s + 16));
        Assert.Equal(((uint)local, (uint)remote), (GuestMemory.R32(s + 12), GuestMemory.R32(s + 16)));

        uint data = s + 0x100, size = s + 20;
        GuestMemory.W32(size, 256);
        Assert.Equal(0, Call(q, "sceVoiceQoSReadPacket", conn, data, size));
        Assert.Equal(0u, GuestMemory.R32(size)); // nothing to send
        GuestMemory.W32(size, 64);
        Assert.Equal(0, Call(q, "sceVoiceQoSWritePacket", conn, data, size));
        Assert.Equal(64u, GuestMemory.R32(size)); // all consumed

        // attributes: set -> get, never written -> zeros, separate key spaces
        GuestMemory.W32(s + 0x200, 0xCAFE);
        Assert.Equal(0, Call(q, "sceVoiceQoSSetLocalEndpointAttribute", local, 3u, s + 0x200, 4));
        GuestMemory.W32(s + 0x300, 0xFFFF);
        Assert.Equal(0, Call(q, "sceVoiceQoSGetLocalEndpointAttribute", local, 3u, s + 0x300, 4));
        Assert.Equal(0xCAFEu, GuestMemory.R32(s + 0x300));
        Assert.Equal(0, Call(q, "sceVoiceQoSGetConnectionAttribute", conn, 3u, s + 0x300, 4));
        Assert.Equal(0u, GuestMemory.R32(s + 0x300));
        GuestMemory.W32(s + 0x300, 0xFFFF);
        Assert.Equal(0, Call(q, "sceVoiceQoSGetStatus", conn, 1u, s + 0x300, 4));
        Assert.Equal(0u, GuestMemory.R32(s + 0x300));

        // deleting the local endpoint drops its connection
        Assert.Equal(0, Call(q, "sceVoiceQoSDeleteLocalEndpoint", local));
        Assert.Equal(VoiceQoS.ERR_GENERIC, Call(q, "sceVoiceQoSDisconnect", conn));
        Assert.Equal(0, Call(q, "sceVoiceQoSDeleteRemoteEndpoint", remote));
        Assert.Equal(VoiceQoS.ERR_GENERIC, Call(q, "sceVoiceQoSDeleteRemoteEndpoint", remote));
        Assert.Equal(0, Call(q, "sceVoiceQoSEnd"));
        Assert.Equal(VoiceQoS.ERR_GENERIC, Call(q, "sceVoiceQoSEnd"));
    }

    // ------------------------------------------------------------------ SceAudioenc / SceG729

    /// <summary>Allocates a SceAudioencCtrl with its SceAudioencInfoCelp, PCM/ES buffers and SceAudioencOptInfoCelp.</summary>
    uint NewEncCtrl(uint bitRate)
    {
        uint c = fx.Memory.Alloc(0x2000, "audioenc ctrl");
        GuestMemory.Fill(c, 0, 0x2000);
        uint info = c + 0x40, opt = c + 0x80, pcm = c + 0x400, es = c + 0x1000;
        GuestMemory.W32(c, 0x2C);
        GuestMemory.W32(c + 8, pcm);
        GuestMemory.W32(c + 0x14, es);
        GuestMemory.W32(c + 0x20, 16);
        GuestMemory.W32(c + 0x24, info);
        GuestMemory.W32(c + 0x28, opt);
        GuestMemory.W32(info, 0x10);
        GuestMemory.W32(info + 8, 8000);
        GuestMemory.W32(info + 12, bitRate);
        GuestMemory.W32(opt, 0x2C);
        return c;
    }

    [Fact]
    public void AudioencCelpContextSizesAndFrames()
    {
        var a = new Audioenc();
        uint init = fx.Memory.Alloc(0x10, "audioenc init");
        GuestMemory.W32(init, 8);
        GuestMemory.W32(init + 4, 1);
        uint ctrl = NewEncCtrl(12200);
        Assert.Equal(Audioenc.ERR_NOT_INITIALIZED, Call(a, "sceAudioencCreateEncoder", ctrl, Audioenc.TYPE_CELP));
        Assert.Equal(Audioenc.ERR_INVALID_TYPE, Call(a, "sceAudioencInitLibrary", 0x1006u, init));
        Assert.Equal(Audioenc.ERR_INVALID_PTR, Call(a, "sceAudioencInitLibrary", Audioenc.TYPE_CELP, 0u));
        GuestMemory.W32(init + 4, 2);
        Assert.Equal(Audioenc.ERR_INVALID_INIT_PARAM, Call(a, "sceAudioencInitLibrary", Audioenc.TYPE_CELP, init));
        GuestMemory.W32(init + 4, 1);
        Assert.Equal(0, Call(a, "sceAudioencInitLibrary", Audioenc.TYPE_CELP, init));
        Assert.Equal(Audioenc.ERR_ALREADY_INITIALIZED, Call(a, "sceAudioencInitLibrary", Audioenc.TYPE_CELP, init));

        // validation of ctrl / info
        GuestMemory.W32(ctrl, 0x28);
        Assert.Equal(Audioenc.ERR_INVALID_SIZE, Call(a, "sceAudioencCreateEncoder", ctrl, Audioenc.TYPE_CELP));
        GuestMemory.W32(ctrl, 0x2C);
        GuestMemory.W32(ctrl + 0x20, 24);
        Assert.Equal(Audioenc.ERR_INVALID_WORD_LENGTH, Call(a, "sceAudioencCreateEncoder", ctrl, Audioenc.TYPE_CELP));
        GuestMemory.W32(ctrl + 0x20, 16);
        GuestMemory.W32(GuestMemory.R32(ctrl + 0x24) + 12, 12345);
        Assert.Equal(Audioenc.ERR_CELP_INVALID_CONFIG, Call(a, "sceAudioencCreateEncoder", ctrl, Audioenc.TYPE_CELP));
        GuestMemory.W32(GuestMemory.R32(ctrl + 0x24) + 12, 12200);

        Assert.Equal(0, Call(a, "sceAudioencCreateEncoder", ctrl, Audioenc.TYPE_CELP));
        Assert.Equal((160u, 16u), (GuestMemory.R32(ctrl + 0x10), GuestMemory.R32(ctrl + 0x1C)));
        Assert.Equal(Audioenc.ERR_ALL_HANDLES_IN_USE, Call(a, "sceAudioencCreateEncoder", NewEncCtrl(3850), Audioenc.TYPE_CELP));
        uint es = GuestMemory.R32(ctrl + 0x14);
        GuestMemory.Fill(es, 0xAA, 64);
        Assert.Equal(0, Call(a, "sceAudioencEncode", ctrl));
        Assert.Equal((160u, 16u), (GuestMemory.R32(ctrl + 0x0C), GuestMemory.R32(ctrl + 0x18)));
        Assert.Equal(0, GuestMemory.Read<byte>(es + 15));
        Assert.Equal(0xAA, GuestMemory.Read<byte>(es + 16)); // nothing past the frame
        Assert.Equal(0, Call(a, "sceAudioencEncodeNFrames", ctrl, 3u));
        Assert.Equal((480u, 48u), (GuestMemory.R32(ctrl + 0x0C), GuestMemory.R32(ctrl + 0x18)));
        Assert.Equal(0, Call(a, "sceAudioencGetOptInfo", ctrl));
        Assert.Equal(0u, GuestMemory.R32(GuestMemory.R32(ctrl + 0x28) + 0x24)); // headerSize
        uint ie = ctrl + 0x1F00;
        GuestMemory.W32(ie, 5);
        Assert.Equal(0, Call(a, "sceAudioencGetInternalError", ctrl, ie));
        Assert.Equal(0u, GuestMemory.R32(ie));
        Assert.Equal(0, Call(a, "sceAudioencClearContext", ctrl));
        Assert.Equal(Audioenc.ERR_A_HANDLE_IN_USE, Call(a, "sceAudioencTermLibrary", Audioenc.TYPE_CELP));

        // External encoders need no library slot and give their context back on delete
        uint ext = NewEncCtrl(5700);
        Assert.Equal(0x10000, Call(a, "sceAudioencGetContextSize", ext, Audioenc.TYPE_CELP));
        Assert.Equal(Audioenc.ERR_INVALID_SIZE, Call(a, "sceAudioencCreateEncoderExternal", ext, Audioenc.TYPE_CELP, 0x8100_0000u, 0x100u));
        Assert.Equal(0, Call(a, "sceAudioencCreateEncoderExternal", ext, Audioenc.TYPE_CELP, 0x8100_0000u, 0x10000u));
        Assert.Equal((320u, 15u), (GuestMemory.R32(ext + 0x10), GuestMemory.R32(ext + 0x1C)));
        Assert.Equal(0, Call(a, "sceAudioencDeleteEncoderExternal", ext, ie));
        Assert.Equal(0x8100_0000u, GuestMemory.R32(ie));
        Assert.Equal(Audioenc.ERR_NOT_HANDLE_IN_USE, Call(a, "sceAudioencEncode", ext));

        Assert.Equal(0, Call(a, "sceAudioencDeleteEncoder", ctrl));
        Assert.Equal(Audioenc.ERR_NOT_HANDLE_IN_USE, Call(a, "sceAudioencDeleteEncoder", ctrl));
        GuestMemory.W32(ctrl + 4, 0);
        Assert.Equal(Audioenc.ERR_INVALID_HANDLE, Call(a, "sceAudioencEncode", ctrl));
        Assert.Equal(0, Call(a, "sceAudioencTermLibrary", Audioenc.TYPE_CELP));
        Assert.Equal(Audioenc.ERR_NOT_INITIALIZED, Call(a, "sceAudioencTermLibrary", Audioenc.TYPE_CELP));
    }

    [Theory]
    [InlineData(3850u, 320, 20)]
    [InlineData(4650u, 320, 24)]
    [InlineData(5700u, 160, 15)]
    [InlineData(7300u, 160, 19)]
    [InlineData(12200u, 80, 16)]
    public void AudioencFrameTableMatchesVoiceHeader(uint bitRate, int samples, int bytes) =>
        Assert.Equal((samples, bytes), Audioenc.Frame(bitRate));

    [Fact]
    public void G729ContextLifecycle()
    {
        var g = new G729();
        uint ctx = fx.Memory.Alloc(0x2000, "g729"), io = ctx + 0x1000;
        Assert.Equal(G729.ContextSize, Call(g, "sceG729DecodeGetContextSize", 0u));
        Assert.Equal(G729.ContextSize, Call(g, "sceG729EncodeGetContextSize", 0u));
        Assert.Equal(G729.ERR_INVALID_ARGUMENT, Call(g, "sceG729EncodeCore", ctx, io, io + 0x200));
        Assert.Equal(G729.ERR_INVALID_ARGUMENT, Call(g, "sceG729EncodeInit", 0u, 0u));
        Assert.Equal(0, Call(g, "sceG729EncodeInit", ctx, 0u));
        GuestMemory.Fill(io + 0x200, 0xAA, 16);
        Assert.Equal(0, Call(g, "sceG729EncodeCore", ctx, io, io + 0x200));
        Assert.Equal(0, GuestMemory.Read<byte>(io + 0x209));
        Assert.Equal(0xAA, GuestMemory.Read<byte>(io + 0x20A));
        Assert.Equal(0, Call(g, "sceG729EncodeReset", ctx));
        Assert.Equal(G729.ERR_INVALID_ARGUMENT, Call(g, "sceG729DecodeReset", ctx));
        Assert.Equal(0, Call(g, "sceG729DecodeInit", ctx));
        Assert.Equal(0, Call(g, "sceG729DecodeCore", ctx, io + 0x200, io + 0x400));
        Assert.Equal(0, Call(g, "sceG729DecodeReset", ctx));
    }
}
