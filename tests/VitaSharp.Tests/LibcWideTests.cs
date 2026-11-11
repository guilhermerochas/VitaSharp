using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceLibc's wide-character functions with the C standard's semantics (wchar_t is 16 bits, UTF-16; the multibyte form
/// UTF-8; classification in the C locale) and the Annex K bounds-checked forms, which return ERANGE (34) and leave the
/// destination empty on a constraint violation, as libc.suprx does.
/// </summary>
[Collection("guest")]
public sealed class LibcWideTests(GuestMemoryFixture fx) : IDisposable
{
    const int ERANGE = 34;
    readonly HleHarness _h = new(fx, "libcwide");
    Libc C => _h.Module<Libc>();

    public void Dispose() => _h.Dispose();

    uint Wide(string s, uint extra = 16)
    {
        uint p = _h.Alloc((uint)s.Length * 2 + extra * 2);
        for (int i = 0; i < s.Length; i++) GuestMemory.Write(p + (uint)i * 2, (ushort)s[i]);
        return p;
    }

    static string Read(uint p)
    {
        var sb = new System.Text.StringBuilder();
        for (ushort c; (c = GuestMemory.Read<ushort>(p)) != 0; p += 2) sb.Append((char)c);
        return sb.ToString();
    }

    uint U(string fn, params object[] a) => (uint)Invoke(C, fn, a)!;

    [Fact]
    public void CopyConcatCompareAndSearch()
    {
        uint a = Wide("hello"), b = Wide("help"), dst = Wide("", 32);
        Assert.Equal(5u, U("wcslen", a));
        Assert.Equal(3u, U("wcsnlen_s", a, 3u));
        Assert.Equal(0u, U("wcsnlen_s", 0u, 3u));
        Assert.Equal(dst, U("wcscpy", dst, a));
        Assert.Equal("hello", Read(dst));
        U("wcscat", dst, b);
        Assert.Equal("hellohelp", Read(dst));
        U("wcsncat", dst, a, 2u);
        Assert.Equal("hellohelphe", Read(dst));
        // wcsncpy pads with NULs up to n
        uint pad = Wide("xxxxxxxx");
        U("wcsncpy", pad, b, 6u);
        Assert.Equal("help", Read(pad));
        Assert.Equal((ushort)0, GuestMemory.Read<ushort>(pad + 10));
        Assert.Equal((ushort)'x', GuestMemory.Read<ushort>(pad + 12));

        Assert.True(Call(C, "wcscmp", a, b) < 0);   // 'l' < 'p'
        Assert.True(Call(C, "wcscmp", b, a) > 0);
        Assert.Equal(0, Call(C, "wcscmp", a, a));
        Assert.Equal(0, Call(C, "wcsncmp", a, b, 3u));
        Assert.Equal(0, Call(C, "wcscoll", a, Wide("hello")));
        Assert.Equal(5u, U("wcsxfrm", dst, a, 10u));
        Assert.Equal("hello", Read(dst));

        Assert.Equal(a + 4, U("wcschr", a, (int)'l'));
        Assert.Equal(a + 6, U("wcsrchr", a, (int)'l'));
        Assert.Equal(0u, U("wcschr", a, (int)'z'));
        Assert.Equal(a + 10, U("wcschr", a, 0)); // the terminator can be found
        Assert.Equal(a + 4, U("wcsstr", a, Wide("llo")));
        Assert.Equal(0u, U("wcsstr", a, Wide("lp")));
        Assert.Equal(2u, U("wcsspn", a, Wide("eh")));
        Assert.Equal(2u, U("wcscspn", a, Wide("lo")));
        Assert.Equal(a + 4, U("wcspbrk", a, Wide("ol")));
        Assert.Equal(0u, U("wcspbrk", a, Wide("xyz")));
    }

    [Fact]
    public void TokensAndMemoryFunctions()
    {
        uint s = Wide(",,a,bb;c"), delim = Wide(",;"), save = _h.Alloc(4);
        Assert.Equal("a", Read(U("wcstok", s, delim, save)));
        Assert.Equal("bb", Read(U("wcstok", 0u, delim, save)));
        Assert.Equal("c", Read(U("wcstok", 0u, delim, save)));
        Assert.Equal(0u, U("wcstok", 0u, delim, save));
        // wcstok_s also counts down the characters left
        uint s2 = Wide("x y"), max = _h.Word(4), save2 = _h.Alloc(4), sp = Wide(" ");
        Assert.Equal("x", Read(U("wcstok_s", s2, max, sp, save2)));
        Assert.Equal(2u, GuestMemory.R32(max));
        Assert.Equal(0u, U("wcstok_s", 0u, 0u, sp, save2)); // a NULL size pointer is a violation

        uint m = Wide("", 8);
        U("wmemset", m, (int)'q', 4u);
        Assert.Equal("qqqq", Read(m));
        uint m2 = Wide("", 8);
        U("wmemcpy", m2, m, 3u);
        Assert.Equal("qqq", Read(m2));
        U("wmemmove", m2 + 2, m2, 3u);
        Assert.Equal("qqqq", Read(m2));
        Assert.Equal(0, Call(C, "wmemcmp", m, m2, 4u));
        Assert.True(Call(C, "wmemcmp", Wide("ab"), Wide("ac"), 2u) < 0);
        uint abc = Wide("abc");
        Assert.Equal(abc + 4, U("wmemchr", abc, (int)'c', 3u));
        Assert.Equal(0u, U("wmemchr", abc, (int)'z', 3u));
    }

    [Fact]
    public void BoundsCheckedFormsReportERange()
    {
        uint src = Wide("abcdef"), dst = Wide("zzzzzzzzzz");
        Assert.Equal(0, Call(C, "wcscpy_s", dst, 10u, src));
        Assert.Equal("abcdef", Read(dst));
        Assert.Equal(ERANGE, Call(C, "wcscpy_s", dst, 6u, src)); // no room for the terminator
        Assert.Equal("", Read(dst));
        Assert.Equal(ERANGE, Call(C, "wcscpy_s", 0u, 6u, src));
        Assert.Equal(ERANGE, Call(C, "wcscpy_s", dst, 0u, src));
        Assert.Equal(ERANGE, Call(C, "wcscpy_s", dst, 6u, 0u));
        Assert.Equal(0, Call(C, "wcsncpy_s", dst, 10u, src, 3u));
        Assert.Equal("abc", Read(dst));
        Assert.Equal(ERANGE, Call(C, "wcsncpy_s", dst, 3u, src, 3u));
        Assert.Equal(0, Call(C, "wcscat_s", dst, 10u, Wide("12")));
        Assert.Equal("12", Read(dst));
        Call(C, "wcscpy_s", dst, 10u, Wide("abc"));
        Assert.Equal(0, Call(C, "wcscat_s", dst, 10u, Wide("12")));
        Assert.Equal("abc12", Read(dst));
        Assert.Equal(ERANGE, Call(C, "wcscat_s", dst, 7u, Wide("12")));
        Assert.Equal("", Read(dst));
        Call(C, "wcscpy_s", dst, 10u, Wide("abc"));
        Assert.Equal(0, Call(C, "wcsncat_s", dst, 10u, Wide("12345"), 2u));
        Assert.Equal("abc12", Read(dst));
        Assert.Equal(ERANGE, Call(C, "wcsncat_s", dst, 6u, Wide("12345"), 2u));
        Assert.Equal(ERANGE, Call(C, "wcsncat_s", dst, 6u, 0u, 2u));

        uint m = Wide("abcdef");
        Assert.Equal(0, Call(C, "wmemcpy_s", dst, 10u, m, 4u));
        Assert.Equal("abcd", Read(dst).Substring(0, 4));
        Assert.Equal(ERANGE, Call(C, "wmemcpy_s", dst, 3u, m, 4u)); // n > size: the destination is cleared
        Assert.Equal((ushort)0, GuestMemory.Read<ushort>(dst));
        Assert.Equal(ERANGE, Call(C, "wmemcpy_s", m, 6u, m + 2, 3u)); // overlapping
        Assert.Equal(0, Call(C, "wmemmove_s", m, 6u, m + 2, 3u));
        Assert.Equal(ERANGE, Call(C, "wmemmove_s", m, 2u, m + 2, 3u));
        Assert.Equal(ERANGE, Call(C, "wmemmove_s", 0u, 2u, m, 1u));
    }

    [Fact]
    public void ClassificationInTheCLocale()
    {
        Assert.NotEqual(0, Call(C, "iswalpha", (int)'q'));
        Assert.Equal(0, Call(C, "iswalpha", 0xE9));       // é: not a letter in the C locale
        Assert.NotEqual(0, Call(C, "iswdigit", (int)'7'));
        Assert.NotEqual(0, Call(C, "iswxdigit", (int)'F'));
        Assert.NotEqual(0, Call(C, "iswspace", (int)'\t'));
        Assert.NotEqual(0, Call(C, "iswblank", (int)' '));
        Assert.NotEqual(0, Call(C, "iswcntrl", 0x7F));
        Assert.NotEqual(0, Call(C, "iswpunct", (int)'!'));
        Assert.NotEqual(0, Call(C, "iswgraph", (int)'!'));
        Assert.Equal(0, Call(C, "iswgraph", (int)' '));
        Assert.NotEqual(0, Call(C, "iswprint", (int)' '));
        Assert.NotEqual(0, Call(C, "iswupper", (int)'Q'));
        Assert.NotEqual(0, Call(C, "iswlower", (int)'q'));
        Assert.NotEqual(0, Call(C, "iswalnum", (int)'0'));
        Assert.Equal((int)'a', Call(C, "towlower", (int)'A'));
        Assert.Equal((int)'A', Call(C, "towupper", (int)'a'));
        Assert.Equal(0xE9, Call(C, "towupper", 0xE9));
        foreach (var (name, ch, expect) in new[] { ("alpha", 'x', true), ("digit", 'x', false), ("upper", 'X', true), ("space", ' ', true), ("xdigit", 'g', false) })
        {
            uint desc = (uint)Invoke(C, "wctype", name)!;
            Assert.NotEqual(0u, desc);
            Assert.Equal(expect, Call(C, "iswctype", (int)ch, desc) != 0);
        }
        Assert.Equal(0u, (uint)Invoke(C, "wctype", "nonsense")!);
        Assert.Equal(0, Call(C, "iswctype", (int)'x', 0u));
        Assert.Equal((int)'b', Call(C, "towctrans", (int)'B', (uint)Invoke(C, "wctrans", "tolower")!));
        Assert.Equal((int)'B', Call(C, "towctrans", (int)'b', (uint)Invoke(C, "wctrans", "toupper")!));
        Assert.Equal(0u, (uint)Invoke(C, "wctrans", "rot13")!);
        Assert.Equal((int)'z', Call(C, "_Towctrans", (int)'z', 0u));
        Assert.Equal((int)'A', Call(C, "btowc", (int)'A'));
        Assert.Equal(0xFFFF, Call(C, "btowc", 0xE9));    // WEOF: wint_t is 16 bits like wchar_t
        Assert.Equal((int)'A', Call(C, "wctob", (int)'A'));
        Assert.Equal(-1, Call(C, "wctob", 0xE9));        // EOF
    }

    [Fact]
    public void MultibyteConversionsAreUtf8()
    {
        _h.OnGuest(_ =>
        {
            // "aé€" = 61 | C3 A9 | E2 82 AC
            uint mb = _h.Alloc(16);
            new byte[] { 0x61, 0xC3, 0xA9, 0xE2, 0x82, 0xAC, 0 }.CopyTo(GuestMemory.Span(mb, 7));
            uint wc = _h.Alloc(4), st = _h.Alloc(8);
            Assert.Equal(1, Call(C, "mbtowc", wc, mb, 4u));
            Assert.Equal((ushort)'a', GuestMemory.Read<ushort>(wc));
            Assert.Equal(2, Call(C, "mbtowc", wc, mb + 1, 4u));
            Assert.Equal((ushort)0xE9, GuestMemory.Read<ushort>(wc));
            Assert.Equal(3, Call(C, "mblen", mb + 3, 4u));
            Assert.Equal(0, Call(C, "mblen", mb + 6, 4u)); // the NUL character
            // a sequence split across calls continues in the state
            Assert.Equal(unchecked((uint)-2), U("mbrtowc", wc, mb + 3, 2u, st));
            Assert.Equal(1u, U("mbrtowc", wc, mb + 5, 1u, st));
            Assert.Equal((ushort)0x20AC, GuestMemory.Read<ushort>(wc));
            Assert.Equal(unchecked((uint)-2), U("mbrlen", mb + 1, 1u, 0u));
            uint bad = _h.Alloc(4);
            GuestMemory.Write(bad, (byte)0xFF);
            Assert.Equal(unchecked((uint)-1), U("mbrtowc", wc, bad, 1u, st)); // EILSEQ
            Assert.Equal(0u, U("mbrtowc", wc, 0u, 0u, st)); // NULL resets the state

            // wide to UTF-8
            uint out8 = _h.Alloc(8);
            Assert.Equal(3u, U("wcrtomb", out8, 0x20AC, 0u));
            Assert.Equal(new byte[] { 0xE2, 0x82, 0xAC }, GuestMemory.ReadSpan(out8, 3).ToArray());
            Assert.Equal(2, Call(C, "wctomb", out8, 0xE9));
            Assert.Equal(unchecked((uint)-1), U("wcrtomb", out8, 0xD800, 0u)); // a lone surrogate
            uint w = Wide("aé€");
            uint mbs = _h.Alloc(16);
            Assert.Equal(6u, U("wcstombs", mbs, w, 16u));
            Assert.Equal(new byte[] { 0x61, 0xC3, 0xA9, 0xE2, 0x82, 0xAC, 0 }, GuestMemory.ReadSpan(mbs, 7).ToArray());
            uint back = Wide("", 8);
            Assert.Equal(3u, U("mbstowcs", back, mbs, 8u));
            Assert.Equal("aé€", Read(back));
            // the _s forms: count through *retval, the destination limits
            uint ret = _h.Alloc(4);
            Assert.Equal(0, Call(C, "mbstowcs_s", ret, back, 8u, mbs, 7u));
            Assert.Equal(3u, GuestMemory.R32(ret));
            Assert.Equal(0, Call(C, "wcstombs_s", ret, mbs, 16u, w, 15u));
            Assert.Equal(6u, GuestMemory.R32(ret));
            // a character that does not fit in what is left of dmax is a constraint violation, not a truncation
            // (libc.suprx 3.74: "wcstombs_s: buffer overflow", the destination emptied and *retval -1)
            Assert.Equal(ERANGE, Call(C, "wcstombs_s", ret, mbs, 2u, w, 15u));
            Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(ret));
            Assert.Equal((byte)0, GuestMemory.Read<byte>(mbs));
            // len counts wide characters: two of them, then the terminator
            Assert.Equal(0, Call(C, "wcstombs_s", ret, mbs, 16u, w, 2u));
            Assert.Equal(3u, GuestMemory.R32(ret));
            Assert.Equal(new byte[] { 0x61, 0xC3, 0xA9, 0 }, GuestMemory.ReadSpan(mbs, 4).ToArray());
            Assert.Equal(ERANGE, Call(C, "wcstombs_s", 0u, mbs, 16u, w, 2u));
            Assert.Equal(ERANGE, Call(C, "wcstombs_s", ret, 0u, 4u, w, 2u)); // no destination but a size
            // wcsrtombs_s (len in bytes): the whole string, then one that stops at a character that does not fit
            uint sp = _h.Alloc(4), state = _h.Alloc(8);
            GuestMemory.W32(sp, w);
            Assert.Equal(0, Call(C, "wcsrtombs_s", ret, mbs, 16u, sp, 15u, state));
            Assert.Equal(6u, GuestMemory.R32(ret));
            Assert.Equal(0u, GuestMemory.R32(sp)); // the NUL was converted
            GuestMemory.W32(sp, w);
            Assert.Equal(ERANGE, Call(C, "wcsrtombs_s", ret, mbs, 16u, sp, 2u, state)); // 'a' fits, 'é' needs 2 more
            Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(ret));
            Assert.Equal(w + 2, GuestMemory.R32(sp));
            Assert.Equal((byte)0x61, GuestMemory.Read<byte>(mbs));
            Assert.Equal((byte)0, GuestMemory.Read<byte>(mbs + 1));
            GuestMemory.W32(sp, w);
            Assert.Equal(0, Call(C, "wcsrtombs_s", ret, mbs, 16u, sp, 3u, state)); // exactly 'a' + 'é': stops there
            Assert.Equal(3u, GuestMemory.R32(ret));
            Assert.Equal(w + 4, GuestMemory.R32(sp));
            GuestMemory.W32(sp, w);
            Assert.Equal(0, Call(C, "wcsrtombs_s", ret, 0u, 0u, sp, 0u, state)); // no destination: counts to the NUL
            Assert.Equal(6u, GuestMemory.R32(ret));
            Assert.Equal(ERANGE, Call(C, "wcsrtombs_s", ret, mbs, 16u, sp, 3u, 0u)); // no state
            Assert.Equal(0, Call(C, "wctomb_s", ret, out8, 4u, 0x41));
            Assert.Equal(1u, GuestMemory.R32(ret));
            Assert.Equal(0, Call(C, "wcrtomb_s", ret, out8, 4u, 0xE9, 0u));
            Assert.Equal(2u, GuestMemory.R32(ret));
        });
    }
}
