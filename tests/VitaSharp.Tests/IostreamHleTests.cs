using System.Reflection;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// SceLibstdcxx iostreams of the HLE C library (libc.suprx 3.74's abridged library): ios_base::Init / _Winit build
/// the standard streams, basic_filebuf reads and writes through the C stdio FILE (and converts wchar_t to UTF-8),
/// the _ctype/_codecvt facets and the ios_base helpers; everything runs on a guest thread through HLE stubs.
/// </summary>
[Collection("guest")]
public sealed class IostreamHleTests(GuestMemoryFixture fx)
{
    sealed class World : IDisposable
    {
        public readonly Emulator Emu;
        public readonly Libc Libc;
        public readonly string Root = Path.Combine(Path.GetTempPath(), "vitasharp-ios-" + Guid.NewGuid().ToString("N"));
        readonly uint _stubs;
        uint _nextStub;
        readonly Dictionary<uint, uint> _stubAt = [];

        public World(GuestMemoryFixture fx)
        {
            Directory.CreateDirectory(Path.Combine(Root, "ux0", "data"));
            Emu = new Emulator(new EmulatorOptions { FsRoot = Root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
            Libc = Emu.Hle.Modules.OfType<Libc>().Single();
            _stubs = Emu.Memory.Alloc(0x2000, "ios test stubs");
            _nextStub = _stubs;
        }

        public uint Stub(string name, string library = "SceLibstdcxx")
        {
            uint nid = NidDatabase.LookupName(name).Single(n => n.Library == library).Nid;
            if (_stubAt.TryGetValue(nid, out uint a))
            {
                return a;
            }

            a = _nextStub;
            _nextStub += 16;
            Linker.WriteHleStub(a, Emu.Hle.GetHleFunctionIndex(nid, 0, library), nid);
            Emu.Code.Invalidate(a, 16);
            return _stubAt[nid] = a;
        }

        /// <summary>Calls a guest function (an HLE stub or a vtable slot) on a guest thread.</summary>
        public uint Run(uint fn, params uint[] args)
        {
            uint result = 0;
            Exception? failure = null;
            var th = Emu.Kernel.CreateThread("ios-test", 0, 0x10000100, 0x10000, 0, 0x70000);
            th.HostBody = t =>
            {
                try
                {
                    result = t.CallGuest(fn, args);
                } // HLE stubs are ARM code
                catch (Exception e)
                {
                    failure = e;
                }
            };
            th.Start(0, 0);
            Assert.True(th.WaitForExit(60_000), "test thread did not finish");
            Emu.Kernel.DeleteThread(th);
            if (failure != null)
            {
                throw new Xunit.Sdk.XunitException(failure.ToString());
            }

            return result;
        }

        public uint Call(string name, params uint[] args) => Run(Stub(name), args);
        public uint CallC(string name, params uint[] args) => Run(Stub(name, "SceLibc"), args);

        /// <summary>The virtual function at byte offset <paramref name="off"/> of the object's vtable.</summary>
        public uint Virtual(uint obj, uint off, params uint[] args) => Run(GuestMemory.R32(GuestMemory.R32(obj) + off), [obj, .. args]);

        public uint Alloc(uint n)
        {
            uint p = Emu.Memory.Alloc(n, "ios test");
            GuestMemory.Fill(p, 0, n);
            return p;
        }
        public uint Str(string s)
        {
            uint p = Alloc((uint)s.Length + 1);
            GuestMemory.WriteCString(p, s);
            return p;
        }

        public T Invoke<T>(string method, params object[] args) =>
            (T)typeof(Libc).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Libc, args)!;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException) { }
        }
    }

    const uint VtFilebufC = 0xC3233C50, VtFilebufW = 0xF269A8EB, VtCodecvtC = 0x9B3E5D29, VtCodecvtW = 0x95405266,
        VtCtypeC = 0x214E1395, VtCtypeW = 0x798E7C29, VtStreambufC = 0x05548FF7;

    [Fact]
    public void InitBuildsTheStandardStreamsOnce()
    {
        using var w = new World(fx);
        uint obj = w.Alloc(4);
        uint count = w.Emu.Hle.GetHleVariable(0x554B7625);
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(count));
        w.Call("_ZNSt8ios_base4InitC1Ev", obj);
        w.Call("_ZNSt8ios_base4InitC1Ev", obj);
        Assert.Equal(2u, GuestMemory.R32(count));
        uint cin = w.Emu.Hle.GetHleVariable(0x768F2944), cout = w.Emu.Hle.GetHleVariable(0xB3867BB4),
            cerr = w.Emu.Hle.GetHleVariable(0x14B35A37), clog = w.Emu.Hle.GetHleVariable(0xD361A52A);
        // the basic_ios parts: cin's at +8, the ostreams' at +4 (the vbase offset in each vtable)
        Assert.Equal(8u, GuestMemory.R32(GuestMemory.R32(cin) - 12));
        Assert.Equal(4u, GuestMemory.R32(GuestMemory.R32(cout) - 12));
        uint iIn = cin + 8, iOut = cout + 4, iErr = cerr + 4, iLog = clog + 4;
        Assert.Equal(new[] { 1u, 2u, 3u, 4u }, new[] { iIn, iOut, iErr, iLog }.Select(i => GuestMemory.R32(i + 4)));
        Assert.All(new[] { iIn, iOut, iErr, iLog }, i =>
        {
            Assert.Equal(0u, GuestMemory.R32(i + 8));        // good
            Assert.Equal(6u, GuestMemory.R32(i + 0x14));     // precision
            Assert.Equal((byte)' ', GuestMemory.Read<byte>(i + 0x2C));
            Assert.Equal(w.Libc.Rtti.Vptr(VtFilebufC), GuestMemory.R32(GuestMemory.R32(i + 0x24)));
        });
        Assert.Equal(cout, GuestMemory.R32(iIn + 0x28));
        Assert.Equal(cout, GuestMemory.R32(iErr + 0x28));
        Assert.Equal(cout, GuestMemory.R32(iLog + 0x28));
        Assert.Equal(0u, GuestMemory.R32(iOut + 0x28));
        Assert.Equal(0x203u, GuestMemory.R32(iErr + 0x10)); // skipws | dec | unitbuf
        Assert.Equal(0x201u, GuestMemory.R32(iLog + 0x10));
        // cerr and clog share the stderr filebuf; cin, cout and cerr each have their own FILE
        Assert.Equal(GuestMemory.R32(iErr + 0x24), GuestMemory.R32(iLog + 0x24));
        var files = new[] { iIn, iOut, iErr }.Select(i => GuestMemory.R32(GuestMemory.R32(i + 0x24) + 0x8C)).ToArray();
        Assert.Equal(3, files.Distinct().Count());
        Assert.DoesNotContain(0u, files);
        uint fb = GuestMemory.R32(iOut + 0x24);
        Assert.Equal(w.Libc.Rtti.Vptr(VtCodecvtC), GuestMemory.R32(fb + 0x78));
        Assert.Equal((byte)0, GuestMemory.Read<byte>(fb + 0x88)); // _Closef: the filebuf does not own stdout
        // a character written through cout's buffer goes out through overflow (the areas stay empty)
        Assert.Equal((uint)'x', w.Virtual(fb, 0x10, 'x'));
        w.Call("_ZNSt8ios_base4InitD1Ev", obj);
        w.Call("_ZNSt8ios_base4InitD1Ev", obj);
        Assert.Equal(0u, GuestMemory.R32(count));
        Assert.Equal(0u, GuestMemory.R32(iOut + 8)); // the final flush (pubsync) succeeded
    }

    [Fact]
    public void WinitBuildsTheWideStreams()
    {
        using var w = new World(fx);
        uint obj = w.Alloc(4);
        w.Call("_ZNSt6_WinitC1Ev", obj);
        uint wcin = w.Emu.Hle.GetHleVariable(0x8054BC8E), wcout = w.Emu.Hle.GetHleVariable(0x4F726424),
            wcerr = w.Emu.Hle.GetHleVariable(0x8B2001F7);
        uint fb = GuestMemory.R32(wcout + 4 + 0x24);
        Assert.Equal(w.Libc.Rtti.Vptr(VtFilebufW), GuestMemory.R32(fb));
        Assert.Equal(w.Libc.Rtti.Vptr(VtCodecvtW), GuestMemory.R32(fb + 0x78));
        Assert.Equal((ushort)' ', GuestMemory.Read<ushort>(wcout + 4 + 0x2C));
        Assert.Equal(wcout, GuestMemory.R32(wcin + 8 + 0x28));
        Assert.Equal(wcout, GuestMemory.R32(wcerr + 4 + 0x28));
        Assert.Equal(1u, GuestMemory.R32(w.Emu.Hle.GetHleVariable(0x8137F8D7)));
        w.Call("_ZNSt6_WinitD1Ev", obj);
        Assert.Equal(0u, GuestMemory.R32(w.Emu.Hle.GetHleVariable(0x8137F8D7)));
    }

    /// <summary>A basic_filebuf over a file opened with fopen, the way the inline constructor and open() set it up
    /// (_Closef: the filebuf closes it).</summary>
    uint OpenFilebuf(World w, string name, string mode, bool wide)
    {
        uint file = w.CallC("fopen", w.Str("ux0:data/" + name), w.Str(mode));
        Assert.NotEqual(0u, file);
        uint fb = w.Alloc(0x90);
        typeof(Libc).GetMethod("FilebufCtor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w.Libc, [fb, file, wide]);
        GuestMemory.Write(fb + 0x88, (byte)1);
        return fb;
    }

    [Fact]
    public void FilebufWritesThroughTheFileAndClosesIt()
    {
        using var w = new World(fx);
        uint fb = OpenFilebuf(w, "out.txt", "w", false);
        uint text = w.Str("hello, stream\n");
        Assert.Equal(14u, w.Virtual(fb, 0x28, text, 14));          // xsputn
        Assert.Equal((uint)'!', w.Virtual(fb, 0x10, '!'));         // overflow
        Assert.Equal(0u, w.Virtual(fb, 0x10, 0xFFFFFFFF));         // overflow(eof) = not_eof
        Assert.Equal(0u, w.Virtual(fb, 0x38));                     // sync
        w.Call("_ZNSt13basic_filebufIcSt11char_traitsIcEED1Ev", fb);
        Assert.Equal(0u, GuestMemory.R32(fb + 0x8C));
        Assert.Equal(w.Libc.Rtti.Vptr(VtStreambufC), GuestMemory.R32(fb));
        Assert.Equal("hello, stream\n!", File.ReadAllText(Path.Combine(w.Root, "ux0", "data", "out.txt")));
    }

    [Fact]
    public void FilebufReadsPutsBackAndSeeks()
    {
        using var w = new World(fx);
        File.WriteAllText(Path.Combine(w.Root, "ux0", "data", "in.txt"), "abcdef");
        uint fb = OpenFilebuf(w, "in.txt", "r", false);
        Assert.Equal((uint)'a', w.Virtual(fb, 0x1C));              // underflow peeks
        Assert.Equal((uint)'a', w.Virtual(fb, 0x20));              // uflow takes it
        Assert.Equal((uint)'b', w.Virtual(fb, 0x20));
        Assert.Equal((uint)'z', w.Virtual(fb, 0x14, 'z'));         // pbackfail pushes back into the file
        Assert.Equal((uint)'z', w.Virtual(fb, 0x20));
        uint buf = w.Alloc(16);
        Assert.Equal(4u, w.Virtual(fb, 0x24, buf, 10));            // xsgetn to the end
        Assert.Equal("cdef", GuestMemory.ReadCString(buf));
        Assert.Equal(0xFFFFFFFFu, w.Virtual(fb, 0x20));            // eof
        uint pos = w.Alloc(0x20);
        w.Call("_ZNSt13basic_filebufIcSt11char_traitsIcEE7seekoffElNSt5_IosbIiE8_SeekdirENS4_9_OpenmodeE", pos, fb, 1, 0, 8);
        Assert.Equal(0u, GuestMemory.R32(pos));
        Assert.Equal(1L, GuestMemory.Read<long>(pos + 8));
        Assert.Equal((uint)'b', w.Virtual(fb, 0x20));
        w.Call("_ZNSt13basic_filebufIcSt11char_traitsIcEED1Ev", fb);
    }

    [Fact]
    public void WideFilebufConvertsToUtf8()
    {
        using var w = new World(fx);
        uint fb = OpenFilebuf(w, "wide.txt", "w", true);
        foreach (char c in "aé€")
        {
            Assert.Equal((uint)c, w.Virtual(fb, 0x10, c));
        }

        Assert.Equal((byte)1, GuestMemory.Read<byte>(fb + 0x7E)); // _Wrotesome
        w.Call("_ZNSt13basic_filebufIwSt11char_traitsIwEED1Ev", fb);
        Assert.Equal("aé€", File.ReadAllText(Path.Combine(w.Root, "ux0", "data", "wide.txt"), Encoding.UTF8));

        fb = OpenFilebuf(w, "wide.txt", "r", true);
        Assert.Equal((uint)'a', w.Virtual(fb, 0x20));
        Assert.Equal((uint)'é', w.Virtual(fb, 0x20));
        Assert.Equal((uint)'€', w.Virtual(fb, 0x1C)); // underflow puts it back
        Assert.Equal((uint)'€', w.Virtual(fb, 0x20));
        Assert.Equal(0xFFFFFFFFu, w.Virtual(fb, 0x20));
        w.Call("_ZNSt13basic_filebufIwSt11char_traitsIwEED1Ev", fb);
    }

    [Fact]
    public void FacetsOfTheCLocale()
    {
        using var w = new World(fx);
        uint ct = w.Alloc(4), wct = w.Alloc(4), cvt = w.Alloc(4);
        GuestMemory.W32(ct, w.Libc.Rtti.Vptr(VtCtypeC));
        GuestMemory.W32(wct, w.Libc.Rtti.Vptr(VtCtypeW));
        GuestMemory.W32(cvt, w.Libc.Rtti.Vptr(VtCodecvtW));
        Assert.Equal((uint)'a', w.Virtual(ct, 8, 'A'));            // do_tolower
        Assert.Equal((uint)'Q', w.Virtual(ct, 0x10, 'q'));         // do_toupper
        uint s = w.Str("MiXeD");
        Assert.Equal(s + 5, w.Virtual(ct, 0xC, s, s + 5));
        Assert.Equal("mixed", GuestMemory.ReadCString(s));
        // _ctype<wchar_t>: do_is(digit 0x20, '7'), scan_is(upper 0x02), widen/narrow, tolower
        Assert.Equal(1u, w.Virtual(wct, 8, 0x20, '7'));
        Assert.Equal(0u, w.Virtual(wct, 8, 0x20, 'x'));
        uint ws = w.Alloc(16);
        foreach (var (c, i) in "abCd".Select((c, i) => (c, i)))
        {
            GuestMemory.Write(ws + (uint)i * 2, (ushort)c);
        }

        Assert.Equal(ws + 4, w.Virtual(wct, 0x10, 0x02, ws, ws + 8));
        Assert.Equal((uint)'z', w.Virtual(wct, 0x18, 'Z'));
        Assert.Equal((uint)'A', w.Virtual(wct, 0x28, 'A'));        // widen
        Assert.Equal((uint)'?', w.Virtual(wct, 0x30, 0x20AC, '?')); // narrow of a character that is not one byte
        // _codecvt<wchar_t, char>: do_out then do_in round trip, do_length
        uint st = w.Alloc(8), from = w.Alloc(8), next = w.Alloc(8), to = w.Alloc(16), tnext = w.Alloc(4);
        GuestMemory.Write(from, (ushort)'h');
        GuestMemory.Write(from + 2, (ushort)0x20AC);
        Assert.Equal(0u, w.Virtual(cvt, 0x18, st, from, from + 4, next, to, to + 16, tnext));
        Assert.Equal(to + 4, GuestMemory.R32(tnext));
        Assert.Equal(new byte[] { (byte)'h', 0xE2, 0x82, 0xAC }, GuestMemory.ReadSpan(to, 4).ToArray());
        Assert.Equal(2u, w.Virtual(cvt, 0x20, st, to, to + 4, 10));
        uint back = w.Alloc(8);
        Assert.Equal(0u, w.Virtual(cvt, 0x14, st, to, to + 4, next, back, back + 8, tnext));
        Assert.Equal(back + 4, GuestMemory.R32(tnext));
        Assert.Equal((ushort)0x20AC, GuestMemory.Read<ushort>(back + 2));
        // an output too small for the next character stops before it (partial when nothing was converted)
        Assert.Equal(1u, w.Virtual(cvt, 0x18, st, from + 2, from + 4, next, to, to + 2, tnext));
        Assert.Equal(to, GuestMemory.R32(tnext));
    }

    [Fact]
    public void IosBaseArraysCallbacksAndCopyfmt()
    {
        using var w = new World(fx);
        uint a = w.Alloc(0x30), b = w.Alloc(0x30);
        w.Call("_ZNSt8ios_base5_InitEv", a);
        w.Call("_ZNSt8ios_base5_InitEv", b);
        uint n = w.Call("_ZNSt8ios_base8_FindarrEi", a, 5);
        Assert.Equal(n, w.Call("_ZNSt8ios_base8_FindarrEi", a, 5));
        GuestMemory.W32(n + 8, 77);
        GuestMemory.W32(a + 0x10, 0x4801);
        GuestMemory.W32(a + 0x14, 3);
        GuestMemory.W32(a + 0xC, 0xFF);
        w.Call("_ZNSt8ios_base7copyfmtERKS_", b, a);
        Assert.Equal(0x4801u, GuestMemory.R32(b + 0x10));
        Assert.Equal(3u, GuestMemory.R32(b + 0x14));
        Assert.Equal(0x17u, GuestMemory.R32(b + 0xC)); // the exception mask, valid bits only
        uint m = w.Call("_ZNSt8ios_base8_FindarrEi", b, 5);
        Assert.NotEqual(n, m);
        Assert.Equal(77u, GuestMemory.R32(m + 8));
        w.Call("_ZNSt8ios_base5_TidyEv", b);
        Assert.Equal(0u, GuestMemory.R32(b + 0x1C));
    }

    [Fact]
    public void StrstreamBuffersGrowSeekAndFreeze()
    {
        using var w = new World(fx);
        // ostrstream() over a dynamic buffer: 100 characters make it grow from 32 bytes
        uint os = w.Alloc(0xC8);
        w.Call("_ZNSt10ostrstreamC1EPciNSt5_IosbIiE9_OpenmodeE", os, 0, 0, 2);
        uint sb = os + 8;
        Assert.Equal(sb, GuestMemory.R32(os + 0x98 + 0x24)); // basic_ios::rdbuf
        string text = string.Concat(Enumerable.Range(0, 100).Select(i => (char)('a' + i % 26)));
        uint src = w.Str(text);
        Assert.Equal(100u, w.Virtual(sb, 0x28, src, 100));          // xsputn through overflow
        Assert.Equal(5u, GuestMemory.R32(sb + 0x84));               // _Dynamic | _Allocated
        Assert.Equal((uint)'a', w.Virtual(sb, 0x1C));               // underflow sees what was written
        uint buf = w.Alloc(128);
        Assert.Equal(100u, w.Virtual(sb, 0x24, buf, 128));
        Assert.Equal(text, GuestMemory.ReadCString(buf));
        uint pos = w.Alloc(0x20);
        w.Call("_ZNSt12strstreambuf7seekoffElNSt5_IosbIiE8_SeekdirENS1_9_OpenmodeE", pos, sb, 10, 0, 1);
        Assert.Equal(10u, GuestMemory.R32(pos));
        Assert.Equal((uint)'k', w.Virtual(sb, 0x20));               // uflow at offset 10
        w.Call("_ZNSt12strstreambuf7seekoffElNSt5_IosbIiE8_SeekdirENS1_9_OpenmodeE", pos, sb, 200, 0, 1);
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(pos));           // past _Seekhigh
        w.Call("_ZNSt12strstreambuf6freezeEb", sb, 1);
        Assert.Equal(0xFFFFFFFFu, w.Virtual(sb, 0x10, 'z'));        // a frozen buffer takes no more
        w.Call("_ZNSt12strstreambuf6freezeEb", sb, 0);
        Assert.Equal((uint)'z', w.Virtual(sb, 0x10, 'z'));
        w.Call("_ZNSt10ostrstreamD1Ev", os);

        // ostrstream(buf, n, app) over a fixed array writes after its string, and stops at its end
        uint fixedBuf = w.Alloc(8);
        GuestMemory.WriteCString(fixedBuf, "ab");
        w.Call("_ZNSt10ostrstreamC1EPciNSt5_IosbIiE9_OpenmodeE", os, fixedBuf, 6, 0xA);
        Assert.Equal(4u, w.Virtual(sb, 0x28, w.Str("cdefgh"), 6));
        Assert.Equal("abcdef", GuestMemory.ReadCString(fixedBuf));
        w.Call("_ZNSt10ostrstreamD1Ev", os);

        // strstream: the iostream parts share the embedded buffer
        uint ss = w.Alloc(0xD0);
        w.Call("_ZNSt9strstreamC1EPciNSt5_IosbIiE9_OpenmodeE", ss, 0, 0, 3);
        Assert.Equal(0xA0u, GuestMemory.R32(GuestMemory.R32(ss) - 12));
        Assert.Equal(ss + 0x10, GuestMemory.R32(ss + 0xA0 + 0x24));
        Assert.Equal((uint)'q', w.Virtual(ss + 0x10, 0x10, 'q'));
        Assert.Equal((uint)'q', w.Virtual(ss + 0x10, 0x20));
        w.Call("_ZNSt9strstreamD1Ev", ss);
    }
}
