using System.Text;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// eglib's GMarkupParseContext: the XML subset Mono reads its configuration with. The context copies the caller's
/// GMarkupParser { start_element, end_element, text, passthrough, error } (+0..+0x10) and keeps user_data (+0x14),
/// its destroy notifier (+0x18) and the flags (+0x1C) in guest memory; text not yet complete waits for the next
/// parse call. Callbacks receive (context, ..., user_data, GError **error) and stop the parse by setting *error.
/// Entity references (&amp;lt; &amp;gt; &amp;amp; &amp;quot; &amp;apos; and numeric ones) are decoded in text and attribute values.
/// </summary>
public sealed unsafe partial class MonoBridge
{
    sealed class MarkupState
    {
        public readonly List<byte> Pending = new();
        public readonly Stack<string> Open = new();
    }

    readonly Dictionary<uint, MarkupState> _markup = new();

    [Hle("monoeg_g_markup_parse_context_new", Library = Lib)]
    uint GMarkupNew(uint parser, uint flags, uint userData, uint notify)
    {
        if (parser == 0) return 0;
        uint ctx = Malloc0(0x20);
        GuestMemory.Copy(ctx, parser, 0x14);
        W32(ctx + 0x14, userData); W32(ctx + 0x18, notify); W32(ctx + 0x1C, flags);
        lock (_markup) _markup[ctx] = new MarkupState();
        return ctx;
    }

    [Hle("monoeg_g_markup_parse_context_free", Library = Lib)]
    void GMarkupFree(uint ctx)
    {
        if (ctx == 0) return;
        if (R32(ctx + 0x18) is var notify and not 0) Call(notify, R32(ctx + 0x14));
        lock (_markup) _markup.Remove(ctx);
        Free(ctx);
    }

    /// <summary>g_markup_parse_context_end_parse: every error is reported while parsing, so TRUE.</summary>
    [Hle("monoeg_g_markup_parse_context_end_parse", Library = Lib)]
    bool GMarkupEndParse(uint ctx, uint err) => ctx != 0;

    MarkupState StateOf(uint ctx)
    {
        lock (_markup)
        {
            if (!_markup.TryGetValue(ctx, out var s)) _markup[ctx] = s = new MarkupState();
            return s;
        }
    }

    /// <summary>g_markup_parse_context_parse(context, text, len, error): len &lt; 0 = up to the NUL.</summary>
    [Hle("monoeg_g_markup_parse_context_parse", Library = Lib)]
    bool GMarkupParse(uint ctx, uint text, int len, uint err)
    {
        if (ctx == 0 || text == 0) return false;
        var st = StateOf(ctx);
        st.Pending.AddRange(Bytes(text, len));
        uint errCell = err != 0 ? err : Malloc0(4);
        if (err != 0) W32(err, 0);
        try { return ParsePending(ctx, st, errCell); }
        finally { if (err == 0) { GErrorFree(R32(errCell)); Free(errCell); } }
    }

    bool Fail(uint ctx, uint errCell, string message)
    {
        uint e = NewError(MarkupDomain, MarkupParse, message);
        W32(errCell, e);
        if (R32(ctx + 0x10) is var onError and not 0) Call(onError, ctx, e, R32(ctx + 0x14));
        return false;
    }

    static string Decode(string s)
    {
        if (!s.Contains('&')) return s;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            int semi;
            if (s[i] != '&' || (semi = s.IndexOf(';', i)) < 0) { sb.Append(s[i]); continue; }
            string ent = s[(i + 1)..semi];
            string? rep = ent switch { "lt" => "<", "gt" => ">", "amp" => "&", "quot" => "\"", "apos" => "'", _ => null };
            if (rep == null && ent.StartsWith('#'))
            {
                bool hex = ent.Length > 1 && (ent[1] == 'x' || ent[1] == 'X');
                if (int.TryParse(hex ? ent[2..] : ent[1..], hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, null, out int cp)
                    && cp is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
                    rep = char.ConvertFromUtf32(cp);
            }
            if (rep == null) { sb.Append(s[i]); continue; }
            sb.Append(rep);
            i = semi;
        }
        return sb.ToString();
    }

    bool Callback(uint fn, uint errCell, params uint[] args)
    {
        if (fn == 0) return true;
        Call(fn, args);
        return R32(errCell) == 0;
    }

    bool ParsePending(uint ctx, MarkupState st, uint errCell)
    {
        var buf = st.Pending.ToArray();
        int pos = 0;
        bool ok = true;
        while (ok && pos < buf.Length)
        {
            if (buf[pos] != '<')
            {
                int lt = Array.IndexOf(buf, (byte)'<', pos);
                if (lt < 0) break; // the text may go on in the next call
                ok = Text(ctx, errCell, buf.AsSpan(pos, lt - pos), st);
                pos = lt;
                continue;
            }
            var rest = buf.AsSpan(pos);
            string? terminator = rest.StartsWith("<!--"u8) ? "-->" : rest.StartsWith("<![CDATA["u8) ? "]]>" : rest.StartsWith("<?"u8) ? "?>" : ">";
            int end = rest.IndexOf(Encoding.ASCII.GetBytes(terminator));
            if (end < 0) break; // incomplete markup
            int total = end + terminator.Length;
            var tag = rest[..total];
            if (terminator == "]]>")
            {
                var inner = tag[9..^3];
                uint t = DupBytes(inner);
                ok = Callback(R32(ctx + 8), errCell, ctx, t, (uint)inner.Length, R32(ctx + 0x14), errCell);
                Free(t);
            }
            else if (terminator != ">" || tag.StartsWith("<!"u8))
            {
                uint t = DupBytes(tag);
                ok = Callback(R32(ctx + 0xC), errCell, ctx, t, (uint)tag.Length, R32(ctx + 0x14), errCell);
                Free(t);
            }
            else ok = Element(ctx, errCell, Encoding.UTF8.GetString(tag[1..^1]), st);
            pos += total;
        }
        st.Pending.RemoveRange(0, pos);
        return ok;
    }

    bool Text(uint ctx, uint errCell, ReadOnlySpan<byte> raw, MarkupState st)
    {
        string s = Encoding.UTF8.GetString(raw);
        if (s.Trim().Length == 0) return true;
        if (st.Open.Count == 0) return Fail(ctx, errCell, "Text outside of the document element");
        byte[] b = Encoding.UTF8.GetBytes(Decode(s));
        uint t = DupBytes(b);
        bool ok = Callback(R32(ctx + 8), errCell, ctx, t, (uint)b.Length, R32(ctx + 0x14), errCell);
        Free(t);
        return ok;
    }

    bool Element(uint ctx, uint errCell, string body, MarkupState st)
    {
        if (body.StartsWith('/'))
        {
            string name = body[1..].Trim();
            if (st.Open.Count == 0 || st.Open.Peek() != name) return Fail(ctx, errCell, $"Unexpected closing tag '{name}'");
            st.Open.Pop();
            uint n = StrDup(name);
            bool ok2 = Callback(R32(ctx + 4), errCell, ctx, n, R32(ctx + 0x14), errCell);
            Free(n);
            return ok2;
        }
        bool selfClose = body.EndsWith('/');
        if (selfClose) body = body[..^1];
        int i = 0;
        while (i < body.Length && !char.IsWhiteSpace(body[i])) i++;
        string elem = body[..i];
        if (elem.Length == 0) return Fail(ctx, errCell, "Empty element name");
        var names = new List<string>();
        var values = new List<string>();
        while (true)
        {
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            if (i >= body.Length) break;
            int ns = i;
            while (i < body.Length && body[i] != '=' && !char.IsWhiteSpace(body[i])) i++;
            string an = body[ns..i];
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            if (i >= body.Length || body[i] != '=') return Fail(ctx, errCell, $"Attribute '{an}' of '{elem}' has no value");
            i++;
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            if (i >= body.Length || (body[i] != '"' && body[i] != '\'')) return Fail(ctx, errCell, $"Attribute '{an}' of '{elem}' is not quoted");
            char q = body[i++];
            int close = body.IndexOf(q, i);
            if (close < 0) return Fail(ctx, errCell, $"Attribute '{an}' of '{elem}' is not closed");
            names.Add(an);
            values.Add(Decode(body[i..close]));
            i = close + 1;
        }
        uint nameP = StrDup(elem);
        var namesV = names.Select(n => Encoding.UTF8.GetBytes(n)).ToList();
        var valuesV = values.Select(v => Encoding.UTF8.GetBytes(v)).ToList();
        uint an2 = StrVector(namesV), av = StrVector(valuesV);
        bool ok = Callback(R32(ctx), errCell, ctx, nameP, an2, av, R32(ctx + 0x14), errCell);
        GStrfreev(an2); GStrfreev(av);
        if (ok)
        {
            if (selfClose) ok = Callback(R32(ctx + 4), errCell, ctx, nameP, R32(ctx + 0x14), errCell);
            else st.Open.Push(elem);
        }
        Free(nameP);
        return ok;
    }
}
