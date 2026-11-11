using System.Globalization;
using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// eglib, second part: Unicode conversion, character classes, paths, files and directories, the markup (XML subset)
/// parser, logging, timers and the process facts (user, home, temporary folder, charset).
/// </summary>
public sealed unsafe partial class MonoBridge
{
    // GError domains: eglib's quarks are constants; the codes are GLib's
    const uint ConvertDomain = 0, FileDomain = 0, MarkupDomain = 0, SpawnDomain = 0;
    const int ConvertNoConversion = 0, ConvertIllegalSequence = 1, ConvertPartialInput = 3, ConvertBadUri = 4,
        ConvertNotAbsolutePath = 5;
    const int FileNoent = 4, FileAcces = 2, FileIsdir = 1, FileExist = 0, FileFailed = 24;
    const int MarkupParse = 1;
    const int SpawnFailed = 19;

    // ------------------------------------------------------------------ UTF-8 / UTF-16 / UCS-4

    /// <summary>Decodes UTF-8 code points; stops at the first invalid or (when <paramref name="final"/>) truncated
    /// sequence: returns false with <paramref name="bad"/> = its offset and <paramref name="partial"/> for a truncated one.</summary>
    static bool DecodeUtf8(ReadOnlySpan<byte> s, List<int> cps, out int bad, out bool partial)
    {
        partial = false;
        for (int i = 0; i < s.Length;)
        {
            int c = s[i], need, cp;
            if (c < 0x80) { cps.Add(c); i++; continue; }
            if (c is >= 0xC2 and <= 0xDF) { need = 1; cp = c & 0x1F; }
            else if (c is >= 0xE0 and <= 0xEF) { need = 2; cp = c & 0x0F; }
            else if (c is >= 0xF0 and <= 0xF4) { need = 3; cp = c & 0x07; }
            else { bad = i; return false; }
            if (i + need >= s.Length)
            {
                // truncated, unless the bytes present are already wrong
                for (int k = i + 1; k < s.Length; k++) if ((s[k] & 0xC0) != 0x80) { bad = i; return false; }
                bad = i; partial = true; return false;
            }
            for (int k = 1; k <= need; k++)
            {
                if ((s[i + k] & 0xC0) != 0x80) { bad = i; return false; }
                cp = (cp << 6) | (s[i + k] & 0x3F);
            }
            if ((need == 2 && cp < 0x800) || (need == 3 && (cp < 0x10000 || cp > 0x10FFFF)) || cp is >= 0xD800 and <= 0xDFFF) { bad = i; return false; }
            cps.Add(cp);
            i += need + 1;
        }
        bad = -1;
        return true;
    }

    static byte[] EncodeUtf8(List<int> cps)
    {
        var sb = new StringBuilder();
        foreach (int cp in cps) sb.Append(char.ConvertFromUtf32(cp));
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    void Wr(uint p, uint v) { if (p != 0) W32(p, v); }

    /// <summary>g_utf8_to_utf16(str, len, items_read, items_written, error): len &lt; 0 = up to the NUL; a NUL-terminated
    /// malloc'ed result, NULL with G_CONVERT_ERROR_ILLEGAL_SEQUENCE (or PARTIAL_INPUT) at a bad sequence.</summary>
    [Hle("monoeg_g_utf8_to_utf16", Library = Lib)]
    uint GUtf8ToUtf16(uint str, int len, uint read, uint written, uint err)
    {
        var b = Bytes(str, len);
        var cps = new List<int>();
        if (!DecodeUtf8(b, cps, out int bad, out bool partial))
        {
            if (partial && read != 0) { /* stop before the truncated sequence */ }
            else
            {
                Wr(read, (uint)bad);
                Wr(written, 0);
                SetError(err, ConvertDomain, partial ? ConvertPartialInput : ConvertIllegalSequence,
                    partial ? "Partial byte sequence encountered in the input." : "Illegal byte sequence encounted in the input.");
                return 0;
            }
        }
        var units = new List<ushort>();
        foreach (int cp in cps)
        {
            if (cp < 0x10000) units.Add((ushort)cp);
            else { int v = cp - 0x10000; units.Add((ushort)(0xD800 + (v >> 10))); units.Add((ushort)(0xDC00 + (v & 0x3FF))); }
        }
        uint p = Malloc((uint)(units.Count + 1) * 2);
        for (int i = 0; i < units.Count; i++) GuestMemory.Write(p + (uint)i * 2, units[i]);
        GuestMemory.Write(p + (uint)units.Count * 2, (ushort)0);
        Wr(read, (uint)(partial ? bad : b.Length));
        Wr(written, (uint)units.Count);
        return p;
    }

    static List<ushort> Utf16Units(uint str, int len)
    {
        var u = new List<ushort>();
        if (str == 0) return u;
        for (int i = 0; len < 0 || i < len; i++)
        {
            ushort c = R16(str + (uint)i * 2);
            if (len < 0 && c == 0) break;
            u.Add(c);
        }
        return u;
    }

    /// <summary>UTF-16 units to code points; false at a lone surrogate (its index in <paramref name="bad"/>).</summary>
    static bool DecodeUtf16(List<ushort> u, List<int> cps, out int bad)
    {
        for (int i = 0; i < u.Count; i++)
        {
            int c = u[i];
            if (c is >= 0xD800 and <= 0xDBFF)
            {
                if (i + 1 >= u.Count || u[i + 1] is < 0xDC00 or > 0xDFFF) { bad = i; return false; }
                cps.Add(0x10000 + ((c - 0xD800) << 10) + (u[i + 1] - 0xDC00));
                i++;
            }
            else if (c is >= 0xDC00 and <= 0xDFFF) { bad = i; return false; }
            else cps.Add(c);
        }
        bad = -1;
        return true;
    }

    [Hle("monoeg_g_utf16_to_utf8", Library = Lib)]
    uint GUtf16ToUtf8(uint str, int len, uint read, uint written, uint err)
    {
        var u = Utf16Units(str, len);
        var cps = new List<int>();
        if (!DecodeUtf16(u, cps, out int bad))
        {
            Wr(read, (uint)bad); Wr(written, 0);
            SetError(err, ConvertDomain, ConvertIllegalSequence, "Illegal byte sequence encounted in the input.");
            return 0;
        }
        byte[] o = EncodeUtf8(cps);
        Wr(read, (uint)u.Count); Wr(written, (uint)o.Length);
        return DupBytes(o);
    }

    uint Ucs4Out(List<int> cps)
    {
        uint p = Malloc((uint)(cps.Count + 1) * 4);
        for (int i = 0; i < cps.Count; i++) W32(p + (uint)i * 4, (uint)cps[i]);
        W32(p + (uint)cps.Count * 4, 0);
        return p;
    }

    [Hle("monoeg_g_utf16_to_ucs4", Library = Lib)]
    uint GUtf16ToUcs4(uint str, int len, uint read, uint written, uint err)
    {
        var u = Utf16Units(str, len);
        var cps = new List<int>();
        if (!DecodeUtf16(u, cps, out int bad))
        {
            Wr(read, (uint)bad); Wr(written, 0);
            SetError(err, ConvertDomain, ConvertIllegalSequence, "Illegal byte sequence encounted in the input.");
            return 0;
        }
        Wr(read, (uint)u.Count); Wr(written, (uint)cps.Count);
        return Ucs4Out(cps);
    }

    [Hle("monoeg_g_ucs4_to_utf16", Library = Lib)]
    uint GUcs4ToUtf16(uint str, int len, uint read, uint written, uint err)
    {
        var units = new List<ushort>();
        int i = 0;
        for (; str != 0 && (len < 0 || i < len); i++)
        {
            uint c = R32(str + (uint)i * 4);
            if (len < 0 && c == 0) break;
            if (c > 0x10FFFF || c is >= 0xD800 and <= 0xDFFF)
            {
                Wr(read, (uint)i); Wr(written, 0);
                SetError(err, ConvertDomain, ConvertIllegalSequence, "Illegal byte sequence encounted in the input.");
                return 0;
            }
            if (c < 0x10000) units.Add((ushort)c);
            else { uint v = c - 0x10000; units.Add((ushort)(0xD800 + (v >> 10))); units.Add((ushort)(0xDC00 + (v & 0x3FF))); }
        }
        uint p = Malloc((uint)(units.Count + 1) * 2);
        for (int k = 0; k < units.Count; k++) GuestMemory.Write(p + (uint)k * 2, units[k]);
        GuestMemory.Write(p + (uint)units.Count * 2, (ushort)0);
        Wr(read, (uint)i); Wr(written, (uint)units.Count);
        return p;
    }

    /// <summary>g_utf8_validate(str, max_len, end): max_len &lt; 0 = up to the NUL; *end = the first invalid byte or the end.</summary>
    [Hle("monoeg_g_utf8_validate", Library = Lib)]
    bool GUtf8Validate(uint str, int maxLen, uint end)
    {
        var b = Bytes(str, maxLen);
        var cps = new List<int>();
        bool ok = DecodeUtf8(b, cps, out int bad, out _);
        Wr(end, str + (uint)(ok ? b.Length : bad));
        return ok;
    }

    /// <summary>g_utf8_strdown(str, len): a lower-case copy (len &lt; 0 = the whole string).</summary>
    [Hle("monoeg_g_utf8_strdown", Library = Lib)]
    uint GUtf8Strdown(uint str, int len) => str == 0 ? 0 : StrDup(Encoding.UTF8.GetString(Bytes(str, len)).ToLowerInvariant());

    [Hle("monoeg_g_unichar_tolower", Library = Lib)]
    uint GUnicharTolower(uint c) => Rune.IsValid(c) ? (uint)Rune.ToLowerInvariant(new Rune(c)).Value : c;

    [Hle("monoeg_g_unichar_xdigit_value", Library = Lib)]
    int GUnicharXdigitValue(uint c) => c < 0x80 ? GAsciiXdigitValue((int)c)
        : c is >= 0xFF10 and <= 0xFF19 ? (int)(c - 0xFF10) : c is >= 0xFF21 and <= 0xFF26 ? (int)(c - 0xFF21 + 10)
        : c is >= 0xFF41 and <= 0xFF46 ? (int)(c - 0xFF41 + 10) : -1;

    /// <summary>g_unichar_type(c): GUnicodeType (GLib's order) from the character's Unicode category.</summary>
    [Hle("monoeg_g_unichar_type", Library = Lib)]
    int GUnicharType(uint c)
    {
        if (!Rune.IsValid(c)) return c is >= 0xD800 and <= 0xDFFF ? 4 : 2;
        return Rune.GetUnicodeCategory(new Rune(c)) switch
        {
            UnicodeCategory.Control => 0, UnicodeCategory.Format => 1, UnicodeCategory.OtherNotAssigned => 2,
            UnicodeCategory.PrivateUse => 3, UnicodeCategory.Surrogate => 4, UnicodeCategory.LowercaseLetter => 5,
            UnicodeCategory.ModifierLetter => 6, UnicodeCategory.OtherLetter => 7, UnicodeCategory.TitlecaseLetter => 8,
            UnicodeCategory.UppercaseLetter => 9, UnicodeCategory.SpacingCombiningMark => 10, UnicodeCategory.EnclosingMark => 11,
            UnicodeCategory.NonSpacingMark => 12, UnicodeCategory.DecimalDigitNumber => 13, UnicodeCategory.LetterNumber => 14,
            UnicodeCategory.OtherNumber => 15, UnicodeCategory.ConnectorPunctuation => 16, UnicodeCategory.DashPunctuation => 17,
            UnicodeCategory.ClosePunctuation => 18, UnicodeCategory.FinalQuotePunctuation => 19, UnicodeCategory.InitialQuotePunctuation => 20,
            UnicodeCategory.OtherPunctuation => 21, UnicodeCategory.OpenPunctuation => 22, UnicodeCategory.CurrencySymbol => 23,
            UnicodeCategory.ModifierSymbol => 24, UnicodeCategory.MathSymbol => 25, UnicodeCategory.OtherSymbol => 26,
            UnicodeCategory.LineSeparator => 27, UnicodeCategory.ParagraphSeparator => 28, UnicodeCategory.SpaceSeparator => 29,
            _ => 2,
        };
    }

    static Encoding? CodesetEncoding(string name) => name.ToUpperInvariant().Replace("_", "-") switch
    {
        "UTF-8" or "UTF8" => new UTF8Encoding(false, true),
        "UTF-16" or "UTF-16LE" or "UTF16" or "UCS-2" or "UCS-2LE" or "UNICODE" => new UnicodeEncoding(false, false, true),
        "UTF-16BE" or "UCS-2BE" => new UnicodeEncoding(true, false, true),
        "UTF-32" or "UTF-32LE" or "UCS-4" or "UCS-4LE" or "UTF32" => new UTF32Encoding(false, false, true),
        "UTF-32BE" or "UCS-4BE" => new UTF32Encoding(true, false, true),
        "ISO-8859-1" or "LATIN1" or "ISO8859-1" => Encoding.Latin1,
        "ASCII" or "US-ASCII" or "ANSI-X3.4-1968" => Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => null,
    };

    /// <summary>g_convert(str, len, to, from, bytes_read, bytes_written, error): between the Unicode encodings, Latin-1
    /// and ASCII; the result ends with four NUL bytes (a terminator for any of them).</summary>
    [Hle("monoeg_g_convert", Library = Lib)]
    uint GConvert(uint str, int len, uint to, uint from, uint read, uint written, uint err)
    {
        if (str == 0) return 0;
        var te = CodesetEncoding(Utf8(to));
        var fe = CodesetEncoding(Utf8(from));
        if (te == null || fe == null)
        {
            SetError(err, ConvertDomain, ConvertNoConversion, $"Conversion from {Utf8(from)} to {Utf8(to)} not supported.");
            return 0;
        }
        byte[] input = len >= 0 ? Bytes(str, len) : fe is UnicodeEncoding ? TerminatedUnits(str, 2) : fe is UTF32Encoding ? TerminatedUnits(str, 4) : Bytes(str);
        byte[] output;
        try { output = te.GetBytes(fe.GetString(input)); }
        catch (Exception e) when (e is DecoderFallbackException or EncoderFallbackException)
        {
            SetError(err, ConvertDomain, ConvertIllegalSequence, "Illegal byte sequence encounted in the input.");
            return 0;
        }
        uint p = Malloc((uint)output.Length + 4);
        output.CopyTo(GuestMemory.Span(p, output.Length));
        W32(p + (uint)output.Length, 0);
        Wr(read, (uint)input.Length); Wr(written, (uint)output.Length);
        return p;
    }

    static byte[] TerminatedUnits(uint s, int unit)
    {
        uint n = 0;
        while (true)
        {
            bool zero = true;
            for (int k = 0; k < unit; k++) if (R8(s + n + (uint)k) != 0) zero = false;
            if (zero) break;
            n += (uint)unit;
        }
        return GuestMemory.ReadSpan(s, (int)n).ToArray();
    }

    /// <summary>The system's text is UTF-8: g_locale_to/from_utf8 and g_filename_from_utf8 copy len bytes (len &lt; 0:
    /// the string), checking the UTF-8.</summary>
    [Hle("monoeg_g_locale_to_utf8", Library = Lib)]
    [Hle("monoeg_g_locale_from_utf8", Library = Lib)]
    [Hle("monoeg_g_filename_from_utf8", Library = Lib)]
    uint GLocaleCopy(uint str, int len, uint read, uint written, uint err)
    {
        if (str == 0) return 0;
        var b = Bytes(str, len);
        var cps = new List<int>();
        if (!DecodeUtf8(b, cps, out int bad, out _))
        {
            Wr(read, (uint)bad);
            SetError(err, ConvertDomain, ConvertIllegalSequence, "Invalid sequence in conversion input");
            return 0;
        }
        Wr(read, (uint)b.Length); Wr(written, (uint)b.Length);
        return DupBytes(b);
    }

    /// <summary>g_get_charset(&amp;charset): "UTF-8", TRUE (the charset is UTF-8).</summary>
    [Hle("monoeg_g_get_charset", Library = Lib)]
    bool GGetCharset(uint charset) { Wr(charset, C.ConstString("UTF-8")); return true; }

    static bool UriKeeps(byte c) => c < 0x80 && ((c >= '@' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '&' && c < 0x3B)
        || c == '!' || c == '$' || c == '_' || c == '=' || c == '~');

    /// <summary>g_filename_to_uri(filename, hostname, error): "file://" and the path with every other byte as %XX; a
    /// relative path is G_CONVERT_ERROR_NOT_ABSOLUTE_PATH.</summary>
    [Hle("monoeg_g_filename_to_uri", Library = Lib)]
    uint GFilenameToUri(uint filename, uint hostname, uint err)
    {
        if (filename == 0) return 0;
        var b = Bytes(filename);
        if (b.Length == 0 || b[0] != '/')
        {
            SetError(err, ConvertDomain, ConvertNotAbsolutePath, $"The filename '{Utf8(filename)}' is not an absolute path");
            return 0;
        }
        var sb = new StringBuilder("file://");
        foreach (byte c in b) sb.Append(UriKeeps(c) ? ((char)c).ToString() : $"%{c:X2}");
        return StrDup(sb.ToString());
    }

    /// <summary>g_filename_from_uri(uri, hostname, error): the %XX-decoded path of a "file://" URI (*hostname NULL).</summary>
    [Hle("monoeg_g_filename_from_uri", Library = Lib)]
    uint GFilenameFromUri(uint uri, uint hostname, uint err)
    {
        if (uri == 0) return 0;
        var b = Bytes(uri);
        if (!b.AsSpan().StartsWith("file://"u8))
        {
            SetError(err, ConvertDomain, ConvertBadUri, $"URI does not start with the file: scheme");
            return 0;
        }
        var o = new List<byte>();
        for (int i = 7; i < b.Length; i++)
        {
            if (b[i] == '%')
            {
                int hi = i + 2 < b.Length ? GAsciiXdigitValue(b[i + 1]) : -1, lo = hi >= 0 ? GAsciiXdigitValue(b[i + 2]) : -1;
                if (hi < 0 || lo < 0)
                {
                    SetError(err, ConvertDomain, ConvertBadUri, "URI contains an invalid escaped character");
                    return 0;
                }
                o.Add((byte)(hi * 16 + lo));
                i += 2;
            }
            else o.Add(b[i]);
        }
        Wr(hostname, 0);
        return DupBytes(o.ToArray());
    }

    // ------------------------------------------------------------------ paths

    /// <summary>g_build_path(separator, first, ..., NULL): the elements joined by one separator each (separators at the
    /// joins collapse; the first element's leading and the last one's trailing ones are kept).</summary>
    [Hle("monoeg_g_build_path", Library = Lib)]
    uint GBuildPath(uint separator, uint first, VarArgs va)
    {
        var list = new List<uint>();
        for (uint e = first; e != 0; e = va.NextU32()) list.Add(e);
        return BuildPathFrom(separator, list.ToArray());
    }

    uint BuildPathFrom(uint separator, uint[] elements)
    {
        byte[] sep = Bytes(separator);
        var elems = elements.Select(e => Bytes(e)).ToList();
        var parts = elems.Where(e => e.Length > 0).ToList();
        if (sep.Length == 0) return DupBytes(parts.SelectMany(p => p).ToArray());
        var o = new List<byte>();
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i].AsSpan();
            int start = 0, end = p.Length;
            if (i > 0) while (start < end && p[start..end].StartsWith(sep)) start += sep.Length;
            if (i < parts.Count - 1) while (end > start && p[start..end].EndsWith(sep)) end -= sep.Length;
            bool lastAllSeparators = i > 0 && i == parts.Count - 1 && start >= end;
            if (i > 0 && (start < end || lastAllSeparators) && !o.AsReadOnlySpanEndsWith(sep)) o.AddRange(sep);
            for (int k = start; k < end; k++) o.Add(p[k]);
        }
        return DupBytes(o.ToArray());
    }

    [Hle("monoeg_g_path_get_basename", Library = Lib)]
    uint GPathGetBasename(uint filename)
    {
        var b = Bytes(filename);
        if (b.Length == 0) return StrDup(".");
        int r = Array.LastIndexOf(b, (byte)'/');
        if (r < 0) return DupBytes(b);
        if (r == b.Length - 1)
        {
            int r2 = Array.LastIndexOf(b, (byte)'/', Math.Max(r - 1, 0));
            if (r == 0 || r2 < 0) return r == 0 ? StrDup("/") : DupBytes(b[..r]);
            return DupBytes(b[(r2 + 1)..r]);
        }
        return DupBytes(b[(r + 1)..]);
    }

    [Hle("monoeg_g_path_get_dirname", Library = Lib)]
    uint GPathGetDirname(uint filename)
    {
        var b = Bytes(filename);
        int p = Array.LastIndexOf(b, (byte)'/');
        if (p < 0) return StrDup(".");
        if (p == 0) return StrDup("/");
        return DupBytes(b[..p]);
    }

    /// <summary>g_path_is_absolute: a '/' path or a device path ("app0:...").</summary>
    [Hle("monoeg_g_path_is_absolute", Library = Lib)]
    bool GPathIsAbsolute(uint filename)
    {
        var s = Utf8(filename);
        if (s.Length == 0) return false;
        if (s[0] == '/') return true;
        int colon = s.IndexOf(':'), slash = s.IndexOf('/');
        return colon > 0 && (slash < 0 || colon < slash);
    }

    // The PSM runtime's file system: /Application (the program, read only), /Documents (saved data), /Temp (scratch);
    // device paths pass through. Relative paths are relative to the bridge's current folder.
    string _cwd = "/Application";

    string FullPath(string p)
    {
        if (p.Length == 0) return _cwd;
        if (p[0] == '/' || (p.IndexOf(':') is var c and > 0 && (p.IndexOf('/') is var s && (s < 0 || c < s)))) return p;
        return _cwd.TrimEnd('/') + "/" + p;
    }

    /// <summary>The device path of a PSM path (inferred mapping: /Application = app0:, /Documents = savedata0:,
    /// /Temp = ux0:temp).</summary>
    string PsmPath(string p)
    {
        p = FullPath(p);
        static string Map(string p, string root, string dev) =>
            p.Equals(root, StringComparison.OrdinalIgnoreCase) ? dev : p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? dev + p[(root.Length + 1)..] : "";
        foreach (var (root, dev) in new[] { ("/Application", "app0:"), ("/Documents", "savedata0:"), ("/Temp", "ux0:temp/") })
            if (Map(p, root, dev) is { Length: > 0 } m) return m;
        return p;
    }

    [Hle("monoeg_g_get_current_dir", Library = Lib)] uint GGetCurrentDir() => StrDup(_cwd);
    [Hle("g_file_vita_get_current_dir", Library = Lib)] uint GVitaGetCurrentDir() => StrDup(_cwd);

    /// <summary>g_file_vita_set_current_dir(path): the folder relative paths resolve against (inferred: 0 / -1).</summary>
    [Hle("g_file_vita_set_current_dir", Library = Lib)]
    int GVitaSetCurrentDir(uint path)
    {
        if (path == 0) return -1;
        string p = FullPath(Utf8(path));
        if (Io.sceIoDopen(PsmPath(p)) is var fd and >= 0) { Io.sceIoDclose(fd); _cwd = p; return 0; }
        Libc.SetErrno(Libc.ENOENT);
        return -1;
    }

    /// <summary>g_file_vita_get_full_path(path): a malloc'ed absolute path (against the current folder).</summary>
    [Hle("g_file_vita_get_full_path", Library = Lib)] uint GVitaGetFullPath(uint path) => path == 0 ? 0 : StrDup(FullPath(Utf8(path)));

    [Hle("monoeg_g_get_home_dir", Library = Lib)] uint GGetHomeDir() => C.ConstString("/Documents");
    [Hle("monoeg_g_get_tmp_dir", Library = Lib)] uint GGetTmpDir() => C.ConstString("/Temp");
    [Hle("monoeg_g_get_user_name", Library = Lib)] uint GGetUserName() => C.ConstString("somebody");

    /// <summary>g_find_program_in_path: there are no programs to run by name on the system (NULL).</summary>
    [Hle("monoeg_g_find_program_in_path", Library = Lib)] uint GFindProgramInPath(uint program) => 0;

    /// <summary>g_spawn_async_with_pipes(...): the system starts no child processes; FALSE with G_SPAWN_ERROR_FAILED.</summary>
    [Hle("monoeg_g_spawn_async_with_pipes", Library = Lib)]
    bool GSpawnAsyncWithPipes(HleContext c)
    {
        uint err = c.StackWord(24); // working_directory, argv, envp, flags | child_setup, user_data, pid, stdin, stdout, stderr, error
        SetError(err, SpawnDomain, SpawnFailed, "Processes cannot be started on this system");
        return false;
    }

    /// <summary>g_shell_quote(s): the string in single quotes, each ' as '\''.</summary>
    [Hle("monoeg_g_shell_quote", Library = Lib)]
    uint GShellQuote(uint unquoted) => unquoted == 0 ? 0 : StrDup("'" + Utf8(unquoted).Replace("'", "'\\''") + "'");

    string? _prgname;
    [Hle("monoeg_g_set_prgname", Library = Lib)] void GSetPrgname(uint name) => _prgname = name == 0 ? null : Utf8(name);

    // ------------------------------------------------------------------ files

    const int SCE_O_RDONLY = 1, SCE_O_RDWR = 3, SCE_O_CREAT = 0x200, SCE_O_EXCL = 0x800;
    const uint SCE_S_IFDIR = 0x1000, SCE_S_IFREG = 0x2000, SCE_S_IFLNK = 0x4000, SCE_S_IXUSR = 0x40;

    int FileErrorCode(int sceErr) => IoErrno(sceErr) switch
    {
        Libc.ENOENT => FileNoent, Libc.EACCES => FileAcces, Libc.EISDIR => FileIsdir, Libc.EEXIST => FileExist, _ => FileFailed,
    };

    /// <summary>g_file_get_contents(filename, contents, length, error): the whole file, malloc'ed and NUL-terminated.</summary>
    [Hle("monoeg_g_file_get_contents", Library = Lib)]
    bool GFileGetContents(uint filename, uint contents, uint length, uint err)
    {
        if (filename == 0 || contents == 0) return false;
        W32(contents, 0);
        string name = Utf8(filename);
        int fd = Io.sceIoOpen(PsmPath(name), SCE_O_RDONLY, 0);
        if (fd < 0) { SetError(err, FileDomain, FileErrorCode(fd), $"Error opening file '{name}'"); return false; }
        long size = Io.sceIoLseek(fd, 0, 2);
        Io.sceIoLseek(fd, 0, 0);
        if (size < 0 || size > 0x7FFFFFFF) { Io.sceIoClose(fd); SetError(err, FileDomain, FileFailed, $"Error reading file '{name}'"); return false; }
        uint buf = Malloc((uint)size + 1), got = 0;
        while (got < size)
        {
            int r = Io.sceIoRead(fd, buf + got, (uint)size - got);
            if (r <= 0) break;
            got += (uint)r;
        }
        Io.sceIoClose(fd);
        W8(buf + got, 0);
        W32(contents, buf);
        Wr(length, got);
        return true;
    }

    /// <summary>g_file_open_tmp(tmpl, name_used, error): a new file in the temporary folder ("XXXXXX" replaced),
    /// open for reading and writing; its descriptor, -1 with an error.</summary>
    [Hle("monoeg_g_file_open_tmp", Library = Lib)]
    int GFileOpenTmp(uint tmpl, uint nameUsed, uint err)
    {
        string t = tmpl == 0 ? ".XXXXXX" : Utf8(tmpl);
        if (!t.EndsWith("XXXXXX") || t.Contains('/'))
        {
            SetError(err, FileDomain, FileFailed, $"Invalid template '{t}'");
            return -1;
        }
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var name = new StringBuilder(t[..^6]);
            for (int i = 0; i < 6; i++) name.Append(chars[Random.Shared.Next(chars.Length)]);
            string full = "/Temp/" + name;
            int fd = Io.sceIoOpen(PsmPath(full), SCE_O_RDWR | SCE_O_CREAT | SCE_O_EXCL, 0x1B6);
            if (fd == unchecked((int)0x80010011)) continue; // exists: next name
            if (fd < 0) { SetError(err, FileDomain, FileErrorCode(fd), $"Error creating temporary file '{full}'"); return -1; }
            Wr(nameUsed, StrDup(full));
            return fd;
        }
        SetError(err, FileDomain, FileExist, "No free temporary file name");
        return -1;
    }

    /// <summary>g_file_test(filename, test): G_FILE_TEST_IS_REGULAR 1, IS_SYMLINK 2, IS_DIR 4, IS_EXECUTABLE 8, EXISTS 16.</summary>
    [Hle("monoeg_g_file_test", Library = Lib)]
    bool GFileTest(uint filename, int test)
    {
        if (filename == 0 || test == 0) return false;
        uint st = Malloc0(0x60);
        bool ok = Io.sceIoGetstat(PsmPath(Utf8(filename)), st) >= 0;
        uint mode = R32(st);
        Free(st);
        if (!ok) return false;
        return ((test & 16) != 0)
            || ((test & 1) != 0 && (mode & SCE_S_IFREG) != 0)
            || ((test & 4) != 0 && (mode & SCE_S_IFDIR) != 0)
            || ((test & 2) != 0 && (mode & SCE_S_IFLNK) != 0)
            || ((test & 8) != 0 && (mode & SCE_S_IXUSR) != 0 && (mode & SCE_S_IFDIR) == 0);
    }

    // GDir: { SceUID fd, ..., SceIoDirent at +0x10 (the name at +0x58 of it) }
    const uint DirentAt = 0x10, DirentSize = 0x160;

    [Hle("monoeg_g_dir_open", Library = Lib)]
    uint GDirOpen(uint path, uint flags, uint err)
    {
        if (path == 0) return 0;
        string p = Utf8(path);
        int fd = Io.sceIoDopen(PsmPath(p));
        if (fd < 0) { SetError(err, FileDomain, FileErrorCode(fd), $"Error opening directory '{p}'"); return 0; }
        uint d = Malloc0(DirentAt + DirentSize);
        W32(d, (uint)fd);
        return d;
    }

    /// <summary>g_dir_read_name(dir): the next entry's name ("." and ".." skipped), NULL at the end.</summary>
    [Hle("monoeg_g_dir_read_name", Library = Lib)]
    uint GDirReadName(uint dir)
    {
        if (dir == 0) return 0;
        while (true)
        {
            if (Io.sceIoDread((int)R32(dir), dir + DirentAt) <= 0) return 0;
            uint name = dir + DirentAt + 0x58;
            string n = Utf8(name);
            if (n is "." or "..") continue;
            return name;
        }
    }

    [Hle("monoeg_g_dir_close", Library = Lib)]
    void GDirClose(uint dir)
    {
        if (dir == 0) return;
        Io.sceIoDclose((int)R32(dir));
        Free(dir);
    }

    // ------------------------------------------------------------------ logging

    // GLogLevelFlags: ERROR 4, CRITICAL 8, WARNING 16, MESSAGE 32, INFO 64, DEBUG 128; FATAL 2
    uint _alwaysFatal = 4, _fatalMask = 4;

    [Hle("monoeg_g_log_set_always_fatal", Library = Lib)]
    uint GLogSetAlwaysFatal(uint mask) { uint old = _alwaysFatal; _alwaysFatal = mask; return old; }

    [Hle("monoeg_g_log_set_fatal_mask", Library = Lib)]
    uint GLogSetFatalMask(uint domain, uint mask) { uint old = _fatalMask; _fatalMask = mask; return old; }

    void LogMessage(uint domain, uint level, string msg)
    {
        string d = domain == 0 ? "" : Utf8(domain) + ": ";
        string line = $"[mono] {d}{msg}";
        if ((level & (4 | 8)) != 0) Log.Error(line);
        else if ((level & 16) != 0) Log.Warn(line);
        else Log.Info(line);
        if ((level & (_alwaysFatal | _fatalMask | 2)) != 0) Abort();
    }

    void Abort()
    {
        Log.Error("[mono] fatal message: the program stops (abort)");
        Emu.Hle.Modules.OfType<ProcessMgr>().First().ExitProcess(1);
    }

    [Hle("monoeg_g_log", Library = Lib)] void GLog(uint domain, uint level, uint fmt, VarArgs va) => LogMessage(domain, level, Fmt(fmt, va));
    [Hle("monoeg_g_logv", Library = Lib)] void GLogv(uint domain, uint level, uint fmt, uint va) => LogMessage(domain, level, Fmt(fmt, Va(va)));

    [Hle("monoeg_g_print", Library = Lib)] void GPrint(uint fmt, VarArgs va) => Log.Info("[mono] " + Fmt(fmt, va).TrimEnd('\n'));
    [Hle("monoeg_g_printerr", Library = Lib)] void GPrinterr(uint fmt, VarArgs va) => Log.Warn("[mono] " + Fmt(fmt, va).TrimEnd('\n'));

    /// <summary>g_assertion_message(format, ...): prints the message and stops the program.</summary>
    [Hle("monoeg_assertion_message", Library = Lib)]
    void GAssertionMessage(uint fmt, VarArgs va) { Log.Error("[mono] " + Fmt(fmt, va).TrimEnd('\n')); Abort(); }

    // ------------------------------------------------------------------ GTimer { timeval start; timeval stop }

    static (uint Sec, uint Usec) Now()
    {
        long us = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;
        return ((uint)(us / 1_000_000), (uint)(us % 1_000_000));
    }

    [Hle("monoeg_g_timer_new", Library = Lib)]
    uint GTimerNew() { uint t = Malloc0(16); GTimerStart(t); return t; }

    [Hle("monoeg_g_timer_destroy", Library = Lib)] void GTimerDestroy(uint t) => Free(t);

    [Hle("monoeg_g_timer_start", Library = Lib)]
    void GTimerStart(uint t) { var (s, u) = Now(); W32(t, s); W32(t + 4, u); W32(t + 8, 0); W32(t + 12, 0); }

    [Hle("monoeg_g_timer_stop", Library = Lib)]
    void GTimerStop(uint t) { var (s, u) = Now(); W32(t + 8, s); W32(t + 12, u); }

    /// <summary>g_timer_elapsed(timer, microseconds): seconds since start (to stop, when stopped); *microseconds = the
    /// fractional part.</summary>
    [Hle("monoeg_g_timer_elapsed", Library = Lib)]
    double GTimerElapsed(uint t, uint micro)
    {
        (uint s, uint u) = R32(t + 8) == 0 && R32(t + 12) == 0 ? Now() : (R32(t + 8), R32(t + 12));
        long usec = (long)u - R32(t + 4), sec = (long)s - R32(t);
        if (micro != 0)
        {
            long m = usec, ss = sec;
            if (m < 0) { m += 1_000_000; ss--; }
            W32(micro, (uint)m);
        }
        return (sec * 1_000_000.0 + usec) / 1_000_000.0;
    }

    [Hle("monoeg_g_usleep", Library = Lib)] void GUsleep(uint us) => Tm.sceKernelDelayThread(us);
}

static class ByteListExtensions
{
    public static bool AsReadOnlySpanEndsWith(this List<byte> l, ReadOnlySpan<byte> s) =>
        l.Count >= s.Length && System.Runtime.InteropServices.CollectionsMarshal.AsSpan(l)[(l.Count - s.Length)..].SequenceEqual(s);
}
