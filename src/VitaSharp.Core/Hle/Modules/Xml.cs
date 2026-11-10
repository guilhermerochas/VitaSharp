using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibXml (sce::Xml, vs0:sys/external/libSceXml.suprx, SCE_SYSMODULE_XML): the SDK's XML library, a DOM
/// (Dom::Document, DocumentBuilder, Node, NodeList), a SAX parser (Sax::Parser with the game's DocumentHandler),
/// attributes and the small value types. HLE replacement used when the firmware module is not installed (or with
/// --hle-modules=xml); the contract follows the firmware 3.74 module:
/// <list type="bullet">
/// <item>Initializer, Attr, AttributeList, NodeList, Document, DocumentBuilder and Sax::Parser are one word, a pointer
/// to an implementation block that initialize() allocates through the game's MemAllocator (vtable slot 2
/// allocate(size, userData), slot 3 deallocate(ptr, userData)) with the firmware's sizes; 0 = not initialized. A
/// second initialize() answers 0x00850003 (MultipleInitialized), a missing Initializer 0x80850002, an allocation
/// failure 0x80850001. Only one Initializer may be live at a time.</item>
/// <item>String and SimpleData are { const char* buffer, size_t size } passed by pointer and returned by value (hidden
/// pointer in r0, this in r1).</item>
/// <item>NodeId is 64 bits: the low word names the node, the high word the document's engine block (allocated from the
/// game's allocator; its +0x460 holds the owning Document object, what Node::getOwnerDocument answers). Copies of a
/// Document (copy constructor, operator=, DocumentBuilder::getDocument) share the engine, so node ids stay valid
/// across them. A Node object (0x1C bytes) is { id low, id high, engine, node, Initializer*, 0, 0 }.</item>
/// <item>Node types: 0 the document (name "xml", value <c>version="1.0"</c>), 1 attribute, 2 entity reference, 4
/// element, 7 processing instruction, 8 text, 0x0F CDATA section, 0x17 DTD, 0x1F comment. A parsed document is
/// read-only until setWritable() (which answers the root element).</item>
/// <item>The Sax::DocumentHandler vtable: slot 2 startDocument(ud), 3 startElement(ud, const String* name, const
/// AttributeList*), 4 text(ud, const String*), 5 endElement(ud, const String* name), 6 endDocument(ud), 7
/// skippedText(ud, type, const String*) with 4/5 DTD, 6/7 PI, 8/9 CDATA, 10/11 comment (the odd type is the last
/// piece), 8 entity(ud, type, const String*) for an unresolved reference (0 amp, 1 quot, 2 lt, 3 gt, 4 apos), 9
/// errorFound(ud, ResultType). A handler that answers non-zero aborts the parse (0x00850002 XmlParseAborted).</item>
/// <item>parse(String*, isEnd): input without isEnd is buffered and answers 0x00850001 (XmlParseInProgress). Syntax
/// errors use the firmware's ResultType values (0x80850008 unexpected EOF ... 0x8085001A duplicate attribute).</item>
/// </list>
/// The parser and serializer are VitaSharp's own (the module's bXCe engine is not reproduced): the same tree, names,
/// texts and results, not the same internal allocations.
/// </summary>
public sealed unsafe partial class Xml : HleModule
{
    const string Lib = "SceLibXml";
    const uint LibNid = 0xC2B22E99;

    // ResultType (sce::Xml::Util::strResult names)
    internal const int ErrGeneric = unchecked((int)0x80850000), ErrNoMemory = unchecked((int)0x80850001),
        ErrNotInitialized = unchecked((int)0x80850002), ErrInvalidArgument = unchecked((int)0x80850003),
        ErrNotSupported = unchecked((int)0x80850004), ErrParserBusy = unchecked((int)0x80850007),
        ErrUnexpectedEof = unchecked((int)0x80850008), ErrSyntax = unchecked((int)0x80850009),
        ErrEndTagMismatch = unchecked((int)0x8085000A), ErrInvalidChar = unchecked((int)0x8085000B),
        ErrInvalidDec = unchecked((int)0x8085000C), ErrInvalidHex = unchecked((int)0x8085000D),
        ErrNoCloseAngle = unchecked((int)0x8085000E), ErrNoEquals = unchecked((int)0x8085000F),
        ErrNoSemicolon = unchecked((int)0x80850010), ErrNoQuote = unchecked((int)0x80850011),
        ErrNoCommentEnd = unchecked((int)0x80850012), ErrNoCDataEnd = unchecked((int)0x80850013),
        ErrNoDtdEnd = unchecked((int)0x80850014), ErrUnknownEncoding = unchecked((int)0x80850015),
        ErrHandlerNotSet = unchecked((int)0x80850016), ErrInvalidPi = unchecked((int)0x80850017),
        ErrInvalidDocElement = unchecked((int)0x80850018), ErrNoDocElement = unchecked((int)0x80850019),
        ErrDuplicateAttr = unchecked((int)0x8085001A), ErrNotFound = unchecked((int)0x80850200);
    internal const int InfoInProgress = 0x850001, InfoAborted = 0x850002, InfoMultipleInit = 0x850003;

    internal const int TDocument = 0, TAttr = 1, TEntityRef = 2, TElement = 4, TElementNew = 5, TCDataNode = 7, TText = 8,
        TPiNode = 0x17, TComment = 0x1F;

    // the firmware's implementation block sizes (what the game's allocator sees)
    const uint SizeInit = 0x18, SizeInitWrap = 8, SizeAttr = 0x14, SizeAttrList = 0x10, SizeAttrListBody = 0x14,
        SizeNodeList = 0x18, SizeDocument = 0x1C, SizeEngine = 0x464, SizeBuilder = 0x30, SizeParser = 8, SizeParserBody = 0x10;

    public override bool FirmwareFallbackOnly => true;

    // ------------------------------------------------------------------ host-side state

    /// <summary>Every implementation block the HLE handed out, by its guest address.</summary>
    readonly Dictionary<uint, object> _impl = [];
    /// <summary>Document engines by their guest block (the high word of a NodeId).</summary>
    readonly Dictionary<uint, XDoc> _engines = [];
    readonly object _lock = new();
    int _initCount;

    sealed class XInit { public uint Allocator, UserData, Wrapper; }
    sealed class XAttr { public uint Init; public byte[]? Name, Value; public uint NamePtr, ValuePtr; }
    sealed class XAttrList { public uint Init, Body; public readonly List<(uint Name, uint Value)> Items = []; }
    sealed class XNodeList { public uint Init; public readonly List<ulong> Items = []; }
    sealed class XDocRef { public uint Init; public XDoc? Doc; public bool Owner; public uint Serialized; public int Status; }
    /// <summary>The DOM builder skips whitespace-only texts unless told otherwise (what the firmware does).</summary>
    sealed class XBuilder { public uint Init; public XDoc? Doc, Building; public bool SkipWs = true, SkipText, Resolve; public XmlScanner? Scanner; public DomSink? Sink; }
    sealed class XParser { public uint Init, Body, Handler, UserData; public bool SkipWs, Resolve; public XmlScanner? Scanner; }

    internal sealed class XNode
    {
        public int Type;
        public byte[] Name = [], Value = [];
        public int EntityType;
        public XNode? Parent;
        public readonly List<XNode> Children = [];
        public readonly List<XNode> Attrs = [];
        public uint Id;
        public uint NamePtr, ValuePtr;
    }

    internal sealed class XDoc
    {
        public uint Engine, Init;
        public bool ReadOnly;
        public readonly List<XNode?> Nodes = [null];
        public readonly List<uint> Blocks = [];
        public XNode Root = null!;

        public XNode New(int type, byte[]? name = null, byte[]? value = null)
        {
            var n = new XNode { Type = type, Name = name ?? [], Value = value ?? [], Id = (uint)Nodes.Count };
            Nodes.Add(n);
            return n;
        }

        public XNode? Get(uint id) => id > 0 && id < Nodes.Count ? Nodes[(int)id] : null;
        public ulong IdOf(XNode? n) => n == null ? 0 : (ulong)Engine << 32 | n.Id;
    }

    static readonly byte[] DocName = "xml"u8.ToArray(), DocValue = "version=\"1.0\""u8.ToArray(),
        XmlDecl = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"u8.ToArray();
    static readonly string[] Predefined = ["amp", "quot", "lt", "gt", "apos"];
    static readonly byte[] PredefinedChar = "&\"<>'"u8.ToArray();

    // ------------------------------------------------------------------ guest memory, calls and the allocator

    static uint R32(uint a) => GuestMemory.R32(a);
    static void W32(uint a, uint v) => GuestMemory.W32(a, v);

    [ThreadStatic] static uint t_sp;

    static uint StackTop()
    {
        uint sp = GuestThread.Current!.Core.State->R[13] - 64;
        return t_sp != 0 && t_sp < sp ? t_sp : sp;
    }

    static uint Call(uint fn, params ReadOnlySpan<uint> args) => GuestThread.Current!.CallGuestOnStack(fn, StackTop(), args);

    static uint CallVirt(uint obj, int slot, params ReadOnlySpan<uint> args)
    {
        Span<uint> a = stackalloc uint[args.Length + 1];
        a[0] = obj;
        args.CopyTo(a[1..]);
        return Call(R32(R32(obj) + (uint)slot * 4), a);
    }

    /// <summary>The allocator behind an Initializer object (0 when it is not initialized).</summary>
    XInit? InitOf(uint initializer)
    {
        if (initializer == 0) return null;
        uint impl = R32(initializer);
        lock (_lock) return impl != 0 && _impl.TryGetValue(impl, out var o) ? o as XInit : null;
    }

    uint Alloc(uint initializer, uint size)
    {
        var i = InitOf(initializer);
        return i == null ? 0 : CallVirt(i.Allocator, 2, size, i.UserData);
    }

    void Free(uint initializer, uint p)
    {
        if (p == 0) return;
        var i = InitOf(initializer);
        if (i != null) CallVirt(i.Allocator, 3, p, i.UserData);
    }

    T? Impl<T>(uint obj) where T : class
    {
        if (obj == 0) return null;
        uint p = R32(obj);
        lock (_lock) return p != 0 && _impl.TryGetValue(p, out var o) ? o as T : null;
    }

    uint NewImpl(uint initializer, uint size, object state)
    {
        uint p = Alloc(initializer, size);
        if (p == 0) return 0;
        GuestMemory.Fill(p, 0, size);
        W32(p + 4, initializer);
        lock (_lock) _impl[p] = state;
        return p;
    }

    void FreeImpl(uint obj, uint initializer)
    {
        uint p = R32(obj);
        lock (_lock) _impl.Remove(p);
        Free(initializer, p);
        W32(obj, 0);
    }

    /// <summary>A guest copy of host bytes (NUL-terminated) from the allocator, or 0.</summary>
    uint GuestBytes(uint initializer, byte[] b)
    {
        uint p = Alloc(initializer, (uint)b.Length + 1);
        if (p == 0) return 0;
        b.CopyTo(GuestMemory.Span(p, b.Length));
        GuestMemory.Write<byte>(p + (uint)b.Length, 0);
        return p;
    }

    /// <summary>The bytes of a String* (null pointer, null buffer or zero size: null).</summary>
    static byte[]? StrArg(uint s)
    {
        if (s == 0) return null;
        uint buf = R32(s), size = R32(s + 4);
        return buf == 0 || size == 0 ? null : GuestMemory.ReadSpan(buf, (int)size).ToArray();
    }

    static uint CStrLen(uint p)
    {
        if (p == 0) return 0;
        uint n = 0;
        while (n < 0x100000 && GuestMemory.Read<byte>(p + n) != 0) n++;
        return n;
    }

    static uint SetString(uint ret, uint buf, uint size) { W32(ret, buf); W32(ret + 4, size); return ret; }
    static uint SetCString(uint ret, uint buf) => SetString(ret, buf, CStrLen(buf));

    // ------------------------------------------------------------------ module data: the vtables the library owns

    enum Stub { AllocD1, AllocD0, Abort, VabD1, VabD0 }

    static readonly (uint Nid, uint LibNid, string Lib)[] StubTargets =
    [
        (0x56002B9D, LibNid, Lib),            // MemAllocator::~MemAllocator() [D1]
        (0x90B82579, LibNid, Lib),            // MemAllocator::~MemAllocator() [D0]
        (0x2F2C6046, 0xCAE9ACE6, "SceLibKernel"), // sceClibAbort (allocate/deallocate are pure virtual)
        (0xD9217FC8, LibNid, Lib),            // VarAllocBuffer::~VarAllocBuffer() [D1]
        (0xD61CAAFC, LibNid, Lib),            // VarAllocBuffer::~VarAllocBuffer() [D0]
    ];

    const uint DAllocVt = 0x00, DVabVt = 0x20, DStubs = 0x40;
    uint _data;

    uint Data
    {
        get
        {
            lock (_lock)
            {
                if (_data != 0) return _data;
                uint d = Mem.Alloc(GuestMemory.PageSize, "SceLibXml (HLE)");
                GuestMemory.Fill(d, 0, GuestMemory.PageSize);
                for (int i = 0; i < StubTargets.Length; i++)
                {
                    var (nid, libNid, lib) = StubTargets[i];
                    Linker.WriteHleStub(d + DStubs + (uint)i * 16, Emu.Hle.GetHleFunctionIndex(nid, libNid, lib), nid);
                }
                uint S(Stub s) => d + DStubs + (uint)s * 16;
                W32(d + DAllocVt, S(Stub.AllocD1)); W32(d + DAllocVt + 4, S(Stub.AllocD0));
                W32(d + DAllocVt + 8, S(Stub.Abort)); W32(d + DAllocVt + 12, S(Stub.Abort));
                W32(d + DVabVt, S(Stub.VabD1)); W32(d + DVabVt + 4, S(Stub.VabD0));
                Emu.Code.Invalidate(d, GuestMemory.PageSize);
                return _data = d;
            }
        }
    }

    // ------------------------------------------------------------------ MemAllocator, Initializer

    [Hle("_ZN3sce3Xml12MemAllocatorC1Ev")]
    [Hle("_ZN3sce3Xml12MemAllocatorC2Ev")]
    [Hle("_ZN3sce3Xml12MemAllocatorD1Ev")]
    [Hle("_ZN3sce3Xml12MemAllocatorD2Ev")]
    uint MemAllocatorCtor(uint me) { W32(me, Data + DAllocVt); return me; }

    /// <summary>The deleting destructor: the object came from operator new, which the module maps to the C heap.</summary>
    [Hle("_ZN3sce3Xml12MemAllocatorD0Ev")]
    uint MemAllocatorD0(uint me) { W32(me, Data + DAllocVt); return me; }

    [Hle("_ZN3sce3Xml11InitializerC1Ev")]
    [Hle("_ZN3sce3Xml11InitializerC2Ev")]
    uint InitializerCtor(uint me) { W32(me, 0); return me; }

    /// <summary>InitParameter { MemAllocator*, userData, 4 more words } is copied into a 0x18 block; only one
    /// Initializer may be live (the module's counter).</summary>
    [Hle("_ZN3sce3Xml11Initializer10initializeEPKNS0_13InitParameterE")]
    int InitializerInitialize(uint me, uint param)
    {
        if (R32(me) != 0) return InfoMultipleInit;
        if (param == 0 || R32(param) == 0) return ErrInvalidArgument;
        lock (_lock) if (_initCount >= 1) return InfoMultipleInit;
        uint alloc = R32(param), ud = R32(param + 4);
        uint p = CallVirt(alloc, 2, SizeInit, ud);
        if (p == 0) return ErrNoMemory;
        GuestMemory.Copy(p, param, SizeInit);
        var st = new XInit { Allocator = alloc, UserData = ud };
        lock (_lock) _impl[p] = st;
        W32(me, p);
        uint w = CallVirt(alloc, 2, SizeInitWrap, ud);
        if (w == 0)
        {
            lock (_lock) _impl.Remove(p);
            CallVirt(alloc, 3, p, ud);
            W32(me, 0);
            return ErrNoMemory;
        }
        W32(w, Data + DAllocVt); W32(w + 4, alloc);
        st.Wrapper = w;
        lock (_lock) _initCount++;
        return 0;
    }

    [Hle("_ZN3sce3Xml11Initializer9terminateEv")]
    int InitializerTerminate(uint me)
    {
        uint p = R32(me);
        XInit? st;
        lock (_lock) st = p != 0 && _impl.TryGetValue(p, out var o) ? o as XInit : null;
        if (st == null) return ErrNotInitialized;
        lock (_lock) { if (_initCount > 0) _initCount--; _impl.Remove(p); }
        if (st.Wrapper != 0) CallVirt(st.Allocator, 3, st.Wrapper, st.UserData);
        CallVirt(st.Allocator, 3, p, st.UserData);
        W32(me, 0);
        return 0;
    }

    [Hle("_ZN3sce3Xml11InitializerD1Ev")]
    [Hle("_ZN3sce3Xml11InitializerD2Ev")]
    uint InitializerDtor(uint me) { InitializerTerminate(me); return me; }

    // ------------------------------------------------------------------ String, SimpleData, SerializeParameter, results

    [Hle("_ZN3sce3Xml6StringC1Ev")]
    [Hle("_ZN3sce3Xml6StringC2Ev")]
    [Hle("_ZN3sce3Xml10SimpleDataC1Ev")]
    [Hle("_ZN3sce3Xml10SimpleDataC2Ev")]
    uint StringCtor(uint me) => SetString(me, 0, 0);

    [Hle("_ZN3sce3Xml6StringC1EPKcj")]
    [Hle("_ZN3sce3Xml6StringC2EPKcj")]
    [Hle("_ZN3sce3Xml10SimpleDataC1EPKcj")]
    [Hle("_ZN3sce3Xml10SimpleDataC2EPKcj")]
    uint StringCtorN(uint me, uint buf, uint size) => SetString(me, buf, size);

    [Hle("_ZN3sce3Xml6StringC1EPKc")]
    [Hle("_ZN3sce3Xml6StringC2EPKc")]
    uint StringCtorC(uint me, uint buf) => SetCString(me, buf);

    [Hle("_ZN3sce3Xml6StringC1ERKS1_")]
    [Hle("_ZN3sce3Xml6StringC2ERKS1_")]
    [Hle("_ZN3sce3Xml6StringaSERKS1_")]
    uint StringCopy(uint me, uint other) => SetString(me, R32(other), R32(other + 4));

    /// <summary>{ flags (bit 0: write the XML declaration), 16 reserved bytes }.</summary>
    [Hle("_ZN3sce3Xml18SerializeParameterC1Ev")]
    [Hle("_ZN3sce3Xml18SerializeParameterC2Ev")]
    uint SerializeParameterCtor(uint me) { GuestMemory.Fill(me, 0, 0x14); return me; }

    /// <summary>The engine's internal results (0x8085_2xxx) to the public ResultType.</summary>
    [Hle("_ZN3sce3Xml20bXResultToResultTypeEi")]
    int BxResult(int r) => BxResultToResultType(r);

    internal static int BxResultToResultType(int r)
    {
        uint u = unchecked((uint)r);
        return u switch
        {
            0 => 0,
            0x80852000 => ErrNoMemory, 0x80852001 => ErrNotInitialized, 0x80852002 => ErrInvalidArgument,
            0x80852004 => ErrNotSupported, 0x80852006 => unchecked((int)0x80850005), 0x80852007 => unchecked((int)0x80850006),
            0x80852008 => ErrParserBusy, 0x8085200A => ErrUnexpectedEof, 0x8085200B => ErrSyntax,
            0x8085200D => ErrEndTagMismatch, 0x80852011 => ErrInvalidChar, 0x80852013 => ErrNoCloseAngle,
            0x80852014 => ErrNoEquals, 0x80852015 => ErrNoSemicolon, 0x80852016 => ErrNoCommentEnd,
            0x80852017 => ErrNoCDataEnd, 0x80852019 => ErrNoQuote, 0x80852020 => unchecked((int)0x80850015),
            0x80852024 => ErrHandlerNotSet, 0x80852025 => unchecked((int)0x80850017), 0x80852026 => ErrInvalidDocElement,
            0x80852027 => ErrNoDocElement, 0x80852029 => ErrNoDtdEnd, 0x8085202A => ErrDuplicateAttr,
            0x80852500 => ErrInvalidChar, 0x80852502 => ErrInvalidDec, 0x80852503 => ErrInvalidHex,
            0x00852000 => InfoMultipleInit,
            0x80852100 => InfoInProgress, 0x80852101 => InfoAborted,
            _ => ErrGeneric,
        };
    }

    [Hle("_ZN3sce3Xml4Util9strResultEi")]
    uint StrResult(int r)
    {
        string s = r switch
        {
            ErrGeneric => "GenericError", ErrNoMemory => "NoMemory", ErrNotInitialized => "NotInitialized",
            ErrInvalidArgument => "InvalidArgument", ErrNotSupported => "NotSupported",
            unchecked((int)0x80850005) => "InitializeFailed", unchecked((int)0x80850006) => "InvalidBinXml",
            ErrParserBusy => "ParserBusy", ErrUnexpectedEof => "XmlUnexpectedEoF", ErrSyntax => "XmlSyntaxError",
            ErrEndTagMismatch => "XmlEndTagMismatch", ErrInvalidChar => "XmlInvalidChar", ErrInvalidDec => "XmlInvalidDecValue",
            ErrInvalidHex => "XmlInvalidHexValue", ErrNoCloseAngle => "XmlClosingAngleBracketCharNotFound",
            ErrNoEquals => "XmlEqualityCharNotFound", ErrNoSemicolon => "XmlSemiColonCharNotFound",
            ErrNoQuote => "XmlQuoteCharNotFound", ErrNoCommentEnd => "XmlEndOfCommentNotFound",
            ErrNoCDataEnd => "XmlEndOfCDSectNotFound", ErrNoDtdEnd => "XmlEndOfDtdNotFound",
            ErrUnknownEncoding => "XmlUnknownEncoding", ErrHandlerNotSet => "XmlHandlerNotSet", ErrInvalidPi => "XmlInvalidPi",
            ErrInvalidDocElement => "XmlInvalidDocumentElement", ErrNoDocElement => "XmlDocumentElementNotFound",
            ErrDuplicateAttr => "XmlDuplicateAttrName", InfoMultipleInit => "MultipleInitialized",
            InfoAborted => "XmlParseAborted", InfoInProgress => "XmlParseInProgress", 0x850000 => "GenericMessage",
            0 => "Success", _ => "Unknown error.",
        };
        return ResultText(s);
    }

    readonly Dictionary<string, uint> _texts = [];

    /// <summary>The module's rodata strings (one guest copy each, for the process).</summary>
    uint ResultText(string s)
    {
        lock (_lock)
        {
            if (_texts.TryGetValue(s, out uint p)) return p;
            p = Mem.Alloc((uint)s.Length + 1, "SceLibXml text (HLE)");
            GuestMemory.WriteCString(p, s);
            return _texts[s] = p;
        }
    }

    // ------------------------------------------------------------------ VarAllocBuffer (0x14: vptr, Initializer*, buffer, size, capacity)

    [Hle("_ZN3sce3Xml14VarAllocBufferC1EPKNS0_11InitializerE")]
    [Hle("_ZN3sce3Xml14VarAllocBufferC2EPKNS0_11InitializerE")]
    uint VabCtor(uint me, uint init)
    {
        W32(me, Data + DVabVt); W32(me + 4, init); W32(me + 8, 0); W32(me + 12, 0); W32(me + 16, 0);
        return me;
    }

    void VabRelease(uint me)
    {
        uint buf = R32(me + 8);
        if (buf != 0) { Free(R32(me + 4), buf); W32(me + 8, 0); }
        W32(me + 12, 0); W32(me + 16, 0);
    }

    [Hle("_ZN3sce3Xml14VarAllocBuffer5clearEv")]
    [Hle("_ZN3sce3Xml14VarAllocBuffer9terminateEv")]
    int VabClear(uint me)
    {
        if (R32(me + 8) != 0 && InitOf(R32(me + 4)) == null) return ErrNotInitialized;
        VabRelease(me);
        return 0;
    }

    [Hle("_ZN3sce3Xml14VarAllocBufferD1Ev")]
    [Hle("_ZN3sce3Xml14VarAllocBufferD2Ev")]
    uint VabDtor(uint me) { VabRelease(me); return me; }

    /// <summary>The deleting destructor (operator delete is the module's C heap free).</summary>
    [Hle("_ZN3sce3Xml14VarAllocBufferD0Ev")]
    uint VabDtor0(uint me) { VabRelease(me); return me; }

    /// <summary>Drops the old buffer and allocates <paramref name="size"/> bytes (the capacity; the size stays 0).</summary>
    [Hle("_ZN3sce3Xml14VarAllocBuffer7reserveEj")]
    int VabReserve(uint me, uint size)
    {
        VabRelease(me);
        if (InitOf(R32(me + 4)) == null) return ErrNotInitialized;
        uint p = Alloc(R32(me + 4), size);
        W32(me + 8, p);
        if (p == 0) return ErrNoMemory;
        W32(me + 16, size);
        return 0;
    }

    /// <summary>copy(data, size, terminate): needs the capacity (size + 1 with the terminator). Like the module, it
    /// refuses an empty buffer (size word 0) with NotInitialized.</summary>
    [Hle("_ZN3sce3Xml14VarAllocBuffer4copyEPKhjb")]
    int VabCopy(uint me, uint data, uint size, bool terminate)
    {
        if (R32(me + 12) == 0) return ErrNotInitialized;
        if (data == 0 || R32(me + 16) < size + (terminate ? 1u : 0u)) return ErrInvalidArgument;
        GuestMemory.Copy(R32(me + 8), data, size);
        if (terminate) GuestMemory.Write<byte>(R32(me + 8) + size, 0);
        W32(me + 12, size);
        return 0;
    }

    [Hle("_ZN3sce3Xml14VarAllocBuffer7copyStrEPKcj")]
    int VabCopyStr(uint me, uint s, uint size) => VabCopy(me, s, size, true);

    [Hle("_ZN3sce3Xml14VarAllocBuffer7copyStrERKNS0_6StringE")]
    int VabCopyString(uint me, uint str) => VabCopy(me, R32(str), R32(str + 4), true);

    // ------------------------------------------------------------------ Attr

    [Hle("_ZN3sce3Xml4AttrC1Ev")]
    [Hle("_ZN3sce3Xml4AttrC2Ev")]
    uint AttrCtor(uint me) { W32(me, 0); return me; }

    [Hle("_ZN3sce3Xml4Attr10initializeEPKNS0_11InitializerE")]
    int AttrInitialize(uint me, uint init)
    {
        if (R32(me) != 0) return InfoMultipleInit;
        if (InitOf(init) == null) return ErrNotInitialized;
        uint p = NewImpl(init, SizeAttr, new XAttr { Init = init });
        if (p == 0) return ErrNoMemory;
        W32(me, p);
        return 0;
    }

    void AttrRelease(XAttr a)
    {
        Free(a.Init, a.NamePtr); Free(a.Init, a.ValuePtr);
        a.NamePtr = a.ValuePtr = 0;
    }

    [Hle("_ZN3sce3Xml4Attr9terminateEv")]
    int AttrTerminate(uint me)
    {
        var a = Impl<XAttr>(me);
        if (a == null) return ErrNotInitialized;
        AttrRelease(a);
        FreeImpl(me, a.Init);
        return 0;
    }

    [Hle("_ZN3sce3Xml4AttrD1Ev")]
    [Hle("_ZN3sce3Xml4AttrD2Ev")]
    uint AttrDtor(uint me) { AttrTerminate(me); return me; }

    [Hle("_ZN3sce3Xml4AttrC1ERKS1_")]
    [Hle("_ZN3sce3Xml4AttrC2ERKS1_")]
    uint AttrCopyCtor(uint me, uint other)
    {
        W32(me, 0);
        if (Impl<XAttr>(other) is { } src && AttrInitialize(me, src.Init) == 0) AttrCopyFrom(me, src);
        return me;
    }

    void AttrCopyFrom(uint me, XAttr src)
    {
        if (Impl<XAttr>(me) is not { } dst) return;
        AttrRelease(dst);
        dst.Name = src.Name; dst.Value = src.Value;
        if (src.Name != null) dst.NamePtr = GuestBytes(dst.Init, src.Name);
        if (src.Value != null) dst.ValuePtr = GuestBytes(dst.Init, src.Value);
    }

    [Hle("_ZN3sce3Xml4AttraSERKS1_")]
    uint AttrAssign(uint me, uint other)
    {
        if (me == other) return me;
        var src = Impl<XAttr>(other);
        if (src == null) { AttrTerminate(me); return me; }
        if (R32(me) == 0 && AttrInitialize(me, src.Init) != 0) return me;
        AttrCopyFrom(me, src);
        return me;
    }

    int AttrSet(uint me, uint str, bool value)
    {
        byte[]? b = StrArg(str);
        if (b == null) return ErrInvalidArgument;
        var a = Impl<XAttr>(me);
        if (a == null) return ErrNotInitialized;
        uint p = GuestBytes(a.Init, b);
        if (p == 0) return ErrNoMemory;
        if (value) { Free(a.Init, a.ValuePtr); a.Value = b; a.ValuePtr = p; }
        else { Free(a.Init, a.NamePtr); a.Name = b; a.NamePtr = p; }
        return 0;
    }

    [Hle("_ZN3sce3Xml4Attr7setNameEPKNS0_6StringE")]
    int AttrSetName(uint me, uint str) => AttrSet(me, str, false);

    [Hle("_ZN3sce3Xml4Attr8setValueEPKNS0_6StringE")]
    int AttrSetValue(uint me, uint str) => AttrSet(me, str, true);

    [Hle("_ZNK3sce3Xml4Attr7getNameEv")]
    uint AttrGetName(uint ret, uint me) =>
        Impl<XAttr>(me) is { NamePtr: not 0 } a ? SetString(ret, a.NamePtr, (uint)a.Name!.Length) : SetString(ret, 0, 0);

    [Hle("_ZNK3sce3Xml4Attr8getValueEv")]
    uint AttrGetValue(uint ret, uint me) =>
        Impl<XAttr>(me) is { ValuePtr: not 0 } a ? SetString(ret, a.ValuePtr, (uint)a.Value!.Length) : SetString(ret, 0, 0);

    [Hle("_ZNK3sce3Xml4Attr11isAvailableEv")]
    bool AttrIsAvailable(uint me) => Impl<XAttr>(me) != null;

    // ------------------------------------------------------------------ AttributeList (pairs of name/value pointers, not copied)

    [Hle("_ZN3sce3Xml13AttributeListC1Ev")]
    [Hle("_ZN3sce3Xml13AttributeListC2Ev")]
    uint AttrListCtor(uint me) { W32(me, 0); return me; }

    [Hle("_ZN3sce3Xml13AttributeList10initializeEPKNS0_11InitializerE")]
    int AttrListInitialize(uint me, uint init)
    {
        if (R32(me) != 0) return InfoMultipleInit;
        if (InitOf(init) == null) return ErrNoMemory;
        var st = new XAttrList { Init = init };
        uint p = NewImpl(init, SizeAttrList, st);
        if (p == 0) return ErrNoMemory;
        W32(me, p);
        st.Body = Alloc(init, SizeAttrListBody);
        if (st.Body == 0) { FreeImpl(me, init); return ErrNoMemory; }
        return 0;
    }

    [Hle("_ZN3sce3Xml13AttributeList9terminateEv")]
    int AttrListTerminate(uint me)
    {
        if (Impl<XAttrList>(me) is { } l)
        {
            Free(l.Init, l.Body);
            FreeImpl(me, l.Init);
        }
        return 0;
    }

    [Hle("_ZN3sce3Xml13AttributeListD1Ev")]
    [Hle("_ZN3sce3Xml13AttributeListD2Ev")]
    uint AttrListDtor(uint me) { AttrListTerminate(me); return me; }

    [Hle("_ZN3sce3Xml13AttributeListC1ERKS1_")]
    [Hle("_ZN3sce3Xml13AttributeListC2ERKS1_")]
    uint AttrListCopyCtor(uint me, uint other)
    {
        W32(me, 0);
        if (Impl<XAttrList>(other) is { } src && AttrListInitialize(me, src.Init) == 0) Impl<XAttrList>(me)!.Items.AddRange(src.Items);
        return me;
    }

    [Hle("_ZN3sce3Xml13AttributeList12addAttributeEPKNS0_6StringES4_")]
    int AttrListAdd(uint me, uint name, uint value)
    {
        var l = Impl<XAttrList>(me);
        if (l == null) return ErrNotInitialized;
        if (StrArg(name) == null || StrArg(value) == null) return ErrInvalidArgument;
        l.Items.Add((R32(name), R32(value)));
        return 0;
    }

    [Hle("_ZN3sce3Xml13AttributeList5clearEv")]
    void AttrListClear(uint me) => Impl<XAttrList>(me)?.Items.Clear();

    [Hle("_ZNK3sce3Xml13AttributeList9getLengthEv")]
    int AttrListLength(uint me) => Impl<XAttrList>(me)?.Items.Count ?? 0;

    [Hle("_ZNK3sce3Xml13AttributeList11isAvailableEv")]
    bool AttrListAvailable(uint me) => Impl<XAttrList>(me) != null;

    /// <summary>An Attr (by value) holding copies of the pair; empty when the list or the entry is missing.</summary>
    uint AttrFromPair(uint ret, XAttrList l, (uint Name, uint Value) e)
    {
        AttrCtor(ret);
        if (AttrInitialize(ret, l.Init) != 0) return ret;
        uint s = Scratch(16);
        SetCString(s, e.Name); SetCString(s + 8, e.Value);
        AttrSetName(ret, s); AttrSetValue(ret, s + 8);
        t_sp = 0;
        return ret;
    }

    /// <summary>Temporary guest bytes below the caller's stack (released by clearing <see cref="t_sp"/>).</summary>
    static uint Scratch(uint size) => t_sp = (StackTop() - size) & ~7u;

    [Hle("_ZNK3sce3Xml13AttributeList12getAttributeEj")]
    uint AttrListGetAt(uint ret, uint me, uint index)
    {
        var l = Impl<XAttrList>(me);
        if (l == null || index >= l.Items.Count) { AttrCtor(ret); if (l != null) AttrInitialize(ret, l.Init); return ret; }
        return AttrFromPair(ret, l, l.Items[(int)index]);
    }

    [Hle("_ZNK3sce3Xml13AttributeList12getAttributeEPKNS0_6StringE")]
    uint AttrListGetByName(uint ret, uint me, uint name)
    {
        var l = Impl<XAttrList>(me);
        byte[]? n = StrArg(name);
        if (l != null && n != null)
            foreach (var e in l.Items)
                if (GuestMemory.ReadSpan(e.Name, (int)CStrLen(e.Name)).SequenceEqual(n)) return AttrFromPair(ret, l, e);
        AttrCtor(ret);
        return ret;
    }

    // ------------------------------------------------------------------ NodeList

    [Hle("_ZN3sce3Xml3Dom8NodeListC1Ev")]
    [Hle("_ZN3sce3Xml3Dom8NodeListC2Ev")]
    uint NodeListCtor(uint me) { W32(me, 0); return me; }

    [Hle("_ZN3sce3Xml3Dom8NodeList10initializeEPKNS0_11InitializerE")]
    int NodeListInitialize(uint me, uint init)
    {
        if (R32(me) != 0) return InfoMultipleInit;
        uint p = InitOf(init) == null ? 0 : NewImpl(init, SizeNodeList, new XNodeList { Init = init });
        W32(me, p);
        return p == 0 ? ErrNoMemory : 0;
    }

    [Hle("_ZN3sce3Xml3Dom8NodeList9terminateEv")]
    int NodeListTerminate(uint me)
    {
        var l = Impl<XNodeList>(me);
        if (l == null) return ErrNotInitialized;
        FreeImpl(me, l.Init);
        return 0;
    }

    [Hle("_ZN3sce3Xml3Dom8NodeListD1Ev")]
    [Hle("_ZN3sce3Xml3Dom8NodeListD2Ev")]
    uint NodeListDtor(uint me) { NodeListTerminate(me); return me; }

    [Hle("_ZN3sce3Xml3Dom8NodeListC1ERKS2_")]
    [Hle("_ZN3sce3Xml3Dom8NodeListC2ERKS2_")]
    uint NodeListCopyCtor(uint me, uint other)
    {
        W32(me, 0);
        if (Impl<XNodeList>(other) is { } src && NodeListInitialize(me, src.Init) == 0) Impl<XNodeList>(me)!.Items.AddRange(src.Items);
        return me;
    }

    [Hle("_ZN3sce3Xml3Dom8NodeList5clearEv")]
    void NodeListClear(uint me) => Impl<XNodeList>(me)?.Items.Clear();

    [Hle("_ZNK3sce3Xml3Dom8NodeList9getLengthEv")]
    int NodeListLength(uint me) => Impl<XNodeList>(me)?.Items.Count ?? 0;

    [Hle("_ZNK3sce3Xml3Dom8NodeList11isAvailableEv")]
    bool NodeListAvailable(uint me) => Impl<XNodeList>(me) != null;

    [Hle("_ZN3sce3Xml3Dom8NodeList11insertFirstEy")]
    ulong NodeListInsertFirst(uint me, ulong id)
    {
        if (Impl<XNodeList>(me) is not { } l || id == 0) return 0;
        l.Items.Insert(0, id);
        return id;
    }

    [Hle("_ZN3sce3Xml3Dom8NodeList10insertLastEy")]
    ulong NodeListInsertLast(uint me, ulong id)
    {
        if (Impl<XNodeList>(me) is not { } l || id == 0) return 0;
        l.Items.Add(id);
        return id;
    }

    [Hle("_ZN3sce3Xml3Dom8NodeList10removeItemEy")]
    ulong NodeListRemove(uint me, ulong id) => Impl<XNodeList>(me) is { } l && l.Items.Remove(id) ? id : 0;

    [Hle("_ZNK3sce3Xml3Dom8NodeList4itemEj")]
    [Hle("_ZNK3sce3Xml3Dom8NodeListixEj")]
    ulong NodeListItem(uint me, uint index) => Impl<XNodeList>(me) is { } l && index < l.Items.Count ? l.Items[(int)index] : 0;

    [Hle("_ZNK3sce3Xml3Dom8NodeList8findItemEy")]
    ulong NodeListFindId(uint me, ulong id) => Impl<XNodeList>(me) is { } l && l.Items.Contains(id) ? id : 0;

    /// <summary>The first item whose node name is <paramref name="name"/>.</summary>
    [Hle("_ZNK3sce3Xml3Dom8NodeList8findItemEPKNS0_6StringE")]
    ulong NodeListFindName(uint me, uint name)
    {
        byte[]? n = StrArg(name);
        if (Impl<XNodeList>(me) is not { } l || n == null) return 0;
        foreach (ulong id in l.Items)
            if (Resolve(id) is { } r && NameOf(r.Node).AsSpan().SequenceEqual(n)) return id;
        return 0;
    }

    XNodeList? NewOutList(uint list, uint init)
    {
        if (Impl<XNodeList>(list) is { } l) { l.Items.Clear(); return l; }
        if (R32(list) != 0) W32(list, 0);
        return NodeListInitialize(list, init) == 0 ? Impl<XNodeList>(list) : null;
    }

    // ------------------------------------------------------------------ the node model

    (XDoc Doc, XNode Node)? Resolve(ulong id)
    {
        uint eng = (uint)(id >> 32) & ~3u, n = (uint)id;
        lock (_lock)
            return eng != 0 && _engines.TryGetValue(eng, out var d) && d.Get(n) is { } node ? (d, node) : null;
    }

    static byte[] NameOf(XNode n) => n.Type switch
    {
        TDocument => DocName,
        TAttr or TElement or TElementNew => n.Name,
        _ => [],
    };

    /// <summary>The guest copy of a node's name (cached; 0 for an empty name).</summary>
    uint NamePtr(XDoc d, XNode n)
    {
        byte[] b = NameOf(n);
        if (b.Length == 0) return 0;
        if (n.NamePtr == 0) n.NamePtr = Track(d, GuestBytes(d.Init, b));
        return n.NamePtr;
    }

    uint ValuePtr(XDoc d, XNode n)
    {
        byte[] b = n.Type == TDocument ? DocValue : n.Value;
        if (n.Type is TElement or TElementNew) return 0;
        if (n.ValuePtr == 0) n.ValuePtr = Track(d, GuestBytes(d.Init, b));
        return n.ValuePtr;
    }

    static uint Track(XDoc d, uint p)
    {
        if (p != 0) d.Blocks.Add(p);
        return p;
    }

    void Untrack(XDoc d, ref uint p)
    {
        if (p == 0) return;
        d.Blocks.Remove(p);
        Free(d.Init, p);
        p = 0;
    }

    XDoc? NewEngine(uint init, uint owner)
    {
        uint eng = Alloc(init, SizeEngine);
        if (eng == 0) return null;
        GuestMemory.Fill(eng, 0, SizeEngine);
        W32(eng + 0x460, owner);
        var d = new XDoc { Engine = eng, Init = init };
        d.Root = d.New(TDocument);
        lock (_lock) _engines[eng] = d;
        return d;
    }

    void FreeEngine(XDoc d)
    {
        lock (_lock) _engines.Remove(d.Engine);
        foreach (uint p in d.Blocks) Free(d.Init, p);
        d.Blocks.Clear();
        Free(d.Init, d.Engine);
    }

    static XNode? DocElement(XDoc d) => d.Root.Children.FirstOrDefault(c => c.Type is TElement or TElementNew);

    static bool Accepts(XNode parent, XNode child) => parent.Type switch
    {
        TDocument => child.Type is TElement or TElementNew or TPiNode or TComment
            && (child.Type is not (TElement or TElementNew) || !parent.Children.Any(c => c.Type is TElement or TElementNew && c != child)),
        TElement or TElementNew => child.Type is TElement or TElementNew or TText or TCDataNode or TPiNode or TComment or TEntityRef,
        TAttr => child.Type is TText,
        _ => false,
    };

    static bool IsAncestor(XNode a, XNode n)
    {
        for (var p = n; p != null; p = p.Parent) if (p == a) return true;
        return false;
    }

    static void Detach(XNode n)
    {
        if (n.Parent == null) return;
        if (n.Type == TAttr) n.Parent.Attrs.Remove(n); else n.Parent.Children.Remove(n);
        n.Parent = null;
    }

    /// <summary>Inserts <paramref name="child"/> under <paramref name="parent"/> before <paramref name="before"/> (null:
    /// at the end); false when the types do not fit or the child contains the parent.</summary>
    static bool Insert(XNode parent, XNode child, XNode? before)
    {
        if (!Accepts(parent, child) || IsAncestor(child, parent)) return false;
        if (before != null && before.Parent != parent) return false;
        Detach(child);
        int at = before == null ? parent.Children.Count : parent.Children.IndexOf(before);
        parent.Children.Insert(at, child);
        child.Parent = parent;
        return true;
    }

    /// <summary>A deep copy of <paramref name="src"/> (from any document) into <paramref name="d"/>, detached.</summary>
    static XNode CloneInto(XDoc d, XNode src)
    {
        var n = d.New(src.Type, src.Name, src.Value);
        n.EntityType = src.EntityType;
        foreach (var a in src.Attrs) { var c = CloneInto(d, a); c.Parent = n; n.Attrs.Add(c); }
        foreach (var ch in src.Children) { var c = CloneInto(d, ch); c.Parent = n; n.Children.Add(c); }
        return n;
    }

    void DeleteTree(XDoc d, XNode n)
    {
        Detach(n);
        void Drop(XNode x)
        {
            foreach (var a in x.Attrs) Drop(a);
            foreach (var c in x.Children) Drop(c);
            Untrack(d, ref x.NamePtr); Untrack(d, ref x.ValuePtr);
            d.Nodes[(int)x.Id] = null;
        }
        Drop(n);
    }

    // ------------------------------------------------------------------ Document

    [Hle("_ZN3sce3Xml3Dom8DocumentC1Ev")]
    [Hle("_ZN3sce3Xml3Dom8DocumentC2Ev")]
    uint DocumentCtor(uint me) { W32(me, 0); return me; }

    XDocRef? DocRef(uint me) => Impl<XDocRef>(me);
    XDoc? Doc(uint me) => DocRef(me)?.Doc;

    /// <summary>An empty, writable document (its engine holds only the document node).</summary>
    [Hle("_ZN3sce3Xml3Dom8Document10initializeEPKNS0_11InitializerE")]
    int DocumentInitialize(uint me, uint init)
    {
        if (R32(me) != 0) return InfoMultipleInit;
        if (InitOf(init) == null) return ErrNotInitialized;
        var r = new XDocRef { Init = init };
        uint p = NewImpl(init, SizeDocument, r);
        if (p == 0) return ErrNoMemory;
        W32(me, p);
        r.Doc = NewEngine(init, me);
        if (r.Doc == null) { FreeImpl(me, init); return ErrNoMemory; }
        r.Owner = true;
        return 0;
    }

    /// <summary>A document that shares <paramref name="src"/>'s engine (not its owner).</summary>
    int DocumentShare(uint me, XDocRef src)
    {
        if (R32(me) == 0)
        {
            var r = new XDocRef { Init = src.Init };
            uint p = NewImpl(src.Init, SizeDocument, r);
            if (p == 0) return ErrNoMemory;
            W32(me, p);
        }
        var dst = DocRef(me)!;
        DocumentRelease(dst);
        dst.Doc = src.Doc;
        dst.Owner = false;
        return 0;
    }

    void DocumentRelease(XDocRef r)
    {
        if (r.Serialized != 0) { Free(r.Init, r.Serialized); r.Serialized = 0; }
        if (r.Doc != null && r.Owner) FreeEngine(r.Doc);
        r.Doc = null;
        r.Owner = false;
    }

    [Hle("_ZN3sce3Xml3Dom8Document9terminateEv")]
    int DocumentTerminate(uint me)
    {
        var r = DocRef(me);
        if (r == null) return ErrNotInitialized;
        DocumentRelease(r);
        FreeImpl(me, r.Init);
        return 0;
    }

    [Hle("_ZN3sce3Xml3Dom8DocumentD1Ev")]
    [Hle("_ZN3sce3Xml3Dom8DocumentD2Ev")]
    uint DocumentDtor(uint me) { DocumentTerminate(me); return me; }

    [Hle("_ZN3sce3Xml3Dom8DocumentC1ERKS2_")]
    [Hle("_ZN3sce3Xml3Dom8DocumentC2ERKS2_")]
    uint DocumentCopyCtor(uint me, uint other)
    {
        W32(me, 0);
        if (DocRef(other) is { Doc: not null } src) DocumentShare(me, src);
        return me;
    }

    [Hle("_ZN3sce3Xml3Dom8DocumentaSERKS2_")]
    uint DocumentAssign(uint me, uint other)
    {
        if (me != other && DocRef(other) is { Doc: not null } src) DocumentShare(me, src);
        return me;
    }

    [Hle("_ZNK3sce3Xml3Dom8Document11isAvailableEv")]
    bool DocumentAvailable(uint me) => Doc(me) != null;

    [Hle("_ZNK3sce3Xml3Dom8Document9getStatusEv")]
    int DocumentStatus(uint me) => DocRef(me) is { } r ? (r.Doc == null ? ErrNotInitialized : r.Status) : ErrNotInitialized;

    [Hle("_ZN3sce3Xml3Dom8Document11resetStatusEv")]
    void DocumentResetStatus(uint me) { if (DocRef(me) is { } r) r.Status = 0; }

    [Hle("_ZNK3sce3Xml3Dom8Document10isReadOnlyEv")]
    bool DocumentReadOnly(uint me) => Doc(me)?.ReadOnly ?? true;

    /// <summary>Makes a parsed (read-only) document editable; answers the document node.</summary>
    [Hle("_ZN3sce3Xml3Dom8Document11setWritableEv")]
    ulong DocumentSetWritable(uint me)
    {
        if (Doc(me) is not { } d) return 0;
        d.ReadOnly = false;
        return d.IdOf(d.Root);
    }

    /// <summary>The node a NodeId names in this document (like the module, only the low word is looked at).</summary>
    static XNode? Node(XDoc d, ulong id) => d.Get((uint)id);

    [Hle("_ZNK3sce3Xml3Dom8Document10getDocRootEv")]
    [Hle("_ZNK3sce3Xml3Dom8Document10getXmlMetaEv")]
    ulong DocumentDocRoot(uint me) => Doc(me) is { } d ? d.IdOf(d.Root) : 0;

    [Hle("_ZNK3sce3Xml3Dom8Document7getRootEv")]
    ulong DocumentRoot(uint me) => Doc(me) is { } d ? d.IdOf(DocElement(d)) : 0;

    [Hle("_ZNK3sce3Xml3Dom8Document13getFirstChildEy")]
    ulong DocumentFirstChild(uint me, ulong id) => Doc(me) is { } d && Node(d, id) is { Children.Count: > 0 } n ? d.IdOf(n.Children[0]) : 0;

    [Hle("_ZNK3sce3Xml3Dom8Document12getLastChildEy")]
    ulong DocumentLastChild(uint me, ulong id) => Doc(me) is { } d && Node(d, id) is { Children.Count: > 0 } n ? d.IdOf(n.Children[^1]) : 0;

    static XNode? NextSibling(XNode n)
    {
        if (n.Parent == null) return null;
        var list = n.Type == TAttr ? n.Parent.Attrs : n.Parent.Children;
        int i = list.IndexOf(n);
        return i >= 0 && i + 1 < list.Count ? list[i + 1] : null;
    }

    [Hle("_ZNK3sce3Xml3Dom8Document10getSiblingEy")]
    ulong DocumentSibling(uint me, ulong id) => Doc(me) is { } d && Node(d, id) is { Type: not TAttr } n ? d.IdOf(NextSibling(n)) : 0;

    [Hle("_ZNK3sce3Xml3Dom8Document9getParentEy")]
    ulong DocumentParent(uint me, ulong id) => Doc(me) is { } d && Node(d, id) is { } n ? d.IdOf(ParentOf(n)) : 0;

    /// <summary>A node's parent as the module reports it: the top-level nodes have none (not the document node).</summary>
    internal static XNode? ParentOf(XNode n) => n.Parent is { Type: not TDocument } p ? p : null;

    [Hle("_ZNK3sce3Xml3Dom8Document12getFirstAttrEy")]
    ulong DocumentFirstAttr(uint me, ulong id) => Doc(me) is { } d && Node(d, id) is { Attrs.Count: > 0 } n ? d.IdOf(n.Attrs[0]) : 0;

    [Hle("_ZNK3sce3Xml3Dom8Document11getNextAttrEy")]
    ulong DocumentNextAttr(uint me, ulong id) => Doc(me) is { } d && Node(d, id) is { Type: TAttr } n ? d.IdOf(NextSibling(n)) : 0;

    [Hle("_ZNK3sce3Xml3Dom8Document11getNodeTypeEy")]
    int DocumentNodeType(uint me, ulong id) => Doc(me) is { } d && Node(d, id) is { } n ? n.Type : -1;

    [Hle("_ZNK3sce3Xml3Dom8Document13hasChildNodesEy")]
    bool DocumentHasChildren(uint me, ulong id) => Doc(me) is { } d && Node(d, id) is { Children.Count: > 0 };

    [Hle("_ZNK3sce3Xml3Dom8Document13hasAttributesEy")]
    bool DocumentHasAttrs(uint me, ulong id) => Doc(me) is { } d && Node(d, id) is { Attrs.Count: > 0 };

    /// <summary>The module's "name" of a node: an element's or attribute's name, a text's or skipped part's text,
    /// nothing for the document node.</summary>
    [Hle("_ZNK3sce3Xml3Dom8Document11getNodeNameEy")]
    uint DocumentNodeName(uint ret, uint me, ulong id)
    {
        if (Doc(me) is not { } d || Node(d, id) is not { } n || n.Type == TDocument) return SetString(ret, 0, 0);
        return SetCString(ret, n.Type is TElement or TElementNew or TAttr ? NamePtr(d, n) : ValuePtr(d, n));
    }

    [Hle("_ZNK3sce3Xml3Dom8Document7getTextEy")]
    uint DocumentText(uint ret, uint me, ulong id) =>
        Doc(me) is { } d && Node(d, id) is { Type: TText } n ? SetString(ret, ValuePtr(d, n), (uint)n.Value.Length) : SetString(ret, 0, 0);

    [Hle("_ZNK3sce3Xml3Dom8Document11getAttrNameEy")]
    uint DocumentAttrName(uint ret, uint me, ulong id) =>
        Doc(me) is { } d && Node(d, id) is { Type: TAttr } n ? SetString(ret, NamePtr(d, n), (uint)n.Name.Length) : SetString(ret, 0, 0);

    [Hle("_ZNK3sce3Xml3Dom8Document12getAttrValueEy")]
    uint DocumentAttrValue(uint ret, uint me, ulong id) =>
        Doc(me) is { } d && Node(d, id) is { Type: TAttr } n ? SetString(ret, ValuePtr(d, n), (uint)n.Value.Length) : SetString(ret, 0, 0);

    /// <summary>The markup of a skipped part (PI, DTD, CDATA, comment) as it appeared in the source.</summary>
    [Hle("_ZNK3sce3Xml3Dom8Document14getSkippedTextEy")]
    uint DocumentSkippedText(uint ret, uint me, ulong id) =>
        Doc(me) is { } d && Node(d, id) is { Type: TPiNode or TCDataNode or TComment } n ? SetString(ret, ValuePtr(d, n), (uint)n.Value.Length) : SetString(ret, 0, 0);

    [Hle("_ZNK3sce3Xml3Dom8Document13getEntityTypeEy")]
    int DocumentEntityType(uint me, ulong id) => Doc(me) is { } d && Node(d, id) is { Type: TEntityRef } n ? n.EntityType : 0;

    [Hle("_ZNK3sce3Xml3Dom8Document9getEntityEy")]
    uint DocumentEntity(uint ret, uint me, ulong id) =>
        Doc(me) is { } d && Node(d, id) is { Type: TEntityRef } n ? SetString(ret, ValuePtr(d, n), (uint)n.Value.Length) : SetString(ret, 0, 0);

    [Hle("_ZNK3sce3Xml3Dom8Document12getAttributeEyPKNS0_6StringE")]
    uint DocumentGetAttribute(uint ret, uint me, ulong id, uint name)
    {
        byte[]? key = StrArg(name);
        if (Doc(me) is { } d && Node(d, id) is { } n && key != null && n.Attrs.FirstOrDefault(a => a.Name.AsSpan().SequenceEqual(key)) is { } a)
            return SetString(ret, ValuePtr(d, a), (uint)a.Value.Length);
        return SetString(ret, 0, 0);
    }

    int FillList(uint me, uint list, IEnumerable<XNode> nodes, bool emptyIsError)
    {
        var r = DocRef(me);
        if (r?.Doc is not { } d) return ErrNotInitialized;
        if (list == 0) return ErrInvalidArgument;
        var l = NewOutList(list, r.Init);
        if (l == null) return ErrNoMemory;
        foreach (var n in nodes) l.Items.Add(d.IdOf(n));
        return emptyIsError && l.Items.Count == 0 ? ErrNotFound : 0;
    }

    /// <summary>The list of a node's children or attributes (empty for a node without them; an unknown node is
    /// NotFound).</summary>
    int NodeList(uint me, ulong id, uint list, bool attrs)
    {
        if (Doc(me) is not { } d) return ErrNotInitialized;
        if (Node(d, id) is not { } n) return FillList(me, list, [], true);
        return FillList(me, list, attrs ? n.Attrs : n.Children, false);
    }

    [Hle("_ZNK3sce3Xml3Dom8Document13getChildNodesEyPNS1_8NodeListE")]
    int DocumentChildNodes(uint me, ulong id, uint list) =>
        NodeList(me, id, list, false);

    [Hle("_ZNK3sce3Xml3Dom8Document13getAttributesEyPNS1_8NodeListE")]
    int DocumentAttributes(uint me, ulong id, uint list) =>
        NodeList(me, id, list, true);

    static IEnumerable<XNode> Descendants(XNode n)
    {
        foreach (var c in n.Children)
        {
            yield return c;
            foreach (var x in Descendants(c)) yield return x;
        }
    }

    [Hle("_ZNK3sce3Xml3Dom8Document20getElementsByTagNameEyPKNS0_6StringEPNS1_8NodeListE")]
    int DocumentElementsByTag(uint me, ulong id, uint name, uint list)
    {
        if (Doc(me) is not { } d) return ErrNotInitialized;
        byte[]? key = StrArg(name);
        if (key == null || list == 0) return ErrInvalidArgument;
        var start = Node(d, id) ?? d.Root;
        return FillList(me, list, Descendants(start).Where(x => x.Type is TElement or TElementNew && x.Name.AsSpan().SequenceEqual(key)), false);
    }

    XDoc? Writable(uint me) => Doc(me) is { ReadOnly: false } d ? d : null;

    /// <summary>What an edit of a read-only (parsed, not yet setWritable) document answers.</summary>
    internal const int ErrReadOnly = unchecked((int)0x80850202);

    /// <summary>A new element (name, optional attributes from an AttributeList and an optional text child).</summary>
    XNode? MakeElement(XDoc d, uint name, uint attrs, uint text)
    {
        byte[]? n = StrArg(name);
        if (n == null) return null;
        var e = d.New(TElementNew, n);
        if (Impl<XAttrList>(attrs) is { } l)
            foreach (var (an, av) in l.Items)
            {
                var a = d.New(TAttr, GuestMemory.ReadSpan(an, (int)CStrLen(an)).ToArray(), GuestMemory.ReadSpan(av, (int)CStrLen(av)).ToArray());
                a.Parent = e; e.Attrs.Add(a);
            }
        if (StrArg(text) is { } t) { var c = d.New(TText, null, t); c.Parent = e; e.Children.Add(c); }
        return e;
    }

    [Hle("_ZN3sce3Xml3Dom8Document15addElementChildEyPKNS0_6StringEPKNS0_13AttributeListES5_")]
    ulong DocumentAddElementChild(uint me, ulong parent, uint name, uint attrs, uint text)
    {
        if (Doc(me) is not { } d) return 0;
        var p = Node(d, parent) ?? (parent == 0 ? d.Root : null);
        if (p == null || MakeElement(d, name, attrs, text) is not { } e) return 0;
        // a read-only document makes the element but does not link it (the module answers it all the same)
        if (!d.ReadOnly && !Insert(p, e, null)) { DeleteTree(d, e); return 0; }
        return d.IdOf(e);
    }

    /// <summary>A detached element (type 5); insertNode puts it in the tree.</summary>
    [Hle("_ZN3sce3Xml3Dom8Document13createElementEPKNS0_6StringEPKNS0_13AttributeListES5_")]
    ulong DocumentCreateElement(uint me, uint name, uint attrs, uint text)
    {
        if (Writable(me) is not { } d || MakeElement(d, name, attrs, text) is not { } e) return 0;
        return d.IdOf(e);
    }

    /// <summary>Answers the new node, or { 0, InvalidArgument } for an empty string (the module's way).</summary>
    [Hle("_ZN3sce3Xml3Dom8Document14createTextNodeEPKNS0_6StringE")]
    ulong DocumentCreateText(uint me, uint text)
    {
        if (Writable(me) is not { } d) return 0;
        if (StrArg(text) is not { } t) return unchecked((uint)ErrInvalidArgument);
        return d.IdOf(d.New(TText, null, t));
    }

    /// <summary>insertNode(parent, refChild, newChild): newChild right after refChild under parent (0: as the first
    /// child), like the module's engine; 0 when refChild is not a child of parent or the document is read-only.</summary>
    [Hle("_ZN3sce3Xml3Dom8Document10insertNodeEyyy")]
    ulong DocumentInsertNode(uint me, ulong parent, ulong refChild, ulong child)
    {
        if (Writable(me) is not { } d || Node(d, parent) is not { } p || Node(d, child) is not { } c) return 0;
        return InsertAfter(d, p, c, refChild) ? d.IdOf(c) : 0;
    }

    internal static bool InsertAfter(XDoc d, XNode parent, XNode child, ulong refChild)
    {
        if (refChild == 0) return Insert(parent, child, parent.Children.FirstOrDefault(x => x != child));
        if (Node(d, refChild) is not { } r || r.Parent != parent || r == child) return false;
        int i = parent.Children.IndexOf(r);
        var next = i + 1 < parent.Children.Count ? parent.Children[i + 1] : null;
        if (next == child) return true;
        return Insert(parent, child, next);
    }

    /// <summary>removeChild(a, b): detaches whichever of the two is the other's child; answers the removed node.</summary>
    [Hle("_ZN3sce3Xml3Dom8Document11removeChildEyy")]
    ulong DocumentRemoveChild(uint me, ulong a, ulong b)
    {
        if (Writable(me) is not { } d || Node(d, a) is not { } na || Node(d, b) is not { } nb) return 0;
        var child = nb.Parent == na ? nb : na.Parent == nb ? na : null;
        if (child == null) return 0;
        Detach(child);
        return d.IdOf(child);
    }

    /// <summary>Frees a node that is not in the tree (made by createElement/createTextNode or taken out by
    /// removeChild) with everything below it; a node still in the tree is left alone.</summary>
    [Hle("_ZN3sce3Xml3Dom8Document13recurseDeleteEy")]
    int DocumentRecurseDelete(uint me, ulong id)
    {
        if (Doc(me) is not { } d) return ErrNotInitialized;
        if (Node(d, id) is { Parent: null } n && n != d.Root) DeleteTree(d, n);
        return 0;
    }

    /// <summary>Replaces this document's content with a copy of <paramref name="src"/>'s tree.</summary>
    [Hle("_ZN3sce3Xml3Dom8Document12importParentEPKS2_y")]
    int DocumentImportParent(uint me, uint src, ulong id)
    {
        if (Doc(me) is not { } d) return ErrNotInitialized;
        if (src == 0 || Doc(src) is not { } s) return ErrInvalidArgument;
        foreach (var c in d.Root.Children.ToArray()) DeleteTree(d, c);
        XNode[] from = Node(s, id) is { Type: TElement or TElementNew } top ? [top] : [.. s.Root.Children];
        foreach (var c in from)
        {
            var copy = CloneInto(d, c);
            copy.Parent = d.Root; d.Root.Children.Add(copy);
        }
        d.ReadOnly = false;
        return 0;
    }

    /// <summary>importNode(parent, refChild, src, node): a copy of src's node inserted before refChild under parent.</summary>
    [Hle("_ZN3sce3Xml3Dom8Document10importNodeEyyPKS2_y")]
    ulong DocumentImportNode(uint me, ulong parent, ulong refChild, uint src, ulong node)
    {
        if (Writable(me) is not { } d) return 0;
        if (src == 0) return unchecked((uint)ErrInvalidArgument);
        if (Doc(src) is not { } s || Node(s, node) is not { } n || Node(d, parent) is not { } p) return 0;
        var before = refChild == 0 ? null : Node(d, refChild);
        var copy = CloneInto(d, n);
        if (!Insert(p, copy, before)) { DeleteTree(d, copy); return 0; }
        return d.IdOf(copy);
    }

    /// <summary>The text of a text node, or of an element's text child (created when missing).</summary>
    [Hle("_ZN3sce3Xml3Dom8Document7setTextEyPKNS0_6StringE")]
    int DocumentSetText(uint me, ulong id, uint text)
    {
        if (Doc(me) is not { } d) return ErrNotInitialized;
        if (d.ReadOnly) return ErrReadOnly;
        if (Node(d, id) is not { } n) return ErrInvalidArgument;
        byte[] t = StrArg(text) ?? [];
        var target = n.Type is TText ? n : n.Type is TElement or TElementNew ? n.Children.FirstOrDefault(c => c.Type == TText) : null;
        if (target == null)
        {
            if (n.Type is not (TElement or TElementNew)) return ErrInvalidArgument;
            target = d.New(TText);
            target.Parent = n; n.Children.Add(target);
        }
        Untrack(d, ref target.ValuePtr);
        target.Value = t;
        return 0;
    }

    int SetAttr(uint me, ulong id, uint name, uint value, bool create)
    {
        if (Doc(me) is not { } d) return ErrNotInitialized;
        if (d.ReadOnly) return ErrReadOnly;
        byte[]? key = StrArg(name);
        if (Node(d, id) is not { Type: TElement or TElementNew } n || key == null || value == 0) return ErrInvalidArgument;
        byte[] v = StrArg(value) ?? [];
        var a = n.Attrs.FirstOrDefault(x => x.Name.AsSpan().SequenceEqual(key));
        if (a == null)
        {
            if (!create) return 0;
            a = d.New(TAttr, key);
            a.Parent = n; n.Attrs.Add(a);
        }
        Untrack(d, ref a.ValuePtr);
        a.Value = v;
        return 0;
    }

    [Hle("_ZN3sce3Xml3Dom8Document12setAttrValueEyPKNS0_6StringES5_")]
    int DocumentSetAttrValue(uint me, ulong id, uint name, uint value) => SetAttr(me, id, name, value, false);

    [Hle("_ZN3sce3Xml3Dom8Document12setAttributeEyPKNS0_6StringES5_")]
    int DocumentSetAttribute(uint me, ulong id, uint name, uint value) => SetAttr(me, id, name, value, true);

    [Hle("_ZN3sce3Xml3Dom8Document16setAttributeListEyPKNS0_13AttributeListE")]
    int DocumentSetAttributeList(uint me, ulong id, uint list)
    {
        if (Doc(me) is not { } d) return ErrNotInitialized;
        if (d.ReadOnly) return ErrReadOnly;
        if (Node(d, id) is not { Type: TElement or TElementNew } n || Impl<XAttrList>(list) is not { } l) return ErrInvalidArgument;
        foreach (var a in n.Attrs.ToArray()) DeleteTree(d, a);
        foreach (var (an, av) in l.Items)
        {
            var a = d.New(TAttr, GuestMemory.ReadSpan(an, (int)CStrLen(an)).ToArray(), GuestMemory.ReadSpan(av, (int)CStrLen(av)).ToArray());
            a.Parent = n; n.Attrs.Add(a);
        }
        return 0;
    }

    [Hle("_ZN3sce3Xml3Dom8Document15removeAttributeEyPKNS0_6StringE")]
    int DocumentRemoveAttribute(uint me, ulong id, uint name)
    {
        if (Doc(me) is not { } d) return ErrNotInitialized;
        if (d.ReadOnly) return ErrReadOnly;
        byte[]? key = StrArg(name);
        if (Node(d, id) is not { } n || key == null) return ErrInvalidArgument;
        if (n.Attrs.FirstOrDefault(x => x.Name.AsSpan().SequenceEqual(key)) is { } a) DeleteTree(d, a);
        return 0;
    }

    [Hle("_ZN3sce3Xml3Dom8Document16removeAttributesEy")]
    int DocumentRemoveAttributes(uint me, ulong id)
    {
        if (Doc(me) is not { } d) return ErrNotInitialized;
        if (d.ReadOnly) return ErrReadOnly;
        if (Node(d, id) is not { } n) return ErrInvalidArgument;
        foreach (var a in n.Attrs.ToArray()) DeleteTree(d, a);
        return 0;
    }

    /// <summary>serialize(param, out): the tree as UTF-8 XML in a buffer the document keeps (until the next serialize
    /// or terminate); param bit 0 adds the XML declaration.</summary>
    [Hle("_ZN3sce3Xml3Dom8Document9serializeEPKNS0_18SerializeParameterEPNS0_6StringE")]
    int DocumentSerialize(uint me, uint param, uint outStr)
    {
        var r = DocRef(me);
        if (r?.Doc is not { } d) return ErrNotInitialized;
        if (outStr == 0) return ErrInvalidArgument;
        if (r.Serialized != 0) { Free(r.Init, r.Serialized); r.Serialized = 0; }
        var sb = new List<byte>();
        if (param != 0 && (R32(param) & 1) != 0) sb.AddRange(XmlDecl);
        Serialize(sb, d.Root.Children);
        uint p = GuestBytes(r.Init, [.. sb]);
        if (p == 0) return ErrNoMemory;
        r.Serialized = p;
        SetString(outStr, p, (uint)sb.Count);
        return 0;
    }

    /// <summary>The module's serializer: texts, names and values as stored (no escaping); a childless element ends
    /// with "/&gt;", and, like the module, the next sibling of such an element when it is not an element is followed by
    /// that element's end tag.</summary>
    internal static void Serialize(List<byte> o, List<XNode> siblings)
    {
        byte[]? pending = null;
        foreach (var n in siblings)
        {
            switch (n.Type)
            {
                case TElement or TElementNew:
                    o.Add((byte)'<'); o.AddRange(n.Name);
                    foreach (var a in n.Attrs) { o.Add((byte)' '); o.AddRange(a.Name); o.AddRange("=\""u8); o.AddRange(a.Value); o.Add((byte)'"'); }
                    pending = n.Name;
                    if (n.Children.Count == 0) { o.AddRange("/>"u8); continue; }
                    o.Add((byte)'>');
                    Serialize(o, n.Children);
                    o.AddRange("</"u8); o.AddRange(n.Name); o.Add((byte)'>');
                    pending = null;
                    continue;
                case TText or TPiNode or TCDataNode or TComment: o.AddRange(n.Value); break;
                case TEntityRef: o.AddRange(n.Value); break;
            }
            if (pending != null) { o.AddRange("</"u8); o.AddRange(pending); o.Add((byte)'>'); pending = null; }
        }
    }
}
