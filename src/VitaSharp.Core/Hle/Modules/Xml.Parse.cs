using System.Text;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// sce::Xml: the XML scanner shared by the DOM builder and the SAX parser, Dom::Node, Dom::DocumentBuilder and
/// Sax::Parser. The scanner follows what the firmware's parser reports (checked against libSceXml.suprx 3.74 by
/// XmlHleTests): it is incremental (parse(text, false) reports what the piece completes and answers
/// XmlParseInProgress; a text or a comment/PI/CDATA/DTD cut by the piece is reported in parts); a leading BOM is
/// skipped; the XML declaration and PIs are reported as processing instructions; skipped markup comes with the text
/// after its opener up to and including its closer; without resolveEntity references stay as written in texts and
/// attribute values, with it a predefined entity or a character reference is a text of its own; unknown entities stay
/// as written; errors use the module's ResultType values (a duplicate attribute and an attribute without '=' answer
/// XmlUnexpectedEoF, markup outside the root element XmlInvalidChar, like the firmware).
/// </summary>
public sealed unsafe partial class Xml
{
    internal enum Ev { StartElement, EndElement, Text, Skipped }

    internal readonly struct XmlEvent
    {
        public Ev Kind { get; init; }
        public byte[] Name { get; init; }
        public byte[] Data { get; init; }
        public List<(byte[] Name, byte[] Value)>? Attrs { get; init; }
        /// <summary>Skipped: 4/5 DTD, 6/7 PI, 8/9 CDATA, 10/11 comment (the odd type is the last piece).</summary>
        public int Type { get; init; }
        /// <summary>Text: it is a resolved reference (the DOM appends it to the text before it).</summary>
        public bool FromEntity { get; init; }
    }

    /// <summary>The scanner's state across the pieces of one document.</summary>
    internal sealed class XmlScanner(bool resolve, bool skipWs)
    {
        byte[] _buf = [];
        int _pos;
        bool _bomChecked, _sawRoot, _done;
        readonly Stack<byte[]> _stack = new();
        /// <summary>Inside a skipped construct cut by a piece: its type (0: none) and closer.</summary>
        int _skipType;
        byte[] _closer = [];

        public bool Started;
        public bool Resolve { get; set; } = resolve;
        public bool SkipWs { get; set; } = skipWs;

        static XmlEvent Text(byte[] t, bool entity = false) => new() { Kind = Ev.Text, Data = t, Name = [], FromEntity = entity };
        static XmlEvent Skip(int type, byte[] d) => new() { Kind = Ev.Skipped, Type = type, Data = d, Name = [] };

        /// <summary>Scans what the new piece completes, handing each event to <paramref name="sink"/> (non-zero: stop
        /// with that result). 0 at a complete end, XmlParseInProgress for a piece before the end, else the error.</summary>
        public int Feed(byte[] piece, bool isEnd, Func<XmlEvent, int> sink)
        {
            _buf = _pos < _buf.Length ? [.. _buf.AsSpan(_pos), .. piece] : piece;
            _pos = 0;
            if (!_bomChecked && (_buf.Length >= 3 || isEnd))
            {
                _bomChecked = true;
                if (_buf.Length >= 3 && _buf[0] == 0xEF && _buf[1] == 0xBB && _buf[2] == 0xBF) _pos = 3;
            }
            int r = Run(isEnd, sink);
            if (r != 0) { _done = true; return r; }
            if (!isEnd) return InfoInProgress;
            if (_skipType != 0) return _skipType switch { 4 => ErrNoDtdEnd, 6 => ErrInvalidPi, 8 => ErrNoCDataEnd, _ => ErrNoCommentEnd };
            if (_pos < _buf.Length || _stack.Count != 0) return ErrUnexpectedEof;
            return _sawRoot ? 0 : ErrNoDocElement;
        }

        public bool Done => _done;
        public int Buffered => _buf.Length - _pos;

        int Run(bool isEnd, Func<XmlEvent, int> sink)
        {
            byte[] s = _buf;
            while (_pos < s.Length)
            {
                int r;
                if (_skipType != 0)
                {
                    int end = IndexOf(s, _closer, _pos);
                    if (end < 0)
                    {
                        if (isEnd) return 0;
                        int keep = Math.Max(_pos, s.Length - (_closer.Length - 1));
                        if (keep > _pos) { r = sink(Skip(_skipType, s[_pos..keep])); if (r != 0) return r; _pos = keep; }
                        return 0;
                    }
                    end += _closer.Length;
                    r = sink(Skip(_skipType + 1, s[_pos..end]));
                    if (r != 0) return r;
                    _pos = end;
                    _skipType = 0;
                    continue;
                }
                byte c = s[_pos];
                if (c != '<')
                {
                    r = ScanText(s, isEnd, sink, out bool wait);
                    if (r != 0 || wait) return r;
                    continue;
                }
                if (_pos + 1 >= s.Length) return isEnd ? ErrUnexpectedEof : 0;
                byte n = s[_pos + 1];
                if (n == '!' || n == '?')
                {
                    // the opener decides the kind; wait for enough bytes to tell
                    (byte[] Open, byte[] Close, int Type)? kind = null;
                    foreach (var k in Openers)
                    {
                        int have = s.Length - _pos;
                        if (have < k.Open.Length)
                        {
                            if (s.AsSpan(_pos).SequenceEqual(k.Open.AsSpan(0, have))) { if (!isEnd) return 0; }
                            continue;
                        }
                        if (s.AsSpan(_pos, k.Open.Length).SequenceEqual(k.Open)) { kind = k; break; }
                    }
                    if (kind == null) return ErrSyntax;
                    if (kind.Value.Type == 8 && _stack.Count == 0) return ErrInvalidChar;
                    _pos += kind.Value.Open.Length;
                    _skipType = kind.Value.Type;
                    _closer = kind.Value.Close;
                    continue;
                }
                r = ScanTag(s, isEnd, sink, out bool waitTag);
                if (r != 0 || waitTag) return r;
            }
            return 0;
        }

        static readonly (byte[] Open, byte[] Close, int Type)[] Openers =
        [
            ("<!--"u8.ToArray(), "-->"u8.ToArray(), 10),
            ("<![CDATA["u8.ToArray(), "]]>"u8.ToArray(), 8),
            ("<!DOCTYPE"u8.ToArray(), ">"u8.ToArray(), 4),
            ("<?"u8.ToArray(), "?>"u8.ToArray(), 6),
        ];

        int ScanText(byte[] s, bool isEnd, Func<XmlEvent, int> sink, out bool wait)
        {
            wait = false;
            int start = _pos, j = _pos;
            while (j < s.Length && s[j] != '<' && !(Resolve && s[j] == '&')) j++;
            if (j > start)
            {
                bool atEnd = j >= s.Length;
                byte[] t = s[start..j];
                bool ws = t.All(IsSpace);
                if (_stack.Count == 0)
                {
                    if (!ws) return ErrInvalidChar;
                }
                else if (!(ws && SkipWs))
                {
                    int r = sink(Text(t));
                    if (r != 0) return r;
                }
                _pos = j;
                if (atEnd) { wait = true; return 0; }
            }
            if (j < s.Length && s[j] == '&')
            {
                int semi = Array.IndexOf(s, (byte)';', j + 1);
                if (semi < 0)
                {
                    if (!isEnd) { wait = true; return 0; }
                    return ErrNoSemicolon;
                }
                string ent = Encoding.ASCII.GetString(s, j + 1, semi - j - 1);
                byte[]? val = ResolveEntity(ent);
                if (_stack.Count == 0) return ErrInvalidChar;
                int r = sink(val != null ? Text(val, true) : Text(s[j..(semi + 1)]));
                if (r != 0) return r;
                _pos = semi + 1;
            }
            return 0;
        }

        static byte[]? ResolveEntity(string ent)
        {
            int idx = Array.IndexOf(Predefined, ent);
            if (idx >= 0) return [PredefinedChar[idx]];
            if (!ent.StartsWith('#')) return null;
            var o = new List<byte>();
            return CharRef(ent, o) == 0 ? [.. o] : null;
        }

        int ScanTag(byte[] s, bool isEnd, Func<XmlEvent, int> sink, out bool wait)
        {
            wait = false;
            int close = TagEnd(s, _pos);
            if (close < 0)
            {
                if (!isEnd) { wait = true; return 0; }
            }
            int i = _pos;
            if (s[i + 1] == '/')
            {
                int j = i + 2, st = j;
                while (j < s.Length && IsNameChar(s[j])) j++;
                if (j >= s.Length) return ErrUnexpectedEof;
                if (j == st) return ErrSyntax;
                byte[] name = s[st..j];
                while (j < s.Length && IsSpace(s[j])) j++;
                if (j >= s.Length) return ErrUnexpectedEof;
                if (s[j] != '>') return ErrNoCloseAngle;
                if (_stack.Count == 0 || !_stack.Peek().AsSpan().SequenceEqual(name)) return ErrEndTagMismatch;
                _stack.Pop();
                int r = sink(new XmlEvent { Kind = Ev.EndElement, Name = name, Data = [] });
                if (r != 0) return r;
                _pos = j + 1;
                return 0;
            }
            if (!IsNameStart(s[i + 1])) return ErrInvalidChar;
            if (_stack.Count == 0 && _sawRoot) return ErrInvalidChar;
            int k = i + 1, ns = k;
            while (k < s.Length && IsNameChar(s[k])) k++;
            byte[] ename = s[ns..k];
            var attrs = new List<(byte[], byte[])>();
            while (true)
            {
                int ws = k;
                while (k < s.Length && IsSpace(s[k])) k++;
                if (k >= s.Length) return ErrUnexpectedEof;
                if (s[k] == '>' || s[k] == '/') break;
                if (k == ws || !IsNameStart(s[k])) return ErrSyntax;
                int an = k;
                while (k < s.Length && IsNameChar(s[k])) k++;
                byte[] aname = s[an..k];
                while (k < s.Length && IsSpace(s[k])) k++;
                if (k >= s.Length || s[k] != '=') return ErrUnexpectedEof;
                k++;
                while (k < s.Length && IsSpace(s[k])) k++;
                if (k >= s.Length) return ErrUnexpectedEof;
                byte q = s[k];
                if (q != '"' && q != '\'') return ErrNoQuote;
                int vs = ++k;
                while (k < s.Length && s[k] != q) k++;
                if (k >= s.Length) return ErrNoQuote;
                byte[] raw = s[vs..k];
                byte[] value = raw;
                if (Resolve)
                {
                    var o = new List<byte>();
                    Unescape(raw, o);
                    value = [.. o];
                }
                k++;
                if (attrs.Any(a => a.Item1.AsSpan().SequenceEqual(aname))) return ErrUnexpectedEof;
                attrs.Add((aname, value));
            }
            bool empty = s[k] == '/';
            if (empty)
            {
                if (k + 1 >= s.Length) return ErrUnexpectedEof;
                if (s[k + 1] != '>') return ErrNoCloseAngle;
                k++;
            }
            _sawRoot = true;
            int r2 = sink(new XmlEvent { Kind = Ev.StartElement, Name = ename, Attrs = attrs, Data = [] });
            if (r2 != 0) return r2;
            if (empty)
            {
                r2 = sink(new XmlEvent { Kind = Ev.EndElement, Name = ename, Data = [] });
                if (r2 != 0) return r2;
            }
            else _stack.Push(ename);
            _pos = k + 1;
            return 0;
        }

        /// <summary>The '>' that ends the tag at <paramref name="at"/> (quotes respected), or -1.</summary>
        static int TagEnd(byte[] s, int at)
        {
            byte q = 0;
            for (int j = at + 1; j < s.Length; j++)
            {
                byte x = s[j];
                if (q != 0) { if (x == q) q = 0; }
                else if (x is (byte)'"' or (byte)'\'') q = x;
                else if (x == '>') return j;
            }
            return -1;
        }
    }

    static int CharRef(string ent, List<byte> o)
    {
        bool hex = ent.Length > 1 && (ent[1] == 'x' || ent[1] == 'X');
        string digits = ent[(hex ? 2 : 1)..];
        if (digits.Length == 0) return hex ? ErrInvalidHex : ErrInvalidDec;
        if (!int.TryParse(digits, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.None, null, out int cp)
            || cp <= 0 || cp > 0x10FFFF || cp is >= 0xD800 and <= 0xDFFF)
            return hex ? ErrInvalidHex : ErrInvalidDec;
        o.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(cp)));
        return 0;
    }

    /// <summary>An attribute value with its references resolved (unknown ones stay as written).</summary>
    static void Unescape(byte[] v, List<byte> o)
    {
        for (int i = 0; i < v.Length; i++)
        {
            int end = v[i] == '&' ? Array.IndexOf(v, (byte)';', i + 1) : -1;
            byte[]? val = end < 0 ? null : XmlScanner_Resolve(Encoding.ASCII.GetString(v, i + 1, end - i - 1));
            if (val == null) { o.Add(v[i]); continue; }
            o.AddRange(val);
            i = end;
        }
    }

    static byte[]? XmlScanner_Resolve(string ent)
    {
        int idx = Array.IndexOf(Predefined, ent);
        if (idx >= 0) return [PredefinedChar[idx]];
        if (!ent.StartsWith('#')) return null;
        var o = new List<byte>();
        return CharRef(ent, o) == 0 ? [.. o] : null;
    }

    static bool IsSpace(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
    static bool IsNameStart(byte c) => c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'_' or (byte)':' or >= 0x80;
    static bool IsNameChar(byte c) => IsNameStart(c) || c is >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'.';

    static int IndexOf(byte[] s, ReadOnlySpan<byte> p, int from)
    {
        if (from > s.Length) return -1;
        int k = s.AsSpan(from).IndexOf(p);
        return k < 0 ? -1 : from + k;
    }

    /// <summary>Builds the document tree from the scanner's events: elements (type 4) with their attributes, a text
    /// node per text (a resolved reference joins the text before it), a PI as type 0x17 with the text after its "&lt;?"
    /// up to and including "?&gt;", a CDATA section as type 7 with its content; the XML declaration, comments and the
    /// DTD are not kept. The skipped parts are dropped too with setSkipIgnorableText(true).</summary>
    internal sealed class DomSink(XDoc d, bool skipText)
    {
        XNode _cur = d.Root;
        readonly List<byte> _skip = [];

        public int On(XmlEvent e)
        {
            switch (e.Kind)
            {
                case Ev.StartElement:
                    var el = d.New(TElement, e.Name);
                    foreach (var (n, v) in e.Attrs!) { var a = d.New(TAttr, n, v); a.Parent = el; el.Attrs.Add(a); }
                    el.Parent = _cur; _cur.Children.Add(el);
                    _cur = el;
                    break;
                case Ev.EndElement:
                    _cur = _cur.Parent ?? d.Root;
                    break;
                case Ev.Text:
                    if (e.FromEntity && _cur.Children.Count > 0 && _cur.Children[^1].Type == TText)
                        _cur.Children[^1].Value = [.. _cur.Children[^1].Value, .. e.Data];
                    else { var t = d.New(TText, null, e.Data); t.Parent = _cur; _cur.Children.Add(t); }
                    break;
                case Ev.Skipped:
                    _skip.AddRange(e.Data);
                    if ((e.Type & 1) == 0) break;
                    byte[] all = [.. _skip];
                    _skip.Clear();
                    if (skipText) break;
                    bool decl = e.Type == 7 && all.Length >= 4 && all[0] == 'x' && all[1] == 'm' && all[2] == 'l' && IsSpace(all[3]);
                    if (decl || e.Type is 5 or 11) break;
                    var sk = e.Type == 7 ? d.New(TPiNode, null, all) : d.New(TCDataNode, null, all[..^3]);
                    sk.Parent = _cur; _cur.Children.Add(sk);
                    break;
            }
            return 0;
        }
    }

    // ------------------------------------------------------------------ Dom::Node (0x1C bytes, not an implementation pointer)

    [Hle("_ZN3sce3Xml3Dom4NodeC1Ey")]
    [Hle("_ZN3sce3Xml3Dom4NodeC2Ey")]
    uint NodeCtor(uint me, ulong id)
    {
        uint lo = (uint)id, hi = (uint)(id >> 32), eng = hi & ~3u;
        W32(me, lo); W32(me + 4, hi); W32(me + 8, eng); W32(me + 12, lo);
        uint init = 0;
        lock (_lock) if (eng != 0 && _engines.TryGetValue(eng, out var d)) init = d.Init;
        W32(me + 16, init); W32(me + 20, 0); W32(me + 24, 0);
        return me;
    }

    [Hle("_ZN3sce3Xml3Dom4NodeD1Ev")]
    [Hle("_ZN3sce3Xml3Dom4NodeD2Ev")]
    uint NodeDtor(uint me) => me;

    (XDoc Doc, XNode Node)? NodeOf(uint me) => me == 0 || R32(me + 8) == 0 ? null : Resolve((ulong)R32(me + 8) << 32 | R32(me + 12));

    [Hle("_ZNK3sce3Xml3Dom4Node11isAvailableEv")]
    bool NodeAvailable(uint me) => me != 0 && R32(me + 8) != 0 && R32(me + 12) != 0;

    [Hle("_ZNK3sce3Xml3Dom4Node11getNodeTypeEv")]
    int NodeType(uint me) => NodeOf(me) is { } r ? r.Node.Type : -1;

    [Hle("_ZNK3sce3Xml3Dom4Node11getNodeNameEv")]
    uint NodeName(uint ret, uint me) => NodeOf(me) is { } r ? SetCString(ret, NamePtr(r.Doc, r.Node)) : SetString(ret, 0, 0);

    [Hle("_ZNK3sce3Xml3Dom4Node12getNodeValueEv")]
    uint NodeValue(uint ret, uint me) => NodeOf(me) is { } r && r.Node.Type != TElement ? SetCString(ret, ValuePtr(r.Doc, r.Node)) : SetString(ret, 0, 0);

    [Hle("_ZNK3sce3Xml3Dom4Node13getParentNodeEv")]
    ulong NodeParent(uint me) => NodeOf(me) is { } r ? r.Doc.IdOf(ParentOf(r.Node)) : 0;

    [Hle("_ZNK3sce3Xml3Dom4Node13getFirstChildEv")]
    ulong NodeFirstChild(uint me) => NodeOf(me) is { Node.Children.Count: > 0 } r ? r.Doc.IdOf(r.Node.Children[0]) : 0;

    [Hle("_ZNK3sce3Xml3Dom4Node12getLastChildEv")]
    ulong NodeLastChild(uint me) => NodeOf(me) is { Node.Children.Count: > 0 } r ? r.Doc.IdOf(r.Node.Children[^1]) : 0;

    [Hle("_ZNK3sce3Xml3Dom4Node14getNextSiblingEv")]
    ulong NodeNextSibling(uint me) => NodeOf(me) is { } r ? r.Doc.IdOf(NextSibling(r.Node)) : 0;

    [Hle("_ZNK3sce3Xml3Dom4Node13hasChildNodesEv")]
    bool NodeHasChildren(uint me) => NodeOf(me) is { Node.Children.Count: > 0 };

    [Hle("_ZNK3sce3Xml3Dom4Node13hasAttributesEv")]
    bool NodeHasAttrs(uint me) => NodeOf(me) is { Node.Attrs.Count: > 0 };

    /// <summary>The Document object that owns the engine (what the engine's +0x460 holds).</summary>
    [Hle("_ZNK3sce3Xml3Dom4Node16getOwnerDocumentEv")]
    uint NodeOwner(uint me) => me != 0 && R32(me + 8) is var eng and not 0 ? R32(eng + 0x460) : 0;

    /// <summary>Node::getChildNodes and getAttributes answer an empty NodeList by value (the module builds the list in
    /// a temporary it destroys before returning; Document::getChildNodes/getAttributes are the working forms).</summary>
    [Hle("_ZNK3sce3Xml3Dom4Node13getChildNodesEv")]
    [Hle("_ZNK3sce3Xml3Dom4Node13getAttributesEv")]
    uint NodeEmptyList(uint ret, uint me) { W32(ret, 0); return ret; }

    /// <summary>Moves a node of this document (or copies one from another) to the end of this node's children; the
    /// types must fit (an element takes elements, text, comments, PIs; the document one element). Answers the child
    /// as it now is in this document, 0 when refused.</summary>
    [Hle("_ZN3sce3Xml3Dom4Node11appendChildEy")]
    ulong NodeAppendChild(uint me, ulong child) => NodeInsert(me, child, 0, true);

    [Hle("_ZN3sce3Xml3Dom4Node12insertBeforeEyy")]
    ulong NodeInsertBefore(uint me, ulong child, ulong refChild) => NodeInsert(me, child, refChild, false);

    /// <summary>insertBefore uses the module's engine insert (right after refChild, first when 0).</summary>
    ulong NodeInsert(uint me, ulong child, ulong refChild, bool append)
    {
        if (!NodeAvailable(me) || NodeOf(me) is not { } r || Resolve(child) is not { } c) return 0;
        if (r.Doc.ReadOnly) return 0;
        var n = c.Doc == r.Doc ? c.Node : CloneInto(r.Doc, c.Node);
        bool ok = refChild == 0 && append ? Insert(r.Node, n, null) : InsertAfter(r.Doc, r.Node, n, refChild);
        if (!ok) { if (n != c.Node) DeleteTree(r.Doc, n); return 0; }
        return r.Doc.IdOf(n);
    }

    [Hle("_ZN3sce3Xml3Dom4Node11removeChildEy")]
    ulong NodeRemoveChild(uint me, ulong child)
    {
        if (!NodeAvailable(me) || NodeOf(me) is not { } r || r.Doc.ReadOnly || Node(r.Doc, child) is not { } c || c.Parent != r.Node) return 0;
        Detach(c);
        return child;
    }

    // ------------------------------------------------------------------ Dom::DocumentBuilder

    [Hle("_ZN3sce3Xml3Dom15DocumentBuilderC1Ev")]
    [Hle("_ZN3sce3Xml3Dom15DocumentBuilderC2Ev")]
    uint BuilderCtor(uint me) { W32(me, 0); return me; }

    [Hle("_ZN3sce3Xml3Dom15DocumentBuilder10initializeEPKNS0_11InitializerE")]
    int BuilderInitialize(uint me, uint init)
    {
        if (R32(me) != 0) return InfoMultipleInit;
        if (InitOf(init) == null) return ErrNotInitialized;
        uint p = NewImpl(init, SizeBuilder, new XBuilder { Init = init });
        if (p == 0) return ErrNoMemory;
        W32(me, p);
        return 0;
    }

    [Hle("_ZN3sce3Xml3Dom15DocumentBuilder9terminateEv")]
    int BuilderTerminate(uint me)
    {
        var b = Impl<XBuilder>(me);
        if (b == null) return ErrNotInitialized;
        if (b.Doc != null) FreeEngine(b.Doc);
        if (b.Building != null) FreeEngine(b.Building);
        b.Doc = b.Building = null;
        b.Scanner = null;
        FreeImpl(me, b.Init);
        return 0;
    }

    [Hle("_ZN3sce3Xml3Dom15DocumentBuilderD1Ev")]
    [Hle("_ZN3sce3Xml3Dom15DocumentBuilderD2Ev")]
    uint BuilderDtor(uint me) { BuilderTerminate(me); return me; }

    [Hle("_ZN3sce3Xml3Dom15DocumentBuilder26setSkipIgnorableWhiteSpaceEb")]
    void BuilderSkipWs(uint me, bool on) { if (Impl<XBuilder>(me) is { } b) b.SkipWs = on; }

    [Hle("_ZN3sce3Xml3Dom15DocumentBuilder16setResolveEntityEb")]
    void BuilderResolve(uint me, bool on) { if (Impl<XBuilder>(me) is { } b) b.Resolve = on; }

    /// <summary>Refused (ParserBusy's in-progress form, like the module) while a chunked parse is pending.</summary>
    [Hle("_ZN3sce3Xml3Dom15DocumentBuilder20setSkipIgnorableTextEb")]
    int BuilderSkipText(uint me, bool on)
    {
        var b = Impl<XBuilder>(me);
        if (b == null) return ErrNotInitialized;
        if (b.Scanner != null) return InfoInProgress;
        b.SkipText = on;
        return 0;
    }

    /// <summary>parse(text, isEnd): builds a new read-only document (the previous one is released) from the pieces;
    /// a piece before the end answers XmlParseInProgress.</summary>
    [Hle("_ZN3sce3Xml3Dom15DocumentBuilder5parseEPKNS0_6StringEb")]
    int BuilderParse(uint me, uint str, bool isEnd)
    {
        var b = Impl<XBuilder>(me);
        if (b == null) return ErrNotInitialized;
        byte[]? piece = StrArg(str);
        if (piece == null) return ErrInvalidArgument;
        if (b.Scanner == null)
        {
            if (b.Doc != null) { FreeEngine(b.Doc); b.Doc = null; }
            b.Building = NewEngine(b.Init, 0);
            if (b.Building == null) return ErrNoMemory;
            b.Scanner = new XmlScanner(b.Resolve, b.SkipWs);
            b.Sink = new DomSink(b.Building, b.SkipText);
        }
        int r = b.Scanner.Feed(piece, isEnd, b.Sink!.On);
        if (r == InfoInProgress) return r;
        b.Scanner = null;
        b.Sink = null;
        var d = b.Building!;
        b.Building = null;
        if (r != 0) { FreeEngine(d); return r; }
        d.ReadOnly = true;
        b.Doc = d;
        return 0;
    }

    /// <summary>A Document (by value) sharing the parsed engine; empty before a successful parse.</summary>
    [Hle("_ZN3sce3Xml3Dom15DocumentBuilder11getDocumentEv")]
    uint BuilderGetDocument(uint ret, uint me)
    {
        W32(ret, 0);
        if (Impl<XBuilder>(me) is { Doc: { } d } b)
        {
            DocumentShare(ret, new XDocRef { Init = b.Init, Doc = d });
            W32(d.Engine + 0x460, ret);
        }
        return ret;
    }

    // ------------------------------------------------------------------ Sax::Parser

    [Hle("_ZN3sce3Xml3Sax6ParserC1Ev")]
    [Hle("_ZN3sce3Xml3Sax6ParserC2Ev")]
    uint ParserCtor(uint me) { W32(me, 0); return me; }

    [Hle("_ZN3sce3Xml3Sax6Parser10initializeEPKNS0_11InitializerE")]
    int ParserInitialize(uint me, uint init)
    {
        if (R32(me) != 0) return InfoMultipleInit;
        if (InitOf(init) == null) return ErrNotInitialized;
        var st = new XParser { Init = init };
        uint p = NewImpl(init, SizeParser, st);
        if (p == 0) return ErrNoMemory;
        W32(p, init);
        st.Body = Alloc(init, SizeParserBody);
        if (st.Body == 0) { FreeImpl(me, init); return ErrNoMemory; }
        W32(me, p);
        return 0;
    }

    [Hle("_ZN3sce3Xml3Sax6Parser9terminateEv")]
    int ParserTerminate(uint me)
    {
        var p = Impl<XParser>(me);
        if (p == null) return ErrNotInitialized;
        Free(p.Init, p.Body);
        FreeImpl(me, p.Init);
        return 0;
    }

    [Hle("_ZN3sce3Xml3Sax6ParserD1Ev")]
    [Hle("_ZN3sce3Xml3Sax6ParserD2Ev")]
    uint ParserDtor(uint me) { ParserTerminate(me); return me; }

    [Hle("_ZN3sce3Xml3Sax6Parser18setDocumentHandlerEPNS1_15DocumentHandlerE")]
    void ParserSetHandler(uint me, uint handler) { if (Impl<XParser>(me) is { } p) p.Handler = handler; }

    [Hle("_ZN3sce3Xml3Sax6Parser11setUserDataEPv")]
    void ParserSetUserData(uint me, uint ud) { if (Impl<XParser>(me) is { } p) p.UserData = ud; }

    [Hle("_ZN3sce3Xml3Sax6Parser26setSkipIgnorableWhiteSpaceEb")]
    void ParserSkipWs(uint me, bool on) { if (Impl<XParser>(me) is { } p) p.SkipWs = on; }

    [Hle("_ZN3sce3Xml3Sax6Parser16setResolveEntityEb")]
    void ParserResolve(uint me, bool on) { if (Impl<XParser>(me) is { } p) p.Resolve = on; }

    /// <summary>Drops a pending chunked parse.</summary>
    [Hle("_ZN3sce3Xml3Sax6Parser5resetEv")]
    int ParserReset(uint me)
    {
        var p = Impl<XParser>(me);
        if (p == null) return ErrNotInitialized;
        p.Scanner = null;
        return 0;
    }

    /// <summary>parse(text, isEnd): the events the piece completes go to the game's DocumentHandler (startDocument
    /// on the first piece of a document, endDocument after a complete one, errorFound with the ResultType on an
    /// error); a piece before the end answers XmlParseInProgress.</summary>
    [Hle("_ZN3sce3Xml3Sax6Parser5parseEPKNS0_6StringEb")]
    int ParserParse(uint me, uint str, bool isEnd)
    {
        var p = Impl<XParser>(me);
        if (p == null) return ErrNotInitialized;
        byte[]? piece = StrArg(str);
        if (piece == null) return ErrInvalidArgument;
        if (p.Handler == 0) return ErrHandlerNotSet;
        uint h = p.Handler, ud = p.UserData;
        p.Scanner ??= new XmlScanner(p.Resolve, p.SkipWs);
        var sc = p.Scanner;
        // one scratch block for the strings handed to the handler: what is buffered bounds every name and text
        uint scratch = Alloc(p.Init, (uint)(sc.Buffered + piece.Length) + 0x40);
        if (scratch == 0) return ErrNoMemory;
        uint list = 0;
        try
        {
            if (!sc.Started)
            {
                sc.Started = true;
                if (CallVirt(h, 2, ud) != 0) { p.Scanner = null; return InfoAborted; }
            }
            uint str8 = scratch, data = scratch + 0x10;
            uint Put(byte[] b)
            {
                b.CopyTo(GuestMemory.Span(data, b.Length));
                GuestMemory.Write<byte>(data + (uint)b.Length, 0);
                return SetString(str8, data, (uint)b.Length);
            }
            int r = sc.Feed(piece, isEnd, e =>
            {
                uint res;
                switch (e.Kind)
                {
                    case Ev.StartElement:
                    {
                        // the attribute strings follow the name in the scratch block
                        byte[] all = [.. e.Name, 0, .. e.Attrs!.SelectMany(a => (byte[])[.. a.Name, 0, .. a.Value, 0])];
                        all.CopyTo(GuestMemory.Span(data, all.Length));
                        SetString(str8, data, (uint)e.Name.Length);
                        uint lst = Scratch(8);
                        AttrListCtor(lst);
                        if (AttrListInitialize(lst, p.Init) != 0) { t_sp = 0; return ErrNoMemory; }
                        list = lst;
                        uint at = data + (uint)e.Name.Length + 1;
                        foreach (var (an, av) in e.Attrs!)
                        {
                            Impl<XAttrList>(lst)!.Items.Add((at, at + (uint)an.Length + 1));
                            at += (uint)(an.Length + av.Length + 2);
                        }
                        res = CallVirt(h, 3, ud, str8, lst);
                        AttrListTerminate(lst);
                        list = 0;
                        t_sp = 0;
                        break;
                    }
                    case Ev.EndElement: res = CallVirt(h, 5, ud, Put(e.Name)); break;
                    case Ev.Text: res = CallVirt(h, 4, ud, Put(e.Data)); break;
                    default: res = CallVirt(h, 7, ud, (uint)e.Type, Put(e.Data)); break;
                }
                return res != 0 ? InfoAborted : 0;
            });
            if (r == InfoInProgress) return r;
            p.Scanner = null;
            if (r == InfoAborted) return r;
            if (r != 0) { CallVirt(h, 9, ud, unchecked((uint)r)); return r; }
            return CallVirt(h, 6, ud) != 0 ? InfoAborted : 0;
        }
        finally
        {
            if (list != 0) AttrListTerminate(list);
            t_sp = 0;
            Free(p.Init, scratch);
        }
    }
}
