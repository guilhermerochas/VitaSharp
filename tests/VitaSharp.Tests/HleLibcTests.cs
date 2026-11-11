using System.Reflection;
using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>HLE batch 4: SceLibc/SceLibm (the C library a process gets when it does not load libc.suprx).</summary>
[Collection("guest")]
public sealed class HleLibcTests(GuestMemoryFixture fx)
{
    static object? Invoke(object module, string name, params object?[] args) =>
        module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    static T Call<T>(object module, string name, params object?[] args) => (T)Invoke(module, name, args)!;

    uint Str(string s)
    {
        uint p = fx.Memory.Alloc((uint)s.Length + 16, "libc-test");
        GuestMemory.Fill(p, 0, (uint)s.Length + 16);
        GuestMemory.WriteCString(p, s);
        return p;
    }

    uint Wide(string s)
    {
        uint p = fx.Memory.Alloc((uint)s.Length * 2 + 16, "libc-test");
        GuestMemory.Fill(p, 0, (uint)s.Length * 2 + 16);
        for (int i = 0; i < s.Length; i++)
        {
            GuestMemory.Write(p + (uint)i * 2, (ushort)s[i]);
        }

        return p;
    }

    uint Buf(uint n)
    {
        uint p = fx.Memory.Alloc(n, "libc-test");
        GuestMemory.Fill(p, 0, n);
        return p;
    }

    static void SetBacking(object o, string prop, object value) =>
        o.GetType().GetField($"<{prop}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    Libc NewLibc()
    {
        var emu = (Emulator)RuntimeHelpers.GetUninitializedObject(typeof(Emulator));
        SetBacking(emu, "Memory", fx.Memory);
        var libc = new Libc { Emu = emu };
        typeof(Libc).GetField("_clib", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(libc, new Clib { Emu = emu });
        typeof(Libc).GetField("_heapLimit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(libc, -1L);
        return libc;
    }

    /// <summary>A va_list: the words in guest memory.</summary>
    uint VaList(params uint[] words)
    {
        uint p = Buf((uint)words.Length * 4 + 4);
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(p + (uint)i * 4, words[i]);
        }

        return p;
    }

    [Fact]
    public void StringFunctions()
    {
        var c = NewLibc();
        uint a = Str("hello world"), b = Str("hello"), set = Str(" o");
        Assert.Equal(11u, Call<uint>(c, "strlen", a));
        Assert.True(Call<int>(c, "strcmp", a, b) > 0);
        Assert.Equal(0, Call<int>(c, "strncmp", a, b, 5u));
        Assert.Equal(0, Call<int>(c, "strcasecmp", Str("HeLLo"), b));
        Assert.Equal(a + 4, Call<uint>(c, "strchr", a, (int)'o'));
        Assert.Equal(a + 7, Call<uint>(c, "strrchr", a, (int)'o'));
        Assert.Equal(a + 6, Call<uint>(c, "strstr", a, Str("world")));
        Assert.Equal(0u, Call<uint>(c, "strstr", a, Str("xyz")));
        Assert.Equal(4u, Call<uint>(c, "strcspn", a, set));
        Assert.Equal(a + 4, Call<uint>(c, "strpbrk", a, set));
        uint dst = Buf(32);
        Call<uint>(c, "strcpy", dst, b);
        Call<uint>(c, "strcat", dst, Str(" there"));
        Assert.Equal("hello there", GuestMemory.ReadCString(dst));
        uint save = Buf(4), tok = Str("a,b,,c"), delim = Str(",");
        Assert.Equal("a", GuestMemory.ReadCString(Call<uint>(c, "strtok_r", tok, delim, save)));
        Assert.Equal("b", GuestMemory.ReadCString(Call<uint>(c, "strtok_r", 0u, delim, save)));
        Assert.Equal("c", GuestMemory.ReadCString(Call<uint>(c, "strtok_r", 0u, delim, save)));
        Assert.Equal(0u, Call<uint>(c, "strtok_r", 0u, delim, save));
        Assert.Equal("range error", GuestMemory.ReadCString(Call<uint>(c, "strerror", 34)));
        Assert.Equal("error 0x000003e7", GuestMemory.ReadCString(Call<uint>(c, "strerror", 999)));
    }

    [Fact]
    public void AnnexKViolationsReturnErangeAndEmptyTheDestination()
    {
        var c = NewLibc();
        var stderr = typeof(Libc).GetField("_stderr", BindingFlags.Instance | BindingFlags.NonPublic)!;
        uint dst = Buf(8);
        GuestMemory.WriteCString(dst, "xyz");
        // the default handler writes to stderr: give the test a stream that discards (no descriptor behind it)
        Assert.Equal(34, CallQuiet(c, "strcpy_s", dst, 4u, Str("too long")));
        Assert.Equal(0, GuestMemory.Read<byte>(dst));
        Assert.Equal(0, CallQuiet(c, "strcpy_s", dst, 8u, Str("fits")));
        Assert.Equal("fits", GuestMemory.ReadCString(dst));
        _ = stderr;
    }

    /// <summary>Calls with a handler that ignores violations (ignore_handler_s semantics through a NULL-free path).</summary>
    static int CallQuiet(Libc c, string name, params object[] args)
    {
        var h = typeof(Libc).GetField("_constraintHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // a non-zero handler with no guest thread is not called: the violation is still reported by the return value
        h.SetValue(c, 1u);
        try
        {
            return Call<int>(c, name, args);
        }
        finally
        {
            h.SetValue(c, 0u);
        }
    }

    [Fact]
    public void IntegerConversions()
    {
        var c = NewLibc();
        uint end = Buf(4), s = Str("  -0x1Fz");
        Assert.Equal(-31, Call<int>(c, "strtol", s, end, 0));
        Assert.Equal(s + 7, GuestMemory.R32(end));
        Assert.Equal(int.MaxValue, Call<int>(c, "strtol", Str("99999999999"), 0u, 10));
        Assert.Equal(0xFFFFFFFFu, Call<uint>(c, "strtoul", Str("-1"), 0u, 10));
        Assert.Equal(77, Call<int>(c, "atoi", Str("077")));
        Assert.Equal(1234567890123L, Call<long>(c, "strtoll", Str("1234567890123"), 0u, 10));
        uint perr = Buf(4);
        Assert.Equal(int.MinValue, Call<int>(c, "_Stolx", Str("-99999999999"), 0u, 10, perr));
        Assert.Equal(1u, GuestMemory.R32(perr));
        Assert.Equal(0, Call<int>(c, "strtol", s = Str("xyz"), end, 10));
        Assert.Equal(s, GuestMemory.R32(end)); // no digits: end = start
        Assert.Equal(255, Call<int>(c, "wcstol", Wide("ff"), 0u, 16));
    }

    [Fact]
    public void FloatConversions()
    {
        var c = NewLibc();
        uint end = Buf(4), s = Str("3.5e2xyz");
        Assert.Equal(350.0, Call<double>(c, "strtod", s, end));
        Assert.Equal(s + 5, GuestMemory.R32(end));
        Assert.Equal(0.25, Call<double>(c, "strtod", Str("0x1p-2"), 0u));
        Assert.True(double.IsPositiveInfinity(Call<double>(c, "strtod", Str("INFINITY"), 0u)));
        Assert.True(double.IsNaN(Call<double>(c, "strtod", Str("nan(123)"), 0u)));
        Assert.Equal(1.5f, Call<float>(c, "strtof", Str(" 1.5"), 0u));
        Assert.Equal(1200.0, Call<double>(c, "_Stod", Str("12"), 0u, 2));
    }

    [Fact]
    public void PrintfAndScanf()
    {
        var c = NewLibc();
        uint buf = Buf(64);
        Assert.Equal(11, Call<int>(c, "vsnprintf", buf, 64u, "%d-%5s|%x", VaList(42, Str("ab"), 255)));
        Assert.Equal("42-   ab|ff", GuestMemory.ReadCString(buf));
        Assert.Equal(11, Call<int>(c, "vsnprintf", buf, 4u, "%d-%5s|%x", VaList(42, Str("ab"), 255)));
        Assert.Equal("42-", GuestMemory.ReadCString(buf));
        uint wbuf = Buf(64);
        Assert.Equal(-1, Call<int>(c, "vswprintf", wbuf, 3u, Wide("%ls!"), VaList(Wide("abc"))));
        Assert.Equal(4, Call<int>(c, "vswprintf", wbuf, 16u, Wide("%ls!"), VaList(Wide("abc"))));
        uint i1 = Buf(4), f1 = Buf(8), s1 = Buf(16), n1 = Buf(4);
        Assert.Equal(3, Call<int>(c, "vsscanf", Str("  12 3.25 word rest"), "%d %lf %s%n", VaList(i1, f1, s1, n1)));
        Assert.Equal(12u, GuestMemory.R32(i1));
        Assert.Equal(3.25, GuestMemory.Read<double>(f1));
        Assert.Equal("word", GuestMemory.ReadCString(s1));
        Assert.Equal(14u, GuestMemory.R32(n1));
        Assert.Equal(1, Call<int>(c, "vsscanf", Str("abc123"), "%[a-c]%*d", VaList(s1)));
        Assert.Equal("abc", GuestMemory.ReadCString(s1));
        Assert.Equal(-1, Call<int>(c, "vsscanf", Str(""), "%d", VaList(i1)));
        // sscanf_s: %s takes a size; a word that does not fit fails the conversion
        Assert.Equal(0, Call<int>(c, "vsscanf_s", Str("toolong"), Str("%s"), VaList(s1, 4)));
    }

    [Fact]
    public void HeapAndMspace()
    {
        var c = NewLibc();
        uint p = Call<uint>(c, "malloc", 100u);
        Assert.NotEqual(0u, p);
        Assert.Equal(0u, p & 7);
        Assert.True(Call<uint>(c, "malloc_usable_size", p) >= 100);
        GuestMemory.Fill(p, 0xAB, 100);
        uint q = Call<uint>(c, "realloc", p, 300u);
        Assert.Equal(0xAB, GuestMemory.Read<byte>(q + 99));
        uint z = Call<uint>(c, "calloc", 10u, 10u);
        Assert.Equal(0, GuestMemory.Read<byte>(z + 50));
        uint al = Call<uint>(c, "memalign", 256u, 10u);
        Assert.Equal(0u, al & 255);
        Invoke(c, "free", q);
        uint stats = Buf(16);
        Assert.Equal(0, Call<int>(c, "malloc_stats", stats));
        Assert.True(GuestMemory.R32(stats) >= 1u << 20);
        // mspace over a caller block
        uint block = Buf(0x10000);
        uint ms = Call<uint>(c, "mspace_create", block, 0x10000u);
        Assert.Equal(block, ms);
        uint m = Call<uint>(c, "mspace_malloc", ms, 64u);
        Assert.InRange(m, block, block + 0x10000);
        Assert.Equal(0u, Call<uint>(c, "mspace_create_with_flag", block, 0x10000u, 1u)); // unknown flag bit
        Assert.Equal(0, Call<int>(c, "mspace_is_heap_empty", ms));
        Invoke(c, "mspace_free", ms, m);
        Assert.Equal(1, Call<int>(c, "mspace_is_heap_empty", ms));
    }

    [Fact]
    public void RandMatchesTheFirmwareGenerators()
    {
        var c = NewLibc();
        Invoke(c, "srand", 1u);
        ulong state = 1;
        for (int i = 0; i < 4; i++)
        {
            state = state * 0x5851F42D4C957F2DUL + 1;
            Assert.Equal((int)((state >> 32) & 0x7FFFFFFF), Call<int>(c, "rand"));
        }
        uint seed = Buf(4);
        GuestMemory.W32(seed, 1);
        Assert.Equal((1u * 0x41C64E6D + 0x3039) >> 16, Call<uint>(c, "rand_r", seed));
    }

    [Fact]
    public void WideAndMultibyte()
    {
        var c = NewLibc();
        uint w = Wide("héllo");
        Assert.Equal(5u, Call<uint>(c, "wcslen", w));
        Assert.Equal(w + 2, Call<uint>(c, "wcschr", w, 0xE9));
        uint mb = Buf(16), src = Buf(4);
        GuestMemory.W32(src, w);
        Assert.Equal(6u, Call<uint>(c, "wcsrtombs", mb, src, 16u, 0u));
        Assert.Equal("héllo", GuestMemory.ReadCString(mb));
        uint back = Buf(16);
        Assert.Equal(5u, Call<uint>(c, "mbstowcs", back, mb, 8u));
        Assert.Equal(0xE9, GuestMemory.Read<ushort>(back + 2));
        uint wc = Buf(4), st = Buf(8);
        Assert.Equal(unchecked((uint)-2), Call<uint>(c, "mbrtowc", wc, mb + 1, 1u, st)); // first byte of é
        Assert.Equal(1u, Call<uint>(c, "mbrtowc", wc, mb + 2, 1u, st));
        Assert.Equal(0xE9, GuestMemory.Read<ushort>(wc));
        Assert.Equal(1, Call<int>(c, "iswctype", (int)'7', Call<uint>(c, "wctype", "digit")));
    }

    [Fact]
    public void TimeFormatting()
    {
        var c = NewLibc();
        uint t = Buf(4), tm = Buf(36), buf = Buf(64);
        GuestMemory.W32(t, 0); // 1970-01-01 00:00:00 UTC, a Thursday
        Assert.Equal(tm, Call<uint>(c, "gmtime_s", t, tm));
        Assert.Equal(70u, GuestMemory.R32(tm + 20));
        Assert.Equal(4u, GuestMemory.R32(tm + 24));
        Assert.Equal(24u, Call<uint>(c, "strftime", buf, 64u, "%a %b %e %H:%M:%S %Y", tm));
        Assert.Equal("Thu Jan  1 00:00:00 1970", GuestMemory.ReadCString(buf));
        Assert.Equal(0u, Call<uint>(c, "strftime", buf, 5u, "%Y-%m", tm)); // does not fit
        Assert.Equal(0, Call<int>(c, "asctime_s", buf, 26u, tm));
        Assert.Equal("Thu Jan  1 00:00:00 1970\n", GuestMemory.ReadCString(buf));
        Assert.Equal(-10.0, Call<double>(c, "difftime", 5u, 15u));
    }

    [Fact]
    public void MathInternalsAndRounding()
    {
        var m = new Libm();
        Assert.Equal(1, Call<int>(m, "_FFpcomp", 1f, 2f));
        Assert.Equal(4, Call<int>(m, "_FFpcomp", 3f, 2f));
        Assert.Equal(0, Call<int>(m, "_FFpcomp", float.NaN, 2f));
        Assert.Equal((short)-1, Call<short>(m, "_FDclass", 1.5f));
        Assert.Equal((short)2, Call<short>(m, "_FDclass", float.NaN));
        Assert.Equal((short)-2, Call<short>(m, "_FDclass", 1e-40f));
        Assert.Equal(3f, Call<float>(m, "roundf", 2.5f));
        Assert.Equal(2f, Call<float>(m, "rintf", 2.5f));
        Assert.Equal(-1f, Call<float>(m, "_FSin", MathF.PI / 2, 2u), 5);
        Assert.Equal(0.8427007929497149, Libm.Erf(1), 12);
        Assert.Equal(24.0, Libm.TGamma(5), 9);
        Assert.Equal(Math.Log(120), Libm.LGamma(6), 9);
    }

    [Fact]
    public void CxxRuntimePieces()
    {
        var c = NewLibc();
        uint p = Call<uint>(c, "_Znwj", null!, 24u); // no HLE context: only used to throw bad_alloc
        Assert.NotEqual(0u, p);
        Invoke(c, "_ZdlPv", p);
        uint cell = Buf(8), expected = Buf(4);
        GuestMemory.W32(cell, 5);
        Assert.Equal(5, Call<int>(c, "_Atomic_fetch_add_4", cell, 3, 5));
        Assert.Equal(8u, GuestMemory.R32(cell));
        GuestMemory.W32(expected, 7);
        Assert.Equal(0, Call<int>(c, "_Atomic_compare_exchange_strong_4", cell, expected, 1, 5, 5));
        Assert.Equal(8u, GuestMemory.R32(expected)); // the current value comes back
        Assert.Equal(1, Call<int>(c, "_Atomic_compare_exchange_strong_4", cell, expected, 1, 5, 5));
        Assert.Equal(1u, GuestMemory.R32(cell));
        uint t1 = Buf(8), t2 = Buf(8), name = Str("N3FooE");
        GuestMemory.W32(t1 + 4, name);
        GuestMemory.W32(t2 + 4, name);
        Assert.Equal(1, Call<int>(c, "_ZNKSt9type_infoeqERKS_", t1, t2));
        Assert.Equal(name, Call<uint>(c, "_ZNKSt9type_info4nameEv", t1));
        Assert.Equal("no such file or directory", GuestMemory.ReadCString(Call<uint>(c, "_ZSt13_Syserror_mapi", 2)));
        Assert.Equal(0u, Call<uint>(c, "_ZSt13_Syserror_mapi", 5000));
        Assert.Equal("bad allocation", GuestMemory.ReadCString(Call<uint>(c, "_ZNKSt9bad_alloc4whatEv", 0u)));
        // 1-, 2- and 8-byte atomics: the neighbouring bytes of a sub-word cell are kept
        uint w = Buf(16);
        GuestMemory.W32(w, 0x11223344);
        Assert.Equal(0x33u, Call<uint>(c, "_Atomic_fetch_add_1", w + 1, 0xFFu, 5));
        Assert.Equal(0x11223244u, GuestMemory.R32(w));
        Assert.Equal(0x1122u, Call<uint>(c, "_Atomic_exchange_2", w + 2, 0xBEEFu, 5));
        Assert.Equal(0xBEEF3244u, GuestMemory.R32(w));
        uint exp1 = Buf(4);
        GuestMemory.Write(exp1, (byte)0x99);
        Assert.Equal(0, Call<int>(c, "_Atomic_compare_exchange_strong_1", w, exp1, 0x55u, 5, 5));
        Assert.Equal((byte)0x44, GuestMemory.Read<byte>(exp1));
        Assert.Equal(1, Call<int>(c, "_Atomic_compare_exchange_strong_1", w, exp1, 0x55u, 5, 5));
        Assert.Equal(0xBEEF3255u, GuestMemory.R32(w));
        GuestMemory.Write(w + 8, 0x1_0000_0001L);
        Assert.Equal(0x1_0000_0001L, Call<long>(c, "_Atomic_fetch_add_8", w + 8, 0xFFFF_FFFFL, 5));
        Assert.Equal(0x2_0000_0000L, GuestMemory.Read<long>(w + 8));
        Assert.Equal(0u, Call<uint>(c, "_Atomic_flag_test_and_set", w + 4, 5));
        Assert.Equal(1u, Call<uint>(c, "_Atomic_flag_test_and_set", w + 4, 5));
        Invoke(c, "_Atomic_flag_clear", w + 4, 5);
        // a generic compare-exchange under a free flag
        uint obj = Buf(8), e = Buf(8), d = Buf(8);
        GuestMemory.Write(obj, 7L);
        GuestMemory.Write(e, 7L);
        GuestMemory.Write(d, 9L);
        Assert.Equal(1, Call<int>(c, "_Atomic_compare_exchange_strong", w + 4, 8u, obj, e, d));
        Assert.Equal(9L, GuestMemory.Read<long>(obj));
        Assert.Equal(0, Call<int>(c, "_Atomic_compare_exchange_strong", w + 4, 8u, obj, e, d));
        Assert.Equal(9L, GuestMemory.Read<long>(e));
        // std::string (Dinkumware: buffer or pointer at +4, size +0x14, capacity +0x18)
        uint s1 = Buf(0x1C), s2 = Buf(0x1C);
        Call<uint>(c, "_ZNSsC1EPKc", s1, Str("short"));
        Assert.Equal(5u, GuestMemory.R32(s1 + 0x14));
        Assert.Equal(15u, GuestMemory.R32(s1 + 0x18));
        Assert.Equal("short", GuestMemory.ReadCString(s1 + 4));
        Call<uint>(c, "_ZNSsC1EPKc", s2, Str("a string longer than fifteen"));
        Assert.Equal(31u, GuestMemory.R32(s2 + 0x18));
        Assert.Equal("a string longer than fifteen", GuestMemory.ReadCString(GuestMemory.R32(s2 + 4)));
        Call<uint>(c, "_ZNSs6assignERKSsjj", null!, s1, s2, 2u, 6u);
        Assert.Equal("string", GuestMemory.ReadCString(s1 + 4));
        Assert.True(Call<bool>(c, "_ZNSs5_GrowEjb", null!, s1, 40u, false));
        Assert.True(GuestMemory.R32(s1 + 0x18) >= 40);
        Assert.Equal("string", GuestMemory.ReadCString(GuestMemory.R32(s1 + 4)));
        Assert.True(Call<bool>(c, "_ZNSs5_GrowEjb", null!, s1, 3u, true)); // back to the internal buffer
        Assert.Equal(15u, GuestMemory.R32(s1 + 0x18));
        Assert.Equal("str", GuestMemory.ReadCString(s1 + 4));
    }
}
