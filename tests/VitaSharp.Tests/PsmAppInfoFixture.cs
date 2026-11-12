using System.Text;

namespace VitaSharp.Tests;

/// <summary>Builds a synthetic PSM app.info ("PSMA" binary XML, the layout <see cref="Core.Package.PsmAppInfo"/>
/// reads): an application element with default_locale and version, a name with localized items and images (icons as
/// files). No Sony content.</summary>
internal static class PsmAppInfoFixture
{
    private sealed class Element(string name)
    {
        public string Name = name;
        public List<(string Name, int Type, object Value)> Attrs = [];
        public List<Element> Children = [];
    }

    public static byte[] Build(IReadOnlyDictionary<string, string> names, string defaultLocale = "en-US", string version = "1.00",
                               IReadOnlyDictionary<string, byte[]>? images = null)
    {
        var root = new Element("application");
        root.Attrs.Add(("default_locale", 3, defaultLocale));
        root.Attrs.Add(("version", 3, version));
        root.Attrs.Add(("runtime_version", 3, "1.21"));
        var name = new Element("name");
        foreach ((string locale, string value) in names)
        {
            var item = new Element("localized_item");
            item.Attrs.Add(("locale", 3, locale));
            item.Attrs.Add(("value", 3, value));
            name.Children.Add(item);
        }
        root.Children.Add(name);
        var imgs = new Element("images");
        foreach ((string key, byte[] data) in images ?? new Dictionary<string, byte[]>())
        {
            imgs.Attrs.Add((key, 8, data));
        }
        root.Children.Add(imgs);

        var strings = new MemoryStream();
        var stringOffsets = new Dictionary<string, int>();
        int Str(string s)
        {
            if (!stringOffsets.TryGetValue(s, out int o))
            {
                o = (int)strings.Length;
                byte[] b = Encoding.UTF8.GetBytes(s);
                strings.Write(b);
                strings.WriteByte(0);
                stringOffsets[s] = o;
            }
            return o;
        }
        var files = new MemoryStream();
        var tree = new MemoryStream();
        var w = new BinaryWriter(tree);

        // layout: each element's offset is known before its children are written (depth first, children after it)
        var offsets = new Dictionary<Element, int>();
        int next = 0;
        void Assign(Element e)
        {
            offsets[e] = next;
            next += 28 + e.Attrs.Count * 16;
            foreach (Element c in e.Children)
            {
                Assign(c);
            }
        }
        Assign(root);
        void Write(Element e, Element? parent)
        {
            w.Write(Str(e.Name));
            w.Write(e.Attrs.Count);
            w.Write(parent != null ? offsets[parent] : -1);
            int i = parent?.Children.IndexOf(e) ?? -1;
            w.Write(parent != null && i > 0 ? offsets[parent.Children[i - 1]] : -1);
            w.Write(parent != null && i < parent.Children.Count - 1 ? offsets[parent.Children[i + 1]] : -1);
            w.Write(e.Children.Count > 0 ? offsets[e.Children[0]] : -1);
            w.Write(e.Children.Count > 0 ? offsets[e.Children[^1]] : -1);
            foreach ((string n, int type, object value) in e.Attrs)
            {
                w.Write(Str(n));
                w.Write(type);
                if (value is string s)
                {
                    byte[] b = Encoding.UTF8.GetBytes(s);
                    w.Write(Str(s));
                    w.Write(b.Length);
                }
                else
                {
                    byte[] data = (byte[])value;
                    w.Write((int)files.Length);
                    w.Write(data.Length);
                    files.Write(data);
                }
            }
            foreach (Element c in e.Children)
            {
                Write(c, e);
            }
        }
        Write(root, null);

        const int Header = 0x58;
        int treeOff = Header;
        int strOff = treeOff + (int)tree.Length;
        int fileOff = strOff + (int)strings.Length;
        var output = new MemoryStream();
        var o = new BinaryWriter(output);
        o.Write("PSMA"u8);
        o.Write(0x100);
        int[] pairs = new int[20];
        pairs[0] = treeOff;
        pairs[1] = (int)tree.Length;
        for (int k = 2; k < 6; k += 2)
        {
            pairs[k] = strOff;
        }

        pairs[6] = strOff;
        pairs[7] = (int)strings.Length;
        for (int k = 8; k < 16; k += 2)
        {
            pairs[k] = fileOff;
        }

        pairs[16] = fileOff;
        pairs[17] = (int)files.Length;
        foreach (int p in pairs)
        {
            o.Write(p);
        }

        o.Write(tree.ToArray());
        o.Write(strings.ToArray());
        o.Write(files.ToArray());
        return output.ToArray();
    }
}
