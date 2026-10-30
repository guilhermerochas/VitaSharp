using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using VitaSharp.Core.Shell;

namespace VitaSharp.Tests;

/// <summary>The Vita shell assets the launcher's home screen reads: template.xml, LiveArea layout, RCO, GIM, VAG, themes.</summary>
public class ShellTests
{
    // ------------------------------------------------------------------ template.xml

    const string GowTemplate = """
        <?xml version="1.0" encoding="utf-8"?>
        <livearea style="a4" format-ver="01.03" content-rev="1">
          <livearea-background>
            <image default="on">backgrounds\bg0.png<cntry lang="en">all</cntry></image>
          </livearea-background>
          <gate>
            <startup-image default="on">gates\startup_english.png<cntry lang="en">all</cntry></startup-image>
            <startup-image>gates\startup_french.png<cntry lang="fr">all</cntry></startup-image>
          </gate>
          <frame id="frame1" multi="o" autoflip="0" rev="" userdata="" />
          <frame id="frame5" multi="o" autoflip="0">
            <liveitem default="on">
              <cntry>all</cntry>
              <text align="center" valign="bottom" width="840" height="70" origin="frame" text-align="right" text-valign="bottom" line-align="center" word-wrap="on" ellipsis="off" margin-top="0" margin-bottom="30" margin-left="0" margin-right="80">
                <str color="#ffffff" size="18" bold="on" oblique="off" underline="off" shadow="off">Sony Computer Entertainment</str>
              </text>
            </liveitem>
          </frame>
        </livearea>
        """;

    [Fact]
    public void TemplateParsesStyleImagesAndLocalisedVariants()
    {
        var t = LiveAreaTemplate.Parse(GowTemplate, "en");
        Assert.Equal("a4", t.Style);
        Assert.Equal("backgrounds/bg0.png", t.Background); // the <cntry> child is not part of the file name; '\' -> '/'
        Assert.Equal("gates/startup_english.png", t.Gate);
        Assert.Equal("gates/startup_french.png", LiveAreaTemplate.Parse(GowTemplate, "fr").Gate);
        // a language without a variant falls back to default="on"
        Assert.Equal("gates/startup_english.png", LiveAreaTemplate.Parse(GowTemplate, "ja").Gate);
        Assert.Equal(["frame1", "frame5"], t.Frames.Select(f => f.Id));
        Assert.Empty(t.Frames[0].Items);
        var text = Assert.Single(t.Frames[1].Items).Text!;
        Assert.Equal("Sony Computer Entertainment", text.PlainText);
        Assert.Equal(("center", "bottom", "right", "bottom", "frame"), (text.Align, text.Valign, text.TextAlign, text.TextValign, text.Origin));
        Assert.Equal((840, 70, 30, 80), (text.Width, text.Height, text.MarginBottom, text.MarginRight));
        var s = Assert.Single(text.Strings);
        Assert.Equal((0xFFFFFFFFu, 18f, true, false), (s.Color, s.Size, s.Bold, s.Italic));
    }

    [Fact]
    public void TemplateKeepsItemsWithContentAndTargets()
    {
        const string xml = """
            <livearea style="A2" format-ver="01.03">
              <livearea-background><image>pic1.png</image></livearea-background>
              <gate><startup-image>gate.png</startup-image></gate>
              <frame id="frame1"><liveitem><image>frame1.png</image><target>psts:browse?category=X</target></liveitem></frame>
              <frame id="frame2"><liveitem>
                  <image align="left" valign="top" x="4" y="-2">frame2.png</image>
                  <target> http://example.com/ </target>
                  <text><str color="00ff00" size="24">
                     line one
                     line two
                  </str></text>
              </liveitem></frame>
              <frame id="frame3"><liveitem /></frame>
              <frame id="frame4"><liveitem><text><str color="#ffffff">   </str></text></liveitem></frame>
            </livearea>
            """;
        var t = LiveAreaTemplate.Parse(xml);
        Assert.Equal("a2", t.Style);
        Assert.Equal(4, t.Frames.Count);
        Assert.Equal("psts:browse?category=X", t.Frames[0].Items[0].Target);
        var item = t.Frames[1].Items[0];
        Assert.Equal("http://example.com/", item.Target);
        Assert.Equal(new LiveAreaImage("frame2.png", "left", "top", "", 4, -2), item.Image);
        Assert.Equal("line one\nline two", item.Text!.PlainText);
        Assert.Equal(0xFF00FF00u, item.Text.Strings[0].Color);
        Assert.Empty(t.Frames[2].Items); // empty liveitem
        Assert.Empty(t.Frames[3].Items); // whitespace-only text
    }

    [Fact]
    public void TemplateAutoflipKeepsEveryMatchingItem()
    {
        const string xml = """
            <livearea style="a1">
              <frame id="frame3" multi="a" autoflip="5">
                <liveitem><image>a.png</image></liveitem>
                <liveitem><lang>ja</lang><image>ja.png</image></liveitem>
                <liveitem><image>b.png</image></liveitem>
              </frame>
            </livearea>
            """;
        var f = LiveAreaTemplate.Parse(xml, "en").Frames.Single();
        Assert.Equal(5, f.AutoflipSeconds);
        Assert.Equal(["a.png", "b.png"], f.Items.Select(i => i.Image!.File));
        Assert.Equal(3, LiveAreaTemplate.Parse(xml, "ja").Frames.Single().Items.Count);
    }

    [Fact]
    public void TemplateRejectsOtherXml()
    {
        Assert.Throws<InvalidDataException>(() => LiveAreaTemplate.Parse("<theme/>"));
        Assert.Throws<InvalidDataException>(() => LiveAreaTemplate.Parse("<livearea"));
    }

    [Fact]
    public void TemplateLoadFallsBackToTheFirmwareDefault()
    {
        var root = Directory.CreateTempSubdirectory("vitasharp-livearea").FullName;
        try
        {
            var app = Path.Combine(root, "ux0", "app", "ABCD00001");
            Directory.CreateDirectory(Path.Combine(app, "sce_sys"));
            Assert.Null(LiveAreaTemplate.Load(app, root));
            var def = Path.Combine(root, "vs0", "data", "internal", "livearea", "default", "sce_sys", "livearea", "contents");
            Directory.CreateDirectory(def);
            File.WriteAllText(Path.Combine(def, "template.xml"), "<livearea style='a1'><livearea-background><image>BG0.PNG</image></livearea-background></livearea>");
            File.WriteAllBytes(Path.Combine(def, "bg0.png"), [1]);
            var t = LiveAreaTemplate.Load(app, root)!;
            Assert.True(t.IsDefault);
            Assert.Equal(Path.Combine(def, "bg0.png"), t.Resolve(t.Background), ignoreCase: true); // case-insensitive file names
            // the game's own LiveArea wins
            var own = Path.Combine(app, "sce_sys", "livearea", "contents");
            Directory.CreateDirectory(own);
            File.WriteAllText(Path.Combine(own, "template.xml"), "<livearea style='a3'/>");
            Assert.Equal("a3", LiveAreaTemplate.Load(app, root)!.Style);
            Assert.False(LiveAreaTemplate.Load(app, root)!.IsDefault);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("pt-BR", "pt-br")]
    [InlineData("pt-PT", "pt")]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en-gb")]
    [InlineData("ja-JP", "ja")]
    [InlineData("zh-TW", "zh-t")]
    [InlineData("zh-CN", "zh-s")]
    [InlineData("nb-NO", "no")]
    [InlineData("cs-CZ", "en")]
    public void VitaLanguageCodes(string culture, string vita) =>
        Assert.Equal(vita, LiveAreaTemplate.VitaLanguage(new System.Globalization.CultureInfo(culture)));

    // ------------------------------------------------------------------ layout

    [Fact]
    public void LayoutConvertsTheStyleTables()
    {
        Assert.Equal(new LaRect(340, 183, 280, 158), LiveAreaLayout.Gate("a1"));
        Assert.Equal(new LaRect(340, 149, 280, 158), LiveAreaLayout.Gate("a4"));
        Assert.Equal(new LaRect(60, 130, 260, 260), LiveAreaLayout.Frame("a1", "frame1"));
        Assert.Equal(new LaRect(60, 390, 840, 150), LiveAreaLayout.Frame("a1", "frame3"));
        Assert.Equal(new LaRect(60, 470, 840, 70), LiveAreaLayout.Frame("a4", "frame5"));
        Assert.Null(LiveAreaLayout.Frame("a1", "frame4"));
        Assert.Equal(LiveAreaLayout.Gate("a1"), LiveAreaLayout.Gate("unknown")); // unknown styles use a1
        Assert.Equal(new LaRect(60, 44, 840, 500), LiveAreaLayout.BackgroundRect);
        // every frame of every style lies inside the background area
        foreach (var style in LiveAreaLayout.KnownStyles)
            for (int i = 1; i <= 10; i++)
                if (LiveAreaLayout.Frame(style, "frame" + i) is { } f)
                    Assert.True(f.X >= 60 - 0.01 && f.Right <= 900.01 && f.Y >= 44 && f.Bottom <= 544, $"{style} frame{i} {f}");
    }

    [Fact]
    public void LayoutPlacesImages()
    {
        var frame = new LaRect(100, 100, 200, 100);
        LiveAreaImage Img(string align = "", string valign = "", int x = 0, int y = 0) => new("i.png", align, valign, "", x, y);
        Assert.Equal(new LaRect(150, 125, 100, 50), LiveAreaLayout.PlaceImage(frame, 100, 50, Img()));
        Assert.Equal(new LaRect(100, 100, 100, 50), LiveAreaLayout.PlaceImage(frame, 100, 50, Img("left", "top")));
        Assert.Equal(new LaRect(200, 150, 100, 50), LiveAreaLayout.PlaceImage(frame, 100, 50, Img("right", "bottom")));
        // x moves right, y moves up
        Assert.Equal(new LaRect(160, 105, 100, 50), LiveAreaLayout.PlaceImage(frame, 100, 50, Img(x: 10, y: 20)));
        // never outside the frame
        Assert.Equal(new LaRect(200, 100, 100, 50), LiveAreaLayout.PlaceImage(frame, 100, 50, Img(x: 500, y: 500)));
        // larger images shrink to fit, keeping their aspect
        Assert.Equal(new LaRect(100, 125, 200, 50), LiveAreaLayout.PlaceImage(frame, 400, 100, Img()));
    }

    [Fact]
    public void LayoutPlacesTextBoxesAndLines()
    {
        var t = LiveAreaTemplate.Parse(GowTemplate).Frames[1].Items[0].Text!;
        var frame = LiveAreaLayout.Frame("a4", "frame5")!.Value;
        var p = LiveAreaLayout.PlaceText(frame, null, null, t);
        // the 840x70 box is the frame; margins bottom 30 and right 80 inside it; lines bottom-right
        Assert.Equal(new LaRect(60, 470, 760, 40), p.Box);
        Assert.Equal((LaAlign.End, LaAlign.End), (p.Horizontal, p.Vertical));

        var plain = new LiveAreaText();
        var bg = new LaRect(70, 140, 100, 100);
        var q = LiveAreaLayout.PlaceText(new LaRect(60, 130, 260, 260), bg, null, plain);
        Assert.Equal(bg, q.Box); // origin defaults to the background image
        Assert.Equal((LaAlign.Center, LaAlign.Center), (q.Horizontal, q.Vertical));
        var right = LiveAreaLayout.PlaceText(new LaRect(0, 0, 300, 100), null, new LaRect(0, 0, 100, 100), new LiveAreaText { Origin = "image", Align = "outside-right" });
        Assert.Equal(new LaRect(100, 0, 200, 100), right.Box);
        Assert.Equal(LaAlign.Start, right.Horizontal);
        // a y offset with valign="top" is measured from the centre
        var up = LiveAreaLayout.PlaceText(new LaRect(0, 0, 200, 200), null, null, new LiveAreaText { Valign = "top", Y = 40 });
        Assert.Equal((-40.0, LaAlign.Center), (up.Box.Y, up.Vertical));
    }

    // ------------------------------------------------------------------ RCO

    /// <summary>Builds an RCOF file: root &lt;resource&gt; with a &lt;texturetable&gt; holding one compressed and one
    /// plain file, plus every attribute type.</summary>
    static byte[] BuildRco(byte[] fileA, byte[] fileB)
    {
        var strings = new MemoryStream();
        var offsets = new Dictionary<string, int>();
        int S(string s)
        {
            if (offsets.TryGetValue(s, out var o)) return o;
            o = (int)strings.Length;
            var b = Encoding.UTF8.GetBytes(s);
            strings.Write(b); strings.WriteByte(0);
            offsets[s] = o;
            return o;
        }
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(fileA);
            compressed = ms.ToArray();
        }
        var files = new MemoryStream();
        files.Write(compressed);
        while (files.Length % 16 != 0) files.WriteByte(0);
        int offB = (int)files.Length;
        files.Write(fileB);

        // id hashes {element offset, hash}, hashes, int and float arrays
        var idInt = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(idInt.AsSpan(4), 0x11111111);
        BinaryPrimitives.WriteUInt32LittleEndian(idInt.AsSpan(12), 0xAABBCCDD);
        BinaryPrimitives.WriteUInt32LittleEndian(idInt.AsSpan(20), 0x12345678);
        var hashes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(hashes.AsSpan(4), 0xCAFEBABE);
        var ints = new byte[12];
        for (int i = 0; i < 3; i++) BinaryPrimitives.WriteInt32LittleEndian(ints.AsSpan(i * 4), (i + 1) * 10);
        var floats = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(floats, 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(floats.AsSpan(4), -2f);
        var wide = Encoding.Unicode.GetBytes("Olá");

        // tree: element = {name, attr count, parent, prev, next, first child, last child} + attrs {name, type, a, b}
        var tree = new MemoryStream();
        var w = new BinaryWriter(tree);
        void Element(string name, int parent, int prev, int next, int first, int last, params (string, int, int, int)[] attrs)
        {
            w.Write(S(name)); w.Write(attrs.Length); w.Write(parent); w.Write(prev); w.Write(next); w.Write(first); w.Write(last);
            foreach (var (n, type, a, b) in attrs) { w.Write(S(n)); w.Write(type); w.Write(a); w.Write(b); }
        }
        // offsets: root 0 (28 + 2*16 = 60), table 60 (28), texA 88 (28 + 4*16 = 92), texB 180
        const int Root = 0, Table = 60, TexA = 88, TexB = 180;
        Element("resource", -1, -1, -1, Table, Table, ("version", 2, BitConverter.SingleToInt32Bits(0.1f), 0), ("id", 11, 0, 0));
        Element("texturetable", Root, -1, -1, TexA, TexB);
        S("compress"); S("on"); S("texture/png");
        Element("texture", Table, -1, TexB, -1, -1, ("id", 11, 8, 0), ("src", 8, 0, compressed.Length),
            ("compress", 3, S("on"), 2), ("origsize", 1, fileA.Length, 0));
        Element("texture", Table, TexA, -1, -1, -1, ("id", 11, 16, 0), ("src", 8, offB, fileB.Length), ("type", 3, S("texture/png"), 11),
            ("style", 5, 1, 0), ("pos", 6, 0, 3), ("size", 7, 0, 2), ("label", 4, 0, 3));
        Assert.Equal(TexB + 28 + 7 * 16, (int)tree.Length);

        byte[][] tables = [tree.ToArray(), [], idInt, strings.ToArray(), wide, hashes, ints, floats, files.ToArray()];
        var outS = new MemoryStream();
        var hw = new BinaryWriter(outS);
        hw.Write(0x464F4352u); hw.Write(0x110u);
        int pos = 0x50;
        foreach (var t in tables) { hw.Write(pos); hw.Write(t.Length); pos += (t.Length + 15) / 16 * 16; }
        foreach (var t in tables) { hw.Write(t); while (outS.Length % 16 != 0) hw.Write((byte)0); }
        return outS.ToArray();
    }

    [Fact]
    public void RcoParsesTreeAttributesAndFiles()
    {
        byte[] a = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray(), b = [1, 2, 3, 4, 5];
        var rco = Rco.Parse(BuildRco(a, b));
        Assert.Equal(0x110u, rco.Version);
        Assert.Equal("resource", rco.Root.Name);
        Assert.Equal(0.1f, rco.Root.Attribute("version")!.Float);
        Assert.Equal("0x11111111", rco.Root.Id);
        var table = Assert.Single(rco.Root.Children);
        Assert.Equal(["texture", "texture"], table.Children.Select(c => c.Name));
        var tb = table.Children[1];
        Assert.Same(table, tb.Parent);
        Assert.Equal(0x12345678u, tb.IdHash);
        Assert.Equal("texture/png", tb.Text("type"));
        Assert.Equal(0xCAFEBABEu, tb.Attribute("style")!.Hash);
        Assert.Equal([10, 20, 30], tb.Attribute("pos")!.Ints!);
        Assert.Equal([0.5f, -2f], tb.Attribute("size")!.Floats!);
        Assert.Equal("Olá", tb.Text("label"));

        Assert.Equal(a, rco.ReadFile(table.Children[0]));   // inflated (compress="on")
        Assert.Equal(b, rco.ReadFile(tb));
        Assert.Same(tb, rco.Find("texturetable", "0x12345678"));
        Assert.Null(rco.Find("texturetable", "0x99999999"));
        var files = rco.Files().ToList();
        Assert.Equal(["0xAABBCCDD", "0x12345678"], files.Select(f => f.Id));
        Assert.All(files, f => Assert.Equal("texturetable", f.Table));
        Assert.Contains("<texture ", rco.Dump());
    }

    [Fact]
    public void RcoRejectsBadFiles()
    {
        Assert.Throws<InvalidDataException>(() => Rco.Parse(new byte[0x50]));
        var good = BuildRco([1], [2]);
        var bad = (byte[])good.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(0x0C), int.MaxValue / 2); // tree size past the end
        Assert.Throws<InvalidDataException>(() => Rco.Parse(bad));
        bad = (byte[])good.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(0x50 + 20), 0x5000); // first child outside the tree
        Assert.Throws<InvalidDataException>(() => Rco.Parse(bad));
    }

    // ------------------------------------------------------------------ GIM

    /// <summary>A GIM: file block {picture {image, palette?}}.</summary>
    static byte[] BuildGim(int format, int order, int w, int h, int bpp, int pitchAlign, byte[] pixels, (int Format, byte[] Data, int Count)? palette = null)
    {
        byte[] Section(int type, int fmt, int ord, int sw, int sh, int sbpp, int align, byte[] data)
        {
            var hdr = new byte[0x40];
            BinaryPrimitives.WriteUInt16LittleEndian(hdr, (ushort)type);
            int size = 0x10 + 0x30 + data.Length;
            BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(4), size);
            BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(8), size);
            BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(12), 0x10);
            var s = hdr.AsSpan(0x10);
            BinaryPrimitives.WriteUInt16LittleEndian(s, 0x30);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], (ushort)fmt);
            BinaryPrimitives.WriteUInt16LittleEndian(s[6..], (ushort)ord);
            BinaryPrimitives.WriteUInt16LittleEndian(s[8..], (ushort)sw);
            BinaryPrimitives.WriteUInt16LittleEndian(s[10..], (ushort)sh);
            BinaryPrimitives.WriteUInt16LittleEndian(s[12..], (ushort)sbpp);
            BinaryPrimitives.WriteUInt16LittleEndian(s[14..], (ushort)align);
            BinaryPrimitives.WriteUInt16LittleEndian(s[16..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(s[0x1C..], 0x30);
            BinaryPrimitives.WriteInt32LittleEndian(s[0x20..], 0x30 + data.Length);
            return [.. hdr, .. data];
        }
        var image = Section(4, format, order, w, h, bpp, pitchAlign, pixels);
        var pal = palette is { } p ? Section(5, p.Format, 0, p.Count, 1, 32, 16, p.Data) : [];
        byte[] Block(int type, byte[] body)
        {
            var hdr = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(hdr, (ushort)type);
            BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(4), 16 + body.Length);
            BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(8), 16);
            BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(12), 16);
            return [.. hdr, .. body];
        }
        return [.. "MIG.00.1PSP\0"u8.ToArray(), 0, 0, 0, 0, .. Block(2, Block(3, [.. image, .. pal]))];
    }

    [Fact]
    public void GimDecodesDirectColour()
    {
        // 2x2 RGBA8888, rows padded to 16 bytes
        var px = new byte[32];
        byte[][] colours = [[255, 0, 0, 255], [0, 255, 0, 128], [0, 0, 255, 255], [10, 20, 30, 0]];
        for (int i = 0; i < 4; i++) colours[i].CopyTo(px, (i / 2) * 16 + (i % 2) * 4);
        var img = Gim.Decode(BuildGim(3, 0, 2, 2, 32, 16, px));
        Assert.Equal((2, 2), (img.Width, img.Height));
        Assert.Equal(colours.SelectMany(c => c), img.Pixels);

        // RGBA5650: pure red, green, blue, white
        var p16 = new byte[32];
        ushort[] v = [0x001F, 0x07E0, 0xF800, 0xFFFF];
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt16LittleEndian(p16.AsSpan((i / 2) * 16 + (i % 2) * 2), v[i]);
        var img16 = Gim.Decode(BuildGim(0, 0, 2, 2, 16, 16, p16));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 }, img16.Pixels);
    }

    [Fact]
    public void GimDecodesIndexedAndSwizzled()
    {
        // 32x8 INDEX8 in the "faster" order: two 16x8 tiles, tile 0 all index 1, tile 1 all index 2
        var px = new byte[256];
        for (int i = 0; i < 128; i++) { px[i] = 1; px[128 + i] = 2; }
        var pal = new byte[16];
        new byte[] { 0, 0, 0, 0, 255, 0, 0, 255, 0, 0, 255, 255 }.CopyTo(pal, 0);
        var img = Gim.Decode(BuildGim(5, 1, 32, 8, 8, 16, px, (3, pal, 4)));
        Assert.Equal((32, 8), (img.Width, img.Height));
        for (int y = 0; y < 8; y++)
        {
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, img.Pixels.AsSpan((y * 32 + 3) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, img.Pixels.AsSpan((y * 32 + 20) * 4, 4).ToArray());
        }
        // INDEX4: low nibble first
        var p4 = new byte[16];
        p4[0] = 0x21;
        var img4 = Gim.Decode(BuildGim(4, 0, 2, 1, 4, 16, p4, (3, pal, 4)));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, img4.Pixels);
    }

    [Fact]
    public void GimRejectsOtherData()
    {
        Assert.False(Gim.IsGim("PNG..."u8));
        Assert.Throws<InvalidDataException>(() => Gim.Decode(new byte[32]));
        var px = new byte[16];
        var gim = BuildGim(3, 0, 2, 2, 32, 16, px);
        BinaryPrimitives.WriteInt32LittleEndian(gim.AsSpan(16 + 32 + 16 + 0x20), 0x10000); // pixels end past the block
        Assert.Throws<InvalidDataException>(() => Gim.Decode(gim));
    }

    // ------------------------------------------------------------------ VAG

    static byte[] Vag(int rate, params byte[][] frames)
    {
        var hdr = new byte[0x30];
        "VAGp"u8.CopyTo(hdr);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(4), 0x20);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(0x0C), (uint)(frames.Length * 16));
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(0x10), (uint)rate);
        return [.. hdr, .. frames.SelectMany(f => f)];
    }

    [Fact]
    public void VagDecodesAdpcmFrames()
    {
        // filter 0, shift 12: each nibble n is the sample (n << 12) >> 12, i.e. its signed 4-bit value
        var f0 = new byte[16];
        f0[0] = 12;
        f0[2] = 0x71; // samples 1, 7
        f0[3] = 0x8F; // samples -1, -8
        // filter 1 (60/64 of the previous sample), shift 12, all zero nibbles, last frame
        var f1 = new byte[16];
        f1[0] = 0x1C;
        f1[1] = 1;
        var (s, rate) = VitaSharp.Core.Shell.Vag.DecodeMono(Vag(22050, f0, f1));
        Assert.Equal(22050, rate);
        Assert.Equal(56, s.Length);
        Assert.Equal(new short[] { 1, 7, -1, -8 }, s[..4]);
        Assert.Equal(0, s[27]);
        Assert.Equal(0, s[28]);
        // the end marker frame (flags 7) stops decoding
        var end = new byte[16];
        end[1] = 7;
        Assert.Equal(28, VitaSharp.Core.Shell.Vag.DecodeMono(Vag(48000, f0, end)).Samples.Length);
        // stereo from a left and a right file
        var st = VitaSharp.Core.Shell.Vag.Decode(Vag(48000, f0), Vag(48000, f1));
        Assert.Equal((2, 56), (st.Channels, st.Samples.Length));
        Assert.Equal((short)7, st.Samples[2]);
        Assert.Throws<InvalidDataException>(() => VitaSharp.Core.Shell.Vag.DecodeMono(new byte[64]));
    }

    // ------------------------------------------------------------------ themes and firmware assets

    [Fact]
    public void ThemesComeFromTheFirmwareAndUx0()
    {
        var root = Directory.CreateTempSubdirectory("vitasharp-theme").FullName;
        try
        {
            var assets = new ShellAssets(root);
            Assert.False(assets.HasFirmware);
            Assert.Empty(assets.Themes());
            Assert.Empty(assets.SystemApps());
            Assert.Null(assets.Texture(ShellAssets.TopMenuRco, ShellAssets.Textures.PageDot));
            Assert.Null(assets.Sound(ShellSoundEffect.Confirm));

            var themeDir = Path.Combine(root, "vs0", "data", "internal", "theme");
            Directory.CreateDirectory(themeDir);
            File.WriteAllBytes(Path.Combine(themeDir, "defaultTheme_homeScreen.png"), [0]);
            var custom = Path.Combine(root, "ux0", "theme", "MYTHEME");
            Directory.CreateDirectory(custom);
            File.WriteAllText(Path.Combine(custom, "theme.xml"), """
                <theme format-ver="01.00">
                  <InfomationProperty>
                    <m_contentId>UP0000-THEME0001_00-0000000000000000</m_contentId>
                    <m_title><m_default>Night</m_default><m_param><m_pt_br>Noite</m_pt_br></m_param></m_title>
                  </InfomationProperty>
                  <InfomationBarProperty><m_barColor>ff102030</m_barColor></InfomationBarProperty>
                  <HomeProperty>
                    <m_bgParam><BackgroundParam><m_imageFilePath>Home\bg1.png</m_imageFilePath><m_fontColor>ffeeeeee</m_fontColor></BackgroundParam></m_bgParam>
                    <m_bgmFilePath>bgm.at9</m_bgmFilePath>
                  </HomeProperty>
                </theme>
                """);
            Directory.CreateDirectory(Path.Combine(custom, "home"));
            File.WriteAllBytes(Path.Combine(custom, "home", "BG1.png"), [0]);
            var themes = assets.Themes("pt-br");
            Assert.Equal([ShellTheme.DefaultId, "UP0000-THEME0001_00-0000000000000000"], themes.Select(t => t.Id));
            Assert.Empty(themes[0].HomeBackgrounds); // the default theme's wave is drawn, not loaded
            var night = themes[1];
            Assert.Equal("Noite", night.Title);
            Assert.Equal("Night", assets.Themes("en")[1].Title);
            Assert.Single(night.HomeBackgrounds);
            Assert.Null(night.BgmPath); // bgm.at9 is missing
            Assert.Equal(0xFF102030u, night.BarColor);
            Assert.Equal(0xFFEEEEEEu, night.FontColor);
        }
        finally { Directory.Delete(root, true); }
    }
}
