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
}
