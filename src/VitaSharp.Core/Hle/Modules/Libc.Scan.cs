using System.Text;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceLibc scanf family: one scanner over a character source (a string in guest memory, narrow or wide, or a stream),
// storing through the pointer arguments. Conversions d i u o x X c s [ f e g a E G F p n %, the * suppression, field
// widths and the hh h l ll j z t L sizes; %ls/%lc/%l[ store UTF-16, plain ones bytes (in a wide scanf too, as the
// multibyte form).
public sealed partial class Libc
{
    /// <summary>A character source with one character of push-back (EOF = -1).</summary>
    sealed class ScanInput(Func<int> next, Action<int> unget)
    {
        public int Count;
        int _peek = int.MinValue;
        public int Get()
        {
            int c = _peek != int.MinValue ? _peek : next();
            _peek = int.MinValue;
            if (c != EOF) Count++;
            return c;
        }
        public int Peek() => _peek != int.MinValue ? _peek : _peek = next();
        public void Unget(int c) { if (c == EOF) return; Count--; _peek = c; }
        public void Finish() { if (_peek != int.MinValue && _peek != EOF) unget(_peek); _peek = int.MinValue; }
    }

    static ScanInput StringInput(uint s, bool wide)
    {
        uint p = s;
        return new ScanInput(() =>
        {
            int c = wide ? W(p) : B(p);
            if (c == 0) return EOF;
            p += wide ? 2u : 1u;
            return c;
        }, _ => { });
    }

    ScanInput StreamInput(Stream st, bool wide) =>
        new(() => wide ? (GetWide(st) is var w && w == WEOF ? EOF : w) : GetByte(st),
            c => { if (wide) ungetwc(c, st.Addr); else { st.Pushback.Push((byte)c); st.Eof = false; } });

    /// <summary>Runs a scanf format; returns the number of assigned conversions, or EOF on an input failure before
    /// the first conversion.</summary>
    static int Scan(ScanInput inp, string fmt, VarArgs va, bool wideFormat, bool secure = false)
    {
        int assigned = 0;
        bool anyConversion = false;
        for (int i = 0; i < fmt.Length; i++)
        {
            char f = fmt[i];
            if (IsSpace(f))
            {
                while (IsSpace(inp.Peek())) inp.Get();
                continue;
            }
            if (f != '%' || i + 1 >= fmt.Length)
            {
                int c = inp.Get();
                if (c != f) { inp.Unget(c); return c == EOF && !anyConversion ? EOF : assigned; }
                continue;
            }
            i++;
            if (fmt[i] == '%')
            {
                while (IsSpace(inp.Peek())) inp.Get();
                int c = inp.Get();
                if (c != '%') { inp.Unget(c); return c == EOF && !anyConversion ? EOF : assigned; }
                continue;
            }
            bool suppress = false;
            if (fmt[i] == '*') { suppress = true; i++; }
            int width = 0;
            while (i < fmt.Length && char.IsDigit(fmt[i])) width = width * 10 + (fmt[i++] - '0');
            string len = "";
            while (i < fmt.Length && "hlLjzt".Contains(fmt[i])) len += fmt[i++];
            if (i >= fmt.Length) break;
            char conv = fmt[i];
            if (conv == 'n')
            {
                if (!suppress) StoreInt(va.NextU32(), len, inp.Count);
                continue;
            }
            if (conv != 'c' && conv != '[') while (IsSpace(inp.Peek())) inp.Get();
            if (inp.Peek() == EOF) return anyConversion ? assigned : EOF;
            anyConversion = true;
            int max = width > 0 ? width : int.MaxValue;
            switch (conv)
            {
                case 'd': case 'i': case 'u': case 'o': case 'x': case 'X': case 'p':
                {
                    int radix = conv switch { 'd' or 'u' => 10, 'o' => 8, 'x' or 'X' or 'p' => 16, _ => 0 };
                    var sb = new StringBuilder();
                    int c = inp.Peek();
                    if (c is '+' or '-' && sb.Length < max) { sb.Append((char)inp.Get()); c = inp.Peek(); }
                    if ((radix is 0 or 16) && c == '0' && sb.Length < max)
                    {
                        sb.Append((char)inp.Get());
                        c = inp.Peek();
                        if ((c | 0x20) == 'x' && sb.Length < max) { sb.Append((char)inp.Get()); radix = 16; }
                        else if (radix == 0) radix = 8;
                    }
                    if (radix == 0) radix = 10;
                    while (sb.Length < max && Digit(inp.Peek()) < radix) sb.Append((char)inp.Get());
                    string t = sb.ToString();
                    string digits = t.TrimStart('+', '-');
                    if (radix == 16 && digits.Length > 1 && (digits[1] | 0x20) == 'x') digits = digits[2..];
                    if (digits.Length == 0 && !t.TrimStart('+', '-').StartsWith('0')) return assigned;
                    ulong v = ParseScanned(t, radix);
                    if (!suppress) { StoreInt(va.NextU32(), conv == 'p' ? "" : len, (long)v); assigned++; }
                    break;
                }
                case 'f': case 'F': case 'e': case 'E': case 'g': case 'G': case 'a': case 'A':
                {
                    var sb = new StringBuilder();
                    while (sb.Length < max)
                    {
                        int c = inp.Peek();
                        if (c == EOF) break;
                        char ch = (char)c;
                        string cand = sb.ToString() + ch;
                        if (!FloatPrefix(cand)) break;
                        sb.Append((char)inp.Get());
                    }
                    var (v, used) = ParseFloatText(sb.ToString());
                    if (used == 0) return assigned;
                    if (!suppress)
                    {
                        uint p = va.NextU32();
                        if (len is "l" or "L" or "ll") GuestMemory.Write(p, v); else GuestMemory.Write(p, (float)v);
                        assigned++;
                    }
                    break;
                }
                case 'c':
                {
                    int n = width > 0 ? width : 1;
                    uint p = suppress ? 0 : va.NextU32();
                    uint room = suppress || !secure ? uint.MaxValue : va.NextU32();
                    if (p != 0 && room < n) return assigned;
                    int k = 0;
                    for (; k < n; k++)
                    {
                        int c = inp.Get();
                        if (c == EOF) break;
                        if (p != 0) Store(p, k, c, len == "l");
                    }
                    if (k == 0) return assigned;
                    if (!suppress) assigned++;
                    break;
                }
                case 's':
                case '[':
                {
                    HashSet<char>? set = null;
                    bool negate = false;
                    if (conv == '[')
                    {
                        set = new HashSet<char>();
                        i++;
                        if (i < fmt.Length && fmt[i] == '^') { negate = true; i++; }
                        if (i < fmt.Length && fmt[i] == ']') { set.Add(']'); i++; }
                        while (i < fmt.Length && fmt[i] != ']')
                        {
                            if (fmt[i] == '-' && set.Count > 0 && i + 1 < fmt.Length && fmt[i + 1] != ']')
                            {
                                char lo = fmt[i - 1], hi = fmt[i + 1];
                                for (char ch = lo; ch <= hi; ch++) set.Add(ch);
                                i += 2;
                                continue;
                            }
                            set.Add(fmt[i++]);
                        }
                    }
                    uint p = suppress ? 0 : va.NextU32();
                    uint room = suppress || !secure ? uint.MaxValue : va.NextU32();
                    int k = 0;
                    while (k < max)
                    {
                        int c = inp.Peek();
                        if (c == EOF) break;
                        bool ok = set == null ? !IsSpace(c) : set.Contains((char)c) != negate;
                        if (!ok) break;
                        inp.Get();
                        if (p != 0) Store(p, k, c, len == "l");
                        k++;
                    }
                    if (k == 0) return assigned;
                    if (p != 0 && (uint)k >= room) { if (room > 0) Store(p, 0, 0, len == "l"); return assigned; }
                    if (p != 0) { Store(p, k, 0, len == "l"); assigned++; }
                    break;
                }
                default:
                    return assigned;
            }
        }
        return assigned;
    }

    static void Store(uint p, int index, int c, bool wide)
    {
        if (wide) GuestMemory.Write(p + (uint)index * 2, (ushort)c);
        else GuestMemory.Write(p + (uint)index, (byte)c);
    }

    static void StoreInt(uint p, string len, long v)
    {
        switch (len)
        {
            case "hh": GuestMemory.Write(p, (byte)v); break;
            case "h": GuestMemory.Write(p, (ushort)v); break;
            case "ll" or "j" or "L": GuestMemory.Write(p, v); break;
            default: GuestMemory.W32(p, (uint)v); break;
        }
    }

    static ulong ParseScanned(string t, int radix)
    {
        bool neg = t.StartsWith('-');
        string d = t.TrimStart('+', '-');
        if (radix == 16 && d.Length > 1 && (d[1] | 0x20) == 'x') d = d[2..];
        ulong v = 0;
        foreach (char ch in d)
        {
            int x = Digit(ch);
            if (x >= radix) break;
            v = unchecked(v * (ulong)radix + (ulong)x);
        }
        return neg ? unchecked((ulong)-(long)v) : v;
    }

    /// <summary>Whether <paramref name="s"/> can still grow into a strtod number (greedy field matching).</summary>
    static bool FloatPrefix(string s)
    {
        string t = s.TrimStart('+', '-');
        if (s.Length - t.Length > 1) return false;
        if (t.Length == 0) return true;
        string lower = t.ToLowerInvariant();
        if ("infinity".StartsWith(lower) || "nan".StartsWith(lower)) return true;
        if (lower.StartsWith("0x"))
        {
            bool dot = false, p = false;
            for (int i = 2; i < lower.Length; i++)
            {
                char c = lower[i];
                if (c == '.' && !dot && !p) dot = true;
                else if (c == 'p' && !p) p = true;
                else if ((c is '+' or '-') && lower[i - 1] == 'p') { }
                else if (!(Uri.IsHexDigit(c) && !p || char.IsDigit(c))) return false;
            }
            return true;
        }
        if (lower == "0x"[..Math.Min(2, lower.Length)]) return true;
        bool sawDot = false, sawE = false;
        for (int i = 0; i < lower.Length; i++)
        {
            char c = lower[i];
            if (char.IsDigit(c)) continue;
            if (c == '.' && !sawDot && !sawE) { sawDot = true; continue; }
            if (c == 'e' && !sawE && i > 0) { sawE = true; continue; }
            if ((c is '+' or '-') && i > 0 && lower[i - 1] == 'e') continue;
            return false;
        }
        return true;
    }

    static (double Value, int Used) ParseFloatText(string s)
    {
        if (s.Length == 0) return (0, 0);
        var bytes = Encoding.Latin1.GetBytes(s + "\0");
        int Ch(uint p) => p < bytes.Length ? bytes[p] : 0;
        var (v, end, _) = ParseDouble(0, Ch, 1);
        return (v, (int)end);
    }

    int ScanStream(uint file, string fmt, VarArgs va, bool wide, bool secure = false)
    {
        if (Get(file) is not { } st) { SetErrno(EBADF); return EOF; }
        var inp = StreamInput(st, wide);
        int r = Scan(inp, fmt, va, wide, secure);
        inp.Finish();
        return r;
    }

    [Hle] int sscanf(uint s, string fmt, VarArgs va) => Scan(StringInput(s, false), fmt, va, false);
    [Hle] int vsscanf(uint s, string fmt, uint va) => Scan(StringInput(s, false), fmt, Va(va), false);
    [Hle] int fscanf(uint file, string fmt, VarArgs va) => ScanStream(file, fmt, va, false);
    [Hle] int vfscanf(uint file, string fmt, uint va) => ScanStream(file, fmt, Va(va), false);
    [Hle] int scanf(string fmt, VarArgs va) => ScanStream(StdinFile().Addr, fmt, va, false);
    [Hle] int vscanf(string fmt, uint va) => ScanStream(StdinFile().Addr, fmt, Va(va), false);

    [Hle] int sscanf_s(uint s, uint fmt, VarArgs va) => s == 0 || fmt == 0 ? ScanFail("sscanf_s") : Scan(StringInput(s, false), GuestMemory.ReadCString(fmt), va, false, true);
    [Hle] int vsscanf_s(uint s, uint fmt, uint va) => s == 0 || fmt == 0 ? ScanFail("vsscanf_s") : Scan(StringInput(s, false), GuestMemory.ReadCString(fmt), Va(va), false, true);
    [Hle] int fscanf_s(uint file, uint fmt, VarArgs va) => file == 0 || fmt == 0 ? ScanFail("fscanf_s") : ScanStream(file, GuestMemory.ReadCString(fmt), va, false, true);
    [Hle] int vfscanf_s(uint file, uint fmt, uint va) => file == 0 || fmt == 0 ? ScanFail("vfscanf_s") : ScanStream(file, GuestMemory.ReadCString(fmt), Va(va), false, true);
    [Hle] int scanf_s(uint fmt, VarArgs va) => fmt == 0 ? ScanFail("scanf_s") : ScanStream(StdinFile().Addr, GuestMemory.ReadCString(fmt), va, false, true);
    [Hle] int vscanf_s(uint fmt, uint va) => fmt == 0 ? ScanFail("vscanf_s") : ScanStream(StdinFile().Addr, GuestMemory.ReadCString(fmt), Va(va), false, true);

    int ScanFail(string fn) { Constraint($"{fn}: bad argument"); return EOF; }

    [Hle] int swscanf(uint s, uint fmt, VarArgs va) => Scan(StringInput(s, true), ReadWide(fmt), va, true);
    [Hle] int vswscanf(uint s, uint fmt, uint va) => Scan(StringInput(s, true), ReadWide(fmt), Va(va), true);
    [Hle] int fwscanf(uint file, uint fmt, VarArgs va) => ScanStream(file, ReadWide(fmt), va, true);
    [Hle] int vfwscanf(uint file, uint fmt, uint va) => ScanStream(file, ReadWide(fmt), Va(va), true);
    [Hle] int wscanf(uint fmt, VarArgs va) => ScanStream(StdinFile().Addr, ReadWide(fmt), va, true);
    [Hle] int vwscanf(uint fmt, uint va) => ScanStream(StdinFile().Addr, ReadWide(fmt), Va(va), true);
    [Hle] int swscanf_s(uint s, uint fmt, VarArgs va) => s == 0 || fmt == 0 ? ScanFail("swscanf_s") : Scan(StringInput(s, true), ReadWide(fmt), va, true, true);
    [Hle] int vswscanf_s(uint s, uint fmt, uint va) => s == 0 || fmt == 0 ? ScanFail("vswscanf_s") : Scan(StringInput(s, true), ReadWide(fmt), Va(va), true, true);
    [Hle] int fwscanf_s(uint file, uint fmt, VarArgs va) => file == 0 || fmt == 0 ? ScanFail("fwscanf_s") : ScanStream(file, ReadWide(fmt), va, true, true);
    [Hle] int vfwscanf_s(uint file, uint fmt, uint va) => file == 0 || fmt == 0 ? ScanFail("vfwscanf_s") : ScanStream(file, ReadWide(fmt), Va(va), true, true);
    [Hle] int wscanf_s(uint fmt, VarArgs va) => fmt == 0 ? ScanFail("wscanf_s") : ScanStream(StdinFile().Addr, ReadWide(fmt), va, true, true);
    [Hle] int vwscanf_s(uint fmt, uint va) => fmt == 0 ? ScanFail("vwscanf_s") : ScanStream(StdinFile().Addr, ReadWide(fmt), Va(va), true, true);
}
