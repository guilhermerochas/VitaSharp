using System.Buffers.Binary;
using System.Text;

namespace VitaSharp.Core.Package;

/// <summary>
/// A PlayStation Mobile title's description (ux0:psm/&lt;id&gt;/RO/Application/app.info): the title's app.xml compiled
/// into Sony's binary XML ("PSMA", the CXML layout of the system's resources), with the icons inside it. The shell
/// reads it to show a PSM title (FW 3.74 shell.self opens ux0:psm/&lt;id&gt;/RO/Application/app.info and builds the
/// title's icon and LiveArea from it). Layout: "PSMA", version, then (offset, size) pairs of the element tree (+0x08),
/// three unused tables, the string table (+0x20) and four more, the file table (+0x48). An element is 7 ints (name,
/// attribute count, parent, previous, next, first child, last child; offsets into the tree, -1 for none) and its
/// attributes of 4 ints (name, type, two values): type 3 a string (offset, length), 1 an int, 8 a file (offset, size).
/// </summary>
public sealed class PsmAppInfo
{
    private const int AttrString = 3;
    private const int AttrFile = 8;

    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _shortNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _images = new(StringComparer.Ordinal);

    /// <summary>The application's default locale ("en-US").</summary>
    public string DefaultLocale { get; private set; } = "";

    /// <summary>The title's version ("1.13").</summary>
    public string Version { get; private set; } = "";

    /// <summary>The PSM runtime version the title needs ("1.21").</summary>
    public string RuntimeVersion { get; private set; } = "";

    /// <summary>The images of the description by name ("icon_128x128", "icon_256x256", "splash_854x480").</summary>
    public IReadOnlyDictionary<string, byte[]> Images => _images;

    /// <summary>The title's name in <paramref name="locale"/> ("pt-BR"; the language alone also matches), else in
    /// its default locale, else the first one; "" when it has none.</summary>
    public string Name(string? locale = null) => Pick(_names, locale);

    /// <summary>The short name, as <see cref="Name"/>.</summary>
    public string ShortName(string? locale = null) => Pick(_shortNames, locale);

    /// <summary>The largest icon up to <paramref name="maxSize"/> pixels wide (PNG), else the smallest one; null
    /// when the description has none.</summary>
    public byte[]? Icon(int maxSize = 256)
    {
        var icons = _images.Where(kv => kv.Key.StartsWith("icon_", StringComparison.Ordinal))
            .Select(kv => (Size: IconSize(kv.Key), Data: kv.Value)).Where(i => i.Size > 0).OrderBy(i => i.Size).ToList();
        if (icons.Count == 0)
        {
            return null;
        }

        var fit = icons.Where(i => i.Size <= maxSize).ToList();
        return fit.Count > 0 ? fit[^1].Data : icons[0].Data;
    }

    private static int IconSize(string name)
    {
        string s = name["icon_".Length..];
        int x = s.IndexOf('x');
        return x > 0 && int.TryParse(s[..x], out int w) ? w : 0;
    }

    private string Pick(Dictionary<string, string> byLocale, string? locale)
    {
        if (locale != null)
        {
            if (byLocale.TryGetValue(locale, out string? exact))
            {
                return exact;
            }

            string lang = locale.Split('-')[0];
            foreach ((string key, string value) in byLocale)
            {
                if (key.Split('-')[0].Equals(lang, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }
        if (byLocale.TryGetValue(DefaultLocale, out string? def))
        {
            return def;
        }

        return byLocale.Values.FirstOrDefault() ?? "";
    }

    /// <summary>Reads an app.info file; null when it is missing or not a PSM description.</summary>
    public static PsmAppInfo? Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Parses an app.info; null when <paramref name="data"/> is not a PSM description.</summary>
    public static PsmAppInfo? Parse(byte[] data)
    {
        if (data.Length < 0x58 || !data.AsSpan(0, 4).SequenceEqual("PSMA"u8))
        {
            return null;
        }

        var reader = new Reader(data);
        if (!reader.Valid)
        {
            return null;
        }

        var info = new PsmAppInfo();
        try
        {
            info.Read(reader);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
        return info;
    }

    private void Read(Reader reader)
    {
        int root = 0;
        DefaultLocale = reader.StringAttr(root, "default_locale") ?? "";
        Version = reader.StringAttr(root, "version") ?? "";
        RuntimeVersion = reader.StringAttr(root, "runtime_version") ?? "";
        foreach (int child in reader.Children(root))
        {
            switch (reader.Name(child))
            {
                case "name":
                    ReadLocalized(reader, child, _names);
                    break;
                case "short_name":
                    ReadLocalized(reader, child, _shortNames);
                    break;
                case "images":
                    foreach ((string name, byte[] file) in reader.FileAttrs(child))
                    {
                        _images[name] = file;
                    }
                    break;
            }
        }
    }

    private static void ReadLocalized(Reader reader, int element, Dictionary<string, string> into)
    {
        foreach (int item in reader.Children(element))
        {
            if (reader.StringAttr(item, "locale") is { } locale && reader.StringAttr(item, "value") is { } value)
            {
                into.TryAdd(locale, value);
            }
        }
    }

    /// <summary>The element tree of a PSMA file.</summary>
    private sealed class Reader
    {
        private readonly byte[] _data;
        private readonly int _tree, _treeSize, _strings, _stringsSize, _files, _filesSize;

        public Reader(byte[] data)
        {
            _data = data;
            _tree = I32(0x08);
            _treeSize = I32(0x0C);
            _strings = I32(0x20);
            _stringsSize = I32(0x24);
            _files = I32(0x48);
            _filesSize = I32(0x4C);
        }

        public bool Valid => In(_tree, _treeSize) && In(_strings, _stringsSize) && In(_files, _filesSize) && _treeSize >= 28;

        private bool In(int offset, int size) => offset >= 0 && size >= 0 && (long)offset + size <= _data.Length;

        private int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset, 4));

        private int Field(int element, int index) => I32(_tree + element + index * 4);

        public string Name(int element) => String(Field(element, 0));

        public IEnumerable<int> Children(int element)
        {
            int guard = 0;
            for (int child = Field(element, 5); child >= 0 && child + 28 <= _treeSize && guard < 4096; child = Field(child, 4))
            {
                guard++;
                yield return child;
            }
        }

        private IEnumerable<(string Name, int Type, int A, int B)> Attrs(int element)
        {
            int count = Field(element, 1);
            for (int i = 0; i < count && element + 28 + (i + 1) * 16 <= _treeSize; i++)
            {
                int attr = _tree + element + 28 + i * 16;
                yield return (String(I32(attr)), I32(attr + 4), I32(attr + 8), I32(attr + 12));
            }
        }

        public string? StringAttr(int element, string name)
        {
            foreach ((string attrName, int type, int offset, int length) in Attrs(element))
            {
                if (type == AttrString && attrName == name && offset >= 0 && length >= 0 && offset + length <= _stringsSize)
                {
                    return Encoding.UTF8.GetString(_data, _strings + offset, length);
                }
            }
            return null;
        }

        public IEnumerable<(string Name, byte[] Data)> FileAttrs(int element)
        {
            foreach ((string attrName, int type, int offset, int size) in Attrs(element))
            {
                if (type == AttrFile && offset >= 0 && size > 0 && (long)offset + size <= _filesSize)
                {
                    yield return (attrName, _data.AsSpan(_files + offset, size).ToArray());
                }
            }
        }

        private string String(int offset)
        {
            if (offset < 0 || offset >= _stringsSize)
            {
                return "";
            }

            int start = _strings + offset;
            int end = Array.IndexOf(_data, (byte)0, start, _stringsSize - offset);
            return Encoding.UTF8.GetString(_data, start, (end < 0 ? _strings + _stringsSize : end) - start);
        }
    }
}
