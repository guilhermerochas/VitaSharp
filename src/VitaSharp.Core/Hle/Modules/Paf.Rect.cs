namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// paf::math::rect: four floats, the centre (x +0, y +4) and the size (width +8, height +0xC), y growing upwards (top =
/// y + height/2). Every constructor and setter that takes a size stores a negative one as 0; the edge setters keep the
/// opposite edge and do not let the edge cross it. Points and v4 results come back through the hidden result pointer.
/// </summary>
public sealed partial class Paf
{
    static float RLeft(uint r) => F(r) - F(r + 8) * 0.5f;
    static float RRight(uint r) => F(r) + F(r + 8) * 0.5f;
    static float RTop(uint r) => F(r + 4) + F(r + 0xC) * 0.5f;
    static float RBottom(uint r) => F(r + 4) - F(r + 0xC) * 0.5f;

    static void RSet(uint r, float x, float y, float w, float h) { F(r, x); F(r + 4, y); F(r + 8, NonNeg(w)); F(r + 0xC, NonNeg(h)); }
    static void RCopy(uint r, uint src) => RSet(r, F(src), F(src + 4), F(src + 8), F(src + 0xC));
    static void V2(uint dst, float x, float y) { F(dst, x); F(dst + 4, y); }

    [Hle("_ZN3paf4math4rectC1Ev")]
    [Hle("_ZN3paf4math4rectC2Ev")]
    [Hle("_ZN3paf4math4rect5clearEv")]
    [Hle("_ZN3paf4math4rect5_0000Ev")]
    void RectClear(uint me) { U(me, 0); U(me + 4, 0); U(me + 8, 0); U(me + 0xC, 0); }

    /// <summary>The unit rect at the origin (centre 0,0, size 1x1).</summary>
    [Hle("_ZN3paf4math4rect5_0011Ev")]
    void RectUnit(uint me) { U(me, 0); U(me + 4, 0); F(me + 8, 1f); F(me + 0xC, 1f); }

    [Hle("_ZN3paf4math4rectC1ERKS1_")]
    [Hle("_ZN3paf4math4rectC2ERKS1_")]
    [Hle("_ZN3paf4math4rectaSERKS1_")]
    [Hle("_ZN3paf4math4rect3setERKS1_")]
    uint RectCopy(uint me, uint src) { RCopy(me, src); return me; }

    /// <summary>(centre, size) as two v2.</summary>
    [Hle("_ZN3paf4math4rectC1ERKNS0_2v2ES4_")]
    [Hle("_ZN3paf4math4rectC2ERKNS0_2v2ES4_")]
    [Hle("_ZN3paf4math4rect3setERKNS0_2v2ES4_")]
    void RectSetV2(uint me, uint center, uint size) => RSet(me, F(center), F(center + 4), F(size), F(size + 4));

    [Hle("_ZN3paf4math4rectC1Effff")]
    [Hle("_ZN3paf4math4rectC2Effff")]
    [Hle("_ZN3paf4math4rect3setEffff")]
    void RectSet(uint me, float x, float y, float w, float h) => RSet(me, x, y, w, h);

    /// <summary>get(): the rect itself (its size again clamped to 0).</summary>
    [Hle("_ZNK3paf4math4rect3getEv")]
    void RectGet(uint ret, uint me) => RCopy(ret, me);

    [Hle("_ZN3paf4math4rect10set_centerEff")] void RectSetCenter(uint me, float x, float y) { F(me, x); F(me + 4, y); }
    [Hle("_ZN3paf4math4rect10set_centerERKNS0_2v2E")] void RectSetCenterV(uint me, uint v) { F(me, F(v)); F(me + 4, F(v + 4)); }
    [Hle("_ZN3paf4math4rect12set_center_xEf")] void RectSetCenterX(uint me, float x) => F(me, x);
    [Hle("_ZN3paf4math4rect12set_center_yEf")] void RectSetCenterY(uint me, float y) => F(me + 4, y);
    [Hle("_ZN3paf4math4rect8set_sizeEff")] void RectSetSize(uint me, float w, float h) { F(me + 8, NonNeg(w)); F(me + 0xC, NonNeg(h)); }
    [Hle("_ZN3paf4math4rect8set_sizeERKNS0_2v2E")] void RectSetSizeV(uint me, uint v) => RectSetSize(me, F(v), F(v + 4));
    [Hle("_ZN3paf4math4rect9set_widthEf")] void RectSetWidth(uint me, float w) => F(me + 8, NonNeg(w));
    [Hle("_ZN3paf4math4rect10set_heightEf")] void RectSetHeight(uint me, float h) => F(me + 0xC, NonNeg(h));
    [Hle("_ZN3paf4math4rect4moveEff")] void RectMove(uint me, float dx, float dy) { F(me, F(me) + dx); F(me + 4, F(me + 4) + dy); }
    [Hle("_ZN3paf4math4rect4moveERKNS0_2v2E")] void RectMoveV(uint me, uint v) => RectMove(me, F(v), F(v + 4));

    /// <summary>Moves the left edge to x keeping the right one (x past the right edge stops there).</summary>
    [Hle("_ZN3paf4math4rect8set_leftEf")]
    void RectSetLeft(uint me, float x)
    {
        float r = RRight(me);
        if (r - x < 0) x = r;
        F(me, (x + r) * 0.5f);
        F(me + 8, NonNeg(r - x));
    }

    [Hle("_ZN3paf4math4rect9set_rightEf")]
    void RectSetRight(uint me, float x)
    {
        float l = RLeft(me);
        if (x - l < 0) x = l;
        F(me, (l + x) * 0.5f);
        F(me + 8, NonNeg(x - l));
    }

    [Hle("_ZN3paf4math4rect7set_topEf")]
    void RectSetTop(uint me, float y)
    {
        float b = RBottom(me);
        if (y - b < 0) y = b;
        F(me + 4, (b + y) * 0.5f);
        F(me + 0xC, NonNeg(y - b));
    }

    [Hle("_ZN3paf4math4rect10set_bottomEf")]
    void RectSetBottom(uint me, float y)
    {
        float t = RTop(me);
        if (t - y < 0) y = t;
        F(me + 4, (y + t) * 0.5f);
        F(me + 0xC, NonNeg(t - y));
    }

    /// <summary>The align_* methods move the rect (size kept) so that the named edge lands on the value.</summary>
    [Hle("_ZN3paf4math4rect10align_leftEf")] void RectAlignLeft(uint me, float x) { float c = F(me); F(me, c + (x - (c - F(me + 8) * 0.5f))); }
    [Hle("_ZN3paf4math4rect11align_rightEf")] void RectAlignRight(uint me, float x) { float c = F(me); F(me, c + (x - (c + F(me + 8) * 0.5f))); }
    [Hle("_ZN3paf4math4rect9align_topEf")] void RectAlignTop(uint me, float y) { float c = F(me + 4); F(me + 4, c + (y - (c + F(me + 0xC) * 0.5f))); }
    [Hle("_ZN3paf4math4rect12align_bottomEf")] void RectAlignBottom(uint me, float y) { float c = F(me + 4); F(me + 4, c + (y - (c - F(me + 0xC) * 0.5f))); }

    /// <summary>The two rects overlap (touching edges do not count).</summary>
    [Hle("_ZN3paf4math4rect3hitERKS1_")]
    bool RectHit(uint me, uint o) =>
        !(RLeft(me) >= RRight(o)) && !(RRight(me) <= RLeft(o)) && !(RTop(me) <= RBottom(o)) && !(RBottom(me) >= RTop(o));

    /// <summary>This rect lies inside <paramref name="o"/> (edges may coincide).</summary>
    [Hle("_ZN3paf4math4rect10is_in_rectERKS1_")]
    bool RectIsIn(uint me, uint o) =>
        !(RLeft(me) < RLeft(o)) && !(RRight(me) > RRight(o)) && !(RTop(me) > RTop(o)) && !(RBottom(me) < RBottom(o));

    /// <summary>The point is inside or on the edge.</summary>
    [Hle("_ZN3paf4math4rect16is_point_in_rectEff")]
    bool RectHasPoint(uint me, float x, float y) => RLeft(me) <= x && x <= RRight(me) && RTop(me) >= y && y >= RBottom(me);

    [Hle("_ZN3paf4math4rect16is_point_in_rectERKNS0_2v2E")]
    bool RectHasPointV(uint me, uint p) => RectHasPoint(me, F(p), F(p + 4));

    /// <summary>libpaf 3.74 compiles operator== and operator!= to the same code: 1 when any of the four floats differs.</summary>
    [Hle("_ZNK3paf4math4recteqERKS1_")]
    [Hle("_ZNK3paf4math4rectneERKS1_")]
    bool RectDiffers(uint me, uint o)
    {
        for (uint i = 0; i < 16; i += 4)
            if (F(me + i) != F(o + i)) return true;
        return false;
    }

    [Hle("_ZNK3paf4math4rect6centerEv")] void RectCenter(uint ret, uint me) => V2(ret, F(me), F(me + 4));
    [Hle("_ZNK3paf4math4rect4sizeEv")] void RectSize(uint ret, uint me) => V2(ret, F(me + 8), F(me + 0xC));
    [Hle("_ZNK3paf4math4rect8center_xEv")] float RectCenterX(uint me) => F(me);
    [Hle("_ZNK3paf4math4rect8center_yEv")] float RectCenterY(uint me) => F(me + 4);
    [Hle("_ZNK3paf4math4rect5widthEv")] float RectWidth(uint me) => F(me + 8);
    [Hle("_ZNK3paf4math4rect6heightEv")] float RectHeight(uint me) => F(me + 0xC);
    [Hle("_ZNK3paf4math4rect4leftEv")] float RectLeftEdge(uint me) => RLeft(me);
    [Hle("_ZNK3paf4math4rect5rightEv")] float RectRightEdge(uint me) => RRight(me);
    [Hle("_ZNK3paf4math4rect3topEv")] float RectTopEdge(uint me) => RTop(me);
    [Hle("_ZNK3paf4math4rect6bottomEv")] float RectBottomEdge(uint me) => RBottom(me);
    [Hle("_ZNK3paf4math4rect8get_areaEv")] float RectArea(uint me) => F(me + 8) * F(me + 0xC);
    [Hle("_ZNK3paf4math4rect8left_topEv")] void RectLeftTop(uint ret, uint me) => V2(ret, RLeft(me), RTop(me));
    [Hle("_ZNK3paf4math4rect11left_bottomEv")] void RectLeftBottom(uint ret, uint me) => V2(ret, RLeft(me), RBottom(me));
    [Hle("_ZNK3paf4math4rect9right_topEv")] void RectRightTop(uint ret, uint me) => V2(ret, RRight(me), RTop(me));
    [Hle("_ZNK3paf4math4rect12right_bottomEv")] void RectRightBottom(uint ret, uint me) => V2(ret, RRight(me), RBottom(me));

    /// <summary>The v4 forms: (x, y, w, h), (left, top, right, bottom), (top, left, bottom, right).</summary>
    [Hle("_ZNK3paf4math4rect8get_xywhEv")]
    void RectXywh(uint ret, uint me) { for (uint i = 0; i < 16; i += 4) U(ret + i, U(me + i)); }

    [Hle("_ZNK3paf4math4rect8get_LTRBEv")]
    void RectLtrb(uint ret, uint me) { V2(ret, RLeft(me), RTop(me)); V2(ret + 8, RRight(me), RBottom(me)); }

    [Hle("_ZNK3paf4math4rect8get_TLBREv")]
    void RectTlbr(uint ret, uint me) { V2(ret, RTop(me), RLeft(me)); V2(ret + 8, RBottom(me), RRight(me)); }
}
