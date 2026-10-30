using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace VitaSharp.Core.Shell;

/// <summary>An image of a liveitem (&lt;background&gt; or &lt;image&gt;): file and placement in its frame.</summary>
public sealed record LiveAreaImage(string File, string Align, string Valign, string Origin, int X, int Y);

/// <summary>One &lt;str&gt; run of a liveitem text.</summary>
public sealed record LiveAreaString(string Text, uint Color, float Size, bool Bold, bool Italic, bool Underline, bool Shadow);

/// <summary>A liveitem &lt;text&gt; block: its box (origin, size, margins, offsets), alignment and the strings.</summary>
public sealed class LiveAreaText
{
    public string Align = "", Valign = "", TextAlign = "", TextValign = "", LineAlign = "", Origin = "";
    public int X, Y, Width, Height, MarginTop, MarginBottom, MarginLeft, MarginRight, LineSpace;
    public bool WordWrap = true, WordScroll, Ellipsis;
    public List<LiveAreaString> Strings { get; } = new();

    /// <summary>The visible text (all runs joined), empty when the strings are only whitespace.</summary>
    public string PlainText => string.Concat(Strings.Select(s => s.Text));
}

/// <summary>One &lt;liveitem&gt;: what a frame shows (background, image, text) and where tapping it goes.</summary>
public sealed class LiveAreaItem
{
    public LiveAreaImage? Background, Image;
    public LiveAreaText? Text;
    public string Target = "";
    public bool IsEmpty => Background == null && Image == null && (Text == null || Text.PlainText.Trim().Length == 0);
}

/// <summary>A &lt;frame&gt;: an area of the LiveArea style ("frame1"...) and its liveitems. With autoflip (seconds)
/// the items rotate.</summary>
public sealed class LiveAreaFrame
{
    public required string Id { get; init; }
    public string Multi = "";
    public int AutoflipSeconds;
    public List<LiveAreaItem> Items { get; } = new();
}

/// <summary>
/// A game's LiveArea description (sce_sys/livearea/contents/template.xml): the style ("a1".."a5", "psmobile"...),
/// the background, the gate's startup image and the frames with their liveitems. Elements that come in localised
/// variants (background images, startup images, liveitems) are chosen like the shell does: a variant whose &lt;lang&gt;
/// or &lt;cntry lang=&gt; matches the system language, else the one marked default="on", else the first.
/// </summary>
public sealed class LiveAreaTemplate
{
    public string Style { get; private set; } = "a1";
    public string FormatVersion { get; private set; } = "";
    /// <summary>Folder the file names are relative to (the contents folder), or "" when parsed from a string.</summary>
    public string ContentsDir { get; private set; } = "";
    public string? Background { get; private set; }
    public string? Gate { get; private set; }
    public List<LiveAreaFrame> Frames { get; } = new();
    /// <summary>True when this is the firmware's default LiveArea (the game ships none).</summary>
    public bool IsDefault { get; private set; }

    /// <summary>Parses template.xml text. <paramref name="language"/> is a Vita language code ("en", "pt-br", "ja"...).</summary>
    public static LiveAreaTemplate Parse(string xml, string language = "en")
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml, LoadOptions.None); }
        catch (XmlException e) { throw new InvalidDataException("template.xml is not valid XML: " + e.Message, e); }
        var root = doc.Root;
        if (root == null || root.Name.LocalName != "livearea") throw new InvalidDataException("template.xml has no <livearea> root");
        var t = new LiveAreaTemplate
        {
            Style = ((string?)root.Attribute("style") ?? "a1").Trim().ToLowerInvariant(),
            FormatVersion = (string?)root.Attribute("format-ver") ?? "",
        };
        if (t.Style.Length == 0) t.Style = "a1";
        var lang = NormaliseLanguage(language);

        if (root.Element("livearea-background") is { } bg && Choose(bg.Elements("image"), lang) is { } bgImage)
            t.Background = FileName(bgImage);
        if (root.Element("gate") is { } gate && Choose(gate.Elements("startup-image"), lang) is { } startup)
            t.Gate = FileName(startup);

        foreach (var f in root.Elements("frame"))
        {
            var id = ((string?)f.Attribute("id") ?? "").Trim();
            if (id.Length == 0) continue;
            var frame = new LiveAreaFrame
            {
                Id = id,
                Multi = ((string?)f.Attribute("multi") ?? "").Trim(),
                AutoflipSeconds = Int(f.Attribute("autoflip")),
            };
            var items = f.Elements("liveitem").ToList();
            // multi="o": one of the variants (by language/country); otherwise (multi="a", autoflip) every matching one
            IEnumerable<XElement> chosen = frame.Multi == "o" || frame.AutoflipSeconds == 0
                ? Choose(items, lang) is { } one ? [one] : []
                : items.Where(i => Matches(i, lang) != false);
            foreach (var li in chosen)
            {
                var item = ParseItem(li);
                if (!item.IsEmpty) frame.Items.Add(item);
            }
            t.Frames.Add(frame);
        }
        return t;
    }

    /// <summary>
    /// The LiveArea of an installed title folder: sce_sys/retail/livearea when present (retail keys), else
    /// sce_sys/livearea, else the firmware's default (vs0:data/internal/livearea/default) when <paramref name="fsRoot"/>
    /// has one. Null when there is nothing to show.
    /// </summary>
    public static LiveAreaTemplate? Load(string appDir, string? fsRoot, string language = "en")
    {
        foreach (var rel in new[] { "sce_sys/retail/livearea/contents", "sce_sys/livearea/contents" })
        {
            var dir = Io.Vfs.ResolveCaseInsensitive(appDir, rel);
            var t = TryLoad(dir, language);
            if (t != null) return t;
        }
        if (fsRoot == null) return null;
        var def = TryLoad(Path.Combine(fsRoot, "vs0", "data", "internal", "livearea", "default", "sce_sys", "livearea", "contents"), language);
        if (def != null) def.IsDefault = true;
        return def;
    }

    static LiveAreaTemplate? TryLoad(string contentsDir, string language)
    {
        var xml = Io.Vfs.ResolveCaseInsensitive(contentsDir, "template.xml");
        if (!File.Exists(xml)) return null;
        try
        {
            var t = Parse(File.ReadAllText(xml), language);
            t.ContentsDir = contentsDir;
            return t;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Util.Log.Warn($"livearea: {xml}: {e.Message}");
            return null;
        }
    }

    /// <summary>Full path of a file named in the template (case-insensitive, '\' accepted), or null when missing.</summary>
    public string? Resolve(string? file)
    {
        if (string.IsNullOrEmpty(file) || ContentsDir.Length == 0) return null;
        var p = Io.Vfs.ResolveCaseInsensitive(ContentsDir, file.Replace('\\', '/'));
        return File.Exists(p) ? p : null;
    }

    static LiveAreaItem ParseItem(XElement li)
    {
        var item = new LiveAreaItem { Target = Direct(li.Element("target")) };
        if (li.Element("background") is { } b && FileName(b) is { Length: > 0 } bf) item.Background = Image(b, bf);
        if (li.Element("image") is { } i && FileName(i) is { Length: > 0 } imf) item.Image = Image(i, imf);
        if (li.Element("text") is { } tx) item.Text = ParseText(tx);
        return item;
    }

    static LiveAreaImage Image(XElement e, string file) => new(file,
        Attr(e, "align"), Attr(e, "valign"), Attr(e, "origin"), Int(e.Attribute("x")), Int(e.Attribute("y")));

    static LiveAreaText ParseText(XElement e)
    {
        var t = new LiveAreaText
        {
            Align = Attr(e, "align"), Valign = Attr(e, "valign"), TextAlign = Attr(e, "text-align"), TextValign = Attr(e, "text-valign"),
            LineAlign = Attr(e, "line-align"), Origin = Attr(e, "origin"),
            X = Int(e.Attribute("x")), Y = Int(e.Attribute("y")), Width = Int(e.Attribute("width")), Height = Int(e.Attribute("height")),
            MarginTop = Int(e.Attribute("margin-top")), MarginBottom = Int(e.Attribute("margin-bottom")),
            MarginLeft = Int(e.Attribute("margin-left")), MarginRight = Int(e.Attribute("margin-right")),
            LineSpace = Int(e.Attribute("line-space")),
            WordWrap = Attr(e, "word-wrap") != "off", WordScroll = Attr(e, "word-scroll") == "on", Ellipsis = Attr(e, "ellipsis") == "on",
        };
        foreach (var s in e.Elements("str"))
        {
            string text = string.Concat(s.Nodes().OfType<XText>().Select(n => n.Value)).Replace("\r\n", "\n");
            // indentation of the XML around the text is not part of it
            text = string.Join("\n", text.Split('\n').Select(l => l.Trim())).Trim('\n');
            t.Strings.Add(new LiveAreaString(text, ParseColor(Attr(s, "color")), Float(s.Attribute("size")),
                Attr(s, "bold") == "on", Attr(s, "oblique") == "on", Attr(s, "underline") == "on", Attr(s, "shadow") == "on"));
        }
        return t;
    }

    /// <summary>"#rrggbb", "rrggbb" or "#aarrggbb" as 0xAARRGGBB; white when missing or not valid.</summary>
    public static uint ParseColor(string s)
    {
        s = s.Trim().TrimStart('#');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return 0xFFFFFFFF;
        return s.Length <= 6 ? 0xFF000000 | v : v;
    }

    /// <summary>Picks one localised variant: a language match, else default="on", else the first that is not
    /// restricted to other languages, else the first.</summary>
    static XElement? Choose(IEnumerable<XElement> variants, string lang)
    {
        var list = variants.ToList();
        if (list.Count == 0) return null;
        return list.FirstOrDefault(v => Matches(v, lang) == true)
            ?? list.FirstOrDefault(v => Attr(v, "default") == "on")
            ?? list.FirstOrDefault(v => Matches(v, lang) == null)
            ?? list[0];
    }

    /// <summary>True when a variant names the language (&lt;lang&gt;en&lt;/lang&gt;, &lt;cntry lang="en"&gt;),
    /// false when it names only others, null when it is not language specific.</summary>
    static bool? Matches(XElement v, string lang)
    {
        var langs = v.Elements("lang").SelectMany(l => l.Value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Concat(v.Elements("cntry").Select(c => (string?)c.Attribute("lang")).Where(l => !string.IsNullOrWhiteSpace(l))!)
            .Select(l => NormaliseLanguage(l!)).ToList();
        if (langs.Count == 0) return null;
        string baseLang = lang.Split('-')[0];
        return langs.Contains(lang) || langs.Contains(baseLang);
    }

    static string NormaliseLanguage(string l) => l.Trim().ToLowerInvariant().Replace('_', '-');

    /// <summary>The element's own text (child elements such as &lt;lang&gt; and &lt;cntry&gt; excluded), trimmed.</summary>
    static string Direct(XElement? e) => e == null ? "" : string.Concat(e.Nodes().OfType<XText>().Select(n => n.Value)).Trim();

    static string FileName(XElement e) => Direct(e).Replace('\\', '/');

    static string Attr(XElement e, string name) => ((string?)e.Attribute(name) ?? "").Trim().ToLowerInvariant();

    static int Int(XAttribute? a) => a != null && int.TryParse(a.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    static float Float(XAttribute? a) => a != null && float.TryParse(a.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>The Vita language code for a .NET culture ("pt-BR" -> "pt-br", "zh-Hant" -> "zh-t").</summary>
    public static string VitaLanguage(CultureInfo c)
    {
        string name = c.Name.ToLowerInvariant();
        if (name.StartsWith("zh")) return name.Contains("hant") || name.EndsWith("tw") || name.EndsWith("hk") ? "zh-t" : "zh-s";
        if (name is "pt-br" or "en-gb") return name;
        string two = c.TwoLetterISOLanguageName.ToLowerInvariant();
        return two is "ja" or "en" or "fr" or "es" or "de" or "it" or "nl" or "pt" or "ru" or "ko" or "fi" or "sv" or "da" or "no" or "nb" or "pl" or "tr"
            ? (two == "nb" ? "no" : two) : "en";
    }
}
