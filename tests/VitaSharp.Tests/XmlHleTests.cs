using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// SceLibXml HLE (Xml.cs, Xml.Parse.cs). Each scenario drives the library through guest calls on a guest thread, like
/// a game, and logs what it sees (results, node types and names, texts, the SAX handler's calls, the serialized
/// text). With the firmware's libSceXml.suprx installed the same scenarios also run against the module's exports and
/// the logs must match; without it only the HLE run happens, with fixed expectations.
/// </summary>
[Collection("guest")]
public sealed unsafe class XmlHleTests(GuestMemoryFixture fx, ITestOutputHelper output)
{
    const string Sample = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- top -->\n<root a=\"1\" b='two &amp; more'>\n  <item id=\"x\">Hello &lt;world&gt;</item>\n  <empty/>\n  <?proc data?>\n  <![CDATA[raw <b>]]>\n  <n>plain</n>\n</root>\n";

    public static IEnumerable<object[]> Scenarios => XmlScenarios.All.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Hle_RunsScenario(string scenario)
    {
        using var h = new XmlHarness(fx, null);
        var log = h.Run(XmlScenarios.All[scenario], false);
        output.WriteLine(string.Join("\n", log));
        Assert.DoesNotContain(log, l => l.StartsWith("EXCEPTION", StringComparison.Ordinal));
        if (XmlScenarios.Expected.TryGetValue(scenario, out var expect))
        {
            foreach (string e in expect)
            {
                Assert.Contains(e, log);
            }
        }
    }

    [XmlFirmwareTheory]
    [MemberData(nameof(Scenarios))]
    public void MatchesFirmwareModule(string scenario)
    {
        using var h = new XmlHarness(fx, XmlFirmware.ModulePath);
        var lle = h.Run(XmlScenarios.All[scenario], true);
        var hle = h.Run(XmlScenarios.All[scenario], false);
        output.WriteLine("--- firmware\n" + string.Join("\n", lle) + "\n--- hle\n" + string.Join("\n", hle));
        Assert.Equal(lle, hle);
    }

    [Fact]
    public void Hle_ResultNamesAndEngineCodes()
    {
        Assert.Equal(unchecked((int)0x80850008), Xml.BxResultToResultType(unchecked((int)0x8085200A)));
        Assert.Equal(0x850001, Xml.BxResultToResultType(unchecked((int)0x80852100)));
        Assert.Equal(0x850002, Xml.BxResultToResultType(unchecked((int)0x80852101)));
        Assert.Equal(unchecked((int)0x80850000), Xml.BxResultToResultType(0x1234));
    }

    internal static string SampleText => Sample;
}

/// <summary>The scenarios: each logs through the driver.</summary>
static class XmlScenarios
{
    public static readonly Dictionary<string, Action<XmlDriver>> All = new()
    {
        ["dom-parse"] = d =>
        {
            d.Init();
            uint b = d.Obj(4), doc = d.Obj(4);
            d.Call(XmlDriver.BuilderCtor, b);
            d.Rec("builder.init", d.Call(XmlDriver.BuilderInit, b, d.Initializer));
            d.Rec("builder.init2", d.Call(XmlDriver.BuilderInit, b, d.Initializer));
            d.Call(XmlDriver.BuilderResolve, b, 1);
            d.Rec("parse", d.Call(XmlDriver.BuilderParse, b, d.Str(XmlHleTests.SampleText), 1));
            d.Call(XmlDriver.BuilderGetDocument, doc, b);
            d.Rec("available", d.Call(XmlDriver.DocAvailable, doc));
            d.Rec("readonly", d.Call(XmlDriver.DocReadOnly, doc) & 0xFF);
            d.Rec("status", d.Call(XmlDriver.DocStatus, doc));
            d.Walk(doc, d.Call64(XmlDriver.DocGetRoot, doc), 0);
            d.Rec("docroot.type", d.Call(XmlDriver.DocNodeType, doc, 0, d.Lo(d.Call64(XmlDriver.DocGetDocRoot, doc)), d.Hi(d.Call64(XmlDriver.DocGetDocRoot, doc))));
            d.Serialize(doc, 0);
            d.Serialize(doc, 1);
            d.Call(XmlDriver.DocDtor, doc);
            d.Rec("builder.term", d.Call(XmlDriver.BuilderTerm, b));
            d.Rec("builder.term2", d.Call(XmlDriver.BuilderTerm, b));
            d.Term();
        },
        ["dom-entities"] = d =>
        {
            d.Init();
            uint b = d.Obj(4), doc = d.Obj(4);
            d.Call(XmlDriver.BuilderCtor, b);
            d.Call(XmlDriver.BuilderInit, b, d.Initializer);
            d.Call(XmlDriver.BuilderSkipWs, b, 1);
            d.Rec("parse", d.Call(XmlDriver.BuilderParse, b, d.Str("<a>x &amp; y<b>&quot;</b></a>"), 1));
            d.Call(XmlDriver.BuilderGetDocument, doc, b);
            d.Walk(doc, d.Call64(XmlDriver.DocGetRoot, doc), 0);
            d.Call(XmlDriver.DocDtor, doc);
            d.Call(XmlDriver.BuilderTerm, b);
            d.Term();
        },
        ["sax"] = d =>
        {
            d.Init();
            uint p = d.Obj(4);
            d.Call(XmlDriver.ParserCtor, p);
            d.Rec("init", d.Call(XmlDriver.ParserInit, p, d.Initializer));
            d.Rec("parse.nohandler", d.Call(XmlDriver.ParserParse, p, d.Str("<a/>"), 1));
            d.Call(XmlDriver.ParserSetHandler, p, d.Handler);
            d.Call(XmlDriver.ParserSetUserData, p, 0x1234);
            d.Call(XmlDriver.ParserSkipWs, p, 1);
            d.Rec("parse", d.Call(XmlDriver.ParserParse, p, d.Str(XmlHleTests.SampleText), 1));
            d.Call(XmlDriver.ParserResolve, p, 1);
            d.Rec("parse.resolved", d.Call(XmlDriver.ParserParse, p, d.Str("<r><x k=\"v\">a&amp;b</x></r>"), 1));
            d.Rec("parse.chunk1", d.Call(XmlDriver.ParserParse, p, d.Str("<r><x>te"), 0));
            d.Rec("parse.chunk2", d.Call(XmlDriver.ParserParse, p, d.Str("xt</x></r>"), 1));
            d.Rec("parse.bad", d.Call(XmlDriver.ParserParse, p, d.Str("<r><x></r>"), 1));
            d.Rec("term", d.Call(XmlDriver.ParserTerm, p));
            d.Term();
        },
        ["errors"] = d =>
        {
            d.Init();
            uint b = d.Obj(4);
            d.Call(XmlDriver.BuilderCtor, b);
            d.Call(XmlDriver.BuilderInit, b, d.Initializer);
            foreach (string s in new[] { "<a></b>", "<a>", "<a b=1/>", "<a b=\"1\" b=\"2\"/>", "<a>&bogus;</a>", "", "<a/><b/>", "<a><!-- x</a>", "<a b></a>", "text" })
            {
                d.Rec("parse " + s, d.Call(XmlDriver.BuilderParse, b, s.Length == 0 ? d.EmptyStr() : d.Str(s), 1));
            }

            d.Call(XmlDriver.BuilderTerm, b);
            d.Term();
        },
        ["dom-build"] = d =>
        {
            d.Init();
            uint doc = d.Obj(4), attrs = d.Obj(4);
            d.Call(XmlDriver.DocCtor, doc);
            d.Rec("init", d.Call(XmlDriver.DocInit, doc, d.Initializer));
            d.Rec("readonly", d.Call(XmlDriver.DocReadOnly, doc) & 0xFF);
            d.Call(XmlDriver.AttrListCtor, attrs);
            d.Rec("attrs.init", d.Call(XmlDriver.AttrListInit, attrs, d.Initializer));
            d.Rec("attrs.add", d.Call(XmlDriver.AttrListAdd, attrs, d.StrObj("k"), d.StrObj("v")));
            d.Rec("attrs.len", d.Call(XmlDriver.AttrListLength, attrs));
            ulong root = d.Call64(XmlDriver.DocCreateElement, doc, d.StrObj("root"), attrs, 0);
            d.Rec("root.type", d.NodeType(doc, root));
            ulong top = d.Call64(XmlDriver.DocGetRoot, doc);
            d.Rec("root==getRoot", root == top ? 1u : 0u);
            ulong child = d.Call64(XmlDriver.DocAddElementChild, doc, 0, d.Lo(root), d.Hi(root), d.StrObj("child"), 0, d.StrObj("body"));
            d.Rec("child.type", d.NodeType(doc, child));
            d.Rec("setAttribute", d.Call(XmlDriver.DocSetAttribute, doc, 0, d.Lo(child), d.Hi(child), d.StrObj("x"), d.StrObj("1")));
            d.Walk(doc, d.Call64(XmlDriver.DocGetRoot, doc), 0);
            d.Serialize(doc, 0);
            d.Call(XmlDriver.AttrListDtor, attrs);
            d.Rec("term", d.Call(XmlDriver.DocTerm, doc));
            d.Rec("term2", d.Call(XmlDriver.DocTerm, doc));
            d.Term();
        },
        ["dom-edit"] = d =>
        {
            d.Init();
            uint doc = d.Obj(4), attrs = d.Obj(4), list = d.Obj(4);
            d.Call(XmlDriver.DocCtor, doc);
            d.Call(XmlDriver.DocInit, doc, d.Initializer);
            ulong dr = d.Call64(XmlDriver.DocGetDocRoot, doc);
            d.Rec("getRoot.empty", d.Call64(XmlDriver.DocGetRoot, doc) == 0 ? 0u : 1u);
            d.Rec("docroot.type", d.NodeType(doc, dr));
            d.Log.Add("docroot.name " + d.StrOfId(XmlDriver.DocNodeName, doc, dr));
            d.Call(XmlDriver.AttrListCtor, attrs);
            d.Call(XmlDriver.AttrListInit, attrs, d.Initializer);
            d.Call(XmlDriver.AttrListAdd, attrs, d.StrObj("k"), d.StrObj("v"));
            ulong el = d.Call64(XmlDriver.DocCreateElement, doc, d.StrObj("root"), 0, 0);
            ulong ins = d.Call64(XmlDriver.DocInsertNode, doc, 0, d.Lo(dr), d.Hi(dr), 0, 0, d.Lo(el), d.Hi(el));
            d.Rec("insertNode==el", ins == el ? 1u : 0u);
            d.Rec("getRoot==el", d.Call64(XmlDriver.DocGetRoot, doc) == el ? 1u : 0u);
            d.Rec("parent(el)==docroot", d.Call64(XmlDriver.DocParent, doc, 0, d.Lo(el), d.Hi(el)) == dr ? 1u : 0u);
            ulong c = d.Call64(XmlDriver.DocAddElementChild, doc, 0, d.Lo(el), d.Hi(el), d.StrObj("c"), attrs, d.StrObj("txt"));
            d.Rec("c.type", d.NodeType(doc, c));
            d.Walk(doc, d.Call64(XmlDriver.DocGetRoot, doc), 0);
            d.Serialize(doc, 0);
            ulong t = d.Call64(XmlDriver.DocCreateTextNode, doc, d.StrObj("hello"));
            d.Rec("t.type", d.NodeType(doc, t));
            d.Rec("insert t", d.Call64(XmlDriver.DocInsertNode, doc, 0, d.Lo(el), d.Hi(el), d.Lo(c), d.Hi(c), d.Lo(t), d.Hi(t)) == t ? 1u : 0u);
            d.Serialize(doc, 0);
            ulong ct = d.Call64(XmlDriver.DocFirstChild, doc, 0, d.Lo(c), d.Hi(c));
            d.Rec("setText", d.Call(XmlDriver.DocSetText, doc, 0, d.Lo(ct), d.Hi(ct), d.StrObj("new")));
            ulong t2 = d.Call64(XmlDriver.DocCreateTextNode, doc, d.StrObj("first"));
            d.Rec("insert t2 ref=c", d.Call64(XmlDriver.DocInsertNode, doc, 0, d.Lo(el), d.Hi(el), d.Lo(c), d.Hi(c), d.Lo(t2), d.Hi(t2)) == t2 ? 1u : 0u);
            d.Serialize(doc, 0);
            d.Serialize(doc, 0);
            d.Rec("setAttrValue k", d.Call(XmlDriver.DocSetAttrValue, doc, 0, d.Lo(c), d.Hi(c), d.StrObj("k"), d.StrObj("w")));
            d.Rec("setAttrValue zz", d.Call(XmlDriver.DocSetAttrValue, doc, 0, d.Lo(c), d.Hi(c), d.StrObj("zz"), d.StrObj("1")));
            d.Rec("setAttribute zz", d.Call(XmlDriver.DocSetAttribute, doc, 0, d.Lo(c), d.Hi(c), d.StrObj("zz"), d.StrObj("2")));
            d.Log.Add("getAttribute zz " + d.StrOfIdArg(XmlDriver.DocGetAttribute, doc, c, d.StrObj("zz")));
            d.Rec("removeAttribute k", d.Call(XmlDriver.DocRemoveAttribute, doc, 0, d.Lo(c), d.Hi(c), d.StrObj("k")));
            d.Rec("removeAttribute none", d.Call(XmlDriver.DocRemoveAttribute, doc, 0, d.Lo(c), d.Hi(c), d.StrObj("none")));
            d.Rec("hasAttributes c", d.Call(XmlDriver.DocHasAttributes, doc, 0, d.Lo(c), d.Hi(c)) & 0xFF);
            d.Serialize(doc, 0);
            d.Call(XmlDriver.NodeListCtor, list);
            d.Rec("childNodes el", d.Call(XmlDriver.DocChildNodes, doc, 0, d.Lo(el), d.Hi(el), list));
            d.Rec("childNodes.len", d.Call(XmlDriver.NodeListLength, list));
            d.Rec("childNodes t", d.Call(XmlDriver.DocChildNodes, doc, 0, d.Lo(t), d.Hi(t), list));
            d.Rec("childNodes.len2", d.Call(XmlDriver.NodeListLength, list));
            d.Rec("byTag", d.Call(XmlDriver.DocElementsByTag, doc, 0, d.Lo(dr), d.Hi(dr), d.StrObj("c"), list));
            d.Rec("byTag.len", d.Call(XmlDriver.NodeListLength, list));
            d.Rec("removeChild(c,el)", d.Call64(XmlDriver.DocRemoveChild, doc, 0, d.Lo(c), d.Hi(c), d.Lo(el), d.Hi(el)) == c ? 1u : 0u);
            d.Serialize(doc, 0);
            d.Rec("recurseDelete el", d.Call(XmlDriver.DocRecurseDelete, doc, 0, d.Lo(el), d.Hi(el)));
            d.Serialize(doc, 0);
            d.Call(XmlDriver.NodeListDtor, list);
            d.Call(XmlDriver.AttrListDtor, attrs);
            d.Call(XmlDriver.DocTerm, doc);
            d.Term();
        },
        ["dom-query"] = d =>
        {
            d.Init();
            uint b = d.Obj(4), doc = d.Obj(4);
            d.Call(XmlDriver.BuilderCtor, b);
            d.Call(XmlDriver.BuilderInit, b, d.Initializer);
            d.Rec("parse", d.Call(XmlDriver.BuilderParse, b, d.Str("<root a=\"1\"><x>one</x><y b=\"2\"/>tail</root>"), 1));
            d.Call(XmlDriver.BuilderGetDocument, doc, b);
            ulong root = d.Call64(XmlDriver.DocGetRoot, doc), dr = d.Call64(XmlDriver.DocGetDocRoot, doc);
            d.Rec("docroot.type", d.NodeType(doc, dr));
            d.Rec("parent(root)==docroot", d.Call64(XmlDriver.DocParent, doc, 0, d.Lo(root), d.Hi(root)) == dr ? 1u : 0u);
            d.Rec("firstChild(docroot)==root", d.Call64(XmlDriver.DocFirstChild, doc, 0, d.Lo(dr), d.Hi(dr)) == root ? 1u : 0u);
            d.Rec("parent(docroot)", d.Call64(XmlDriver.DocParent, doc, 0, d.Lo(dr), d.Hi(dr)) == 0 ? 0u : 1u);
            ulong last = d.Call64(XmlDriver.DocLastChild, doc, 0, d.Lo(root), d.Hi(root));
            d.Rec("last.type", d.NodeType(doc, last));
            d.Log.Add("last.text " + d.StrOfId(XmlDriver.DocText, doc, last));
            d.Log.Add("root.getAttribute a " + d.StrOfIdArg(XmlDriver.DocGetAttribute, doc, root, d.StrObj("a")));
            d.Log.Add("root.getText " + d.StrOfId(XmlDriver.DocText, doc, root));
            d.Log.Add("text.name " + d.StrOfId(XmlDriver.DocNodeName, doc, last));
            ulong add = d.Call64(XmlDriver.DocAddElementChild, doc, 0, d.Lo(root), d.Hi(root), d.StrObj("z"), 0, 0);
            d.Rec("readonly.addElementChild", add == 0 ? 0u : 1u);
            d.Rec("readonly.setAttribute", d.Call(XmlDriver.DocSetAttribute, doc, 0, d.Lo(root), d.Hi(root), d.StrObj("q"), d.StrObj("1")));
            ulong w = d.Call64(XmlDriver.DocSetWritable, doc);
            d.Rec("setWritable.type", d.NodeType(doc, w));
            d.Rec("readonly.after", d.Call(XmlDriver.DocReadOnly, doc) & 0xFF);
            root = d.Call64(XmlDriver.DocGetRoot, doc);
            d.Rec("root.type", d.NodeType(doc, root));
            add = d.Call64(XmlDriver.DocAddElementChild, doc, 0, d.Lo(root), d.Hi(root), d.StrObj("z"), 0, d.StrObj("zt"));
            d.Rec("addElementChild", add == 0 ? 0u : 1u);
            d.Rec("setAttribute", d.Call(XmlDriver.DocSetAttribute, doc, 0, d.Lo(root), d.Hi(root), d.StrObj("q"), d.StrObj("1")));
            d.Walk(doc, root, 0);
            d.Serialize(doc, 0);
            d.Call(XmlDriver.DocDtor, doc);
            d.Call(XmlDriver.BuilderTerm, b);
            d.Term();
        },
        ["nodes"] = d =>
        {
            d.Init();
            uint b = d.Obj(4), doc = d.Obj(4), node = d.Obj(0x1C), node2 = d.Obj(0x1C), nl = d.Obj(4), s = d.Obj(8);
            d.Call(XmlDriver.BuilderCtor, b);
            d.Call(XmlDriver.BuilderInit, b, d.Initializer);
            d.Call(XmlDriver.BuilderParse, b, d.Str("<root a=\"1\" b=\"2\"><x>one</x><y/>tail</root>"), 1);
            d.Call(XmlDriver.BuilderGetDocument, doc, b);
            ulong root = d.Call64(XmlDriver.DocGetRoot, doc);
            d.Call(XmlDriver.NodeCtor, node, 0, d.Lo(root), d.Hi(root));
            d.Rec("available", d.Call(XmlDriver.NodeAvailable, node) & 0xFF);
            d.Rec("type", d.Call(XmlDriver.NodeGetType, node));
            d.Call(XmlDriver.NodeName, s, node);
            d.RecStr("name", s);
            d.Call(XmlDriver.NodeValue, s, node);
            d.RecStr("value", s);
            d.Rec("owner==doc", d.Call(XmlDriver.NodeOwner, node) == doc ? 1u : 0u);
            d.Rec("hasChildNodes", d.Call(XmlDriver.NodeHasChildren, node) & 0xFF);
            d.Rec("hasAttributes", d.Call(XmlDriver.NodeHasAttrs, node) & 0xFF);
            d.Call(XmlDriver.NodeChildNodes, nl, node);
            d.Rec("childNodes.len", d.Call(XmlDriver.NodeListLength, nl));
            ulong item1 = d.Call64(XmlDriver.NodeListItem, nl, 1);
            d.Rec("item1.type", d.NodeType(doc, item1));
            d.Rec("findItem(x)", d.Call64(XmlDriver.NodeListFindName, nl, d.StrObj("x")) == d.Call64(XmlDriver.NodeListItem, nl, 0) ? 1u : 0u);
            d.Rec("findItem(id)", d.Call64(XmlDriver.NodeListFindId, nl, 0, d.Lo(item1), d.Hi(item1)) == item1 ? 1u : 0u);
            d.Rec("removeItem", d.Call64(XmlDriver.NodeListRemove, nl, 0, d.Lo(item1), d.Hi(item1)) == item1 ? 1u : 0u);
            d.Rec("len.after.remove", d.Call(XmlDriver.NodeListLength, nl));
            d.Rec("insertFirst", d.Call64(XmlDriver.NodeListInsertFirst, nl, 0, d.Lo(item1), d.Hi(item1)) == item1 ? 1u : 0u);
            d.Rec("item0==item1", d.Call64(XmlDriver.NodeListItem, nl, 0) == item1 ? 1u : 0u);
            d.Call(XmlDriver.NodeListDtor, nl);
            d.Call(XmlDriver.NodeAttributes, nl, node);
            d.Rec("attributes.len", d.Call(XmlDriver.NodeListLength, nl));
            ulong a0 = d.Call64(XmlDriver.NodeListItem, nl, 0);
            d.Rec("attr.type", d.NodeType(doc, a0));
            d.Call(XmlDriver.NodeListDtor, nl);
            ulong first = d.Call64(XmlDriver.NodeFirstChild, node);
            d.Call(XmlDriver.NodeCtor, node2, 0, d.Lo(first), d.Hi(first));
            d.Call(XmlDriver.NodeName, s, node2);
            d.RecStr("first.name", s);
            ulong txt = d.Call64(XmlDriver.NodeFirstChild, node2);
            d.Call(XmlDriver.NodeCtor, node2, 0, d.Lo(txt), d.Hi(txt));
            d.Call(XmlDriver.NodeName, s, node2);
            d.RecStr("text.name", s);
            d.Call(XmlDriver.NodeValue, s, node2);
            d.RecStr("text.value", s);
            d.Rec("text.parent==first", d.Call64(XmlDriver.NodeParent, node2) == first ? 1u : 0u);
            ulong lastc = d.Call64(XmlDriver.NodeLastChild, node);
            d.Rec("last.type", d.NodeType(doc, lastc));
            d.Call(XmlDriver.NodeCtor, node2, 0, d.Lo(first), d.Hi(first));
            d.Rec("next(first).type", d.NodeType(doc, d.Call64(XmlDriver.NodeNextSibling, node2)));
            d.Serialize(doc, 0);
            d.Call(XmlDriver.NodeDtor, node);
            d.Call(XmlDriver.NodeDtor, node2);
            d.Call(XmlDriver.DocDtor, doc);
            d.Call(XmlDriver.BuilderTerm, b);
            d.Term();
        },
        ["attr"] = d =>
        {
            d.Init();
            uint a = d.Obj(4), s = d.Obj(8);
            d.Call(XmlDriver.AttrCtor, a);
            d.Rec("setName.noinit", d.Call(XmlDriver.AttrSetName, a, d.StrObj("n")));
            d.Rec("init", d.Call(XmlDriver.AttrInit, a, d.Initializer));
            d.Rec("init2", d.Call(XmlDriver.AttrInit, a, d.Initializer));
            d.Rec("available", d.Call(XmlDriver.AttrAvailable, a) & 0xFF);
            d.Rec("setName.empty", d.Call(XmlDriver.AttrSetName, a, d.EmptyStr()));
            d.Rec("setName", d.Call(XmlDriver.AttrSetName, a, d.StrObj("name")));
            d.Rec("setValue", d.Call(XmlDriver.AttrSetValue, a, d.StrObj("value")));
            d.Call(XmlDriver.AttrGetName, s, a);
            d.RecStr("name", s);
            d.Call(XmlDriver.AttrGetValue, s, a);
            d.RecStr("value", s);
            d.Rec("term", d.Call(XmlDriver.AttrTerm, a));
            d.Rec("term2", d.Call(XmlDriver.AttrTerm, a));
            d.Term();
            d.Rec("init.noinitializer", d.Call(XmlDriver.AttrInit, a, d.Initializer));
        },
    };

    /// <summary>What the firmware module answers (checked with MatchesFirmwareModule), for the HLE-only run.</summary>
    public static readonly Dictionary<string, string[]> Expected = new()
    {
        ["dom-parse"] = ["parse 0", "builder.init2 850003", "readonly 1", "node 0 type=4 name=root", "  attr a=1", "  attr b=two & more",
            "  node 1 type=4 name=item", "    attr id=x", "    node 2 type=8 text=Hello <", "    node 2 type=8 text=world>",
            "  node 1 type=23 skipped=proc data?>", "  node 1 type=7 skipped=raw <b>", "docroot.type 0", "builder.term2 80850002",
            "serialize0 <root a=\"1\" b=\"two & more\"><item id=\"x\">Hello <world></item><empty/>proc data?></empty>raw <b><n>plain</n></root> r=0"],
        ["dom-entities"] = ["node 0 type=4 name=a", "  node 1 type=8 text=x &amp; y", "    node 2 type=8 text=&quot;"],
        ["sax"] = ["parse.nohandler 80850016", "cb startDocument ud=1234", "cb skipped 7 xml version=\"1.0\" encoding=\"utf-8\"?>", "cb skipped 11  top -->",
            "cb startElement root [a=1 b=two &amp; more]", "cb text Hello &lt;world&gt;", "cb skipped 9 raw <b>]]>", "cb endDocument ud=1234",
            "cb text &", "parse.chunk1 850001", "cb text te", "cb text xt", "parse.chunk2 0", "cb error 8085000A", "parse.bad 8085000A"],
        ["errors"] = ["parse <a></b> 8085000A", "parse <a> 80850008", "parse <a b=1/> 80850011", "parse <a b=\"1\" b=\"2\"/> 80850008",
            "parse <a>&bogus;</a> 0", "parse  80850003", "parse <a/><b/> 8085000B", "parse <a><!-- x</a> 80850012", "parse <a b></a> 80850008",
            "parse text 8085000B"],
        ["dom-build"] = ["init 0", "readonly 0", "root.type 5", "root==getRoot 0", "child.type 5", "setAttribute 0", "serialize0  r=0", "term2 80850002"],
        ["dom-edit"] = ["getRoot.empty 0", "docroot.name <null>", "insertNode==el 1", "getRoot==el 1", "parent(el)==docroot 0",
            "serialize0 <root><c k=\"v\">new</c>firsthello</root> r=0", "setAttrValue zz 0", "removeAttribute none 0",
            "serialize0 <root><c zz=\"2\">new</c>firsthello</root> r=0", "childNodes.len 3", "byTag.len 1", "serialize0 <root>firsthello</root> r=0"],
        ["dom-query"] = ["parent(root)==docroot 0", "firstChild(docroot)==root 1", "text.name tail", "readonly.addElementChild 1",
            "readonly.setAttribute 80850202", "setWritable.type 0",
            "serialize0 <root a=\"1\" q=\"1\"><x>one</x><y b=\"2\"/>tail</y><z>zt</z></root> r=0"],
        ["nodes"] = ["type 4", "name root", "owner==doc 1", "childNodes.len 0", "attributes.len 0", "first.name x", "text.value one"],
        ["attr"] = ["setName.noinit 80850002", "init 0", "init2 850003", "setName.empty 80850003", "name name", "value value", "term2 80850002",
            "init.noinitializer 80850002"],
    };
}

sealed unsafe class XmlDriver(XmlHarness h, GuestThread thread, Func<string, uint> resolve)
{
    public const string InitCtor = "_ZN3sce3Xml11InitializerC1Ev", InitInit = "_ZN3sce3Xml11Initializer10initializeEPKNS0_13InitParameterE",
        InitTerm = "_ZN3sce3Xml11Initializer9terminateEv",
        BuilderCtor = "_ZN3sce3Xml3Dom15DocumentBuilderC1Ev", BuilderInit = "_ZN3sce3Xml3Dom15DocumentBuilder10initializeEPKNS0_11InitializerE",
        BuilderParse = "_ZN3sce3Xml3Dom15DocumentBuilder5parseEPKNS0_6StringEb", BuilderGetDocument = "_ZN3sce3Xml3Dom15DocumentBuilder11getDocumentEv",
        BuilderTerm = "_ZN3sce3Xml3Dom15DocumentBuilder9terminateEv", BuilderResolve = "_ZN3sce3Xml3Dom15DocumentBuilder16setResolveEntityEb",
        BuilderSkipWs = "_ZN3sce3Xml3Dom15DocumentBuilder26setSkipIgnorableWhiteSpaceEb",
        DocCtor = "_ZN3sce3Xml3Dom8DocumentC1Ev", DocInit = "_ZN3sce3Xml3Dom8Document10initializeEPKNS0_11InitializerE",
        DocTerm = "_ZN3sce3Xml3Dom8Document9terminateEv", DocDtor = "_ZN3sce3Xml3Dom8DocumentD1Ev",
        DocAvailable = "_ZNK3sce3Xml3Dom8Document11isAvailableEv", DocReadOnly = "_ZNK3sce3Xml3Dom8Document10isReadOnlyEv",
        DocStatus = "_ZNK3sce3Xml3Dom8Document9getStatusEv", DocGetRoot = "_ZNK3sce3Xml3Dom8Document7getRootEv",
        DocGetDocRoot = "_ZNK3sce3Xml3Dom8Document10getDocRootEv", DocNodeType = "_ZNK3sce3Xml3Dom8Document11getNodeTypeEy",
        DocNodeName = "_ZNK3sce3Xml3Dom8Document11getNodeNameEy", DocText = "_ZNK3sce3Xml3Dom8Document7getTextEy",
        DocFirstChild = "_ZNK3sce3Xml3Dom8Document13getFirstChildEy", DocSibling = "_ZNK3sce3Xml3Dom8Document10getSiblingEy",
        DocFirstAttr = "_ZNK3sce3Xml3Dom8Document12getFirstAttrEy", DocNextAttr = "_ZNK3sce3Xml3Dom8Document11getNextAttrEy",
        DocAttrName = "_ZNK3sce3Xml3Dom8Document11getAttrNameEy", DocAttrValue = "_ZNK3sce3Xml3Dom8Document12getAttrValueEy",
        DocSkipped = "_ZNK3sce3Xml3Dom8Document14getSkippedTextEy", DocEntityType = "_ZNK3sce3Xml3Dom8Document13getEntityTypeEy",
        DocEntity = "_ZNK3sce3Xml3Dom8Document9getEntityEy",
        DocSerialize = "_ZN3sce3Xml3Dom8Document9serializeEPKNS0_18SerializeParameterEPNS0_6StringE",
        DocCreateElement = "_ZN3sce3Xml3Dom8Document13createElementEPKNS0_6StringEPKNS0_13AttributeListES5_",
        DocAddElementChild = "_ZN3sce3Xml3Dom8Document15addElementChildEyPKNS0_6StringEPKNS0_13AttributeListES5_",
        DocSetAttribute = "_ZN3sce3Xml3Dom8Document12setAttributeEyPKNS0_6StringES5_",
        ParserCtor = "_ZN3sce3Xml3Sax6ParserC1Ev", ParserInit = "_ZN3sce3Xml3Sax6Parser10initializeEPKNS0_11InitializerE",
        ParserParse = "_ZN3sce3Xml3Sax6Parser5parseEPKNS0_6StringEb", ParserTerm = "_ZN3sce3Xml3Sax6Parser9terminateEv",
        ParserSetHandler = "_ZN3sce3Xml3Sax6Parser18setDocumentHandlerEPNS1_15DocumentHandlerE",
        ParserSetUserData = "_ZN3sce3Xml3Sax6Parser11setUserDataEPv", ParserSkipWs = "_ZN3sce3Xml3Sax6Parser26setSkipIgnorableWhiteSpaceEb",
        ParserResolve = "_ZN3sce3Xml3Sax6Parser16setResolveEntityEb",
        AttrCtor = "_ZN3sce3Xml4AttrC1Ev", AttrInit = "_ZN3sce3Xml4Attr10initializeEPKNS0_11InitializerE", AttrTerm = "_ZN3sce3Xml4Attr9terminateEv",
        AttrSetName = "_ZN3sce3Xml4Attr7setNameEPKNS0_6StringE", AttrSetValue = "_ZN3sce3Xml4Attr8setValueEPKNS0_6StringE",
        AttrGetName = "_ZNK3sce3Xml4Attr7getNameEv", AttrGetValue = "_ZNK3sce3Xml4Attr8getValueEv", AttrAvailable = "_ZNK3sce3Xml4Attr11isAvailableEv",
        AttrListCtor = "_ZN3sce3Xml13AttributeListC1Ev", AttrListInit = "_ZN3sce3Xml13AttributeList10initializeEPKNS0_11InitializerE",
        AttrListAdd = "_ZN3sce3Xml13AttributeList12addAttributeEPKNS0_6StringES4_", AttrListLength = "_ZNK3sce3Xml13AttributeList9getLengthEv",
        AttrListDtor = "_ZN3sce3Xml13AttributeListD1Ev",
        DocInsertNode = "_ZN3sce3Xml3Dom8Document10insertNodeEyyy", DocParent = "_ZNK3sce3Xml3Dom8Document9getParentEy",
        DocCreateTextNode = "_ZN3sce3Xml3Dom8Document14createTextNodeEPKNS0_6StringE",
        DocSetText = "_ZN3sce3Xml3Dom8Document7setTextEyPKNS0_6StringE", DocSetAttrValue = "_ZN3sce3Xml3Dom8Document12setAttrValueEyPKNS0_6StringES5_",
        DocGetAttribute = "_ZNK3sce3Xml3Dom8Document12getAttributeEyPKNS0_6StringE",
        DocRemoveAttribute = "_ZN3sce3Xml3Dom8Document15removeAttributeEyPKNS0_6StringE",
        DocHasAttributes = "_ZNK3sce3Xml3Dom8Document13hasAttributesEy",
        DocChildNodes = "_ZNK3sce3Xml3Dom8Document13getChildNodesEyPNS1_8NodeListE",
        DocElementsByTag = "_ZNK3sce3Xml3Dom8Document20getElementsByTagNameEyPKNS0_6StringEPNS1_8NodeListE",
        DocRemoveChild = "_ZN3sce3Xml3Dom8Document11removeChildEyy", DocRecurseDelete = "_ZN3sce3Xml3Dom8Document13recurseDeleteEy",
        DocLastChild = "_ZNK3sce3Xml3Dom8Document12getLastChildEy", DocSetWritable = "_ZN3sce3Xml3Dom8Document11setWritableEv",
        NodeListCtor = "_ZN3sce3Xml3Dom8NodeListC1Ev", NodeListDtor = "_ZN3sce3Xml3Dom8NodeListD1Ev",
        NodeListLength = "_ZNK3sce3Xml3Dom8NodeList9getLengthEv", NodeListItem = "_ZNK3sce3Xml3Dom8NodeList4itemEj",
        NodeListFindName = "_ZNK3sce3Xml3Dom8NodeList8findItemEPKNS0_6StringE", NodeListFindId = "_ZNK3sce3Xml3Dom8NodeList8findItemEy",
        NodeListRemove = "_ZN3sce3Xml3Dom8NodeList10removeItemEy", NodeListInsertFirst = "_ZN3sce3Xml3Dom8NodeList11insertFirstEy",
        NodeCtor = "_ZN3sce3Xml3Dom4NodeC1Ey", NodeDtor = "_ZN3sce3Xml3Dom4NodeD1Ev", NodeAvailable = "_ZNK3sce3Xml3Dom4Node11isAvailableEv",
        NodeGetType = "_ZNK3sce3Xml3Dom4Node11getNodeTypeEv", NodeName = "_ZNK3sce3Xml3Dom4Node11getNodeNameEv",
        NodeValue = "_ZNK3sce3Xml3Dom4Node12getNodeValueEv", NodeOwner = "_ZNK3sce3Xml3Dom4Node16getOwnerDocumentEv",
        NodeHasChildren = "_ZNK3sce3Xml3Dom4Node13hasChildNodesEv", NodeHasAttrs = "_ZNK3sce3Xml3Dom4Node13hasAttributesEv",
        NodeChildNodes = "_ZNK3sce3Xml3Dom4Node13getChildNodesEv", NodeAttributes = "_ZNK3sce3Xml3Dom4Node13getAttributesEv",
        NodeFirstChild = "_ZNK3sce3Xml3Dom4Node13getFirstChildEv", NodeLastChild = "_ZNK3sce3Xml3Dom4Node12getLastChildEv",
        NodeNextSibling = "_ZNK3sce3Xml3Dom4Node14getNextSiblingEv", NodeParent = "_ZNK3sce3Xml3Dom4Node13getParentNodeEv",
        NodeAppendChild = "_ZN3sce3Xml3Dom4Node11appendChildEy";

    public uint Initializer;
    public uint Handler => h.Handler;
    public List<string> Log => h.Current!;

    public uint Call(string name, params uint[] args) => thread.CallGuest(resolve(name), args);

    public ulong Call64(string name, params uint[] args)
    {
        uint lo = thread.CallGuest(resolve(name), args);
        return lo | (ulong)thread.LastCallR1 << 32;
    }

    public uint Lo(ulong v) => (uint)v;
    public uint Hi(ulong v) => (uint)(v >> 32);
    public uint Obj(uint size) => h.Game(size);
    public uint Str(string s) => StrObj(s);
    public uint EmptyStr()
    {
        uint o = h.Game(8);
        GuestMemory.W32(o, 0);
        GuestMemory.W32(o + 4, 0);
        return o;
    }

    public uint StrObj(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        uint p = h.Game((uint)b.Length + 1);
        b.CopyTo(GuestMemory.Span(p, b.Length));
        GuestMemory.Write(p + (uint)b.Length, (byte)0);
        uint o = h.Game(8);
        GuestMemory.W32(o, p);
        GuestMemory.W32(o + 4, (uint)b.Length);
        return o;
    }

    public void Rec(string what, uint v) => Log.Add($"{what} {v:X}");
    public void RecStr(string what, uint str) => Log.Add($"{what} {XmlHarness.ReadStr(str)}");

    public void Init()
    {
        Initializer = h.Game(4);
        uint param = h.Game(0x18);
        GuestMemory.W32(param, h.Allocator);
        GuestMemory.W32(param + 4, 0x55);
        Call(InitCtor, Initializer);
        Rec("initializer", Call(InitInit, Initializer, param));
    }

    public void Term() => Rec("initializer.term", Call(InitTerm, Initializer));

    public uint NodeType(uint doc, ulong id) => Call(DocNodeType, doc, 0, Lo(id), Hi(id));

    string StrOf(string fn, uint doc, ulong id)
    {
        uint s = h.Game(8);
        Call(fn, s, doc, Lo(id), Hi(id));
        return XmlHarness.ReadStr(s);
    }

    /// <summary>Logs a node and its subtree (type, name, attributes, texts).</summary>
    public void Walk(uint doc, ulong id, int depth)
    {
        string ind = new(' ', depth * 2);
        for (; id != 0; id = Call64(DocSibling, doc, 0, Lo(id), Hi(id)))
        {
            uint type = NodeType(doc, id);
            string extra = type switch
            {
                4 or 5 => "name=" + StrOf(DocNodeName, doc, id),
                8 => "text=" + StrOf(DocText, doc, id),
                2 => $"entity={Call(DocEntityType, doc, 0, Lo(id), Hi(id))}:{StrOf(DocEntity, doc, id)}",
                _ => "skipped=" + StrOf(DocSkipped, doc, id),
            };
            Log.Add($"{ind}node {depth} type={type} {extra}");
            for (ulong a = Call64(DocFirstAttr, doc, 0, Lo(id), Hi(id)); a != 0; a = Call64(DocNextAttr, doc, 0, Lo(a), Hi(a)))
            {
                Log.Add($"{ind}  attr {StrOf(DocAttrName, doc, a)}={StrOf(DocAttrValue, doc, a)}");
            }

            Walk(doc, Call64(DocFirstChild, doc, 0, Lo(id), Hi(id)), depth + 1);
        }
    }

    public string StrOfId(string fn, uint doc, ulong id) => StrOf(fn, doc, id);

    public string StrOfIdArg(string fn, uint doc, ulong id, uint arg)
    {
        uint s = h.Game(8);
        Call(fn, s, doc, Lo(id), Hi(id), arg);
        return XmlHarness.ReadStr(s);
    }

    public void Serialize(uint doc, uint flags)
    {
        uint param = h.Game(0x14), s = h.Game(8);
        GuestMemory.W32(param, flags);
        uint r = Call(DocSerialize, doc, param, s);
        Log.Add($"serialize{flags} {XmlHarness.ReadStr(s).Replace("\n", "\\n")} r={r:X}");
    }
}

sealed unsafe class XmlHarness : IDisposable
{
    const uint ArenaSize = 0x40_0000, GameSize = 0x4_0000;
    readonly Emulator _emu;
    readonly string _root;
    readonly uint _arena, _gameBase, _stubs;
    readonly Dictionary<string, uint> _hleStubs = [];
    uint _bump, _gameBump, _nextStub;
    public List<string>? Current;
    public readonly uint Allocator, Handler;

    public XmlHarness(GuestMemoryFixture fx, string? firmwareModule)
    {
        _root = Path.Combine(Path.GetTempPath(), "vitasharp-xml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        if (firmwareModule != null)
        {
            Directory.CreateDirectory(Path.Combine(_root, "vs0", "sys", "external"));
            File.Copy(firmwareModule, Path.Combine(_root, "vs0", "sys", "external", "libSceXml.suprx"));
        }
        _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
        if (firmwareModule != null)
        {
            _emu.LoadModule("vs0:sys/external/libSceXml.suprx");
        }

        var mem = _emu.Memory;
        _arena = mem.Alloc(ArenaSize, "xml test arena");
        _gameBase = mem.Alloc(GameSize, "xml test game");
        _stubs = mem.Alloc(0x4000, "xml test stubs");
        uint next = _stubs;
        uint Stub(string name, Action<HleContext> body)
        {
            uint a = next;
            Linker.WriteHleStub(a, _emu.Hle.RegisterHostFunction(name, body), 0);
            next += 16;
            return a;
        }
        // the game's MemAllocator: vptr -> [D1, D0, allocate, deallocate]
        uint vt = mem.Alloc(0x100, "xml test vtables");
        uint nop = Stub("xmltest:nop", c => { });
        GuestMemory.W32(vt, nop);
        GuestMemory.W32(vt + 4, nop);
        GuestMemory.W32(vt + 8, Stub("xmltest:allocate", c => c.Return(Allocate(c.Gpr(1)))));
        GuestMemory.W32(vt + 12, Stub("xmltest:deallocate", c => { }));
        Allocator = vt + 0x80;
        GuestMemory.W32(Allocator, vt);
        // the DocumentHandler: vptr -> [D1, D0, startDocument, startElement, text, endElement, endDocument, skippedText, entity, errorFound]
        uint hv = vt + 0x40;
        GuestMemory.W32(hv, nop);
        GuestMemory.W32(hv + 4, nop);
        GuestMemory.W32(hv + 8, Stub("xmltest:startDocument", c => { Current!.Add($"cb startDocument ud={c.Gpr(1):X}"); c.Return(0); }));
        GuestMemory.W32(hv + 12, Stub("xmltest:startElement", c => { Current!.Add($"cb startElement {ReadStr(c.Gpr(2))} [{Attrs(c.Gpr(3))}]"); c.Return(0); }));
        GuestMemory.W32(hv + 16, Stub("xmltest:text", c => { Current!.Add($"cb text {ReadStr(c.Gpr(2))}"); c.Return(0); }));
        GuestMemory.W32(hv + 20, Stub("xmltest:endElement", c => { Current!.Add($"cb endElement {ReadStr(c.Gpr(2))}"); c.Return(0); }));
        GuestMemory.W32(hv + 24, Stub("xmltest:endDocument", c => { Current!.Add($"cb endDocument ud={c.Gpr(1):X}"); c.Return(0); }));
        GuestMemory.W32(hv + 28, Stub("xmltest:skippedText", c => { Current!.Add($"cb skipped {c.Gpr(2)} {ReadStr(c.Gpr(3))}"); c.Return(0); }));
        GuestMemory.W32(hv + 32, Stub("xmltest:entity", c => { Current!.Add($"cb entity {c.Gpr(2)} {ReadStr(c.Gpr(3))}"); c.Return(0); }));
        GuestMemory.W32(hv + 36, Stub("xmltest:errorFound", c => { Current!.Add($"cb error {c.Gpr(2):X}"); c.Return(0); }));
        Handler = vt + 0x90;
        GuestMemory.W32(Handler, hv);
        _nextStub = next;
        _emu.Code.Invalidate(0, uint.MaxValue);
    }

    /// <summary>The AttributeList's pairs (through the HLE state when the HLE built it; the firmware's own otherwise
    /// is read with getLength/getAttribute through the same exports, so this only lists what the HLE knows).</summary>
    string Attrs(uint list)
    {
        var t = GuestThread.Current!;
        uint len = t.CallGuest(_resolve!(XmlDriver.AttrListLength), list);
        var sb = new StringBuilder();
        uint attr = Game(4), s = Game(8);
        for (uint i = 0; i < len; i++)
        {
            t.CallGuest(_resolve!("_ZNK3sce3Xml13AttributeList12getAttributeEj"), attr, list, i);
            t.CallGuest(_resolve!(XmlDriver.AttrGetName), s, attr);
            string n = ReadStr(s);
            t.CallGuest(_resolve!(XmlDriver.AttrGetValue), s, attr);
            sb.Append(i == 0 ? "" : " ").Append(n).Append('=').Append(ReadStr(s));
            t.CallGuest(_resolve!("_ZN3sce3Xml4AttrD1Ev"), attr);
        }
        return sb.ToString();
    }

    Func<string, uint>? _resolve;

    public static string ReadStr(uint str)
    {
        uint p = GuestMemory.R32(str), n = GuestMemory.R32(str + 4);
        return p == 0 ? "<null>" : Encoding.UTF8.GetString(GuestMemory.ReadSpan(p, (int)Math.Min(n, 4096)));
    }

    uint Allocate(uint size)
    {
        uint p = _arena + _bump;
        _bump = (_bump + size + 15) & ~15u;
        if (_bump > ArenaSize)
        {
            return 0;
        }

        return p;
    }

    public uint Game(uint size)
    {
        uint p = _gameBase + _gameBump;
        _gameBump = (_gameBump + size + 7) & ~7u;
        if (_gameBump > GameSize)
        {
            throw new InvalidOperationException("xml test game memory exhausted");
        }

        GuestMemory.Fill(p, 0, size);
        return p;
    }

    uint HleStub(string name)
    {
        if (_hleStubs.TryGetValue(name, out uint a))
        {
            return a;
        }

        var info = NidDatabase.LookupName(name).Single(n => n.Library == "SceLibXml");
        a = _nextStub;
        _nextStub += 16;
        Linker.WriteHleStub(a, _emu.Hle.GetHleFunctionIndex(info.Nid, 0xC2B22E99, "SceLibXml"), info.Nid);
        _emu.Code.Invalidate(a, 16);
        return _hleStubs[name] = a;
    }

    public List<string> Run(Action<XmlDriver> body, bool useLle)
    {
        _bump = 0;
        _gameBump = 0;
        var log = Current = [];
        Exception? failure = null;
        _resolve = name => useLle
            ? (_emu.Linker.TryGetFunctionExport(NidDatabase.LookupName(name).Single(n => n.Library == "SceLibXml").Nid, out uint a) ? a : throw new InvalidOperationException(name))
            : HleStub(name);
        var t = _emu.Kernel.CreateThread("xml-test", 0, 0x10000100, 0x10000, 0, 0x70000);
        t.HostBody = th =>
        {
            try
            {
                body(new XmlDriver(this, th, _resolve));
            }
            catch (Exception e)
            {
                failure = e;
            }
        };
        t.Start(0, 0);
        Assert.True(t.WaitForExit(120_000), "xml test thread did not finish");
        _emu.Kernel.DeleteThread(t);
        if (failure != null)
        {
            log.Add("EXCEPTION " + failure);
        }

        return log;
    }

    public void Dispose()
    {
        _emu.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }
}

static class XmlFirmware
{
    static readonly Lazy<string?> _path = new(() =>
        JsonFirmware.ModulePath is { } json && Path.Combine(Path.GetDirectoryName(json)!, "libSceXml.suprx") is var p && File.Exists(p) ? p : null);
    public static string? ModulePath => _path.Value;
}

public sealed class XmlFirmwareTheoryAttribute : TheoryAttribute
{
    public XmlFirmwareTheoryAttribute()
    {
        if (XmlFirmware.ModulePath == null)
        {
            Skip = $"libSceXml.suprx not found (set {EnvVars.FirmwareDir} to a Vita3K data folder or vs0, or {EnvVars.FirmwarePup})";
        }
    }
}
