using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace VitaSharp.Core.Shell;

/// <summary>Kinds of attribute values in an RCO / CXML tree.</summary>
public enum RcoAttributeType
{
    None = 0,
    Int = 1,
    Float = 2,
    String = 3,
    WString = 4,
    Hash = 5,
    IntArray = 6,
    FloatArray = 7,
    File = 8,
    Id = 9,
    IdRef = 10,
    IdHash = 11,
    IdHashRef = 12,
}

/// <summary>One attribute of an <see cref="RcoElement"/>. Which members are set depends on <see cref="Type"/>.</summary>
public sealed class RcoAttribute
{
    public required string Name { get; init; }
    public RcoAttributeType Type { get; init; }
    public int Int { get; init; }
    public float Float { get; init; }
    /// <summary>String, WString, Id and IdRef values.</summary>
    public string? Text { get; init; }
    /// <summary>Hash, IdHash and IdHashRef values.</summary>
    public uint Hash { get; init; }
    public int[]? Ints { get; init; }
    public float[]? Floats { get; init; }
    /// <summary>File: offset and size in the file table.</summary>
    public int FileOffset { get; init; }
    public int FileSize { get; init; }

    public override string ToString() => Type switch
    {
        RcoAttributeType.Int => Int.ToString(),
        RcoAttributeType.Float => Float.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RcoAttributeType.String or RcoAttributeType.WString or RcoAttributeType.Id or RcoAttributeType.IdRef => Text ?? "",
        RcoAttributeType.Hash or RcoAttributeType.IdHash or RcoAttributeType.IdHashRef => $"0x{Hash:X8}",
        RcoAttributeType.IntArray => string.Join(",", Ints ?? []),
        RcoAttributeType.FloatArray => string.Join(",", (Floats ?? []).Select(f => f.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        RcoAttributeType.File => $"file@0x{FileOffset:X}+{FileSize}",
        _ => "",
    };
}

/// <summary>An element of an RCO tree (e.g. &lt;texture id=... src=...&gt; inside &lt;texturetable&gt;).</summary>
public sealed class RcoElement
{
    public required string Name { get; init; }
    public List<RcoAttribute> Attributes { get; } = new();
    public List<RcoElement> Children { get; } = new();
    public RcoElement? Parent { get; internal set; }

    public RcoAttribute? Attribute(string name) => Attributes.FirstOrDefault(a => a.Name == name);
    public string? Text(string name) => Attribute(name)?.Text;

    /// <summary>The element's id: the string id, or the id hash as "0x........" when the tree only keeps hashes.</summary>
    public string? Id => Attribute("id") is { } a ? a.Text ?? $"0x{a.Hash:X8}" : null;
    /// <summary>The id hash, when the tree keeps ids as hashes (firmware RCOs do).</summary>
    public uint? IdHash => Attribute("id") is { Text: null } a ? a.Hash : null;

    public IEnumerable<RcoElement> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    public override string ToString() => $"<{Name} {string.Join(" ", Attributes.Select(a => $"{a.Name}=\"{a}\""))}>";
}

/// <summary>A resource carried in an RCO file table: texture, sound, layout data...</summary>
public sealed record RcoFile(RcoElement Element, string Table, string Id, string Type, byte[] Data);

/// <summary>
/// Reader for the PS Vita's RCO resource containers ("RCOF", version 0x110): the CXML format the system UI (libpaf)
/// loads its pages, styles, textures, sounds and strings from (vs0:vsh/shell/*.rco, data/external/common/
/// common_resource.rco, each system app's *_plugin.rco). Layout: a 0x50 byte header with nine (offset, size) tables â€”
/// element tree, id strings, id hashes, strings, wide strings, hashes, int arrays, float arrays and files. Elements are
/// {name, attribute count, parent, previous, next, first child, last child} followed by 16-byte attributes {name, type,
/// two value words}. File attributes point into the file table; the element's compress="on"/origsize say when the
/// bytes are zlib compressed.
/// </summary>
public sealed class Rco
{
    public const uint Magic = 0x464F4352; // "RCOF"

    public RcoElement Root { get; }
    public uint Version { get; }
    readonly byte[] _data;
    readonly int _fileTable, _fileTableSize;

    Rco(byte[] data)
    {
        _data = data;
        if (data.Length < 0x50 || U32(0) != Magic) throw new InvalidDataException("not an RCO file (no RCOF magic)");
        Version = U32(4);
        var t = new (int off, int size)[9];
        for (int i = 0; i < 9; i++)
        {
            t[i] = ((int)U32(8 + i * 8), (int)U32(12 + i * 8));
            if (t[i].size < 0 || t[i].off < 0 || (long)t[i].off + t[i].size > data.Length) throw new InvalidDataException($"RCO table {i} lies outside the file");
        }
        _tree = t[0]; _idStr = t[1]; _idInt = t[2]; _str = t[3]; _wstr = t[4]; _hash = t[5]; _intArr = t[6]; _floatArr = t[7];
        (_fileTable, _fileTableSize) = t[8];
        if (_tree.size < 28) throw new InvalidDataException("RCO element tree is empty");
        Root = ReadElement(0, null, 0);
    }

    readonly (int off, int size) _tree, _idStr, _idInt, _str, _wstr, _hash, _intArr, _floatArr;

    public static Rco Load(string path) => new(File.ReadAllBytes(path));
    public static Rco Parse(byte[] data) => new(data);

    uint U32(int off) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off));
    int I32(int off) => BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(off));

    RcoElement ReadElement(int offset, RcoElement? parent, int depth)
    {
        if (depth > 64) throw new InvalidDataException("RCO element tree is too deep");
        if (offset < 0 || offset + 28 > _tree.size) throw new InvalidDataException($"RCO element at 0x{offset:X} lies outside the tree");
        int p = _tree.off + offset;
        var e = new RcoElement { Name = CString(_str, I32(p)), Parent = parent };
        int count = I32(p + 4);
        if (count < 0 || offset + 28 + count * 16 > _tree.size) throw new InvalidDataException($"RCO element at 0x{offset:X} has a bad attribute count");
        for (int i = 0; i < count; i++) e.Attributes.Add(ReadAttribute(p + 28 + i * 16));
        int child = I32(p + 20), guard = 0;
        while (child != -1)
        {
            if (++guard > 100_000) throw new InvalidDataException("RCO sibling chain loops");
            var c = ReadElement(child, e, depth + 1);
            e.Children.Add(c);
            child = I32(_tree.off + child + 16); // next sibling
        }
        return e;
    }

    RcoAttribute ReadAttribute(int p)
    {
        string name = CString(_str, I32(p));
        var type = (RcoAttributeType)I32(p + 4);
        int a = I32(p + 8), b = I32(p + 12);
        return type switch
        {
            RcoAttributeType.Int => new RcoAttribute { Name = name, Type = type, Int = a },
            RcoAttributeType.Float => new RcoAttribute { Name = name, Type = type, Float = BitConverter.Int32BitsToSingle(a) },
            RcoAttributeType.String => new RcoAttribute { Name = name, Type = type, Text = Utf8(_str, a, b) },
            RcoAttributeType.WString => new RcoAttribute { Name = name, Type = type, Text = Utf16(_wstr, a, b) },
            // hashes: index of a 4-byte entry in the hash table
            RcoAttributeType.Hash => new RcoAttribute { Name = name, Type = type, Hash = InTable(_hash, a * 4, 4) ? U32(_hash.off + a * 4) : 0 },
            RcoAttributeType.IntArray => new RcoAttribute { Name = name, Type = type, Ints = Array(_intArr, a, b, o => I32(o)) },
            RcoAttributeType.FloatArray => new RcoAttribute { Name = name, Type = type, Floats = Array(_floatArr, a, b, o => BitConverter.Int32BitsToSingle(I32(o))) },
            RcoAttributeType.File => new RcoAttribute { Name = name, Type = type, FileOffset = a, FileSize = b },
            // id strings: {int element offset; char name[]}
            RcoAttributeType.Id or RcoAttributeType.IdRef => new RcoAttribute { Name = name, Type = type, Text = InTable(_idStr, a, 4) ? CString(_idStr, a + 4) : null },
            // id hashes: {int element offset; uint hash}
            RcoAttributeType.IdHash or RcoAttributeType.IdHashRef => new RcoAttribute { Name = name, Type = type, Hash = InTable(_idInt, a, 8) ? U32(_idInt.off + a + 4) : 0 },
            _ => new RcoAttribute { Name = name, Type = type, Int = a },
        };
    }

    static bool InTable((int off, int size) t, int at, int len) => at >= 0 && at + len <= t.size;

    T[] Array<T>((int off, int size) t, int index, int count, Func<int, T> read)
    {
        // array attributes: (element index, count) in 4-byte units
        if (count <= 0 || !InTable(t, index * 4, count * 4)) return [];
        var r = new T[count];
        for (int i = 0; i < count; i++) r[i] = read(t.off + (index + i) * 4);
        return r;
    }

    string CString((int off, int size) t, int at)
    {
        if (at < 0 || at >= t.size) return "";
        int start = t.off + at, end = start, limit = t.off + t.size;
        while (end < limit && _data[end] != 0) end++;
        return Encoding.UTF8.GetString(_data, start, end - start);
    }

    string Utf8((int off, int size) t, int at, int len)
    {
        if (!InTable(t, at, Math.Max(len, 0))) return "";
        return Encoding.UTF8.GetString(_data, t.off + at, len).TrimEnd('\0');
    }

    string Utf16((int off, int size) t, int at, int len)
    {
        // wide strings: offset in bytes, length in characters
        if (len <= 0 || !InTable(t, at, len * 2)) return "";
        return Encoding.Unicode.GetString(_data, t.off + at, len * 2).TrimEnd('\0');
    }

    /// <summary>The bytes of a file attribute, inflated when its element says compress="on".</summary>
    public byte[] ReadFile(RcoElement element, string attribute = "src")
    {
        var a = element.Attribute(attribute);
        if (a == null || a.Type != RcoAttributeType.File) throw new InvalidDataException($"<{element.Name}> has no file attribute '{attribute}'");
        if (a.FileOffset < 0 || a.FileSize < 0 || (long)a.FileOffset + a.FileSize > _fileTableSize) throw new InvalidDataException("RCO file lies outside the file table");
        var raw = _data.AsSpan(_fileTable + a.FileOffset, a.FileSize);
        if (element.Text("compress") != "on") return raw.ToArray();
        int orig = element.Attribute("origsize")?.Int ?? 0;
        using var z = new ZLibStream(new MemoryStream(raw.ToArray()), CompressionMode.Decompress);
        var ms = new MemoryStream(orig > 0 ? orig : a.FileSize * 4);
        z.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Every element with a file attribute: textures, sounds, layouts... with its table ("texturetable",
    /// "soundtable", ...), id and type ("texture/png", "texture/gim", "sound/vag"...). Files that fail to read are skipped.</summary>
    public IEnumerable<RcoFile> Files()
    {
        foreach (var e in Root.Descendants())
        {
            var src = e.Attributes.FirstOrDefault(a => a.Type == RcoAttributeType.File);
            if (src == null) continue;
            byte[] data;
            try { data = ReadFile(e, src.Name); }
            catch (InvalidDataException) { continue; }
            yield return new RcoFile(e, e.Parent?.Name ?? "", e.Id ?? "", e.Text("type") ?? "", data);
        }
    }

    /// <summary>The element of a table ("texturetable", "soundtable"...) with this id: the id string, or its hash
    /// written "0x........" (firmware RCOs keep only hashes; other elements refer to resources by the same hash).</summary>
    public RcoElement? Find(string table, string id)
    {
        uint? hash = id.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(id[2..], System.Globalization.NumberStyles.HexNumber, null, out var h) ? h : null;
        foreach (var t in Root.Descendants().Where(e => e.Name == table))
            foreach (var e in t.Children)
                if (e.Id == id || (hash != null && e.IdHash == hash)) return e;
        return null;
    }

    /// <summary>The tree as indented pseudo-XML (rco-dump).</summary>
    public string Dump()
    {
        var sb = new StringBuilder();
        void Walk(RcoElement e, int depth)
        {
            sb.Append(' ', depth * 2).Append(e).AppendLine();
            foreach (var c in e.Children) Walk(c, depth + 1);
        }
        Walk(Root, 0);
        return sb.ToString();
    }
}
