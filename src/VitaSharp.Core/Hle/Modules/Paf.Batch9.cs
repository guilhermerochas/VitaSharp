namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// More of libpaf's members (FW 3.74), the methods that only read and write their own object, read in the module:
/// widget and text box state, scroll views, rich text items, busy indicators, surfaces (filters, wrap modes, pages,
/// reference counts), the blend state of the ARL renderer, text layouts and styles, images (extents, clipping),
/// property lists, plugin and page parameters, timers, the gesture helpers and the std::map lookups (red-black
/// trees whose header is also the nil node: +4 left, +8 right, +0xC key, +0x10 value, nil flag +0x15).
/// </summary>
public sealed partial class Paf
{
    const uint PafNotReady = 0x80AF2303, PafBadAttr = 0x80AF2302, PafBadFilter = 0x80AF2202, PafNullPtr = 0x80AF2206,
        PafNoData = 0x80AF0022, PafNotFound = 0x80AF008F;

    static int I(uint a) => (int)U(a);
    static short S16(uint a) => (short)H(a);

    /// <summary>A std::map&lt;key, value&gt; find: the value of the node with the key, or <paramref name="none"/>.</summary>
    static uint MapFind(uint header, uint key, bool signedKey, uint none, uint keyOffset = 0xC, uint valueOffset = 0x10, uint nilOffset = 0x15)
    {
        if (header == 0) return none;
        uint n = U(header);
        while (B(n + nilOffset) == 0)
        {
            uint k = U(n + keyOffset);
            bool less = signedKey ? (int)key < (int)k : key < k;
            bool greater = signedKey ? (int)key > (int)k : key > k;
            if (less) n = U(n + 4);
            else if (greater) n = U(n + 8);
            else break;
        }
        return n == header ? none : U(n + valueOffset);
    }

    // ---- cache, timers, http

    [Hle("_ZN3paf5cache9CacheBase23IsEnableReleaseInstanceEv")] bool CacheIsEnableRelease(uint me) => I(me + 0x10) < 2;

    [Hle("_ZN3paf5Timer10ResetTimerEv")]
    void TimerReset(uint me)
    {
        U(me + 0x18, 0);
        U(me + 0x10, U(me + 0x10) & 0xFFFF0FFF);
        U(me + 0xC, 0);
        B(me + 0x12, (byte)(B(me + 0x12) & 0xFE));
    }

    [Hle("_ZN3paf17TimerListenerList13ListenerParamC1EPNS_13TimerListenerEbPvS4_")]
    uint TimerListenerParamCtor(uint me, uint listener, bool once, uint a, uint b)
    {
        U(me, listener); U(me + 4, a); U(me + 8, b);
        B(me + 0xC, (byte)((B(me + 0xC) & 0xFE) | (once ? 1 : 0)));
        return me;
    }

    [Hle("_ZN3paf4http11TransactionC1ERNS0_6ClientE")]
    uint HttpTransactionCtor(uint me, uint client)
    {
        U(me, client); U(me + 4, 0); U(me + 8, uint.MaxValue); U(me + 0xC, uint.MaxValue);
        U(me + 0x18, 0); U(me + 0x10, 0x3F); U(me + 0x14, EmptyString);
        return me;
    }

    // ---- plugin and page parameters

    [Hle("_ZN3paf6Plugin14PageCloseParamC1Ev")]
    uint PageCloseParamCtor(uint me) { B(me, 0); U(me + 0xC, 0); F(me + 4, 200f); U(me + 8, 0); return me; }

    [Hle("_ZN3paf6Plugin13PageOpenParamC1Ev")]
    uint PageOpenParamCtor(uint me)
    {
        B(me + 0x14, 0);
        U(me, 0); U(me + 4, 0); U(me + 8, uint.MaxValue); U(me + 0xC, 0); U(me + 0x10, 0);
        F(me + 0x18, 200f); U(me + 0x1C, 0); U(me + 0x20, 0); U(me + 0x24, 0); U(me + 0x28, 0x80);
        return me;
    }

    /// <summary>Plugin::Argument (+0 type: 0 int, 1 float, 2 other; +4 value) as an int.</summary>
    [Hle("_ZNK3paf6Plugin8ArgumentcviEv")]
    int PluginArgumentToInt(uint me) => I(me) switch { 0 => I(me + 4), 1 => (int)F(me + 4), _ => 0 };

    [Hle("_ZNK3paf6Plugin12GetInterfaceEi")] uint PluginGetInterface(uint me, int id) => MapFind(U(me + 0x58), (uint)id, true, 0);
    [Hle("_ZNK3paf6Module12GetInterfaceEi")] uint ModuleGetInterface(uint me, int id) => MapFind(U(U(me) + 0x10), (uint)id, true, 0);

    [Hle("_ZNK3paf9Framework23GetCommonResourceLocaleEv")]
    uint FrameworkCommonResourceLocale(uint me) => U(me + 0x1C) == 0 ? U(me + 0x5C) : U(U(me + 0x1C) + 0x10C);

    [Hle("_ZN3paf2ui11WidgetParam3GetEv")] int WidgetParamGet(uint me) => U(me + 0x20) == 0 ? -1 : 0;

    // ---- property lists: entries of {value, extra}

    [Hle("_ZNK3paf6common13property_list12PropertyList8GetValueEj")]
    uint PropertyListGetValueAt(uint ret, uint me, uint index)
    {
        if (index < U(me + 0x20)) { uint e = U(me + 0x1C) + index * 8; U(ret, U(e)); U(ret + 4, U(e + 4)); }
        else { U(ret, 0); U(ret + 4, 0); }
        return ret;
    }

    /// <summary>By IDParam (its hash at +0xC): a map keyed at +0x1C with the 8-byte value at +0x20, nil flag +0x29.</summary>
    [Hle("_ZNK3paf6common13property_list12PropertyList8GetValueERKNS_7IDParamE")]
    uint PropertyListGetValueById(uint ret, uint me, uint id)
    {
        uint header = U(me + 0x2C), key = U(id + 0xC), n = header == 0 ? 0 : U(header);
        if (n != 0)
            while (B(n + 0x29) == 0)
            {
                uint k = U(n + 0x1C);
                if (key < k) n = U(n + 4);
                else if (key > k) n = U(n + 8);
                else break;
            }
        if (n == header) { U(ret, 0); U(ret + 4, 0); }
        else { U(ret, U(n + 0x20)); U(ret + 4, U(n + 0x24)); }
        return ret;
    }

    // ---- widgets

    [Hle("_ZNK3paf2ui6Widget9GetCameraEv")] uint WidgetGetCamera(uint me) => U(me + 0x16C) == 0 ? 0 : U(U(me + 0x16C) + 0x280);

    /// <summary>The draw object with an id (bits 3..10 of an entry's flags; entries of 12 bytes at +0x24C, count +0x250).</summary>
    [Hle("_ZN3paf2ui6Widget10GetDrawObjEi")]
    uint WidgetGetDrawObj(uint me, uint id)
    {
        uint p = U(me + 0x24C), end = p + U(me + 0x250) * 12;
        for (; p != end; p += 12) if (((U(p + 8) & 0x7FF) >> 3) == id) return U(p);
        return 0;
    }

    /// <summary>(iterator *ret, me, id): an iterator at the draw object entry with the id (the end when none).</summary>
    [Hle("NID_8A13BDF2", Nid = 0x8A13BDF2, Library = "ScePafWidget")]
    void WidgetFindDrawObjEntry(uint ret, uint me, uint id)
    {
        uint p = U(me + 0x24C), end = p + U(me + 0x250) * 12;
        while (p != end && ((U(p + 8) & 0x7FF) >> 3) != id) p += 12;
        U(ret, p);
    }

    /// <summary>Whether a draw object marked for preparation (+8 bit 0) still needs it (its +0x34 bit 4).</summary>
    [Hle("_ZN3paf2ui6Widget13IsPrepareObjsEv")]
    int WidgetIsPrepareObjs(uint me)
    {
        uint p = U(me + 0x24C), end = p + U(me + 0x250) * 12;
        for (; p != end; p += 12) if ((B(p + 8) & 1) != 0 && (B(U(p) + 0x34) & 0x10) != 0) return 1;
        return 0;
    }

    [Hle("_ZN3paf2ui16WidgetStyleParam15GetDrawObjParamEi")]
    uint WidgetStyleGetDrawObjParam(uint me, int id)
    {
        uint p = U(me + 0x2C), end = p + U(me + 0x30) * 4;
        for (; p != end; p += 4) if (I(U(p) + 0x24) == id) return U(p);
        return 0;
    }

    /// <summary>(me, child, mode): the child when it hangs under this widget (mode 2: directly), else NULL; a widget
    /// that is being destroyed (+0x24 &gt; 0) is never found.</summary>
    [Hle("_ZN3paf2ui6Widget9FindChildEPS1_i")]
    uint WidgetFindChild(uint me, uint child, int mode)
    {
        if (child == 0 || I(child + 0x24) > 0) return 0;
        if (mode == 2) return U(child + 0x168) == me ? child : 0;
        for (uint w = child; w != 0; w = U(w + 0x168)) if (w == me) return child;
        return 0;
    }

    /// <summary>The n-th child (a circular list at +0x170, count +0x174; negative n counts from the end).</summary>
    [Hle("_ZNK3paf2ui6Widget8GetChildEi")]
    uint WidgetGetChild(uint me, int n)
    {
        if ((uint)n >= U(me + 0x174)) return 0;
        uint node = U(me + 0x170) == 0 ? 0 : U(U(me + 0x170) + 4);
        for (; n > 0; n--) if (node != 0) node = U(node + 4);
        return U(node + 8);
    }

    /// <summary>Marks the widget for update (1: itself and its tree, 2: the tree).</summary>
    [Hle("_ZN3paf2ui6Widget14SetUpdateParamEj")]
    void WidgetSetUpdateParam(uint me, uint what)
    {
        byte f = B(me + 0x183);
        if ((what & f) != 0) return;
        if (what == 1) { B(me + 0x183, (byte)(f | 1)); WidgetSetUpdateParam(me, 2); }
        else if (what == 2)
        {
            B(me + 0x183, (byte)(f | 2));
            uint head = U(me + 0x170), n = head == 0 ? 0 : U(head + 4);
            while (n != head) { WidgetSetUpdateParam(U(n + 8), 2); n = n == 0 ? 0 : U(n + 4); }
        }
    }

    /// <summary>(float alpha, me): the widget's own alpha (clamped to 1; negative keeps it) times its parent's
    /// effective alpha, then the same for every child.</summary>
    [Hle("_ZN3paf2ui6Widget17SetMetaAlpha_coreEf")]
    int WidgetSetMetaAlpha(float alpha, uint me)
    {
        float own = F(me + 0x1A4);
        if (alpha >= 0)
        {
            own = alpha;
            F(me + 0x1A4, own);
            if (alpha > 1) { F(me + 0x1A4, 1f); own = 1f; }
        }
        float parent = U(me + 0x168) != 0 ? F(U(me + 0x168) + 0x1A8) : 1f;
        F(me + 0x1A8, own * parent);
        if (U(me + 0x174) != 0)
        {
            uint head = U(me + 0x170), n = head == 0 ? 0 : U(head + 4);
            while (n != head) { WidgetSetMetaAlpha(-1f, U(n + 8)); n = n == 0 ? 0 : U(n + 4); }
        }
        return 0;
    }

    /// <summary>The nearest widget up the tree (stopping at the root +0x16C) that has a focus holder (+0x234).</summary>
    [Hle("NID_CFA1EB7A", Nid = 0xCFA1EB7A, Library = "ScePafWidget")]
    uint WidgetFindFocusOwner(uint me)
    {
        for (uint w = me; w != 0; w = U(w + 0x168))
        {
            if (U(w + 0x234) != 0) return w;
            if (U(me + 0x16C) == w) return 0;
        }
        return 0;
    }

    /// <summary>(me, bool on): makes the widget its focus holder's current entry (and its first one when unset), or
    /// drops it.</summary>
    [Hle("NID_EC6175D6", Nid = 0xEC6175D6, Library = "ScePafWidget")]
    void WidgetSetFocusEntry(uint me, bool on)
    {
        if (me == 0) return;
        uint w = me, h;
        while ((h = U(w + 0x234)) == 0)
        {
            if (U(me + 0x16C) == w) return;
            w = U(w + 0x168);
            if (w == 0) return;
        }
        if (!on) { if (U(h + 4) == me) U(h + 4, 0); }
        else { U(h + 4, me); if (U(h) == 0) U(h, me); }
    }

    /// <summary>Whether the widget is visible enough to take input (shown, mostly opaque, not disabled).</summary>
    [Hle("NID_DB0FFD33", Nid = 0xDB0FFD33, Library = "ScePafWidget")]
    int WidgetIsInteractive(uint w)
    {
        if (w == 0) return 0;
        bool shown = (B(w + 0x192) & 0x80) != 0
            || ((B(w + 0x194) & 0x10) != 0 && F(w + 0x1A0) >= 0.95f && (B(w + 0x195) & 0x38) == 0 && F(w + 0x1A8) >= 0.5f);
        return shown && (B(w + 0x196) & 1) == 0 && (B(w + 0x195) & 0x80) == 0 ? 1 : 0;
    }

    [Hle("NID_730AA895", Nid = 0x730AA895, Library = "ScePafWidget")]
    void WidgetSetState198(uint me, uint v, bool notify)
    {
        if (U(me + 0x198) == v) return;
        U(me + 0x198, v);
        if (!notify) return;
        B(me + 500, (byte)(v == 0 ? 0 : 1));
    }

    [Hle("NID_CEBDCDB3", Nid = 0xCEBDCDB3, Library = "ScePafWidget")]
    int WidgetMatchesId(uint me, uint p) => p != 0 && U(me + 0x27C) == (U(p + 8) & 0xFF) ? 1 : 0;

    [Hle("NID_3546C104", Nid = 0x3546C104, Library = "ScePafWidget")]
    uint WidgetFlags354(uint me, bool masked) => masked ? U(me + 0x354) & ~U(me + 0x360) : U(me + 0x354);

    [Hle("NID_3EB376F2", Nid = 0x3EB376F2, Library = "ScePafWidget")] int WidgetSet308(uint me, uint v) { U(me + 0x308, v); return 0; }
    [Hle("NID_93C21B53", Nid = 0x93C21B53, Library = "ScePafWidget")] void WidgetSet3D4(uint v, uint me) => U(me + 0x3D4, v);
    [Hle("NID_E9C7617A", Nid = 0xE9C7617A, Library = "ScePafWidget")] void WidgetSet27C(uint me, uint v) => U(me + 0x27C, v);
    [Hle("NID_FE7FE5A0", Nid = 0xFE7FE5A0, Library = "ScePafWidget")] void WidgetSet360(uint me, uint v) => U(me + 0x360, v);
    [Hle("NID_72A7F51A", Nid = 0x72A7F51A, Library = "ScePafWidget")] void WidgetSet2AC(uint me, uint a, uint b) { U(me + 0x2AC, a); U(me + 0x2B0, b); }
    [Hle("NID_A50C6B30", Nid = 0xA50C6B30, Library = "ScePafWidget")] void WidgetSet2A4(uint me, uint a, uint b) { U(me + 0x2A4, a); U(me + 0x2A8, b); }
    [Hle("NID_FFD97FCD", Nid = 0xFFD97FCD, Library = "ScePafWidget")] void WidgetSet410(uint me, uint v4) { Q(me + 0x410, Q(v4)); Q(me + 0x418, Q(v4 + 8)); }
    [Hle("NID_0DCB8EA5", Nid = 0x0DCB8EA5, Library = "ScePafWidget")] void WidgetSet3D0(float v, uint me) => F(me + 0x3D0, v < 0 ? 200f : v);

    /// <summary>(float v, me, uint index &lt; 8): one of eight values at +0x484; -1 out of range.</summary>
    [Hle("NID_6A60296B", Nid = 0x6A60296B, Library = "ScePafWidget")]
    int WidgetSetSlot(uint v, uint me, uint index) { if (index >= 8) return -1; U(me + index * 4 + 0x484, v); return 0; }

    [Hle("NID_3C06E3D4", Nid = 0x3C06E3D4, Library = "ScePafWidget")]
    uint WidgetGetSize2(uint me, uint outp) { uint p = U(me + 700); if (p == 0) return PafNoData; Q(outp, Q(p + 0x30)); return 0; }

    /// <summary>The entry (0x38 bytes, key +4) of the widget's table at +0x278 (count +0x277).</summary>
    [Hle("NID_6811A0CF", Nid = 0x6811A0CF, Library = "ScePafWidget")]
    uint WidgetFindEntry(uint me, uint key)
    {
        uint t = U(me + 0x278);
        if (t == 0) return 0;
        for (uint i = 0; i < B(me + 0x277); i++) if (U(t + i * 0x38 + 4) == key) return t + i * 0x38;
        return 0;
    }

    /// <summary>(me, key): the 0x40-byte entry with the key in the array {begin, count} at +4.</summary>
    [Hle("NID_CDC90BA2", Nid = 0xCDC90BA2, Library = "ScePafWidget")]
    uint FindEntry40(uint me, uint key)
    {
        uint a = U(me + 4), p = U(a), end = p + U(a + 4) * 0x40;
        for (; p != end; p += 0x40) if (U(p) == key) return p;
        return 0;
    }

    [Hle("_ZN3paf2ui3Box18AddToExclusiveListEPNS0_6WidgetE")] void BoxAddExclusive(uint me, uint w) => BoxExclusive(me, w, true);
    [Hle("_ZN3paf2ui3Box23DeleteFromExclusiveListEPNS0_6WidgetE")] void BoxDeleteExclusive(uint me, uint w) => BoxExclusive(me, w, false);

    void BoxExclusive(uint me, uint w, bool add)
    {
        bool member = (B(w + 0x194) & 1) != 0;
        if (member == add) return;
        B(w + 0x194, (byte)(add ? B(w + 0x194) | 1 : B(w + 0x194) & 0xFE));
        uint layout = U(me + 0x214);
        if (layout != 0) B(layout + 0xC, (byte)(B(layout + 0xC) | 1));
    }

    [Hle("_ZN3paf2ui10ButtonBase13SetButtonModeEj")]
    void ButtonSetMode(uint me, uint mode)
    {
        if (U(me + 0x284) == mode) return;
        U(me + 0x284, mode);
        B(me + 0x195, (byte)(mode == 2 ? B(me + 0x195) & 0xFE : B(me + 0x195) | 1));
    }

    [Hle("_ZN3paf2ui8ListItem12GetCellIndexEv")] int ListItemCellIndex(uint me) => U(me + 0x27C) == 0 ? -1 : I(U(me + 0x27C) + 4);

    [Hle("_ZN3paf2ui8filelist10CellSource13GetVisualSizeEv")]
    uint CellSourceVisualSize(uint ret) { for (uint i = 0; i < 16; i += 4) F(ret + i, 64f); return ret; }

    // ---- text boxes

    [Hle("_ZN3paf2ui7TextBox9IsEditingEv")] bool TextBoxIsEditing(uint me) => (B(me + 0x289) & 4) != 0;
    [Hle("_ZN3paf2ui7TextBox11GetEditModeERb")] int TextBoxGetEditMode(uint me, uint outp) { B(outp, (byte)((B(me + 0x288) & 8) != 0 ? 1 : 0)); return 0; }
    [Hle("_ZN3paf2ui7TextBox25SetEditAutoTransitionModeEb")] int TextBoxSetAutoTransition(uint me, bool v) { B(me + 0x304, (byte)(v ? 1 : 0)); return 0; }

    /// <summary>TextBox::ImeParam: +4 → +0x2E8, +8 (byte) → +0x2EC, +0xC → +0x2F0, +0x10..+0x18 → +0x2F4..+0x2FC.</summary>
    [Hle("_ZN3paf2ui7TextBox11SetImeParamERKNS1_8ImeParamE")]
    int TextBoxSetImeParam(uint me, uint p)
    {
        U(me + 0x2E8, U(p + 4)); B(me + 0x2EC, B(p + 8)); U(me + 0x2F0, U(p + 0xC));
        U(me + 0x2F4, U(p + 0x10)); U(me + 0x2F8, U(p + 0x14)); U(me + 0x2FC, U(p + 0x18));
        return 0;
    }

    [Hle("_ZN3paf2ui7TextBox11GetImeParamERNS1_8ImeParamE")]
    int TextBoxGetImeParam(uint me, uint p)
    {
        U(p + 4, U(me + 0x2E8)); B(p + 8, B(me + 0x2EC)); U(p + 0xC, U(me + 0x2F0));
        U(p + 0x10, U(me + 0x2F4)); U(p + 0x14, U(me + 0x2F8)); U(p + 0x18, U(me + 0x2FC));
        return 0;
    }

    // ---- rich text: items {.., +4 type, .., +0xC widget} at +0x424 (count +0x428); images of 0x20 bytes at +0x528

    [Hle("_ZN3paf2ui8RichText13GetItemWidgetEi")]
    uint RichTextItemWidget(uint me, int i) => i < 0 || i >= I(me + 0x428) ? 0 : U(U(U(me + 0x424) + (uint)i * 4) + 0xC);

    [Hle("_ZNK3paf2ui8RichText11GetItemTypeEi")]
    int RichTextItemType(uint me, int i)
    {
        if (i < 0 || i >= I(me + 0x428)) return 0;
        int t = I(U(U(me + 0x424) + (uint)i * 4) + 4);
        return t == 3 ? 1 : t;
    }

    [Hle("_ZN3paf2ui8RichText16GetHtmlImageSizeEiRNS_4math2v4E")]
    int RichTextHtmlImageSize(uint me, int i, uint outp)
    {
        if (i < 0 || i >= I(me + 0x52C)) return -1;
        Q(outp, Q(U(me + 0x528) + (uint)i * 0x20 + 0x18));
        Q(outp + 8, 0);
        return 0;
    }

    // ---- scroll views and the busy indicator

    [Hle("_ZN3paf2ui14ScrollViewBase12SetSnapPowerEf")] void ScrollSetSnapPower(float v, uint me) => F(me + 0x3C0, v > 0 ? v : 0f);

    [Hle("_ZN3paf2ui14ScrollViewBase14OnDestroyTimerEPNS0_15ScrollViewTimerE")]
    void ScrollOnDestroyTimer(uint me, uint timer)
    {
        if (U(me + 0x398) != timer) return;
        U(me + 0x398, 0);
        if (B(timer + 0x24) != 0) return;
        uint layout = U(me + 0x214);
        if (layout != 0) B(layout + 0xC, (byte)(B(layout + 0xC) | 1));
    }

    /// <summary>(float pos, me, int id, int extra): the snap point with the id (a list at +0x3B4) becomes the
    /// preferred one; -1 or an unknown id clears it.</summary>
    [Hle("_ZN3paf2ui14ScrollViewBase21SetPreferredSnapPointEifi")]
    void ScrollSetPreferredSnap(float pos, uint me, int id, uint extra)
    {
        uint head = U(me + 0x3B4), found = head;
        if (id != -1)
            for (uint n = head == 0 ? 0 : U(head + 4); n != head; n = n == 0 ? 0 : U(n + 4))
                if (I(n + 0x10) == id) { found = n; break; }
        int chosen = -1;
        if (found != head) { F(me + 0x3C8, pos); U(me + 0x3CC, extra); chosen = id; }
        U(me + 0x3C4, (uint)chosen);
    }

    [Hle("_ZN3paf2ui10ScrollView9IsIgnoredEPNS0_6WidgetE")]
    bool ScrollIsIgnored(uint me, uint w)
    {
        uint p = U(me + 0x408), end = p + U(me + 0x40C) * 4;
        for (; p != end; p += 4) if (U(p) == w) return true;
        return false;
    }

    [Hle("_ZN3paf2ui10ScrollView9GetSnapIDEPNS0_6WidgetE")] int ScrollGetSnapId(uint me, uint w) => (int)MapFind(U(me + 0x3FC), w, false, uint.MaxValue);

    [Hle("_ZN3paf2ui13BusyIndicator11SetBallSizeEf")]
    void BusySetBallSize(float v, uint me) { if (v > 0) { F(me + 0x2A0, v); U(me + 0x290, 0x3F20D97C); } }

    [Hle("_ZN3paf2ui13BusyIndicator4StopEv")]
    void BusyStop(uint me)
    {
        uint state = U(me + 0x27C);
        if (state == 1) { U(me + 0x27C, 3); F(me + 0x298, (1f - F(me + 0x298) / 0.3f) * 0.5f); }
        else if (state == 2) { U(me + 0x27C, 3); F(me + 0x298, 0); }
    }

    // ---- surfaces

    [Hle("_ZN3paf5graph7Surface6AddRefEv")]
    [Hle("_ZN3paf5graph11SurfaceCLUT6AddRefEv")]
    uint SurfaceAddRef(uint me) { U(me + 0x18, U(me + 0x18) + 1); return U(me + 0x18); }

    [Hle("NID_A704EEE6", Nid = 0xA704EEE6, Library = "ScePafGraphics")]
    int AddRefC(uint me) { U(me + 0xC, U(me + 0xC) + 1); return I(me + 0xC); }

    [Hle("_ZN3paf5graph7Surface12SetMagFilterENS0_13TextureFilterE")]
    uint SurfaceSetMagFilter(uint me, int f)
    {
        int v = f == 0 ? 0 : f == 1 ? 1 : -1;
        if (v == I(me + 0x48)) return 0;
        if (v == -1) return PafBadFilter;
        U(me + 0x48, (uint)v); B(me + 0x60, 1);
        return 0;
    }

    [Hle("_ZN3paf5graph7Surface12SetMinFilterENS0_13TextureFilterE")]
    uint SurfaceSetMinFilter(uint me, uint f)
    {
        (int lin, int mip) = f switch { 0 or 2 => (0, 0), 1 or 3 => (1, 0), 4 => (0, 0x200), 5 => (1, 0x200), _ => (-1, -1) };
        if (lin == -1) return PafBadFilter;
        if (lin != I(me + 0x4C) || mip != I(me + 0x50)) { U(me + 0x4C, (uint)lin); U(me + 0x50, (uint)mip); B(me + 0x60, 1); }
        return 0;
    }

    /// <summary>TextureWrap 0..9 to the GXM wrap mode (0 → 2, 1 → 0, 2 → 2, 4 → 1, 5 → 3, 6 → 4, 7 → 6, 8 → 5, 9 → 7).</summary>
    static int WrapMode(int w) => w switch { 0 => 2, 1 => 0, 2 => 2, 4 => 1, 5 => 3, 6 => 4, 7 => 6, 8 => 5, 9 => 7, _ => -1 };

    [Hle("_ZN3paf5graph7Surface8SetWrapSENS0_11TextureWrapE")] uint SurfaceSetWrapS(uint me, int w) => SetWrap(me, 0x54, w);
    [Hle("_ZN3paf5graph7Surface8SetWrapTENS0_11TextureWrapE")] uint SurfaceSetWrapT(uint me, int w) => SetWrap(me, 0x58, w);

    uint SetWrap(uint me, uint off, int w)
    {
        int v = WrapMode(w);
        if (v == -1) return PafBadFilter;
        if (v != I(me + off)) { U(me + off, (uint)v); B(me + 0x60, 1); }
        return 0;
    }

    /// <summary>The current page (+0x66) one back (wrapping to the last of +0x64 pages).</summary>
    [Hle("NID_B000FA14", Nid = 0xB000FA14, Library = "ScePafGraphics")]
    int SurfacePreviousPage(uint me) { int i = S16(me + 0x66) - 1; return i < 0 ? S16(me + 100) - 1 : i; }

    [Hle("_ZN3paf5graph7Surface12GetPageIndexENS0_15FrameBufferTypeEPi")]
    uint SurfaceGetPageIndex(uint me, int type, uint outp)
    {
        if (outp == 0) return PafNullPtr;
        if (type == 0) U(outp, (uint)SurfacePreviousPage(me));
        else if (type == 1) U(outp, (uint)(int)S16(me + 0x66));
        return 0;
    }

    [Hle("_ZN3paf5graph7Surface13GetRawTextureEv")]
    uint SurfaceRawTexture(uint me)
    {
        if (U(me + 0x44) == 0) return 0;
        int i = S16(me + 0x66);
        if (S16(me + 0x68) != 0)
        {
            i += S16(me + 0x68);
            if (i < 0) i = S16(me + 100) - 1;
            else if (S16(me + 100) < i) i = 0;
        }
        return U(U(me + 0x44) + (uint)i * 8);
    }

    // ---- the ARL renderer's blend state (dirty flag +0x788)

    [Hle("_ZN3paf5graph3arl11ArlInstance13BlendEquationENS1_17BlendEquationEnumES3_")]
    void ArlBlendEquation(uint me, uint rgb, uint alpha)
    {
        byte cur = B(me + 0x78B);
        if ((cur & 0xF) == rgb && cur >> 4 == alpha) return;
        B(me + 0x788, 1);
        B(me + 0x78B, (byte)((rgb & 0xF) | ((alpha & 0xF) << 4)));
    }

    [Hle("_ZN3paf5graph3arl11ArlInstance9BlendFuncENS1_14BlendingFactorES3_S3_S3_")]
    void ArlBlendFunc(uint me, uint srcRgb, uint dstRgb, uint srcA, uint dstA)
    {
        byte a = B(me + 0x78C), b = B(me + 0x78D);
        if ((a & 0xF) == srcRgb && (b & 0xF) == srcA && a >> 4 == dstRgb && b >> 4 == dstA) return;
        B(me + 0x788, 1);
        B(me + 0x78C, (byte)((srcRgb & 0xF) | ((dstRgb & 0xF) << 4)));
        B(me + 0x78D, (byte)((srcA & 0xF) | ((dstA & 0xF) << 4)));
    }

    [Hle("_ZN3paf5graph3arl19ShaderParameterBaseC2ERNS1_24ShaderParameterSetupDataE")]
    uint ShaderParameterBaseCtor(uint me, uint setup) { U(me + 8, setup); U(me, 0); U(me + 4, 0); U(me + 0xC, 0xFFFFFFFE); return me; }

    // ---- text layout and styles

    [Hle("_ZN3paf5graph10TextLayout12GetLineCountERj")]
    uint TextLayoutLineCount(uint me, uint outp) { if ((H(me + 0x70) & 0x11) != 0) return PafNotReady; U(outp, U(me + 0x80)); return 0; }

    [Hle("_ZN3paf5graph10TextLayout9GetBoundsERNS_4math2v2E")]
    uint TextLayoutBounds(uint me, uint outp) { if ((H(me + 0x70) & 0x22) != 0) return PafNotReady; Q(outp, Q(me + 0x68)); return 0; }

    [Hle("_ZN3paf5graph10TextLayout18GetLayoutAttributeENS0_19TextLayoutAttributeERNS_4math2v2E")]
    uint TextLayoutAttribute(uint me, int attr, uint outp)
    {
        uint off = attr switch { 6 => 0x1C, 7 => 0x28, 8 => 0x30, _ => 0 };
        if (off == 0) return PafBadAttr;
        Q(outp, Q(me + off));
        return 0;
    }

    /// <summary>(float v, me, attr): attribute 1 (the size) sets both halves of +8.</summary>
    [Hle("_ZN3paf5graph9TextStyle17SetStyleAttributeENS0_18TextStyleAttributeEf")]
    uint TextStyleSetFloat(float v, uint me, int attr) { if (attr != 1) return PafBadAttr; F(me + 8, v); F(me + 0xC, v); return 0; }

    /// <summary>(me, attr 7..15, bool): the style's flag bits at +6 (bit attr - 7).</summary>
    [Hle("_ZN3paf5graph9TextStyle17SetStyleAttributeENS0_18TextStyleAttributeEb")]
    uint TextStyleSetBool(uint me, int attr, bool on)
    {
        if ((uint)(attr - 7) >= 9) return 0;
        ushort bit = (ushort)(1 << (attr - 7)), v = H(me + 6);
        H(me + 6, (ushort)(on ? v | bit : v & ~bit));
        return 0;
    }

    [Hle("_ZN3paf5graph16AttributedString12GetStyleRunsERNS_14_list_iteratorIPNS0_12TextStyleRunENS_9allocatorIS4_EEEES8_")]
    int AttributedStringStyleRuns(uint me, uint begin, uint end)
    {
        uint head = U(me + 0x10);
        U(begin, head == 0 ? 0 : U(head + 4));
        U(end, head);
        return 0;
    }

    // ---- images: the decoder state behind +0x2C (original extent +0x14/+0x18, decode extent +0x1C/+0x20,
    // clip/sub-rectangle +0x24..+0x30, format +0x44, resize type +0x54)

    [Hle("_ZNK3paf5image5Image9GetExtentEv")]
    uint ImageGetExtent(uint ret, uint me) { uint i = ImageInfo(me); U(ret, U(i + 0x1C)); U(ret + 4, U(i + 0x20)); return ret; }

    [Hle("_ZNK3paf5image5Image17GetOriginalExtentEv")]
    uint ImageGetOriginalExtent(uint ret, uint me) { uint i = ImageInfo(me); U(ret, U(i + 0x14)); U(ret + 4, U(i + 0x18)); return ret; }

    static bool ImageDecodable(uint fmt) => fmt is not (0 or 6 or 7 or 8);

    [Hle("_ZN3paf5image5Image12SetDecExtentERKNS0_11ImageExtentENS0_15ImageResizeTypeE")]
    bool ImageSetDecExtent(uint me, uint extent, uint resize)
    {
        int w = I(extent), h = I(extent + 4);
        if (w <= 0 || h <= 0) return false;
        uint i = ImageInfo(me);
        if (!ImageDecodable(U(i + 0x44))) return false;
        U(i + 0x1C, (uint)w); U(i + 0x20, (uint)h);
        U(i + 0x24, 0); U(i + 0x28, 0); U(i + 0x2C, (uint)w); U(i + 0x30, (uint)h);
        U(i + 0x54, resize);
        return true;
    }

    /// <summary>The rectangle {x, y, w, h} clipped to [0, limit): false when nothing is left.</summary>
    static bool ClipRect(uint r, int limitW, int limitH, out int x, out int y, out int w, out int h)
    {
        int rx = I(r), ry = I(r + 4);
        x = Math.Max(rx, 0); y = Math.Max(ry, 0);
        int right = Math.Min(I(r + 8) + rx, limitW), bottom = Math.Min(I(r + 0xC) + ry, limitH);
        w = right - x; h = bottom - y;
        return w > 0 && h > 0;
    }

    [Hle("_ZN3paf5image5Image7SetClipERKNS0_9ImageRectE")]
    bool ImageSetClip(uint me, uint rect)
    {
        uint i = ImageInfo(me);
        if (!ClipRect(rect, I(i + 0x1C), I(i + 0x20), out int x, out int y, out int w, out int h)) return false;
        U(i + 0x24, (uint)x); U(i + 0x28, (uint)y); U(i + 0x2C, (uint)w); U(i + 0x30, (uint)h);
        return true;
    }

    // ---- input and sound

    /// <summary>(me, bitmap, n): the bit index of the n-th set bit, -1 when there are fewer.</summary>
    [Hle("_ZN3paf11inputdevice15GestureListener18get_id_from_bitmapEjj")]
    int GestureIdFromBitmap(uint me, uint bitmap, int n)
    {
        for (int bit = 0, seen = 0; bitmap != 0; bitmap >>= 1, bit++)
            if ((bitmap & 1) != 0) { if (seen == n) return bit; seen++; }
        return -1;
    }

    /// <summary>(me, bool enable): a disable counter (+0x52C); sound is on (+0x530) at 0. Returns the previous state.</summary>
    [Hle("_ZN3paf5sound6Output11EnableSoundEb")]
    bool SoundEnable(uint me, bool enable)
    {
        int n = I(me + 0x52C);
        bool was = B(me + 0x530) != 0;
        if (enable) { if (n > 0) U(me + 0x52C, (uint)--n); }
        else U(me + 0x52C, (uint)++n);
        B(me + 0x530, (byte)(n == 0 ? 1 : 0));
        return was;
    }

    // ---- small helpers without public names

    [Hle("NID_D2E471DC", Nid = 0xD2E471DC, Library = "ScePafMisc")] uint GetField24OfFirst(uint me) => U(U(me) + 0x24);
    [Hle("NID_E62FE5E5", Nid = 0xE62FE5E5, Library = "ScePafCommon")] uint GetField4(uint me) => U(me + 4);
    [Hle("NID_2266A675", Nid = 0x2266A675, Library = "ScePafWidget")] void Copy8(uint dst, uint src) => Q(dst, Q(src));
    [Hle("NID_B4CCA1FD", Nid = 0xB4CCA1FD, Library = "ScePafCommon")] void Set10And14(uint me, uint a, uint b) { U(me + 0x10, a); U(me + 0x14, b); }
    [Hle("NID_D1E648EF", Nid = 0xD1E648EF, Library = "ScePafMisc")] void Clear3Words(uint me) { U(me, 0); U(me + 8, 0); U(me + 4, 0); }
    [Hle("NID_3BCCE98B", Nid = 0x3BCCE98B, Library = "ScePafCommon")] void ClearList(uint me) { U(me, 0); U(me + 4, 0); U(me + 8, 0); U(me + 0x14, 0); U(me + 0x10, 0); }
    [Hle("NID_570E7907", Nid = 0x570E7907, Library = "ScePafGraphics")] void Set160(uint me, uint a, uint b, uint c, uint d) { U(me + 0x160, b); U(me + 0x164, a); U(me + 0x168, c); U(me + 0x16C, d); }

    [Hle("NID_6FD7F728", Nid = 0x6FD7F728, Library = "ScePafGraphics")]
    void DrawObjSetC0(float v, uint me) { if (v != F(me + 0xC0)) { F(me + 0xC0, v); B(me + 0x50, (byte)(B(me + 0x50) | 8)); } }

    [Hle("NID_AEBC68E5", Nid = 0xAEBC68E5, Library = "ScePafGraphics")] void DrawObjSet30(uint me, uint v) => Q(U(me + 0x50) + 0x30, Q(v));
    [Hle("NID_70ED91E6", Nid = 0x70ED91E6, Library = "ScePafGraphics")]
    void DrawObjSet54(uint me, uint v) { uint d = U(me + 0x50); U(d + 0x58, U(v)); U(d + 0x54, U(v + 4)); }
    [Hle("NID_0C968320", Nid = 0x0C968320, Library = "ScePafGraphics")]
    void DrawObjSet5C(uint me, uint v) { uint d = U(me + 0x50); U(d + 0x5C, U(v)); U(d + 0x60, U(v + 4)); U(d + 100, U(v + 8)); U(d + 0x68, U(v + 0xC)); }
    [Hle("NID_111C68BE", Nid = 0x111C68BE, Library = "ScePafGraphics")]
    void DrawObjGet10(uint me, uint outp) { uint p = U(me + 0x88); Q(outp, p != 0 ? Q(p + 0x10) : 0); }

    /// <summary>(float a, me, bool b, const v4 *c): the drawn object's +0x64, flag bit 3 of +0x55 and +0x68..+0x77.</summary>
    [Hle("NID_8454D7FB", Nid = 0x8454D7FB, Library = "ScePafGraphics")]
    void DrawObjSetShadow(uint a, uint me, bool b, uint c)
    {
        uint d = U(me + 0x50);
        if (d == 0) return;
        U(d + 100, a);
        B(d + 0x55, (byte)((B(d + 0x55) & 0xF7) | (b ? 8 : 0)));
        Q(d + 0x68, Q(c)); Q(d + 0x70, Q(c + 8));
    }

    /// <summary>(me, mode): modes 1 and 2 set bit 1 of +0x55 and a pair of constants, others clear it.</summary>
    [Hle("NID_5E94DC78", Nid = 0x5E94DC78, Library = "ScePafGraphics")]
    void DrawObjSetMode(uint me, int mode)
    {
        uint d = U(me + 0x50);
        if (d == 0) return;
        if (mode == 1) { B(d + 0x55, (byte)(B(d + 0x55) | 2)); U(d + 0x58, 0x3F8B22D1); U(d + 0x5C, 0xBCD4FDF4); }
        else if (mode == 2) { B(d + 0x55, (byte)(B(d + 0x55) | 2)); U(d + 0x58, 0x3F8147AE); U(d + 0x5C, 0xBCCCCCCD); }
        else B(d + 0x55, (byte)(B(d + 0x55) & 0xFD));
    }

    /// <summary>(me, value, ** out): the {lo, hi} range of an array (+8, count +0xC) that holds the value.</summary>
    [Hle("NID_F5842A6B", Nid = 0xF5842A6B, Library = "ScePafMisc")]
    uint FindRange(uint me, uint v, uint outp)
    {
        uint p = U(me + 8), end = p + U(me + 0xC) * 8;
        for (; p != end; p += 8) if (U(p) <= v && v <= U(p + 4)) { U(outp, p); return 0; }
        return PafNotFound;
    }

    /// <summary>(me, which 0..3): a word of the block at +0x10 when the object is set up (+4).</summary>
    [Hle("NID_71349B04", Nid = 0x71349B04, Library = "ScePafMisc")]
    uint GetBlockWord(uint me, int which)
    {
        if (U(me + 4) == 0) return 0;
        uint b = U(me + 0x10);
        return b == 0 ? 0 : U(b + (uint)(which is >= 0 and <= 3 ? which : 0) * 4);
    }

    /// <summary>(me, part): whether a part (0 or 1; negative: any of both) of the 0xA0-byte array at +0x160 is on.</summary>
    [Hle("NID_529FB877", Nid = 0x529FB877, Library = "ScePafMisc")]
    int PartIsOn(uint me, int part)
    {
        if (part >= 2) return 0;
        if (part >= 0) return B(me + (uint)part * 0xA0 + 0x1E0) & 1;
        for (uint i = 0; i < 2; i++) if ((B(me + 0x160 + i * 0xA0 + 0x80) & 1) != 0) return 1;
        return 0;
    }

    /// <summary>(wchar *dst, uint *len, const wchar *src, uint n): copies n characters without the spaces when dst has
    /// room for n (*len); returns whether a space was dropped (and then the new length in *len).</summary>
    [Hle("NID_48D5B0D2", Nid = 0x48D5B0D2, Library = "ScePafCommon")]
    int CopyWithoutSpaces(uint dst, uint len, uint src, uint n)
    {
        if (n > U(len)) return 0;
        uint kept = 0;
        bool dropped = false;
        for (uint i = 0; i < n; i++)
        {
            ushort c = H(src + i * 2);
            if (c == ' ') dropped = true;
            else { H(dst, c); dst += 2; kept++; }
        }
        if (dropped) U(len, kept);
        return dropped ? 1 : 0;
    }

    /// <summary>(kind): a default length (as float bits) for 26 kinds: 150, 250, 450 or 500.</summary>
    [Hle("NID_DE5322EB", Nid = 0xDE5322EB, Library = "ScePafWidget")]
    uint DefaultLength(uint kind) => kind switch
    {
        0 or 6 or 7 or 8 or 9 or 10 or 11 or 0xD => 0x43160000,
        1 or 2 or 3 or 4 or 5 or 0xC or 0xE or 0xF or 0x12 or 0x13 => 0x437A0000,
        0x10 or 0x11 or 0x14 or 0x15 => 0x43E10000,
        0x16 or 0x17 or 0x18 or 0x19 => 0x43FA0000,
        _ => 0,
    };
    // ---- thin wrappers over the system libraries

    Http? _http;
    Gxm? _gxm;
    Http Ht => _http ??= Emu.Hle.Modules.OfType<Http>().First();
    Gxm Gx => _gxm ??= Emu.Hle.Modules.OfType<Gxm>().First();

    /// <summary>http::Transaction: the libhttp request id at +0xC.</summary>
    [Hle("_ZN3paf4http11Transaction16GetContentLengthERy")] int HttpTxContentLength(uint me, uint outp) => Ht.sceHttpGetResponseContentLength((int)U(me + 0xC), outp);
    [Hle("_ZN3paf4http11Transaction13GetStatusCodeERi")] int HttpTxStatusCode(uint me, uint outp) => Ht.sceHttpGetStatusCode((int)U(me + 0xC), outp);
    [Hle("_ZN3paf4http11Transaction15SetAutoRedirectEb")] int HttpTxSetAutoRedirect(uint me, bool on) => Ht.sceHttpSetAutoRedirect((int)U(me + 0xC), on ? 1 : 0);
    [Hle("_ZN3paf4http11Transaction11SendRequestEPKvj")] int HttpTxSend(uint me, uint data, uint size) => Ht.sceHttpSendRequest((int)U(me + 0xC), data, size);

    /// <summary>(year, month): the days of the month (sceRtcGetDaysInMonth).</summary>
    [Hle("NID_3D21E902", Nid = 0x3D21E902, Library = "ScePafMisc")] int DaysInMonth(ushort year, ushort month) => R.sceRtcGetDaysInMonth(year, month);

    /// <summary>(SceUID *evf): sets bit 1.</summary>
    [Hle("NID_AEE8496E", Nid = 0xAEE8496E, Library = "ScePafThread")] int EventFlagSet2(uint p) => Tm.sceKernelSetEventFlag((int)U(p), 2);

    /// <summary>(SceUID *evf): waits for bit 0 or 1 (OR) and sets bit 0.</summary>
    [Hle("NID_39B3F3CA", Nid = 0x39B3F3CA, Library = "ScePafThread")]
    int EventFlagWaitSet1(uint p)
    {
        Tm.sceKernelWaitEventFlag((int)U(p), 3, 4, new Memory.Ptr<uint>(0), new Memory.Ptr<uint>(0));
        return Tm.sceKernelSetEventFlag((int)U(p), 1);
    }

    /// <summary>A 10 ms sleep.</summary>
    [Hle("NID_83FFAFCA", Nid = 0x83FFAFCA, Library = "ScePafCommon")] void Sleep10() => ThreadSleep(10);

    /// <summary>ArlInstance::CullMode: kept at +0x768 and set on the GXM context (+0x68C).</summary>
    [Hle("_ZN3paf5graph3arl11ArlInstance8CullModeENS1_12CullModeEnumE")]
    void ArlCullMode(uint me, uint mode) { U(me + 0x768, mode); Gx.sceGxmSetCullMode(U(me + 0x68C), mode); }
    [Hle("_ZN3paf4http11Transaction16SetRequestHeaderEPKcS3_")]
    int HttpTxSetHeader(uint me, uint name, uint value) => Ht.sceHttpAddRequestHeader((int)U(me + 0xC), name, value, 0) is var r and < 0 ? r : 0;

    /// <summary>Surface::operator new / SurfaceCLUT::operator new: the paf heap.</summary>
    [Hle("_ZN3paf5graph7SurfacenwEj")]
    [Hle("_ZN3paf5graph11SurfaceCLUTnwEj")]
    uint SurfaceNew(uint size) => Alloc(size);

    /// <summary>TextureObject::setWrapS/T: mirror modes (5, 7) fall back to clamp (2) on a texture without the mirror
    /// capability (+4 → +0xC bit 7); the GXM texture is at +0x7C.</summary>
    [Hle("_ZN3paf5graph3arl13TextureObject8setWrapSENS1_11TextureWrapE")] void TexSetWrapS(uint me, uint mode) => Gx.sceGxmTextureSetUAddrMode(U(me + 0x7C), TexWrap(me, mode));
    [Hle("_ZN3paf5graph3arl13TextureObject8setWrapTENS1_11TextureWrapE")] void TexSetWrapT(uint me, uint mode) => Gx.sceGxmTextureSetVAddrMode(U(me + 0x7C), TexWrap(me, mode));
    static uint TexWrap(uint me, uint mode) => mode is 5 or 7 && (U(U(me + 4) + 0xC) & 0x80) == 0 ? 2 : mode;

    /// <summary>ArlInstance state per face (bit 0 front, bit 1 back) on the GXM context at +0x68C. The depth function is
    /// kept per face (+0x75C/+0x760) and applied while depth test is on (+0x792), both through the front setter.</summary>
    [Hle("_ZN3paf5graph3arl11ArlInstance9DepthFuncENS1_13DepthFuncEnumENS1_8FaceEnumE")]
    void ArlDepthFunc(uint me, uint func, uint face)
    {
        if ((face & 1) != 0) { U(me + 0x75C, func); if (B(me + 0x792) != 0) Gx.sceGxmSetFrontDepthFunc(U(me + 0x68C), func); }
        if ((face & 2) != 0) { U(me + 0x760, func); if (B(me + 0x792) != 0) Gx.sceGxmSetFrontDepthFunc(U(me + 0x68C), func); }
    }

    [Hle("_ZN3paf5graph3arl11ArlInstance11PolygonModeENS1_15PolygonModeEnumENS1_8FaceEnumE")]
    void ArlPolygonMode(uint me, uint mode, uint face)
    {
        if ((face & 1) != 0) { U(me + 0x754, mode); Gx.sceGxmSetFrontPolygonMode(U(me + 0x68C), mode); }
        if ((face & 2) != 0) { U(me + 0x750, mode); Gx.sceGxmSetBackPolygonMode(U(me + 0x68C), mode); }
    }

    /// <summary>DepthMask(bool write, face): GXM's write-disable value is 0x100000.</summary>
    [Hle("_ZN3paf5graph3arl11ArlInstance9DepthMaskEbNS1_8FaceEnumE")]
    void ArlDepthMask(uint me, bool write, uint face)
    {
        uint v = write ? 0u : 0x100000u;
        if ((face & 1) != 0) { B(me + 0x764, (byte)(write ? 1 : 0)); Gx.sceGxmSetFrontDepthWriteEnable(U(me + 0x68C), v); }
        if ((face & 2) != 0) { B(me + 0x765, (byte)(write ? 1 : 0)); Gx.sceGxmSetBackDepthWriteEnable(U(me + 0x68C), v); }
    }
}
