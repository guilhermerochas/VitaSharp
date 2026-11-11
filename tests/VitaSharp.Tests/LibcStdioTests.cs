using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>SceLibc streams and the Annex K pieces the other libc tests leave out, called through guest stubs on a
/// guest thread as a game calls them: line reads (fgets, fgetws) and their end-of-file and error flags, freopen onto
/// another file, strtok_s's remaining-length bookkeeping, strerror_s truncation and qsort / bsearch with a guest
/// comparator (C11 / the Dinkumware library the Vita ships).</summary>
[Collection("guest")]
public sealed class LibcStdioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-libcio-" + Guid.NewGuid().ToString("N"));
    private readonly Emulator _emu;
    private uint _next;
    private readonly Dictionary<string, uint> _at = [];

    public LibcStdioTests(GuestMemoryFixture fixture)
    {
        Directory.CreateDirectory(Path.Combine(_root, "ux0", "data"));
        _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fixture.Memory, EnableJit = false, CpuBackend = "interp" });
        _next = _emu.Memory.Alloc(0x2000, "libc io stubs");
    }

    public void Dispose()
    {
        _emu.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    private uint Stub(string name)
    {
        if (_at.TryGetValue(name, out uint a))
        {
            return a;
        }

        uint nid = NidDatabase.LookupName(name).Single(n => n.Library == "SceLibc").Nid;
        a = _next;
        _next += 16;
        Linker.WriteHleStub(a, _emu.Hle.GetHleFunctionIndex(nid, 0, "SceLibc"), nid);
        _emu.Code.Invalidate(a, 16);
        return _at[name] = a;
    }

    private uint HostStub(string name, Action<HleContext> body)
    {
        uint a = _next;
        _next += 16;
        Linker.WriteHleStub(a, _emu.Hle.RegisterHostFunction(name, body), 0);
        _emu.Code.Invalidate(a, 16);
        return a;
    }

    private uint Run(uint fn, params uint[] args)
    {
        uint result = 0;
        Exception? failure = null;
        GuestThread th = _emu.Kernel.CreateThread("libc-io-test", 0, 0x10000100, 0x10000, 0, 0x70000);
        th.HostBody = t =>
        {
            try
            {
                result = t.CallGuest(fn, args);
            }
            catch (Exception e)
            {
                failure = e;
            }
        };
        th.Start(0, 0);
        Assert.True(th.WaitForExit(60_000), "test thread did not finish");
        _emu.Kernel.DeleteThread(th);
        if (failure != null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }

        return result;
    }

    private uint C(string name, params uint[] args) => Run(Stub(name), args);
    private uint Alloc(uint n)
    {
        uint p = _emu.Memory.Alloc(n, "libc io test");
        GuestMemory.Fill(p, 0, n);
        return p;
    }
    private uint Str(string s)
    {
        uint p = Alloc((uint)s.Length + 1);
        GuestMemory.WriteCString(p, s);
        return p;
    }
    private void File(string name, string text) => System.IO.File.WriteAllText(Path.Combine(_root, "ux0", "data", name), text);

    [Fact]
    public void FgetsReadsLineByLineAndSetsEndOfFile()
    {
        File("lines.txt", "first\nsecond line\nend");
        uint f = C("fopen", Str("ux0:data/lines.txt"), Str("r")), buf = Alloc(64);
        Assert.NotEqual(0u, f);
        Assert.Equal(buf, C("fgets", buf, 64, f));
        Assert.Equal("first\n", GuestMemory.ReadCString(buf));
        // a buffer of n holds n - 1 characters and the NUL: the rest of the line comes with the next call
        Assert.Equal(buf, C("fgets", buf, 5, f));
        Assert.Equal("seco", GuestMemory.ReadCString(buf));
        Assert.Equal(buf, C("fgets", buf, 64, f));
        Assert.Equal("nd line\n", GuestMemory.ReadCString(buf));
        Assert.Equal(0u, C("feof", f));
        // the last line has no newline: it is returned and the end of file is flagged
        Assert.Equal(buf, C("fgets", buf, 64, f));
        Assert.Equal("end", GuestMemory.ReadCString(buf));
        Assert.NotEqual(0u, C("feof", f));
        // nothing left: NULL, the buffer untouched
        GuestMemory.WriteCString(buf, "keep");
        Assert.Equal(0u, C("fgets", buf, 64, f));
        Assert.Equal("keep", GuestMemory.ReadCString(buf));
        Assert.Equal(0u, C("ferror", f));
        Assert.Equal(0u, C("fclose", f));
    }

    [Fact]
    public void FgetwsReadsWideLines()
    {
        File("wide.txt", "ab\ncd");
        uint f = C("fopen", Str("ux0:data/wide.txt"), Str("r")), buf = Alloc(64);
        Assert.Equal(buf, C("fgetws", buf, 16, f));
        Assert.Equal(new ushort[] { 'a', 'b', '\n', 0 }, Enumerable.Range(0, 4).Select(i => GuestMemory.Read<ushort>(buf + (uint)i * 2)).ToArray());
        Assert.Equal(buf, C("fgetws", buf, 16, f));
        Assert.Equal(new ushort[] { 'c', 'd', 0 }, Enumerable.Range(0, 3).Select(i => GuestMemory.Read<ushort>(buf + (uint)i * 2)).ToArray());
        Assert.Equal(0u, C("fgetws", buf, 16, f));
        Assert.NotEqual(0u, C("feof", f));
        C("fclose", f);
    }

    [Fact]
    public void FreopenMovesTheStreamToAnotherFile()
    {
        File("a.txt", "from a");
        File("b.txt", "from b");
        uint f = C("fopen", Str("ux0:data/a.txt"), Str("r")), buf = Alloc(32);
        Assert.Equal(buf, C("fgets", buf, 32, f));
        Assert.Equal("from a", GuestMemory.ReadCString(buf));
        Assert.NotEqual(0u, C("feof", f));
        // the same FILE now reads the other file from its start, with the end-of-file flag cleared
        Assert.Equal(f, C("freopen", Str("ux0:data/b.txt"), Str("r"), f));
        Assert.Equal(0u, C("feof", f));
        Assert.Equal(buf, C("fgets", buf, 32, f));
        Assert.Equal("from b", GuestMemory.ReadCString(buf));
        // writing mode on the reopened stream: the file is truncated and written
        Assert.Equal(f, C("freopen", Str("ux0:data/b.txt"), Str("w"), f));
        Assert.Equal(0u, C("fputs", Str("new"), f));
        Assert.Equal(0u, C("fclose", f));
        Assert.Equal("new", System.IO.File.ReadAllText(Path.Combine(_root, "ux0", "data", "b.txt")));
        // a missing file: NULL and the stream is closed
        uint g = C("fopen", Str("ux0:data/a.txt"), Str("r"));
        Assert.Equal(0u, C("freopen", Str("ux0:data/missing.txt"), Str("r"), g));
        // an invalid mode
        uint h = C("fopen", Str("ux0:data/a.txt"), Str("r"));
        Assert.Equal(0u, C("freopen", Str("ux0:data/a.txt"), Str("q"), h));
        // freopen_s: the result through the pointer; NULL arguments are constraint violations
        uint res = Alloc(4), k = C("fopen", Str("ux0:data/a.txt"), Str("r"));
        Assert.Equal(0u, C("freopen_s", res, Str("ux0:data/b.txt"), Str("r"), k));
        Assert.Equal(k, GuestMemory.R32(res));
        Assert.NotEqual(0u, C("freopen_s", 0, Str("ux0:data/b.txt"), Str("r"), k));
        C("fclose", k);
    }

    [Fact]
    public void StrtokSCountsTheCharactersLeft()
    {
        // C11 K.3.7.3.1: *s1max is the length still to scan; each call takes away what it consumed
        uint s = Str("ab,,cd,e"), delim = Str(","), max = Alloc(4), save = Alloc(4);
        GuestMemory.W32(max, 8);
        uint t1 = C("strtok_s", s, max, delim, save);
        Assert.Equal("ab", GuestMemory.ReadCString(t1));
        Assert.Equal(5u, GuestMemory.R32(max));
        uint t2 = C("strtok_s", 0, max, delim, save);
        Assert.Equal("cd", GuestMemory.ReadCString(t2));
        Assert.Equal(1u, GuestMemory.R32(max));
        uint t3 = C("strtok_s", 0, max, delim, save);
        Assert.Equal("e", GuestMemory.ReadCString(t3));
        Assert.Equal(0u, C("strtok_s", 0, max, delim, save));
        // a NULL length, delimiter or context pointer, or a first call without a string: NULL
        Assert.Equal(0u, C("strtok_s", s, 0, delim, save));
        Assert.Equal(0u, C("strtok_s", s, max, 0, save));
        Assert.Equal(0u, C("strtok_s", s, max, delim, 0));
        GuestMemory.W32(save, 0);
        Assert.Equal(0u, C("strtok_s", 0, max, delim, save));
    }

    [Fact]
    public void StrerrorSTruncatesWithAnEllipsis()
    {
        const int ERANGE = 34, ENOENT = 2;
        uint big = Alloc(128), small = Alloc(8);
        string full = GuestMemory.ReadCString(C("strerror", ENOENT));
        Assert.Equal((uint)full.Length, C("strerrorlen_s", ENOENT));
        Assert.Equal(0u, C("strerror_s", big, 128, ENOENT));
        Assert.Equal(full, GuestMemory.ReadCString(big));
        // K.3.7.4.2: what fits, ending in "..." when the buffer holds more than three characters
        Assert.Equal((uint)ERANGE, C("strerror_s", small, 8, ENOENT));
        Assert.Equal(full[..4] + "...", GuestMemory.ReadCString(small));
        Assert.Equal((uint)ERANGE, C("strerror_s", small, 3, ENOENT));
        Assert.Equal(full[..2], GuestMemory.ReadCString(small));
        Assert.NotEqual(0u, C("strerror_s", 0, 8, ENOENT));
        Assert.NotEqual(0u, C("strerror_s", small, 0, ENOENT));
    }

    [Fact]
    public void QsortAndBsearchUseTheGuestComparator()
    {
        int calls = 0;
        uint cmp = HostStub("libciotest:cmp", c =>
        {
            calls++;
            int a = (int)GuestMemory.R32(c.Gpr(0)), b = (int)GuestMemory.R32(c.Gpr(1));
            c.Return(a < b ? -1 : a > b ? 1 : 0);
        });
        // pairs (key, tag): equal keys keep their order (the library's sort is a merge sort)
        int[][] items = [[5, 0], [1, 1], [5, 2], [3, 3], [1, 4], [9, 5]];
        uint arr = Alloc(8 * (uint)items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            GuestMemory.W32(arr + (uint)i * 8, (uint)items[i][0]);
            GuestMemory.W32(arr + (uint)i * 8 + 4, (uint)items[i][1]);
        }
        C("qsort", arr, (uint)items.Length, 8, cmp);
        (int, int)[] sorted = Enumerable.Range(0, items.Length).Select(i => ((int)GuestMemory.R32(arr + (uint)i * 8), (int)GuestMemory.R32(arr + (uint)i * 8 + 4))).ToArray();
        Assert.Equal(new[] { (1, 1), (1, 4), (3, 3), (5, 0), (5, 2), (9, 5) }, sorted);
        Assert.True(calls > 0);
        uint key = Alloc(4);
        GuestMemory.W32(key, 3);
        Assert.Equal(arr + 16, C("bsearch", key, arr, (uint)items.Length, 8, cmp));
        GuestMemory.W32(key, 4);
        Assert.Equal(0u, C("bsearch", key, arr, (uint)items.Length, 8, cmp));
        // zero or one element: nothing to compare
        calls = 0;
        C("qsort", arr, 1, 8, cmp);
        C("qsort", arr, 0, 8, cmp);
        Assert.Equal(0, calls);
    }
}
