using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// paf::cxml, the reader of Sony's binary XML (RCO resources, plugin layouts). A file starts with "CXML", version
/// 0x110 and the offset/size of nine tables (tree, id, id-hash, string, wide string, hash, int array, float array,
/// file). The Document object (0xAC bytes) is { access mode +0, header +4 (0x58), then each table's pointer and
/// capacity from +0x5C, allocator +0xA4, its data +0xA8 }. In the tree an element is { name, attribute count,
/// parent, previous, next, first child, last child } (7 words, -1 for none) followed by its attributes { name, type,
/// two data words }; names index the string table. Element is { Document*, offset }, Attribute { Document*, element,
/// offset }, cxml::File { Document*, offset, size }. Errors: 0x80AF0501 bad argument, 0x80AF0505 magic, 0x80AF0506
/// version, 0x80AF0503 out of memory, 0x80AF050D already loaded, 0x80AF0507 no element, 0x80AF050A no such attribute,
/// 0x80AF050B other type.
/// </summary>
public sealed partial class Paf
{
    const int CxBadArg = unchecked((int)0x80AF0501), CxNoMem = unchecked((int)0x80AF0503), CxMagic = unchecked((int)0x80AF0505),
        CxVersion = unchecked((int)0x80AF0506), CxNoElement = unchecked((int)0x80AF0507), CxNoAttr = unchecked((int)0x80AF050A),
        CxType = unchecked((int)0x80AF050B), CxState = unchecked((int)0x80AF050D);
    const uint CxHeader = 0x58, CxTables = 0x5C;

    [Hle("_ZN3paf4cxml8DocumentC1Ev")]
    [Hle("_ZN3paf4cxml8DocumentC2Ev")]
    uint CxDocCtor(uint me) { GuestMemory.Fill(me, 0, 0xAC); return me; }

    [Hle("_ZN3paf4cxml6HeaderC1Ev")]
    [Hle("_ZN3paf4cxml6HeaderC2Ev")]
    uint CxHeaderCtor(uint me) { GuestMemory.Fill(me, 0, CxHeader); GuestMemory.WriteCString(me, "CXML", 4); U(me + 4, 0x110); return me; }

    uint CxTable(uint doc, int i) => U(doc + CxTables + (uint)i * 8);
    bool CxLoaded(uint doc) => CxTable(doc, 0) != 0;

    /// <summary>Frees the tables a read-write or file document owns (a memory document points into the caller's buffer).</summary>
    [Hle("_ZN3paf4cxml8Document5ClearEv")]
    int CxClear(uint doc)
    {
        if (U(doc) != 2)
            for (int i = 0; i < 9; i++) Free(CxTable(doc, i));
        GuestMemory.Fill(doc, 0, CxTables + 9 * 8);
        return 0;
    }

    [Hle("_ZN3paf4cxml8DocumentD1Ev")]
    [Hle("_ZN3paf4cxml8DocumentD2Ev")]
    uint CxDocDtor(uint doc) { CxClear(doc); return doc; }

    int CxCheckHeader(ReadOnlySpan<byte> h)
    {
        if (h.Length < CxHeader) return CxBadArg;
        if (h[0] != 'C' || h[1] != 'X' || h[2] != 'M' || h[3] != 'L') return CxMagic;
        uint version = BitConverter.ToUInt32(h[4..]);
        return version is 0x110 ? 0 : CxVersion;
    }

    /// <summary>(buf, size, copy): with copy the tables are copied (read-write document), without it they stay in
    /// the caller's buffer (read-only memory document).</summary>
    [Hle("_ZN3paf4cxml8Document16CreateFromBufferEPKvjb")]
    int CxFromBuffer(uint doc, uint buf, uint size, bool copy)
    {
        if (buf == 0) return CxBadArg;
        if (CxLoaded(doc)) return CxState;
        int r = CxCheckHeader(size >= CxHeader ? GuestMemory.ReadSpan(buf, (int)CxHeader) : []);
        if (r != 0) return r;
        GuestMemory.Copy(doc + 4, buf, CxHeader);
        U(doc, copy ? 0u : 2u);
        for (int i = 0; i < 9; i++)
        {
            uint off = U(doc + 0xC + (uint)i * 8), len = U(doc + 0x10 + (uint)i * 8);
            if ((ulong)off + len > size) { CxClear(doc); return CxBadArg; }
            uint p = buf + off;
            if (copy)
            {
                p = Alloc(Math.Max(len, 4));
                if (p == 0) { CxClear(doc); return CxNoMem; }
                GuestMemory.Copy(p, buf + off, len);
            }
            U(doc + CxTables + (uint)i * 8, p); U(doc + CxTables + 4 + (uint)i * 8, len);
        }
        return 0;
    }

    /// <summary>Reads <paramref name="n"/> bytes of a paf::File into guest memory through its Read slot.</summary>
    int FileRead(uint file, uint buf, uint n)
    {
        if (Lf(file) is { } s)
            return s.Fd >= 0 ? LfRead(file, buf, n) : MfRead(file, buf, n);
        if (Kernel.GuestThread.Current is not { } t) return FileNoImpl;
        return (int)t.CallGuest(U(U(file) + 10 * 4), file, buf, n);
    }

    long FileSeek(uint file, long off, int whence)
    {
        if (Lf(file) is { } s) return s.Fd >= 0 ? LfSeek(file, off, whence) : MfSeek(file, off, whence);
        if (Kernel.GuestThread.Current is not { } t) return FileNoImpl;
        return (int)t.CallGuest(U(U(file) + 14 * 4), file, 0, (uint)off, (uint)(off >> 32), (uint)whence);
    }

    /// <summary>(SharedPtr&lt;File&gt;, copy file table): every table is read into memory of its own; the file table
    /// only when asked (otherwise cxml::File::Load reads it from the file later).</summary>
    [Hle("_ZN3paf4cxml8Document14CreateFromFileENS_6common9SharedPtrINS_4FileEEEb")]
    int CxFromFile(uint doc, uint fileSp, bool copyFiles)
    {
        uint file = U(fileSp);
        if (file == 0) return CxBadArg;
        if (CxLoaded(doc)) return CxState;
        FileSeek(file, 0, 0);
        if (FileRead(file, doc + 4, CxHeader) != CxHeader) return CxBadArg;
        int r = CxCheckHeader(GuestMemory.ReadSpan(doc + 4, (int)CxHeader));
        if (r != 0) { GuestMemory.Fill(doc + 4, 0, CxHeader); return r; }
        U(doc, 1);
        for (int i = 0; i < 9; i++)
        {
            uint off = U(doc + 0xC + (uint)i * 8), len = U(doc + 0x10 + (uint)i * 8);
            if (i == 8 && !copyFiles) continue;
            uint p = Alloc(Math.Max(len, 4));
            if (p == 0) { CxClear(doc); return CxNoMem; }
            U(doc + CxTables + (uint)i * 8, p); U(doc + CxTables + 4 + (uint)i * 8, len);
            if (len == 0) continue;
            if (FileSeek(file, off, 0) < 0 || FileRead(file, p, len) != len) { CxClear(doc); return CxBadArg; }
        }
        return 0;
    }

    /// <summary>The root element (offset 0), or an empty element for an empty document.</summary>
    [Hle("_ZN3paf4cxml8Document18GetDocumentElementEv")]
    void CxRoot(uint ret, uint doc) { U(ret, doc); U(ret + 4, CxLoaded(doc) && U(doc + 0x10) >= 0x1C ? 0u : 0xFFFFFFFF); }

    [Hle("_ZNK3paf4cxml8Document14GetHeaderMagicEPc")] void CxMagicOut(uint doc, uint buf) => GuestMemory.Copy(buf, doc + 4, 4);

    bool CxValid(uint doc, int off) => doc != 0 && off >= 0 && CxLoaded(doc) && (uint)off + 0x1C <= U(doc + 0x10);
    uint CxElem(uint doc, int off) => CxTable(doc, 0) + (uint)off;
    string CxString(uint doc, uint off) => GuestMemory.ReadCString(CxTable(doc, 3) + off, 1024);

    void CxElementOut(uint ret, uint doc, uint off) { U(ret, doc); U(ret + 4, off); }

    void CxLink(uint ret, uint me, uint field)
    {
        uint doc = U(me);
        int off = (int)U(me + 4);
        CxElementOut(ret, doc, CxValid(doc, off) ? U(CxElem(doc, off) + field) : 0xFFFFFFFF);
    }

    [Hle("_ZNK3paf4cxml7Element9GetParentEv")] void CxParent(uint ret, uint me) => CxLink(ret, me, 8);
    [Hle("_ZNK3paf4cxml7Element18GetPreviousSiblingEv")] void CxPrev(uint ret, uint me) => CxLink(ret, me, 0xC);
    [Hle("_ZNK3paf4cxml7Element14GetNextSiblingEv")] void CxNext(uint ret, uint me) => CxLink(ret, me, 0x10);
    [Hle("_ZNK3paf4cxml7Element13GetFirstChildEv")] void CxFirstChild(uint ret, uint me) => CxLink(ret, me, 0x14);
    [Hle("_ZNK3paf4cxml7Element12GetLastChildEv")] void CxLastChild(uint ret, uint me) => CxLink(ret, me, 0x18);

    [Hle("_ZNK3paf4cxml7Element7GetNameEv")]
    uint CxName(uint me)
    {
        uint doc = U(me);
        int off = (int)U(me + 4);
        return CxValid(doc, off) ? CxTable(doc, 3) + U(CxElem(doc, off)) : 0;
    }

    [Hle("_ZNK3paf4cxml7Element12NumAttributeEv")]
    int CxNumAttr(uint me) => CxValid(U(me), (int)U(me + 4)) ? (int)U(CxElem(U(me), (int)U(me + 4)) + 4) : CxNoElement;

    [Hle("_ZNK3paf4cxml7Element11GetNumChildEv")]
    int CxNumChild(uint me)
    {
        uint doc = U(me);
        int off = (int)U(me + 4);
        if (!CxValid(doc, off)) return CxNoElement;
        int n = 0;
        for (int c = (int)U(CxElem(doc, off) + 0x14); CxValid(doc, c) && n < 0x100000; c = (int)U(CxElem(doc, c) + 0x10)) n++;
        return n;
    }

    [Hle("_ZNK3paf4cxml7Element12GetAttributeEiPNS0_9AttributeE")]
    int CxAttrAt(uint me, int index, uint attr)
    {
        uint doc = U(me);
        int off = (int)U(me + 4);
        if (attr == 0 || !CxValid(doc, off)) return CxNoElement;
        if (index < 0 || index >= (int)U(CxElem(doc, off) + 4)) return CxNoAttr;
        U(attr, doc); U(attr + 4, (uint)off); U(attr + 8, (uint)off + 0x1C + (uint)index * 0x10);
        return 0;
    }

    [Hle("_ZNK3paf4cxml7Element12GetAttributeEPKcPNS0_9AttributeE", Nid = 0xAD8678FB)]
    int CxAttrByName(uint me, uint name, uint attr)
    {
        uint doc = U(me);
        int off = (int)U(me + 4);
        if (attr == 0 || name == 0 || !CxValid(doc, off)) return CxNoElement;
        string want = CRead(name);
        uint e = CxElem(doc, off), n = U(e + 4);
        for (uint i = 0; i < n; i++)
        {
            uint a = e + 0x1C + i * 0x10;
            if (CxString(doc, U(a)) != want) continue;
            U(attr, doc); U(attr + 4, (uint)off); U(attr + 8, (uint)off + 0x1C + i * 0x10);
            return 0;
        }
        return CxNoAttr;
    }

    uint CxAttrRec(uint attr) => CxTable(U(attr), 0) + U(attr + 8);
    bool CxAttrOk(uint attr) => attr != 0 && U(attr) != 0 && (int)U(attr + 8) >= 0 && CxLoaded(U(attr));

    /// <summary>The attribute's data words when its type is <paramref name="type"/>.</summary>
    int CxData(uint attr, int type, out uint d0, out uint d1)
    {
        d0 = d1 = 0;
        if (!CxAttrOk(attr)) return CxNoAttr;
        uint a = CxAttrRec(attr);
        if ((int)U(a + 4) != type) return CxType;
        d0 = U(a + 8); d1 = U(a + 0xC);
        return 0;
    }

    [Hle("_ZNK3paf4cxml9Attribute7GetNameEv")] uint CxAttrName(uint attr) => CxAttrOk(attr) ? CxTable(U(attr), 3) + U(CxAttrRec(attr)) : 0;
    [Hle("_ZNK3paf4cxml9Attribute7GetTypeEv")] int CxAttrType(uint attr) => CxAttrOk(attr) ? (int)U(CxAttrRec(attr) + 4) : 0;

    [Hle("_ZNK3paf4cxml9Attribute6GetIntEPi", Nid = 0x8A684E61)]
    int CxGetInt(uint attr, uint v) { int r = CxData(attr, 1, out uint d, out _); if (r == 0 && v != 0) U(v, d); return r; }

    [Hle("_ZNK3paf4cxml9Attribute8GetFloatEPf", Nid = 0xDE684F4B)]
    int CxGetFloat(uint attr, uint v) { int r = CxData(attr, 2, out uint d, out _); if (r == 0 && v != 0) U(v, d); return r; }

    [Hle("_ZNK3paf4cxml9Attribute9GetStringEPPKcPj", Nid = 0xE2801CFE)]
    int CxGetString(uint attr, uint v, uint len)
    {
        int r = CxData(attr, 3, out uint off, out uint n);
        if (r != 0) return r;
        if (v != 0) U(v, CxTable(U(attr), 3) + off);
        if (len != 0) U(len, n);
        return 0;
    }

    /// <summary>A wide string: offset and length in 16-bit characters.</summary>
    [Hle("_ZNK3paf4cxml9Attribute10GetWStringEPPKwPj", Nid = 0x0AD6B520)]
    int CxGetWString(uint attr, uint v, uint len)
    {
        int r = CxData(attr, 4, out uint off, out uint n);
        if (r != 0) return r;
        if (v != 0) U(v, CxTable(U(attr), 4) + off * 2);
        if (len != 0) U(len, n);
        return 0;
    }

    [Hle("_ZNK3paf4cxml9Attribute7GetHashEPj", Nid = 0xBCD70379)]
    int CxGetHash(uint attr, uint v) { int r = CxData(attr, 5, out uint off, out _); if (r == 0 && v != 0) U(v, U(CxTable(U(attr), 5) + off * 4)); return r; }

    [Hle("_ZNK3paf4cxml9Attribute11GetIntArrayEPPKiPj", Nid = 0xFDF977B9)]
    int CxGetIntArray(uint attr, uint v, uint num)
    {
        int r = CxData(attr, 6, out uint off, out uint n);
        if (r != 0) return r;
        if (v != 0) U(v, CxTable(U(attr), 6) + off * 4);
        if (num != 0) U(num, n);
        return 0;
    }

    [Hle("_ZNK3paf4cxml9Attribute13GetFloatArrayEPPKfPj", Nid = 0x3EBC0341)]
    int CxGetFloatArray(uint attr, uint v, uint num)
    {
        int r = CxData(attr, 7, out uint off, out uint n);
        if (r != 0) return r;
        if (v != 0) U(v, CxTable(U(attr), 7) + off * 4);
        if (num != 0) U(num, n);
        return 0;
    }

    [Hle("_ZNK3paf4cxml9Attribute7GetFileEPNS0_4FileE", Nid = 0x86C66229)]
    int CxGetFile(uint attr, uint file)
    {
        int r = CxData(attr, 8, out uint off, out uint size);
        if (r == 0 && file != 0) { U(file, U(attr)); U(file + 4, off); U(file + 8, size); }
        return r;
    }

    /// <summary>An id attribute: the id table entry { element offset, NUL-terminated id }.</summary>
    [Hle("_ZNK3paf4cxml9Attribute5GetIDEPPKc", Nid = 0x346E532C)]
    int CxGetId(uint attr, uint v) { int r = CxData(attr, 9, out uint off, out _); if (r == 0 && v != 0) U(v, CxTable(U(attr), 1) + off + 4); return r; }

    [Hle("_ZNK3paf4cxml9Attribute8GetIDRefEPPKcPNS0_7ElementE", Nid = 0x5DE85A41)]
    int CxGetIdRef(uint attr, uint v, uint entity)
    {
        int r = CxData(attr, 10, out uint off, out _);
        if (r != 0) return r;
        uint e = CxTable(U(attr), 1) + off;
        if (v != 0) U(v, e + 4);
        if (entity != 0) CxElementOut(entity, U(attr), U(e));
        return 0;
    }

    /// <summary>An id-hash attribute: the id-hash table entry { element offset, hash }.</summary>
    [Hle("_ZNK3paf4cxml9Attribute9GetIDHashEPj", Nid = 0xB744416E)]
    int CxGetIdHash(uint attr, uint v) { int r = CxData(attr, 11, out uint off, out _); if (r == 0 && v != 0) U(v, U(CxTable(U(attr), 2) + off + 4)); return r; }

    [Hle("_ZNK3paf4cxml9Attribute12GetIDHashRefEPjPNS0_7ElementE", Nid = 0xB43C8D9F)]
    int CxGetIdHashRef(uint attr, uint v, uint entity)
    {
        int r = CxData(attr, 12, out uint off, out _);
        if (r != 0) return r;
        uint e = CxTable(U(attr), 2) + off;
        if (v != 0) U(v, U(e + 4));
        if (entity != 0) CxElementOut(entity, U(attr), U(e));
        return 0;
    }

    /// <summary>The file's bytes in the document's file table (NULL when the table was left in the file).</summary>
    [Hle("_ZNK3paf4cxml4File7GetAddrEv")]
    uint CxFileAddr(uint f) => U(f) == 0 || CxTable(U(f), 8) == 0 ? 0 : CxTable(U(f), 8) + U(f + 4);

    /// <summary>(SharedPtr&lt;File&gt;, buf, size): reads the file's bytes from the document's file (at the file table's
    /// offset); returns the bytes read.</summary>
    [Hle("_ZNK3paf4cxml4File4LoadENS_6common9SharedPtrINS_4FileEEEPvj")]
    int CxFileLoad(uint f, uint fileSp, uint buf, uint size)
    {
        uint doc = U(f), file = U(fileSp);
        if (doc == 0 || file == 0 || buf == 0) return CxBadArg;
        uint n = Math.Min(size, U(f + 8));
        if (FileSeek(file, U(doc + 0x4C) + U(f + 4), 0) < 0) return CxBadArg;
        return FileRead(file, buf, n);
    }

    /// <summary>libpaf's default table allocator (types 0..8 allocate, 9..17 free).</summary>
    [Hle("_ZN3paf4cxml16DefaultAllocatorENS0_14AllocationTypeEPvS2_jPS2_Pj")]
    void CxDefaultAllocator(int type, uint user, uint old, uint size, uint addrOut, uint sizeOut)
    {
        if (type >= 9) { Free(old); if (addrOut != 0) U(addrOut, 0); if (sizeOut != 0) U(sizeOut, 0); return; }
        uint p = Alloc(Math.Max(size, 4));
        if (p != 0 && old != 0) { GuestMemory.Copy(p, old, Math.Min(size, Cl.UsableSize(old))); Free(old); }
        if (addrOut != 0) U(addrOut, p);
        if (sizeOut != 0) U(sizeOut, p != 0 ? size : 0);
    }

    // ---- cxml::util: the element helpers
    [Hle("_ZN3paf4cxml4util16CheckElementNameERKNS0_7ElementEPKc")]
    bool CxCheckName(uint elem, uint name) => name != 0 && CxName(elem) is var n and not 0 && GuestMemory.ReadCString(n, 1024) == CRead(name);

    [Hle("_ZN3paf4cxml4util16FindChildElementERKNS0_7ElementEPKc")]
    void CxFindChild(uint ret, uint parent, uint name) => CxFind(ret, parent, name, null);

    /// <summary>The first child with the name (and, in the other forms, whose id attribute or id hash matches).</summary>
    void CxFind(uint ret, uint parent, uint name, Func<uint, uint, bool>? match)
    {
        uint doc = U(parent);
        int off = (int)U(parent + 4);
        CxElementOut(ret, doc, 0xFFFFFFFF);
        if (!CxValid(doc, off)) return;
        string want = CRead(name);
        for (int c = (int)U(CxElem(doc, off) + 0x14), guard = 0; CxValid(doc, c) && guard < 0x100000; c = (int)U(CxElem(doc, c) + 0x10), guard++)
        {
            if (CxString(doc, U(CxElem(doc, c))) != want) continue;
            if (match != null && !match(doc, (uint)c)) continue;
            CxElementOut(ret, doc, (uint)c);
            return;
        }
    }

    int CxAttrOf(uint elem, uint name, out uint attr)
    {
        attr = Scratch(0) + 0xE0;
        return CxAttrByName(elem, name, attr);
    }

    [Hle("_ZN3paf4cxml4util16FindChildElementERKNS0_7ElementEPKcS6_S6_")]
    void CxFindChildId(uint ret, uint parent, uint name, uint idName, uint idValue)
    {
        string v = CRead(idValue);
        CxFind(ret, parent, name, (doc, c) =>
        {
            uint e = Alloc(8); U(e, doc); U(e + 4, c);
            try { return idName == 0 || (CxAttrOf(e, idName, out uint a) == 0 && CxData(a, 9, out uint off, out _) == 0 && GuestMemory.ReadCString(CxTable(doc, 1) + off + 4, 1024) == v); }
            finally { Free(e); }
        });
    }

    [Hle("_ZN3paf4cxml4util16FindChildElementERKNS0_7ElementEPKcS6_j")]
    void CxFindChildHash(uint ret, uint parent, uint name, uint hashName, uint hash)
    {
        CxFind(ret, parent, name, (doc, c) =>
        {
            uint e = Alloc(8); U(e, doc); U(e + 4, c);
            try { return hashName == 0 || (CxAttrOf(e, hashName, out uint a) == 0 && CxData(a, 11, out uint off, out _) == 0 && U(CxTable(doc, 2) + off + 4) == hash); }
            finally { Free(e); }
        });
    }

    /// <summary>The first attribute of type none (an unused slot), 0x80AF050A when every slot is used.</summary>
    [Hle("_ZN3paf4cxml4util18FindEmptyAttributeERKNS0_7ElementEPNS0_9AttributeE")]
    int CxFindEmpty(uint elem, uint attr)
    {
        int n = CxNumAttr(elem);
        if (n < 0) return n;
        for (int i = 0; i < n; i++)
            if (CxAttrAt(elem, i, attr) == 0 && CxAttrType(attr) == 0) return 0;
        return CxNoAttr;
    }

    [Hle("_ZN3paf4cxml4util6GetIntERKNS0_7ElementEPKcPi", Nid = 0xE59C9265)]
    int CxUtilInt(uint elem, uint name, uint v) => CxAttrOf(elem, name, out uint a) is var r and not 0 ? r : CxGetInt(a, v);
    [Hle("_ZN3paf4cxml4util8GetFloatERKNS0_7ElementEPKcPf", Nid = 0xBE772869)]
    int CxUtilFloat(uint elem, uint name, uint v) => CxAttrOf(elem, name, out uint a) is var r and not 0 ? r : CxGetFloat(a, v);
    [Hle("_ZN3paf4cxml4util9GetStringERKNS0_7ElementEPKcPS6_Pj", Nid = 0xD2438FA7)]
    int CxUtilString(uint elem, uint name, uint v, uint len) => CxAttrOf(elem, name, out uint a) is var r and not 0 ? r : CxGetString(a, v, len);
    [Hle("_ZN3paf4cxml4util10GetWStringERKNS0_7ElementEPKcPPKwPj", Nid = 0x1546336D)]
    int CxUtilWString(uint elem, uint name, uint v, uint len) => CxAttrOf(elem, name, out uint a) is var r and not 0 ? r : CxGetWString(a, v, len);
    [Hle("_ZN3paf4cxml4util7GetHashERKNS0_7ElementEPKcPj", Nid = 0x15ADAAB4)]
    int CxUtilHash(uint elem, uint name, uint v) => CxAttrOf(elem, name, out uint a) is var r and not 0 ? r : CxGetHash(a, v);
    [Hle("_ZN3paf4cxml4util11GetIntArrayERKNS0_7ElementEPKcPPKiPj", Nid = 0xD09CE709)]
    int CxUtilIntArray(uint elem, uint name, uint v, uint n) => CxAttrOf(elem, name, out uint a) is var r and not 0 ? r : CxGetIntArray(a, v, n);
    [Hle("_ZN3paf4cxml4util13GetFloatArrayERKNS0_7ElementEPKcPPKfPj", Nid = 0x7B360965)]
    int CxUtilFloatArray(uint elem, uint name, uint v, uint n) => CxAttrOf(elem, name, out uint a) is var r and not 0 ? r : CxGetFloatArray(a, v, n);
    [Hle("_ZN3paf4cxml4util7GetFileERKNS0_7ElementEPKcPNS0_4FileE", Nid = 0x37720983)]
    int CxUtilFile(uint elem, uint name, uint f) => CxAttrOf(elem, name, out uint a) is var r and not 0 ? r : CxGetFile(a, f);
    [Hle("_ZN3paf4cxml4util5GetIDERKNS0_7ElementEPKcPS6_", Nid = 0x445BCAAB)]
    int CxUtilId(uint elem, uint name, uint v) => CxAttrOf(elem, name, out uint a) is var r and not 0 ? r : CxGetId(a, v);
    [Hle("_ZN3paf4cxml4util8GetIDRefERKNS0_7ElementEPKcPS6_PS2_", Nid = 0xBD3A6015)]
    int CxUtilIdRef(uint elem, uint name, uint v, uint ent) => CxAttrOf(elem, name, out uint a) is var r and not 0 ? r : CxGetIdRef(a, v, ent);
    [Hle("_ZN3paf4cxml4util9GetIDHashERKNS0_7ElementEPKcPj")]
    int CxUtilIdHash(uint elem, uint name, uint v) => CxAttrOf(elem, name, out uint a) is var r and not 0 ? r : CxGetIdHash(a, v);
    [Hle("_ZN3paf4cxml4util12GetIDHashRefERKNS0_7ElementEPKcPjPS2_", Nid = 0xABFA169F)]
    int CxUtilIdHashRef(uint elem, uint name, uint v, uint ent) => CxAttrOf(elem, name, out uint a) is var r and not 0 ? r : CxGetIdHashRef(a, v, ent);

    /// <summary>Whether an element owns the id (string) or id hash.</summary>
    [Hle("_ZN3paf4cxml8Document14IsRegisteredIDEPKc")]
    bool CxIsId(uint doc, uint id)
    {
        if (!CxLoaded(doc) || id == 0) return false;
        string want = CRead(id);
        uint t = CxTable(doc, 1), n = U(doc + 0x18);
        for (uint p = 0; p + 4 < n;)
        {
            string s = GuestMemory.ReadCString(t + p + 4, 1024);
            if (s == want) return true;
            p += 4 + (uint)System.Text.Encoding.UTF8.GetByteCount(s) + 1;
        }
        return false;
    }

    [Hle("_ZN3paf4cxml8Document18IsRegisteredIDHashEj")]
    bool CxIsIdHash(uint doc, uint hash)
    {
        if (!CxLoaded(doc)) return false;
        uint t = CxTable(doc, 2), n = U(doc + 0x20);
        for (uint p = 0; p + 8 <= n; p += 8) if (U(t + p + 4) == hash) return true;
        return false;
    }
}
