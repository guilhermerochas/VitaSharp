using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// np_common 3.74's streaming XML reader (NID_88B18A5B; np_utility has its own copy, NID_E2135903): a parser record
/// set up by NID_650D111B (0x248 bytes: +0 the schema, +4 the name buffer, +0x104 the value buffer, +0x214 the
/// depth, +0x218.. the matched element of each level, +0x240 the callback, +0x244 its argument) is fed the document
/// in chunks. The schema is { tables, levels }: tables[level] is a list of 8-byte entries { name, kind } ending
/// with an empty name; kind 1 is an element with text. The callback int (record, event, level, index, a, b, arg)
/// hears event 1 when a known element starts (a = its name), 4 for each of its attributes (a = name, b = value),
/// 3 when a text element ends (b = its text, entities decoded) and 2 when another ends; a negative answer stops the
/// parse with that value. The state between chunks is kept with the record.
/// </summary>
public sealed partial class NpManager
{
    enum XmlMode
    {
        Text,
        TagStart,
        Name,
        Attributes,
        AttributeName,
        AttributeValue,
        EndName,
        Skip,
    }

    sealed class XmlReader
    {
        public XmlMode Mode;
        public readonly StringBuilder Name = new(), AttributeName = new(), Value = new(), Text = new();
        public bool Closing, SelfClosing;
        public char Quote;
        public readonly List<int> Matched = [];
    }

    readonly Dictionary<uint, XmlReader> _xmlReaders = [];

    /// <summary>The record is set up again (NID_650D111B): its reader starts over.</summary>
    void ResetXml(uint record)
    {
        lock (_xmlReaders)
        {
            _xmlReaders.Remove(record);
        }
    }

    const uint XmlName = 4, XmlValue = 0x104, XmlDepth = 0x214, XmlCallback = 0x240, XmlArgument = 0x244;

    [Hle("NID_88B18A5B", Nid = 0x88B18A5B, Library = "SceNpCommon")]
    int NpCommonXmlParse(uint record, uint data, int size) => XmlParse(record, data, size);

    internal int XmlParse(uint record, uint data, int size)
    {
        if (size <= 0)
        {
            return 0;
        }

        XmlReader reader;
        lock (_xmlReaders)
        {
            if (!_xmlReaders.TryGetValue(record, out reader!))
            {
                reader = new XmlReader();
                _xmlReaders[record] = reader;
            }
        }

        ReadOnlySpan<byte> bytes = GuestMemory.Span(data, size);
        foreach (byte b in bytes)
        {
            int result = Step(record, reader, (char)b);
            if (result < 0)
            {
                return result;
            }
        }

        return 0;
    }

    int Step(uint record, XmlReader r, char c)
    {
        switch (r.Mode)
        {
            case XmlMode.Text:
                if (c == '<')
                {
                    r.Mode = XmlMode.TagStart;
                    r.Name.Clear();
                    r.Closing = r.SelfClosing = false;
                }
                else if (CurrentKind(record, r) == 1)
                {
                    r.Text.Append(c);
                }

                return 0;
            case XmlMode.TagStart:
                if (c == '/')
                {
                    r.Closing = true;
                    r.Mode = XmlMode.EndName;
                    return 0;
                }

                if (c is '?' or '!')
                {
                    r.Mode = XmlMode.Skip;
                    return 0;
                }

                r.Mode = XmlMode.Name;
                return Step(record, r, c);
            case XmlMode.Skip:
                if (c == '>')
                {
                    r.Mode = XmlMode.Text;
                }

                return 0;
            case XmlMode.Name:
                if (IsSpace(c) || c is '/' or '>')
                {
                    int started = StartElement(record, r);
                    if (started < 0)
                    {
                        return started;
                    }

                    r.Mode = XmlMode.Attributes;
                    return Step(record, r, c);
                }

                r.Name.Append(c);
                return 0;
            case XmlMode.Attributes:
                if (c == '/')
                {
                    r.SelfClosing = true;
                    return 0;
                }

                if (c == '>')
                {
                    return TagEnd(record, r);
                }

                if (!IsSpace(c))
                {
                    r.AttributeName.Clear();
                    r.AttributeName.Append(c);
                    r.Mode = XmlMode.AttributeName;
                }

                return 0;
            case XmlMode.AttributeName:
                if (c == '=')
                {
                    r.Value.Clear();
                    r.Quote = '\0';
                    r.Mode = XmlMode.AttributeValue;
                }
                else if (c == '>' || c == '/')
                {
                    r.Mode = XmlMode.Attributes;
                    return Step(record, r, c);
                }
                else if (!IsSpace(c))
                {
                    r.AttributeName.Append(c);
                }

                return 0;
            case XmlMode.AttributeValue:
                if (r.Quote == '\0')
                {
                    if (c is '"' or '\'')
                    {
                        r.Quote = c;
                    }

                    return 0;
                }

                if (c == r.Quote)
                {
                    r.Mode = XmlMode.Attributes;
                    return Attribute(record, r);
                }

                r.Value.Append(c);
                return 0;
            case XmlMode.EndName:
                if (c == '>')
                {
                    return EndElement(record, r);
                }

                if (!IsSpace(c))
                {
                    r.Name.Append(c);
                }

                return 0;
        }

        return 0;
    }

    static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n';

    /// <summary>The schema's entries of a level: (name, kind) in order, or none past the schema's levels.</summary>
    static List<(string Name, uint Kind)> Level(uint record, int level)
    {
        List<(string, uint)> entries = [];
        uint schema = GuestMemory.R32(record);
        if (schema == 0 || level < 0 || (uint)level >= GuestMemory.R32(schema + 4))
        {
            return entries;
        }

        uint table = GuestMemory.R32(GuestMemory.R32(schema) + (uint)level * 4);
        for (uint entry = table; entry != 0; entry += 8)
        {
            uint name = GuestMemory.R32(entry);
            string text = name == 0 ? "" : GuestMemory.ReadCString(name, 0x100);
            if (text.Length == 0)
            {
                break;
            }

            entries.Add((text, GuestMemory.R32(entry + 4)));
        }

        return entries;
    }

    static int Depth(XmlReader r) => r.Matched.Count;

    uint CurrentKind(uint record, XmlReader r)
    {
        if (r.Matched.Count == 0 || r.Matched[^1] < 0)
        {
            return 0;
        }

        List<(string Name, uint Kind)> level = Level(record, r.Matched.Count - 1);
        return r.Matched[^1] < level.Count ? level[r.Matched[^1]].Kind : 0;
    }

    int StartElement(uint record, XmlReader r)
    {
        int depth = Depth(r);
        bool parentKnown = depth == 0 || r.Matched[^1] >= 0;
        int index = -1;
        if (parentKnown)
        {
            List<(string Name, uint Kind)> level = Level(record, depth);
            index = level.FindIndex(e => e.Name == r.Name.ToString());
        }

        r.Matched.Add(index);
        r.Text.Clear();
        WriteXmlState(record, r);
        if (index < 0)
        {
            return 0;
        }

        SetBuffer(record, XmlName, r.Name.ToString());
        SetBuffer(record, XmlValue, "");
        return Callback(record, 1, depth, index, record + XmlName, record + XmlValue);
    }

    int Attribute(uint record, XmlReader r)
    {
        int depth = Depth(r) - 1;
        if (depth < 0 || r.Matched[depth] < 0)
        {
            return 0;
        }

        SetBuffer(record, XmlName, r.AttributeName.ToString());
        SetBuffer(record, XmlValue, DecodeEntities(r.Value.ToString()));
        return Callback(record, 4, depth, r.Matched[depth], record + XmlName, record + XmlValue);
    }

    int TagEnd(uint record, XmlReader r)
    {
        r.Mode = XmlMode.Text;
        return r.SelfClosing ? EndElement(record, r) : 0;
    }

    int EndElement(uint record, XmlReader r)
    {
        r.Mode = XmlMode.Text;
        if (r.Matched.Count == 0)
        {
            return 0;
        }

        uint kind = CurrentKind(record, r);
        int index = r.Matched[^1];
        int depth = r.Matched.Count - 1;
        r.Matched.RemoveAt(depth);
        WriteXmlState(record, r);
        if (index < 0)
        {
            return 0;
        }

        if (kind == 1)
        {
            SetBuffer(record, XmlValue, DecodeEntities(r.Text.ToString()));
            r.Text.Clear();
            return Callback(record, 3, depth, index, record + XmlValue, record + XmlValue);
        }

        SetBuffer(record, XmlValue, "");
        return Callback(record, 2, depth, index, record + XmlValue, record + XmlValue);
    }

    static void SetBuffer(uint record, uint offset, string text)
    {
        GuestMemory.Fill(record + offset, 0, 0x100);
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        int length = Math.Min(bytes.Length, 0xFF);
        bytes.AsSpan(0, length).CopyTo(GuestMemory.Span(record + offset, length));
    }

    static void WriteXmlState(uint record, XmlReader r)
    {
        GuestMemory.W32(record + XmlDepth, (uint)r.Matched.Count);
        for (int i = 0; i < r.Matched.Count && i < 8; i++)
        {
            GuestMemory.W32(record + XmlDepth + 4 + (uint)i * 4, unchecked((uint)r.Matched[i]));
        }
    }

    /// <summary>The five XML entities and numeric references (np_common's NID_38B54F30); a bad one stays as text.</summary>
    static string DecodeEntities(string text)
    {
        if (!text.Contains('&', StringComparison.Ordinal))
        {
            return text;
        }

        StringBuilder decoded = new();
        for (int i = 0; i < text.Length; i++)
        {
            int end = text[i] == '&' ? text.IndexOf(';', i) : -1;
            if (end < 0 || end - i > 10)
            {
                decoded.Append(text[i]);
                continue;
            }

            string entity = text[(i + 1)..end];
            string? value = entity switch
            {
                "amp" => "&",
                "lt" => "<",
                "gt" => ">",
                "quot" => "\"",
                "apos" => "'",
                _ => NumericEntity(entity),
            };
            if (value == null)
            {
                decoded.Append(text[i]);
                continue;
            }

            decoded.Append(value);
            i = end;
        }

        return decoded.ToString();
    }

    static string? NumericEntity(string entity)
    {
        if (entity.Length < 2 || entity[0] != '#')
        {
            return null;
        }

        bool hex = entity[1] is 'x' or 'X';
        string digits = hex ? entity[2..] : entity[1..];
        if (!int.TryParse(digits, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int code) || code is <= 0 or > 0x10FFFF)
        {
            return null;
        }

        return Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(code)));
    }

    /// <summary>The record's callback (record, event, level, index, a, b, arg); tests may answer instead.</summary>
    int Callback(uint record, int ev, int level, int index, uint a, uint b)
    {
        uint callback = GuestMemory.R32(record + XmlCallback), argument = GuestMemory.R32(record + XmlArgument);
        if (XmlSink != null)
        {
            return XmlSink(ev, level, index, GuestMemory.ReadCString(a, 0x100), GuestMemory.ReadCString(b, 0x100));
        }

        if (callback == 0 || GuestThread.Current is not { } thread)
        {
            return 0;
        }

        return (int)thread.CallGuest(callback, record, (uint)ev, (uint)level, (uint)index, a, b, argument);
    }

    /// <summary>Tests: answers the parser's callbacks (event, level, index, a, b).</summary>
    internal Func<int, int, int, string, string, int>? XmlSink;

    // the JID helpers for np_utility's copies (NID_54C48F92 / NID_14437509)
    internal int ParseJid(uint parts, uint jid, uint buffer, uint size, uint needed) => NpCommonJidParseThroughEntry(parts, jid, buffer, size, needed);

    internal int NpIdOfJid(uint npId, uint parts) => NpCommonNpIdFromJidThroughEntry(npId, parts);

    internal int JidOfNpId(uint parts, uint npId, uint buffer, uint size, uint needed) => NpCommonJidFromNpIdThroughEntry(parts, npId, buffer, size, needed);

    internal int JoinJid(uint parts, uint output, uint size, uint needed) => NpCommonJidJoinThroughEntry(parts, output, size, needed);
}
