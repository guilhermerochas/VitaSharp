using System.Text;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Pure URI helpers behind sceHttpUri* (escape, unescape, parse, build, merge, dot-segment removal). Strings are handled
/// as Latin-1 so every guest byte maps to one char and back unchanged.
/// </summary>
public static class HttpUri
{
    public const int ERR_OUT_OF_MEMORY = unchecked((int)0x80431022), ERR_INVALID_VALUE = unchecked((int)0x804311FE),
        ERR_OUT_OF_SIZE = unchecked((int)0x80431104), ERR_INVALID_URL = unchecked((int)0x80433060),
        ERR_UNKNOWN_SCHEME = unchecked((int)0x80431061);

    // SceHttpUriBuildType
    public const uint BuildScheme = 0x01, BuildHostname = 0x02, BuildPort = 0x04, BuildPath = 0x08, BuildUsername = 0x10,
        BuildPassword = 0x20, BuildQuery = 0x40, BuildFragment = 0x80, BuildAll = 0xFFFF;

    const string Hex = "0123456789ABCDEF";

    /// <summary>RFC 3986 "unreserved" characters, copied as-is by sceHttpUriEscape.</summary>
    public static bool Safe(byte b) => b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
        or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~';

    public static byte[] Escape(ReadOnlySpan<byte> input)
    {
        var o = new List<byte>(input.Length * 3);
        foreach (byte b in input)
        {
            if (Safe(b)) o.Add(b);
            else { o.Add((byte)'%'); o.Add((byte)Hex[b >> 4]); o.Add((byte)Hex[b & 15]); }
        }
        return o.ToArray();
    }

    static int HexVal(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>Decodes %HH sequences; invalid '%' and '+' are copied literally.</summary>
    public static byte[] Unescape(ReadOnlySpan<byte> input)
    {
        var o = new List<byte>(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '%' && i + 2 < input.Length && HexVal(input[i + 1]) >= 0 && HexVal(input[i + 2]) >= 0)
            {
                o.Add((byte)(HexVal(input[i + 1]) << 4 | HexVal(input[i + 2])));
                i += 2;
            }
            else o.Add(input[i]);
        }
        return o.ToArray();
    }

    /// <summary>Components of a URI reference (RFC 3986 §3). Null = component not present.</summary>
    public sealed class Parts
    {
        public string? Scheme;
        public string? Authority; // raw "[user[:pw]@]host[:port]"
        public string? User, Password, Host;
        public ushort Port;
        public string Path = "";
        public string? Query;     // without '?'
        public string? Fragment;  // without '#'
    }

    /// <summary>Splits a URI reference (absolute or relative) into its components.</summary>
    public static Parts Split(string s)
    {
        var p = new Parts();
        int i = 0;
        // scheme: ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ) ":"
        int colon = s.IndexOf(':');
        if (colon > 0 && char.IsAsciiLetter(s[0]))
        {
            bool ok = true;
            for (int k = 1; k < colon; k++)
                if (!(char.IsAsciiLetterOrDigit(s[k]) || s[k] is '+' or '-' or '.')) { ok = false; break; }
            if (ok) { p.Scheme = s[..colon]; i = colon + 1; }
        }
        int hash = s.IndexOf('#', i);
        if (hash >= 0) { p.Fragment = s[(hash + 1)..]; s = s[..hash]; }
        int q = s.IndexOf('?', i);
        if (q >= 0) { p.Query = s[(q + 1)..]; s = s[..q]; }
        if (s.Length - i >= 2 && s[i] == '/' && s[i + 1] == '/')
        {
            int start = i + 2, end = s.IndexOf('/', start);
            if (end < 0) end = s.Length;
            p.Authority = s[start..end];
            i = end;
            string host = p.Authority;
            int at = host.LastIndexOf('@');
            if (at >= 0)
            {
                string ui = host[..at];
                host = host[(at + 1)..];
                int c = ui.IndexOf(':');
                if (c >= 0) { p.User = ui[..c]; p.Password = ui[(c + 1)..]; }
                else p.User = ui;
            }
            int pc = host.LastIndexOf(':');
            if (pc >= 0 && !host.EndsWith(']'))
            {
                string port = host[(pc + 1)..];
                host = host[..pc];
                if (port.Length > 0 && ushort.TryParse(port, System.Globalization.NumberStyles.None, null, out var pv)) p.Port = pv;
                else if (port.Length > 0) p.Port = 0xFFFF; // marks an invalid port for strict parsing
            }
            p.Host = host;
        }
        p.Path = s[i..];
        return p;
    }

    /// <summary>Strict absolute http/https URL parsing (sceHttpUriParse, connection/request URLs).</summary>
    public static int ParseStrict(string url, out Parts p)
    {
        p = Split(url);
        if (p.Scheme == null) return ERR_INVALID_URL;
        if (!p.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !p.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return ERR_UNKNOWN_SCHEME;
        if (p.Authority == null || string.IsNullOrEmpty(p.Host) || p.Port == 0xFFFF) return ERR_INVALID_URL;
        if ((p.User?.Length ?? 0) >= 256 || (p.Password?.Length ?? 0) >= 256) return ERR_OUT_OF_SIZE;
        return 0;
    }

    /// <summary>RFC 3986 §5.2.4 (does not collapse "//").</summary>
    public static string RemoveDotSegments(string input)
    {
        var output = new StringBuilder();
        while (input.Length > 0)
        {
            if (input.StartsWith("../", StringComparison.Ordinal)) input = input[3..];
            else if (input.StartsWith("./", StringComparison.Ordinal)) input = input[2..];
            else if (input.StartsWith("/./", StringComparison.Ordinal)) input = input[2..];
            else if (input == "/.") input = "/";
            else if (input.StartsWith("/../", StringComparison.Ordinal) || input == "/..")
            {
                input = input.Length == 3 ? "/" : input[3..];
                RemoveLastSegment(output);
            }
            else if (input is "." or "..") input = "";
            else
            {
                int start = input[0] == '/' ? 1 : 0;
                int next = input.IndexOf('/', start);
                if (next < 0) next = input.Length;
                output.Append(input, 0, next);
                input = input[next..];
            }
        }
        return output.ToString();
    }

    static void RemoveLastSegment(StringBuilder sb)
    {
        int len = sb.Length;
        int idx = -1;
        for (int k = len - 1; k >= 0; k--) if (sb[k] == '/') { idx = k; break; }
        sb.Length = idx < 0 ? 0 : idx;
    }

    /// <summary>sceHttpUriSweepPath: removes dot segments from a path or from the path part of an absolute URL.</summary>
    public static string SweepPath(string input)
    {
        string prefix = "", suffix = "";
        int qf = input.IndexOfAny(['?', '#']);
        if (qf >= 0) { suffix = input[qf..]; input = input[..qf]; }
        string path;
        if (input.StartsWith('/')) path = input;
        else
        {
            int sch = input.IndexOf("://", StringComparison.Ordinal);
            if (sch < 0) return input + suffix;
            int slash = input.IndexOf('/', sch + 3);
            if (slash < 0) return input + suffix;
            prefix = input[..slash];
            path = input[slash..];
        }
        return prefix + RemoveDotSegments(path) + suffix;
    }

    /// <summary>Recomposes a reference (RFC 3986 §5.3).</summary>
    public static string Recompose(Parts p)
    {
        var sb = new StringBuilder();
        if (p.Scheme != null) sb.Append(p.Scheme).Append(':');
        if (p.Authority != null) sb.Append("//").Append(p.Authority);
        sb.Append(p.Path);
        if (p.Query != null) sb.Append('?').Append(p.Query);
        if (p.Fragment != null) sb.Append('#').Append(p.Fragment);
        return sb.ToString();
    }

    /// <summary>RFC 3986 §5.2.2 strict reference resolution (sceHttpUriMerge).</summary>
    public static int Merge(string baseUrl, string relative, out string result)
    {
        result = "";
        var b = Split(baseUrl);
        if (b.Scheme == null) return ERR_INVALID_URL;
        if (!b.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !b.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return ERR_UNKNOWN_SCHEME;
        var r = Split(relative);
        var t = new Parts();
        if (r.Scheme != null)
        {
            t.Scheme = r.Scheme; t.Authority = r.Authority; t.Path = RemoveDotSegments(r.Path); t.Query = r.Query;
        }
        else
        {
            if (r.Authority != null)
            {
                t.Authority = r.Authority; t.Path = RemoveDotSegments(r.Path); t.Query = r.Query;
            }
            else
            {
                if (r.Path == "")
                {
                    t.Path = b.Path;
                    t.Query = r.Query ?? b.Query;
                }
                else
                {
                    if (r.Path.StartsWith('/')) t.Path = RemoveDotSegments(r.Path);
                    else
                    {
                        string merged = b.Authority != null && b.Path == "" ? "/" + r.Path
                            : b.Path[..(b.Path.LastIndexOf('/') + 1)] + r.Path;
                        t.Path = RemoveDotSegments(merged);
                    }
                    t.Query = r.Query;
                }
                t.Authority = b.Authority;
            }
            t.Scheme = b.Scheme;
        }
        t.Fragment = r.Fragment;
        result = Recompose(t);
        return 0;
    }

    /// <summary>The fields of SceHttpUriElement (query keeps its leading '?', fragment its '#', as sceHttpUriParse writes them).</summary>
    public sealed record Element(bool Opaque, string Scheme, string User, string Password, string Host, string Path, string Query,
        string Fragment, ushort Port);

    public static Element ToElement(Parts p) => new(false, p.Scheme ?? "", p.User ?? "", p.Password ?? "", p.Host ?? "", p.Path,
        p.Query != null ? "?" + p.Query : "", p.Fragment != null ? "#" + p.Fragment : "", p.Port == 0xFFFF ? (ushort)0 : p.Port);

    /// <summary>sceHttpUriBuild: composes the selected fields of an element.</summary>
    public static string Build(Element e, uint option)
    {
        var sb = new StringBuilder();
        if ((option & BuildScheme) != 0 && e.Scheme != "")
        {
            sb.Append(e.Scheme).Append(':');
            if (!e.Opaque) sb.Append("//");
        }
        if (!e.Opaque)
        {
            if ((option & BuildUsername) != 0 && e.User != "")
            {
                sb.Append(e.User);
                if ((option & BuildPassword) != 0 && e.Password != "") sb.Append(':').Append(e.Password);
                sb.Append('@');
            }
            if ((option & BuildHostname) != 0) sb.Append(e.Host);
            if ((option & BuildPort) != 0 && e.Port != 0) sb.Append(':').Append(e.Port);
        }
        if ((option & BuildPath) != 0) sb.Append(e.Path);
        if ((option & BuildQuery) != 0 && e.Query != "") sb.Append(e.Query[0] == '?' ? "" : "?").Append(e.Query);
        if ((option & BuildFragment) != 0 && e.Fragment != "") sb.Append(e.Fragment[0] == '#' ? "" : "#").Append(e.Fragment);
        return sb.ToString();
    }
}

/// <summary>Pure HTTP text parsers behind sceHttpParseStatusLine / sceHttpParseResponseHeader.</summary>
public static class HttpParse
{
    public const int ERR_INVALID_RESPONSE = unchecked((int)0x80432060), ERR_NOT_FOUND = unchecked((int)0x80432025);

    /// <summary>Parses "HTTP/x.y nnn reason". Offsets are relative to <paramref name="line"/>.</summary>
    public static int StatusLine(ReadOnlySpan<byte> line, out int major, out int minor, out int code, out int phraseOff, out int phraseLen)
    {
        major = minor = code = phraseOff = phraseLen = 0;
        int l = line.Length;
        int cr = line.IndexOfAny((byte)'\r', (byte)'\n');
        if (cr >= 0) l = cr;
        line = line[..l];
        if (l < 12 || !line.StartsWith("HTTP/"u8)) return ERR_INVALID_RESPONSE;
        int i = 5;
        int Digits(ReadOnlySpan<byte> s, ref int pos, out int value)
        {
            int st = pos; value = 0;
            while (pos < s.Length && s[pos] is >= (byte)'0' and <= (byte)'9' && pos - st < 9) value = value * 10 + (s[pos++] - '0');
            return pos - st;
        }
        if (Digits(line, ref i, out int maj) == 0 || i >= l || line[i] != '.') return ERR_INVALID_RESPONSE;
        i++;
        if (Digits(line, ref i, out int min) == 0 || i >= l || line[i] != ' ') return ERR_INVALID_RESPONSE;
        while (i < l && line[i] == ' ') i++;
        if (Digits(line, ref i, out int c) != 3) return ERR_INVALID_RESPONSE;
        if (i < l && line[i] != ' ') return ERR_INVALID_RESPONSE;
        major = maj; minor = min; code = c;
        if (i < l) i++;
        phraseOff = i; phraseLen = l - i;
        return 0;
    }

    /// <summary>Finds header <paramref name="name"/> (case-insensitive); value offset/length exclude surrounding whitespace.</summary>
    public static bool FindHeader(ReadOnlySpan<byte> header, ReadOnlySpan<byte> name, out int valOff, out int valLen)
    {
        valOff = valLen = 0;
        int pos = 0;
        while (pos < header.Length)
        {
            int nl = header[pos..].IndexOf((byte)'\n');
            int end = nl < 0 ? header.Length : pos + nl;
            int lineEnd = end > pos && header[end - 1] == '\r' ? end - 1 : end;
            var line = header[pos..lineEnd];
            int colon = line.IndexOf((byte)':');
            if (colon > 0)
            {
                var n = line[..colon];
                while (n.Length > 0 && n[^1] is (byte)' ' or (byte)'\t') n = n[..^1];
                if (EqualsIgnoreCase(n, name))
                {
                    int vs = colon + 1;
                    while (vs < line.Length && line[vs] is (byte)' ' or (byte)'\t') vs++;
                    int ve = line.Length;
                    while (ve > vs && line[ve - 1] is (byte)' ' or (byte)'\t') ve--;
                    valOff = pos + vs; valLen = ve - vs;
                    return true;
                }
            }
            pos = end + 1;
        }
        return false;
    }

    static bool EqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (char.ToLowerInvariant((char)a[i]) != char.ToLowerInvariant((char)b[i])) return false;
        return true;
    }
}
