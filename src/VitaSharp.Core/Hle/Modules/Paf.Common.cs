using System.Text;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// paf::common: text conversions between UTF-8 and UTF-16 paf strings, number parsing, formatted strings (libpaf
/// formats into a 0x100-byte buffer: 255 characters at most), path splitting and joining, the frame counter,
/// RefObj ({ vtable, count }; the last Release calls the virtual destructor) and common::String ({ string utf8, wstring
/// utf16 at +0xC }, the missing form made from the other on demand).
/// </summary>
public sealed partial class Paf
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>(src, wstring *dst[, flags]): the UTF-16 length, -1 without a destination or source; text that is not
    /// valid UTF-8 gives an empty string (0).</summary>
    int Utf8ToUtf16(uint src, uint dst)
    {
        if (dst == 0) return -1;
        if (src == 0) { WStrAssign(dst, ""); return -1; }
        string s;
        try { s = StrictUtf8.GetString(GuestMemory.ReadSpan(src, (int)StrLen(src))); }
        catch (DecoderFallbackException) { s = ""; }
        WStrAssign(dst, s);
        return s.Length;
    }

    int Utf16ToUtf8(uint src, uint dst)
    {
        if (dst == 0) return -1;
        if (src == 0) { StrAssign(dst, ""); return -1; }
        string s = WRead(src);
        byte[] b;
        try { b = StrictUtf8.GetBytes(s); }
        catch (EncoderFallbackException) { b = []; }
        StrAssign(dst, b);
        return b.Length;
    }

    [Hle("_ZN3paf6common11Utf8ToUtf16EPKcPNS_12basic_stringIwNS_11char_traitsIwEENS_9allocatorIwEEEE")]
    int U8ToU16(uint src, uint dst) => Utf8ToUtf16(src, dst);
    [Hle("_ZN3paf6common11Utf8ToUtf16EPKcPNS_12basic_stringIwNS_11char_traitsIwEENS_9allocatorIwEEEEi")]
    int U8ToU16F(uint src, uint dst, int flags) => Utf8ToUtf16(src, dst);
    [Hle("_ZN3paf6common11Utf8ToUtf16ERKNS_12basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEEPNS1_IwNS2_IwEENS4_IwEEEE")]
    int U8sToU16(uint src, uint dst) => Utf8ToUtf16(U(src), dst);
    [Hle("_ZN3paf6common11Utf8ToUtf16ERKNS_12basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEEPNS1_IwNS2_IwEENS4_IwEEEEi")]
    int U8sToU16F(uint src, uint dst, int flags) => Utf8ToUtf16(U(src), dst);
    [Hle("_ZN3paf6common11Utf16ToUtf8EPKwPNS_12basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEE")]
    int U16ToU8(uint src, uint dst) => Utf16ToUtf8(src, dst);
    [Hle("_ZN3paf6common11Utf16ToUtf8EPKwPNS_12basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEEi")]
    int U16ToU8F(uint src, uint dst, int flags) => Utf16ToUtf8(src, dst);
    [Hle("_ZN3paf6common11Utf16ToUtf8ERKNS_12basic_stringIwNS_11char_traitsIwEENS_9allocatorIwEEEEPNS1_IcNS2_IcEENS4_IcEEEE")]
    int U16sToU8(uint src, uint dst) => Utf16ToUtf8(U(src), dst);
    [Hle("_ZN3paf6common11Utf16ToUtf8ERKNS_12basic_stringIwNS_11char_traitsIwEENS_9allocatorIwEEEEPNS1_IcNS2_IcEENS4_IcEEEEi")]
    int U16sToU8F(uint src, uint dst, int flags) => Utf16ToUtf8(U(src), dst);

    /// <summary>(text, length): libpaf's integer parse reads the digits from the end; any other character counts as a
    /// 0 digit; a leading '-' negates (and, as in 3.74, a '-' first or a '+' second skips the first character).</summary>
    [Hle("_ZN3paf6common10FromStringIiEET_PKcj")]
    int FromStringInt(uint s, uint len)
    {
        if (len == 0) return 0;
        int sign = B(s) == '-' ? -1 : 1;
        uint start = B(s) == '-' || (len > 1 && B(s + 1) == '+') ? 1u : 0u;
        int v = 0, mul = 1;
        for (int i = (int)len - 1; i >= (int)start; i--, mul *= 10)
        {
            byte c = B(s + (uint)i);
            if (c is >= (byte)'0' and <= (byte)'9') v += (c - '0') * mul;
        }
        return v * sign;
    }

    [Hle("_ZN3paf6common10FromStringIfEET_PKcj")]
    float FromStringFloat(uint s, uint len)
    {
        if (len == 0) return 0;
        string t = Encoding.ASCII.GetString(GuestMemory.ReadSpan(s, (int)len));
        return float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0;
    }

    [Hle("_ZN3paf6common12FormatStringEPKcz")]
    void FormatString(uint ret, string fmt, VarArgs va)
    {
        string s = CFormat.Format(fmt, va);
        var b = Encoding.UTF8.GetBytes(s);
        StrInit(ret);
        StrAssign(ret, b.AsSpan(0, Math.Min(b.Length, 0xFF)));
    }

    uint _frameCount;
    [Hle("_ZN3paf6common10FrameCountEv")] uint FrameCount() => _frameCount;
    [Hle("_ZN3paf6common8internal15ResetFrameCountEv")] void ResetFrameCount() => _frameCount = 0;
    [Hle("_ZN3paf6common8internal14IncrFrameCountEv")] void IncrFrameCount() => _frameCount++;

    // ---- paths
    /// <summary>(path, string &dir, string &file): split after the last '/' (or ':' for a bare device); with neither,
    /// dir is empty and file the whole path.</summary>
    [Hle("_ZN3paf6common4path5SplitERKNS_12basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEERS7_SA_")]
    void PathSplit(uint path, uint dir, uint file)
    {
        string p = StrRead(path);
        int i = p.LastIndexOf('/');
        if (i < 0) i = p.LastIndexOf(':');
        if (i < 0) { StrAssign(dir, ""); StrAssign(file, p); return; }
        StrAssign(dir, p[..(i + 1)]);
        StrAssign(file, p[(i + 1)..]);
    }

    /// <summary>(path, string &stem, string &ext): split at the last '.' of the file name (ext keeps the dot); no dot
    /// after the last '/' gives the whole path and an empty extension.</summary>
    [Hle("_ZN3paf6common4path8SplitExtERKNS_12basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEERS7_SA_")]
    void PathSplitExt(uint path, uint stem, uint ext)
    {
        string p = StrRead(path);
        int dot = p.LastIndexOf('.'), slash = p.LastIndexOf('/');
        if (dot < 0 || (slash >= 0 && slash > dot)) { StrAssign(stem, p); StrAssign(ext, ""); return; }
        StrAssign(stem, p[..dot]);
        StrAssign(ext, p[dot..]);
    }

    /// <summary>(string &ret, a, b): b relative to the directory a ("./" dropped, "../" climbing); an empty side gives
    /// the other, an absolute b stays as it is.</summary>
    [Hle("_ZN3paf6common4path4JoinERKNS_12basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEES9_")]
    void PathJoin(uint ret, uint a, uint b)
    {
        StrInit(ret);
        StrAssign(ret, JoinPath(StrRead(a), StrRead(b)));
    }

    internal static string JoinPath(string a, string b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        if (b[0] == '/') return b;
        string dir = a.TrimEnd('/');
        while (true)
        {
            if (b.StartsWith("./", StringComparison.Ordinal)) b = b[2..];
            else if (b.StartsWith("../", StringComparison.Ordinal))
            {
                int i = dir.LastIndexOf('/');
                if (i <= 0 || dir.EndsWith(':')) break;
                dir = dir[..i];
                b = b[3..];
            }
            else break;
        }
        return dir + "/" + b;
    }

    // ---- RefObj
    [Hle("_ZN3paf6common6RefObjC1Ev")]
    [Hle("_ZN3paf6common6RefObjC2Ev")]
    uint RefObjCtor(uint me)
    {
        if (U(me) == 0) U(me, VTable("RefObj", HostSlot("RefObj.D1", c => c.Return(c.Gpr(0))), HostSlot("RefObj.D0", c => { Free(c.Gpr(0)); c.Return(0); })));
        U(me + 4, 0);
        return me;
    }

    [Hle("_ZN3paf6common6RefObj6AddRefEv")]
    int RefObjAddRef(uint me) { lock (_stubLock) { uint n = U(me + 4) + 1; U(me + 4, n); return (int)n; } }

    /// <summary>The count after the release; at 0 the object's deleting destructor (slot 1) runs.</summary>
    [Hle("_ZN3paf6common6RefObj7ReleaseEv")]
    int RefObjRelease(uint me)
    {
        uint n;
        lock (_stubLock) { n = U(me + 4) - 1; U(me + 4, n); }
        if (n == 0 && Kernel.GuestThread.Current is { } t) t.CallGuest(U(U(me) + 4), me);
        return (int)n;
    }

    // ---- common::String
    [Hle("_ZN3paf6common6StringC1Ev")]
    [Hle("_ZN3paf6common6StringC2Ev")]
    uint CStrCtor(uint me) { StrInit(me); StrInit(me + 0xC, true); return me; }

    [Hle("_ZN3paf6common6StringC1EPKc")]
    [Hle("_ZN3paf6common6StringC2EPKc")]
    uint CStrCtorC(uint me, uint s) { CStrCtor(me); if (s != 0) StrAssign(me, GuestMemory.ReadSpan(s, (int)StrLen(s))); return me; }

    [Hle("_ZN3paf6common6StringC1EPKw")]
    [Hle("_ZN3paf6common6StringC2EPKw")]
    uint CStrCtorW(uint me, uint s) { CStrCtor(me); if (s != 0) WStrAssign(me + 0xC, WRead(s)); return me; }

    [Hle("_ZN3paf6common6StringC1ERKNS_12basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEE")]
    [Hle("_ZN3paf6common6StringC2ERKNS_12basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEE")]
    uint CStrCtorS(uint me, uint s) { CStrCtor(me); StrAssign(me, StrRead(s)); return me; }

    [Hle("_ZN3paf6common6StringC1ERKNS_12basic_stringIwNS_11char_traitsIwEENS_9allocatorIwEEEE")]
    [Hle("_ZN3paf6common6StringC2ERKNS_12basic_stringIwNS_11char_traitsIwEENS_9allocatorIwEEEE")]
    uint CStrCtorWs(uint me, uint s) { CStrCtor(me); WStrAssign(me + 0xC, WStrRead(s)); return me; }

    [Hle("_ZN3paf6common6StringD1Ev")]
    [Hle("_ZN3paf6common6StringD2Ev")]
    uint CStrDtor(uint me)
    {
        StrRelease(me + 0xC); U(me + 0xC, 0xFFFFFFFF); U(me + 0x10, 0xFFFFFFFF);
        StrRelease(me); U(me, 0xFFFFFFFF); U(me + 4, 0xFFFFFFFF);
        return me;
    }

    /// <summary>The text of a common::String, from whichever form it holds.</summary>
    string CStrText(uint me) => U(me + 4) != 0 || U(me + 0x10) == 0 ? StrRead(me) : WStrRead(me + 0xC);

    [Hle("_ZNK3paf6common6String9GetStringEv")]
    void CStrGetString(uint ret, uint me) { StrInit(ret); StrAssign(ret, CStrText(me)); }

    [Hle("_ZNK3paf6common6String10GetWStringEv")]
    void CStrGetWString(uint ret, uint me)
    {
        StrInit(ret, true);
        WStrAssign(ret, U(me + 0x10) != 0 || U(me + 4) == 0 ? WStrRead(me + 0xC) : StrRead(me));
    }

    /// <summary>Appends the other string; the result is kept in UTF-8 (the UTF-16 form is dropped).</summary>
    [Hle("_ZN3paf6common6StringpLERKS1_")]
    uint CStrAppend(uint me, uint other)
    {
        string s = CStrText(me) + CStrText(other);
        StrAssign(me, s);
        WStrAssign(me + 0xC, "");
        return me;
    }

    [Hle("_ZN3paf6common6StringeqERKS1_")] bool CStrEq(uint me, uint other) => CStrText(me) == CStrText(other);
    [Hle("_ZN3paf6common6StringneERKS1_")] bool CStrNe(uint me, uint other) => CStrText(me) != CStrText(other);

    [Hle("_ZN3paf6common6StringplERKS1_")]
    void CStrPlus(uint ret, uint me, uint other) { CStrCtor(ret); StrAssign(ret, CStrText(me) + CStrText(other)); }

    [Hle("_ZN3paf6common6String18SetFormattedStringEPKcz")]
    void CStrSetFormatted(uint me, string fmt, VarArgs va)
    {
        var b = Encoding.UTF8.GetBytes(CFormat.Format(fmt, va));
        StrAssign(me, b.AsSpan(0, Math.Min(b.Length, 0xFF)));
        WStrAssign(me + 0xC, "");
    }
}
