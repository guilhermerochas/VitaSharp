using System.Reflection;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>SceLibMonoBridge (the PSM runtime's eglib, pthreads and pss_* layer): eglib's structure layouts and rules,
/// the callbacks through guest code, threads.</summary>
[Collection("guest")]
public sealed class MonoBridgeTests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-monobridge-" + Guid.NewGuid().ToString("N"));
    Emulator? _emu;
    uint _stubs, _nextStub;

    Emulator Emu()
    {
        if (_emu != null)
        {
            return _emu;
        }

        Directory.CreateDirectory(_root);
        _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
        _stubs = _nextStub = fx.Memory.Alloc(0x1000, "monobridge test stubs");
        return _emu;
    }

    MonoBridge M => Emu().Hle.Modules.OfType<MonoBridge>().Single();

    object? Call(string method, params object?[] args) =>
        typeof(MonoBridge).GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(M, args);

    uint U(string method, params object?[] args) => Convert.ToUInt32(Call(method, args));

    uint Alloc(uint size)
    {
        uint p = fx.Memory.Alloc(size, "monobridge test");
        GuestMemory.Fill(p, 0, size);
        return p;
    }

    uint CStr(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s + "\0");
        uint p = Alloc((uint)b.Length);
        b.CopyTo(GuestMemory.Span(p, b.Length));
        return p;
    }

    static string Str(uint p) => GuestMemory.ReadCString(p, 1 << 20);

    static List<string> Vector(uint v)
    {
        var l = new List<string>();
        for (uint p = v; GuestMemory.R32(p) != 0; p += 4)
        {
            l.Add(Str(GuestMemory.R32(p)));
        }

        return l;
    }

    uint Stub(string name, Action<HleContext> body)
    {
        uint a = _nextStub;
        _nextStub += 16;
        Linker.WriteHleStub(a, Emu().Hle.RegisterHostFunction(name, body), 0);
        Emu().Code.Invalidate(a, 16);
        return a;
    }

    /// <summary>Runs <paramref name="body"/> on a guest thread (for the functions that call back into guest code).</summary>
    void OnGuestThread(Action body)
    {
        Exception? failure = null;
        var t = Emu().Kernel.CreateThread("monobridge-test", 0, 0x10000100, 0x10000, 0, 0x70000);
        t.HostBody = _ =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                failure = e;
            }
        };
        t.Start(0, 0);
        Assert.True(t.WaitForExit(60_000), "test thread did not finish");
        Emu().Kernel.DeleteThread(t);
        if (failure != null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    public void Dispose()
    {
        _emu?.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void Strings_FollowEglib()
    {
        Assert.Equal(["a", "b", "", "c"], Vector(U("GStrsplit", CStr("a,b,,c"), CStr(","), 0)));
        Assert.Equal(["a", "b,,c"], Vector(U("GStrsplit", CStr("a,b,,c"), CStr(","), 2)));
        Assert.Equal(["", "x", ""], Vector(U("GStrsplit", CStr("::x::"), CStr("::"), -1)));
        Assert.Empty(Vector(U("GStrsplit", CStr(""), CStr(","), 0)));
        Assert.Equal("ab", Str(U("GStrndup", CStr("abcdef"), 2u)));
        Assert.Equal("x  ", Str(U("GStrchug", CStr("  \tx  "))));
        Assert.Equal("  x", Str(U("GStrchomp", CStr("  x \n"))));
        Assert.Equal("cba", Str(U("GStrreverse", CStr("abc"))));
        Assert.True((bool)Call("GStrHasPrefix", CStr("monoeg"), CStr("mono"))!);
        uint buf = Alloc(8);
        Assert.Equal(10u, U("GStrlcpy", buf, CStr("0123456789"), 4u));
        Assert.Equal("012", Str(buf));
        Assert.Equal("héllo", Str(U("GAsciiStrdown", CStr("HéLLO"), -1)));
        Assert.Equal(0, (int)Call("GAsciiStrncasecmp", CStr("ABCx"), CStr("abcy"), 3u)!);
        Assert.Equal(15, (int)Call("GAsciiXdigitValue", (int)'F')!);
        Assert.Equal("'it'\\''s'", Str(U("GShellQuote", CStr("it's"))));
        Assert.Equal(11u, U("GSpacedPrimesClosest", 1u));
        Assert.Equal(557u, U("GSpacedPrimesClosest", 400u));
        Assert.Equal(13845163u + 0, U("GSpacedPrimesClosest", 13845163u));
    }

    [Fact]
    public void Paths_FollowEglib()
    {
        Assert.Equal("c", Str(U("GPathGetBasename", CStr("/a/b/c"))));
        Assert.Equal("b", Str(U("GPathGetBasename", CStr("/a/b/"))));
        Assert.Equal(".", Str(U("GPathGetBasename", CStr(""))));
        Assert.Equal("/a/b", Str(U("GPathGetDirname", CStr("/a/b/c"))));
        Assert.Equal("/", Str(U("GPathGetDirname", CStr("/a"))));
        Assert.Equal(".", Str(U("GPathGetDirname", CStr("a"))));
        Assert.True((bool)Call("GPathIsAbsolute", CStr("app0:data"))!);
        Assert.False((bool)Call("GPathIsAbsolute", CStr("data/x:y"))!);
        Assert.Equal("file:///Application/a%20b.txt", Str(U("GFilenameToUri", CStr("/Application/a b.txt"), 0u, 0u)));
        Assert.Equal("/Application/a b.txt", Str(U("GFilenameFromUri", CStr("file:///Application/a%20b.txt"), 0u, 0u)));
        uint err = Alloc(4);
        Assert.Equal(0u, U("GFilenameToUri", CStr("rel/x"), 0u, err));
        Assert.Equal(5u, GuestMemory.R32(GuestMemory.R32(err) + 4)); // G_CONVERT_ERROR_NOT_ABSOLUTE_PATH
    }

    [Fact]
    public void GString_GrowsAsEglib()
    {
        uint s = U("GStringNew", CStr("ab"));
        Assert.Equal(2u, GuestMemory.R32(s + 4));
        Assert.Equal(16u, GuestMemory.R32(s + 8));
        Call("GStringAppendLen", s, CStr("0123456789abcdefXYZ"), 16);
        Assert.Equal(18u, GuestMemory.R32(s + 4));
        Assert.Equal((16u + 16 + 16) * 2, GuestMemory.R32(s + 8));
        Call("GStringAppendC", s, (int)'!');
        Assert.Equal("ab0123456789abcdef!", Str(GuestMemory.R32(s)));
        uint data = U("GStringFree", s, false);
        Assert.Equal("ab0123456789abcdef!", Str(data));
    }

    [Fact]
    public void Arrays_KeepEglibLayouts()
    {
        uint a = U("GArrayNew", true, true, 4u);
        Assert.Equal(64u, GuestMemory.R32(a + 20)); // capacity (16 rounded to 64)
        uint vals = Alloc(12);
        GuestMemory.W32(vals, 1);
        GuestMemory.W32(vals + 4, 2);
        GuestMemory.W32(vals + 8, 3);
        Call("GArrayAppendVals", a, vals, 2u);
        Call("GArrayInsertVals", a, 1u, vals + 8, 1u);
        uint d = GuestMemory.R32(a);
        Assert.Equal(3u, GuestMemory.R32(a + 4));
        Assert.Equal(new uint[] { 1, 3, 2, 0 }, new[] { GuestMemory.R32(d), GuestMemory.R32(d + 4), GuestMemory.R32(d + 8), GuestMemory.R32(d + 12) });

        uint p = U("GPtrArrayNew");
        for (uint i = 1; i <= 5; i++)
        {
            Call("GPtrArrayAdd", p, i * 0x10);
        }

        Assert.Equal(16u, GuestMemory.R32(p + 8));
        Assert.Equal(0x20u, U("GPtrArrayRemoveIndex", p, 1u));
        Assert.Equal(0x10u, U("GPtrArrayRemoveIndexFast", p, 0u)); // the last one moves in
        Assert.True((bool)Call("GPtrArrayRemove", p, 0x30u)!);
        uint pd = GuestMemory.R32(p);
        Assert.Equal(2u, GuestMemory.R32(p + 4));
        Assert.Equal(new uint[] { 0x50, 0x40 }, new[] { GuestMemory.R32(pd), GuestMemory.R32(pd + 4) });
    }

    [Fact]
    public void Lists_LinkAsEglib()
    {
        uint l = 0;
        foreach (uint v in new uint[] { 1, 2, 3 })
        {
            l = U("GListAppend", l, v);
        }

        l = U("GListPrepend", l, 0u);
        Assert.Equal(4u, U("GListLength", l));
        Assert.Equal(2u, U("GListNthData", l, 2u));
        uint n2 = U("GListNth", l, 2u);
        Assert.Equal(GuestMemory.R32(n2 + 8), U("GListNth", l, 1u)); // prev link
        l = U("GListRemove", l, 0u);
        l = U("GListReverse", l);
        Assert.Equal(3u, GuestMemory.R32(l));
        Assert.Equal(0u, GuestMemory.R32(l + 8));
        uint copy = U("GListCopy", l);
        Assert.Equal(3u, U("GListLength", copy));
        uint ins = U("GListInsertBefore", l, l, 9u);
        Assert.Equal(9u, GuestMemory.R32(ins));

        uint s = 0;
        foreach (uint v in new uint[] { 1, 2, 3 })
        {
            s = U("GSlistAppend", s, v);
        }

        s = U("GSlistReverse", s);
        Assert.Equal(3u, GuestMemory.R32(s));
        s = U("GSlistRemove", s, 2u);
        Assert.Equal(2u, U("GSlistLength", s));
        Assert.Equal(1u, U("GSlistNthData", s, 1u));

        uint q = U("GQueueNew");
        Assert.True((bool)Call("GQueueIsEmpty", q)!);
        Call("GQueuePushHead", q, 7u);
        Call("GQueuePushHead", q, 8u);
        Assert.Equal(8u, U("GQueuePopHead", q));
        Assert.Equal(7u, U("GQueuePopHead", q));
        Assert.Equal(0u, GuestMemory.R32(q + 4)); // tail cleared
    }

    [Fact]
    public void HashTable_DirectAndStringKeys()
    {
        uint ht = U("GHashTableNew", 0u, 0u);
        Assert.Equal(11u, GuestMemory.R32(ht + 12));
        for (uint i = 1; i <= 100; i++)
        {
            Call("GHashTableInsertReplace", ht, i, i * 2, false);
        }

        Assert.Equal(100u, U("GHashTableSize", ht));
        Assert.Equal(84u, U("GHashTableLookup", ht, 42u));
        Assert.True(GuestMemory.R32(ht + 12) > 11); // rehashed
        Assert.True((bool)Call("GHashTableRemove", ht, 42u)!);
        Assert.Equal(0u, U("GHashTableLookup", ht, 42u));
        uint it = Alloc(32), k = Alloc(4), v = Alloc(4);
        Call("GHashTableIterInit", it, ht);
        int count = 0;
        while ((bool)Call("GHashTableIterNext", it, k, v)!)
        {
            Assert.Equal(GuestMemory.R32(k) * 2, GuestMemory.R32(v));
            count++;
        }
        Assert.Equal(99, count);

        uint strHash = U("ExportStub", "monoeg_g_str_hash"), strEq = U("ExportStub", "monoeg_g_str_equal");
        uint st = U("GHashTableNew", strHash, strEq);
        Call("GHashTableInsertReplace", st, CStr("key"), 5u, false);
        Assert.Equal(5u, U("GHashTableLookup", st, CStr("key")));
        uint ok = Alloc(4), ov = Alloc(4);
        Assert.True((bool)Call("GHashTableLookupExtended", st, CStr("key"), ok, ov)!);
        Assert.Equal("key", Str(GuestMemory.R32(ok)));
        Call("GHashTableDestroy", st);
    }

    [Fact]
    public void Unicode_ConvertsWithErrors()
    {
        uint w = Alloc(4), r = Alloc(4), err = Alloc(4);
        uint u16 = U("GUtf8ToUtf16", CStr("aé😀"), -1, r, w, err);
        Assert.Equal(4u, GuestMemory.R32(w)); // a, é, surrogate pair
        Assert.Equal(0xD83Du, (uint)GuestMemory.Read<ushort>(u16 + 4));
        uint back = U("GUtf16ToUtf8", u16, -1, r, w, err);
        Assert.Equal("aé😀", Str(back));
        uint ucs4 = U("GUtf16ToUcs4", u16, -1, r, w, err);
        Assert.Equal(0x1F600u, GuestMemory.R32(ucs4 + 8));
        uint bad = Alloc(4);
        GuestMemory.Write<byte>(bad, 0x61);
        GuestMemory.Write<byte>(bad + 1, 0xFF);
        Assert.Equal(0u, U("GUtf8ToUtf16", bad, -1, r, w, err));
        Assert.Equal(1u, GuestMemory.R32(r));
        Assert.Equal(1u, GuestMemory.R32(GuestMemory.R32(err) + 4)); // ILLEGAL_SEQUENCE
        uint end = Alloc(4);
        Assert.False((bool)Call("GUtf8Validate", bad, -1, end)!);
        Assert.Equal(bad + 1, GuestMemory.R32(end));
        Assert.Equal(9, (int)Call("GUnicharType", (uint)'A')!);
        Assert.Equal(13, (int)Call("GUnicharType", (uint)'7')!);
        Assert.Equal((uint)'é', U("GUnicharTolower", (uint)'É'));
        uint latin = U("GConvert", CStr("é"), -1, CStr("ISO-8859-1"), CStr("UTF-8"), r, w, err);
        Assert.Equal(1u, GuestMemory.R32(w));
        Assert.Equal(0xE9, GuestMemory.Read<byte>(latin));
    }

    [Fact]
    public void BuildPath_JoinsWithOneSeparator()
    {
        Assert.Equal("/a/b/c", Str(U("BuildPathFrom", CStr("/"), new[] { CStr("/a/"), CStr("/b/"), CStr("c") })));
        Assert.Equal("a/", Str(U("BuildPathFrom", CStr("/"), new[] { CStr("a"), CStr("/") })));
    }

    [Fact]
    public void IntegerHelpers_MatchLibgcc()
    {
        Assert.Equal(-3L, (long)Call("Divdi3", -7L, 2L)!);
        Assert.Equal(-1L, (long)Call("Moddi3", -7L, 2L)!);
        Assert.Equal(long.MinValue, (long)Call("Divdi3", long.MinValue, -1L)!);
        Assert.Equal(0L, (long)Call("Divdi3", 5L, 0L)!);
        Assert.Equal(1L << 40, (long)Call("Ashldi3", 1L << 8, 32)!);
        Assert.Equal(0x7FFFFFFFFFFFFFFFUL, (ulong)Call("Lshrdi3", ulong.MaxValue, 1)!);
        Assert.Equal(3u, U("Umodsi3", 13u, 5u));
    }

    [Fact]
    public void Environment_And_Timer()
    {
        Assert.Equal(0u, U("Getenv", CStr("MONO_PATH")));
        Assert.True((bool)Call("Setenv", CStr("MONO_PATH"), CStr("/Application"), true)!);
        Assert.Equal("/Application", Str(U("Getenv", CStr("MONO_PATH"))));
        Call("Setenv", CStr("MONO_PATH"), CStr("x"), false);
        Assert.Equal("/Application", Str(U("Getenv", CStr("MONO_PATH"))));
        Call("Unsetenv", CStr("MONO_PATH"));
        Assert.Equal(0u, U("Getenv", CStr("MONO_PATH")));
        uint t = U("GTimerNew");
        Call("GTimerStop", t);
        double e = (double)Call("GTimerElapsed", t, 0u)!;
        Assert.InRange(e, 0, 5);
    }

    [Fact]
    public void DataExports_EnvironAndLockTags()
    {
        uint envNid = NidDatabase.LookupName("environ").Single(n => n.Library == "SceLibMonoBridge").Nid;
        uint cell = Emu().Hle.GetHleVariable(envNid);
        Assert.NotEqual(0u, cell);
        Assert.Empty(Vector(GuestMemory.R32(cell)));
        Call("Setenv", CStr("MONO_PATH"), CStr("/Application"), true);
        Call("Setenv", CStr("A"), CStr("1"), true);
        Assert.Equal(["A=1", "MONO_PATH=/Application"], Vector(GuestMemory.R32(cell)));
        Assert.Equal(cell, Emu().Hle.GetHleVariable(envNid)); // the same symbol for every importer
        foreach (uint nid in new uint[] { 0xEFFB20F0, 0x8EF0F4A8, 0xF3F04352 }) // adopt_lock, defer_lock, try_to_lock
        {
            uint tag = Emu().Hle.GetHleVariable(nid);
            Assert.NotEqual(0u, tag);
            Assert.Equal(0, GuestMemory.Read<byte>(tag));
            Assert.Contains(nid, Emu().Hle.VariableNids);
        }
    }

    [Fact]
    public void Callbacks_SortForeachMarkup()
    {
        Emu();
        var seen = new List<string>();
        uint cmp = Stub("mbtest:cmp", c => c.Return((int)c.Gpr(0) - (int)c.Gpr(1)));
        uint each = Stub("mbtest:each", c => seen.Add($"{c.Gpr(0)}:{c.Gpr(1)}"));
        uint start = Stub("mbtest:start", c =>
        {
            var names = Vector(c.Gpr(2));
            var values = Vector(c.Gpr(3));
            seen.Add($"<{Str(c.Gpr(1))} {string.Join(",", names.Zip(values, (n, v) => n + "=" + v))}>");
        });
        uint end = Stub("mbtest:end", c => seen.Add($"</{Str(c.Gpr(1))}>"));
        uint text = Stub("mbtest:text", c => seen.Add($"t:{Encoding.UTF8.GetString(GuestMemory.ReadSpan(c.Gpr(1), (int)c.Gpr(2)))}"));
        uint parser = Alloc(0x14);
        GuestMemory.W32(parser, start);
        GuestMemory.W32(parser + 4, end);
        GuestMemory.W32(parser + 8, text);
        OnGuestThread(() =>
        {
            uint l = 0;
            foreach (uint v in new uint[] { 5, 1, 4, 1, 3 })
            {
                l = U("GListAppend", l, v);
            }

            l = U("GListSort", l, cmp);
            Call("GListForeach", l, each, 9u);
            uint s = U("GSlistInsertSorted", 0u, 3u, cmp);
            s = U("GSlistInsertSorted", s, 1u, cmp);
            s = U("GSlistInsertSorted", s, 2u, cmp);
            Call("GSlistForeach", s, each, 8u);

            uint ctx = U("GMarkupNew", parser, 0u, 0x77u, 0u);
            Assert.True((bool)Call("GMarkupParse", ctx, CStr("<configuration><dllmap dll=\"i:cygwin1.dll\" tar"), -1, 0u)!);
            Assert.True((bool)Call("GMarkupParse", ctx, CStr("get=\"libc\"/><x>a &amp; b</x></configuration>"), -1, 0u)!);
            Assert.True((bool)Call("GMarkupEndParse", ctx, 0u)!);
            uint err = Alloc(4);
            uint ctx2 = U("GMarkupNew", parser, 0u, 0u, 0u);
            Assert.False((bool)Call("GMarkupParse", ctx2, CStr("<a></b>"), -1, err)!);
            Assert.Equal(1u, GuestMemory.R32(GuestMemory.R32(err) + 4));
        });
        Assert.Equal(["1:9", "1:9", "3:9", "4:9", "5:9", "1:8", "2:8", "3:8",
            "<configuration >", "<dllmap dll=i:cygwin1.dll,target=libc>", "</dllmap>", "<x >", "t:a & b", "</x>", "</configuration>", "<a >"], seen);
    }

    [Fact]
    public void Pthreads_CreateJoinMutexKeys()
    {
        Emu();
        uint shared = Alloc(4), mutex = Alloc(4), key = Alloc(4);
        uint worker = Stub("mbtest:worker", c =>
        {
            for (int i = 0; i < 100; i++)
            {
                U("PthreadMutexLock", mutex);
                GuestMemory.W32(shared, GuestMemory.R32(shared) + 1);
                U("PthreadMutexUnlock", mutex);
            }
            U("PthreadSetspecific", GuestMemory.R32(key), 0x1234u);
            c.Return(c.Gpr(0) + 1);
        });
        OnGuestThread(() =>
        {
            Assert.Equal(0u, U("PthreadMutexInit", mutex, 0u));
            Assert.Equal(0u, U("PthreadKeyCreate", key, 0u));
            uint t1 = Alloc(4), t2 = Alloc(4), ret = Alloc(4);
            Assert.Equal(0u, U("PthreadCreate", t1, 0u, worker, 41u));
            Assert.Equal(0u, U("PthreadCreate", t2, 0u, worker, 1u));
            Assert.Equal(0u, U("PthreadJoin", (int)GuestMemory.R32(t1), ret));
            Assert.Equal(42u, GuestMemory.R32(ret));
            Assert.Equal(0u, U("PthreadJoin", (int)GuestMemory.R32(t2), ret));
            Assert.Equal(200u, GuestMemory.R32(shared));
            Assert.Equal(0u, U("PthreadGetspecific", GuestMemory.R32(key))); // only the workers set it
            Assert.Equal(3u, U("PthreadJoin", 0x7FFF, ret)); // ESRCH
            Assert.Equal(0u, U("PthreadMutexTrylock", mutex));
            Assert.Equal(0u, U("PthreadMutexUnlock", mutex));
            Assert.Equal(0u, U("PthreadMutexDestroy", mutex));
            uint cond = Alloc(4), abs = Alloc(8);
            uint m2 = Alloc(4);
            U("PthreadMutexLock", m2);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            GuestMemory.W32(abs, (uint)now);
            GuestMemory.W32(abs + 4, 0);
            Assert.Equal(116u, U("PthreadCondTimedwait", cond, m2, abs)); // ETIMEDOUT: the deadline is past
            U("PthreadMutexUnlock", m2);
        });
    }
}
