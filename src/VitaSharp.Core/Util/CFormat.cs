using System.Globalization;
using System.Text;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Util;

/// <summary>C printf-style formatting over guest varargs.</summary>
public static class CFormat
{
    public static string Format(string fmt, VarArgs args)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < fmt.Length)
        {
            char c = fmt[i++];
            if (c != '%') { sb.Append(c); continue; }
            if (i < fmt.Length && fmt[i] == '%') { sb.Append('%'); i++; continue; }

            bool left = false, plus = false, space = false, alt = false, zero = false;
            for (; i < fmt.Length; i++)
            {
                char f = fmt[i];
                if (f == '-') left = true;
                else if (f == '+') plus = true;
                else if (f == ' ') space = true;
                else if (f == '#') alt = true;
                else if (f == '0') zero = true;
                else break;
            }
            int width = -1;
            if (i < fmt.Length && fmt[i] == '*') { width = (int)args.NextU32(); i++; if (width < 0) { left = true; width = -width; } }
            else while (i < fmt.Length && char.IsDigit(fmt[i])) width = Math.Max(width, 0) * 10 + (fmt[i++] - '0');
            int prec = -1;
            if (i < fmt.Length && fmt[i] == '.')
            {
                i++;
                prec = 0;
                if (i < fmt.Length && fmt[i] == '*') { prec = (int)args.NextU32(); i++; }
                else while (i < fmt.Length && char.IsDigit(fmt[i])) prec = prec * 10 + (fmt[i++] - '0');
            }
            string len = "";
            while (i < fmt.Length && "hlLqjzt".Contains(fmt[i])) len += fmt[i++];
            if (i >= fmt.Length) break;
            char conv = fmt[i++];
            bool wide = len is "ll" or "q" or "j" or "L";
            string body;
            switch (conv)
            {
                case 'd': case 'i':
                {
                    long v = wide ? (long)args.NextU64() : len == "h" ? (short)args.NextU32() : len == "hh" ? (sbyte)args.NextU32() : (int)args.NextU32();
                    body = Math.Abs((decimal)v).ToString(CultureInfo.InvariantCulture);
                    if (prec >= 0) body = body.PadLeft(prec, '0');
                    string sign = v < 0 ? "-" : plus ? "+" : space ? " " : "";
                    body = Pad(sign, body, width, left, zero && prec < 0);
                    break;
                }
                case 'u': case 'x': case 'X': case 'o':
                {
                    ulong v = wide ? args.NextU64() : len == "h" ? (ushort)args.NextU32() : len == "hh" ? (byte)args.NextU32() : args.NextU32();
                    body = conv switch
                    {
                        'u' => v.ToString(CultureInfo.InvariantCulture),
                        'x' => v.ToString("x"),
                        'X' => v.ToString("X"),
                        _ => Convert.ToString((long)v, 8),
                    };
                    if (prec >= 0) body = body.PadLeft(prec, '0');
                    string prefix = alt && v != 0 ? (conv == 'x' ? "0x" : conv == 'X' ? "0X" : conv == 'o' ? "0" : "") : "";
                    body = Pad(prefix, body, width, left, zero && prec < 0);
                    break;
                }
                case 'p':
                    body = Pad("0x", args.NextU32().ToString("x8"), width, left, false);
                    break;
                case 'c':
                    body = Pad("", ((char)(byte)args.NextU32()).ToString(), width, left, false);
                    break;
                case 's':
                {
                    uint p = args.NextU32();
                    string s = p == 0 ? "(null)" : GuestMemory.ReadCString(p);
                    if (prec >= 0 && s.Length > prec) s = s[..prec];
                    body = Pad("", s, width, left, false);
                    break;
                }
                case 'f': case 'F': case 'e': case 'E': case 'g': case 'G': case 'a': case 'A':
                {
                    double v = args.NextDouble();
                    int p = prec < 0 ? 6 : prec;
                    body = conv switch
                    {
                        'f' or 'F' => v.ToString("F" + p, CultureInfo.InvariantCulture),
                        'e' => v.ToString((p == 0 ? "0" : "0." + new string('0', p)) + "e+00", CultureInfo.InvariantCulture),
                        'E' => v.ToString((p == 0 ? "0" : "0." + new string('0', p)) + "E+00", CultureInfo.InvariantCulture),
                        _ => v.ToString("G" + Math.Max(p, 1), CultureInfo.InvariantCulture),
                    };
                    string sign = v >= 0 && plus ? "+" : v >= 0 && space ? " " : "";
                    body = Pad(sign, body, width, left, zero);
                    break;
                }
                case 'n':
                    args.NextU32();
                    body = "";
                    break;
                default:
                    body = "%" + conv;
                    break;
            }
            sb.Append(body);
        }
        return sb.ToString();
    }

    static string Pad(string prefix, string body, int width, bool left, bool zero)
    {
        int total = prefix.Length + body.Length;
        if (width <= total) return prefix + body;
        if (left) return (prefix + body).PadRight(width);
        if (zero)
        {
            bool neg = body.StartsWith('-');
            return prefix + body.PadLeft(width - prefix.Length, '0');
        }
        return (prefix + body).PadLeft(width);
    }
}
