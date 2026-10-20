namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// PVRTC (I and II, 2 and 4 bpp) decompression to RGBA8. PVRTC-I follows Imagination's reference decoder
/// (PowerVR SDK, MIT); PVRTC-II adds its colour layout (shared opacity flag in bit 31, hard-transition flag in
/// bit 15), non-interpolated colours around hard transitions and the 4bpp local-palette mode.
/// Each 64-bit word: low 32 bits modulation, high 32 bits colours; words are stored in Morton (twiddled) order.
/// </summary>
public static class Pvrtc
{
    struct C { public int R, G, B, A; public C(int r, int g, int b, int a) { R = r; G = g; B = b; A = a; } }

    public static byte[] Decode(ReadOnlySpan<byte> src, uint width, uint height, bool twoBpp, bool ii)
    {
        int ww = twoBpp ? 8 : 4, wh = 4;
        int tw = Math.Max((int)width, ww * 2), th = Math.Max((int)height, wh * 2);
        int nx = (tw + ww - 1) / ww, ny = (th + wh - 1) / wh;
        tw = nx * ww; th = ny * wh;
        var full = new byte[tw * th * 4];
        var words = new uint[nx * ny * 2];
        int px2 = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)nx), py2 = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)ny);
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                int o = (int)Twiddle((uint)px2, (uint)py2, (uint)x, (uint)y) * 8;
                if (o + 8 > src.Length) continue;
                words[(y * nx + x) * 2] = BitConverter.ToUInt32(src.Slice(o, 4));
                words[(y * nx + x) * 2 + 1] = BitConverter.ToUInt32(src.Slice(o + 4, 4));
            }

        var block = new C[ww * wh];
        var modVals = new int[16, 8];
        var modModes = new int[16, 8];
        var upA = new C[32];
        var upB = new C[32];
        for (int wy = -1; wy < ny - 1; wy++)
            for (int wx = -1; wx < nx - 1; wx++)
            {
                int p0 = Wrap(nx, wx), p1 = Wrap(ny, wy), q0 = Wrap(nx, wx + 1), r1 = Wrap(ny, wy + 1);
                (uint Mod, uint Col) P = W(p0, p1), Q = W(q0, p1), R = W(p0, r1), S = W(q0, r1);
                DecodeBlock(P, Q, R, S, block, modVals, modModes, upA, upB, twoBpp, ii);
                // map the 4 quadrants (the block spans the centres of P, Q, R, S)
                for (int y = 0; y < wh / 2; y++)
                    for (int x = 0; x < ww / 2; x++)
                    {
                        Put(p0 * ww + x + ww / 2, p1 * wh + y + wh / 2, block[y * ww + x]);
                        Put(q0 * ww + x, p1 * wh + y + wh / 2, block[y * ww + x + ww / 2]);
                        Put(p0 * ww + x + ww / 2, r1 * wh + y, block[(y + wh / 2) * ww + x]);
                        Put(q0 * ww + x, r1 * wh + y, block[(y + wh / 2) * ww + x + ww / 2]);
                    }
            }

        if (tw == width && th == height) return full;
        var outBuf = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(full, y * tw * 4, outBuf, (int)(y * width * 4), (int)width * 4);
        return outBuf;

        (uint, uint) W(int x, int y) => (words[(y * nx + x) * 2], words[(y * nx + x) * 2 + 1]);
        void Put(int x, int y, C c)
        {
            int o = (y * tw + x) * 4;
            full[o] = (byte)c.R; full[o + 1] = (byte)c.G; full[o + 2] = (byte)c.B; full[o + 3] = (byte)c.A;
        }
    }

    static int Wrap(int n, int i) => (i + n) % n;

    static uint Twiddle(uint xSize, uint ySize, uint x, uint y)
    {
        uint minDim = xSize, maxVal = y, result = 0, src = 1, dst = 1;
        int shift = 0;
        if (ySize < xSize) { minDim = ySize; maxVal = x; }
        while (src < minDim)
        {
            if ((y & src) != 0) result |= dst;
            if ((x & src) != 0) result |= dst << 1;
            src <<= 1; dst <<= 2; shift++;
        }
        return result | ((maxVal >> shift) << (2 * shift));
    }

    // 5-bit colour, 4-bit alpha (as in the reference decoder)
    static C ColorA(uint c, bool ii)
    {
        if ((c & (ii ? 0x80000000u : 0x8000u)) != 0)
            return new((int)((c & 0x7c00) >> 10), (int)((c & 0x3e0) >> 5), (int)((c & 0x1e) | ((c & 0x1e) >> 4)), 0xf);
        return new((int)(((c & 0xf00) >> 7) | ((c & 0xf00) >> 11)), (int)(((c & 0xf0) >> 3) | ((c & 0xf0) >> 7)),
                   (int)(((c & 0xe) << 1) | ((c & 0xe) >> 2)), (int)((c & 0x7000) >> 11));
    }

    static C ColorB(uint c, bool ii)
    {
        if ((c & 0x80000000u) != 0)
            return new((int)((c & 0x7c000000) >> 26), (int)((c & 0x3e00000) >> 21), (int)((c & 0x1f0000) >> 16), 0xf);
        return new((int)(((c & 0xf000000) >> 23) | ((c & 0xf000000) >> 27)), (int)(((c & 0xf00000) >> 19) | ((c & 0xf00000) >> 23)),
                   (int)(((c & 0xf0000) >> 15) | ((c & 0xf0000) >> 19)), (int)((c & 0x70000000) >> 27) | (ii ? 1 : 0));
    }

    /// <summary>A word's colour expanded to 8 bits the same way the bilinear path does (for non-interpolated texels).</summary>
    static C Expand(C c, bool twoBpp)
    {
        int ww = twoBpp ? 8 : 4;
        int r = c.R * ww * 4, g = c.G * ww * 4, b = c.B * ww * 4, a = c.A * ww * 4;
        return twoBpp
            ? new((r >> 7) + (r >> 2), (g >> 7) + (g >> 2), (b >> 7) + (b >> 2), (a >> 5) + (a >> 1))
            : new((r >> 6) + (r >> 1), (g >> 6) + (g >> 1), (b >> 6) + (b >> 1), (a >> 4) + a);
    }

    static void Interpolate(C p, C q, C r, C s, C[] o, bool twoBpp)
    {
        int ww = twoBpp ? 8 : 4, wh = 4;
        C qp = new(q.R - p.R, q.G - p.G, q.B - p.B, q.A - p.A), sr = new(s.R - r.R, s.G - r.G, s.B - r.B, s.A - r.A);
        C hp = new(p.R * ww, p.G * ww, p.B * ww, p.A * ww), hr = new(r.R * ww, r.G * ww, r.B * ww, r.A * ww);
        if (twoBpp)
        {
            for (int x = 0; x < ww; x++)
            {
                C res = new(4 * hp.R, 4 * hp.G, 4 * hp.B, 4 * hp.A), dy = new(hr.R - hp.R, hr.G - hp.G, hr.B - hp.B, hr.A - hp.A);
                for (int y = 0; y < wh; y++)
                {
                    o[y * ww + x] = new((res.R >> 7) + (res.R >> 2), (res.G >> 7) + (res.G >> 2), (res.B >> 7) + (res.B >> 2), (res.A >> 5) + (res.A >> 1));
                    res.R += dy.R; res.G += dy.G; res.B += dy.B; res.A += dy.A;
                }
                hp.R += qp.R; hp.G += qp.G; hp.B += qp.B; hp.A += qp.A;
                hr.R += sr.R; hr.G += sr.G; hr.B += sr.B; hr.A += sr.A;
            }
        }
        else
        {
            for (int y = 0; y < wh; y++)
            {
                C res = new(4 * hp.R, 4 * hp.G, 4 * hp.B, 4 * hp.A), dy = new(hr.R - hp.R, hr.G - hp.G, hr.B - hp.B, hr.A - hp.A);
                for (int x = 0; x < ww; x++)
                {
                    o[y * ww + x] = new((res.R >> 6) + (res.R >> 1), (res.G >> 6) + (res.G >> 1), (res.B >> 6) + (res.B >> 1), (res.A >> 4) + res.A);
                    res.R += dy.R; res.G += dy.G; res.B += dy.B; res.A += dy.A;
                }
                hp.R += qp.R; hp.G += qp.G; hp.B += qp.B; hp.A += qp.A;
                hr.R += sr.R; hr.G += sr.G; hr.B += sr.B; hr.A += sr.A;
            }
        }
    }

    // modulation value encodings: 0..8 weights; +10 punch-through alpha; +20 hard (non-interpolated colours);
    // +30 local palette index (PVRTC-II 4bpp)
    static void UnpackModulations((uint Mod, uint Col) w, bool hard, int ox, int oy, int[,] vals, int[,] modes, bool twoBpp, bool ii)
    {
        uint mode = w.Col & 1, bits = w.Mod;
        if (twoBpp)
        {
            if (mode != 0)
            {
                if ((bits & 1) != 0)
                {
                    mode = (bits & (1u << 20)) != 0 ? 3u : 2u;
                    if ((bits & (1u << 21)) != 0) bits |= 1u << 20; else bits &= ~(1u << 20);
                }
                if ((bits & 2) != 0) bits |= 1; else bits &= ~1u;
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        modes[x + ox, y + oy] = (int)mode;
                        if (((x ^ y) & 1) == 0) { vals[x + ox, y + oy] = (int)(bits & 3); bits >>= 2; }
                        else vals[x + ox, y + oy] = 0;
                        if (ii && hard && InHardRegion(x + ox, y + oy, 8)) modes[x + ox, y + oy] += 20;
                    }
            }
            else
            {
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        modes[x + ox, y + oy] = 0;
                        vals[x + ox, y + oy] = (bits & 1) != 0 ? 3 : 0;
                        if (ii && hard && InHardRegion(x + ox, y + oy, 8)) modes[x + ox, y + oy] += 20;
                        bits >>= 1;
                    }
            }
            return;
        }

        // 4bpp: vals indexed [y, x]
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int v = (int)(bits & 3);
                bool hardTexel = ii && hard && InHardRegion(x + ox, y + oy, 4);
                if (mode != 0)
                    v = hardTexel ? v + 30 : v switch { 1 => 4, 2 => 14, 3 => 8, _ => 0 };
                else
                {
                    v *= 3;
                    if (v > 3) v -= 1;
                    if (hardTexel) v += 20;
                }
                vals[y + oy, x + ox] = v;
                bits >>= 2;
            }
    }

    // the texels of the decoded block nearest the boundary between the four words
    static bool InHardRegion(int x, int y, int ww) => y >= 2 && y <= 5 && x >= ww - 2 && x <= ww + 1;

    static readonly int[] RepVals = [0, 3, 5, 8];

    static int ModulationAt(int[,] vals, int[,] modes, int x, int y, bool twoBpp)
    {
        if (!twoBpp) return vals[x, y];
        int mode = modes[x, y], extra = 0;
        if (mode >= 20) { mode -= 20; extra = 20; }
        int v;
        if (mode == 0 || ((x ^ y) & 1) == 0) v = RepVals[vals[x, y]];
        else if (mode == 1) v = (RepVals[vals[x, y - 1]] + RepVals[vals[x, y + 1]] + RepVals[vals[x - 1, y]] + RepVals[vals[x + 1, y]] + 2) / 4;
        else if (mode == 2) v = (RepVals[vals[x - 1, y]] + RepVals[vals[x + 1, y]] + 1) / 2;
        else v = (RepVals[vals[x, y - 1]] + RepVals[vals[x, y + 1]] + 1) / 2;
        return v + extra;
    }

    static void DecodeBlock((uint Mod, uint Col) p, (uint Mod, uint Col) q, (uint Mod, uint Col) r, (uint Mod, uint Col) s,
                            C[] outBlock, int[,] vals, int[,] modes, C[] upA, C[] upB, bool twoBpp, bool ii)
    {
        int ww = twoBpp ? 8 : 4, wh = 4;
        Array.Clear(vals);
        Array.Clear(modes);
        // the hard-transition flag of P (the top-left word) governs the area where the four words meet
        bool hard = ii && (p.Col & 0x8000) != 0;
        UnpackModulations(p, hard, 0, 0, vals, modes, twoBpp, ii);
        UnpackModulations(q, hard, ww, 0, vals, modes, twoBpp, ii);
        UnpackModulations(r, hard, 0, wh, vals, modes, twoBpp, ii);
        UnpackModulations(s, hard, ww, wh, vals, modes, twoBpp, ii);
        Interpolate(ColorA(p.Col, ii), ColorA(q.Col, ii), ColorA(r.Col, ii), ColorA(s.Col, ii), upA, twoBpp);
        Interpolate(ColorB(p.Col, ii), ColorB(q.Col, ii), ColorB(r.Col, ii), ColorB(s.Col, ii), upB, twoBpp);

        C[]? palette = null;
        for (int y = 0; y < wh; y++)
            for (int x = 0; x < ww; x++)
            {
                // 4bpp: vals is stored [row, column] and read as [x, y] (the reference decoder's getModulationValues),
                // which matches the transposed colour interpolation and output store of this path
                int mod = twoBpp ? ModulationAt(vals, modes, x + ww / 2, y + wh / 2, true) : vals[x + ww / 2, y + wh / 2];
                C a = upA[y * ww + x], b = upB[y * ww + x], res;
                bool punch = false;
                if (mod >= 30)
                {
                    palette ??= LocalPalette(p, q, r, s, twoBpp);
                    res = palette[(mod - 30) * 16 + y * ww + x];
                }
                else
                {
                    if (mod >= 20)
                    {
                        // non-interpolated: each quadrant keeps the colours of its own word. The 4bpp path runs
                        // transposed (x walks rows, y walks columns: see the output store below and Interpolate),
                        // so its top-right quadrant is Q, not R (Vita3K pvrt-dec.cpp pvrtcGetDecompressedPixels).
                        bool left = twoBpp ? x < ww / 2 : y < wh / 2, top = twoBpp ? y < wh / 2 : x < ww / 2;
                        var w = top ? (left ? p : q) : (left ? r : s);
                        a = Expand(ColorA(w.Col, ii), twoBpp);
                        b = Expand(ColorB(w.Col, ii), twoBpp);
                        mod -= 20;
                    }
                    else if (mod > 10) { punch = true; mod -= 10; }
                    res = new((a.R * (8 - mod) + b.R * mod) / 8, (a.G * (8 - mod) + b.G * mod) / 8, (a.B * (8 - mod) + b.B * mod) / 8,
                              punch ? 0 : (a.A * (8 - mod) + b.A * mod) / 8);
                }
                if (twoBpp) outBlock[y * ww + x] = res;
                else outBlock[y + x * wh] = res; // the reference decoder transposes 4bpp blocks here
            }
    }

    /// <summary>PVRTC-II 4bpp local palette mode: each texel position picks from the colours of the four words.</summary>
    static C[] LocalPalette((uint Mod, uint Col) p, (uint Mod, uint Col) q, (uint Mod, uint Col) r, (uint Mod, uint Col) s, bool twoBpp)
    {
        C ap = Expand(ColorA(p.Col, true), twoBpp), bp = Expand(ColorB(p.Col, true), twoBpp);
        C aq = Expand(ColorA(q.Col, true), twoBpp), bq = Expand(ColorB(q.Col, true), twoBpp);
        C ar = Expand(ColorA(r.Col, true), twoBpp), br = Expand(ColorB(r.Col, true), twoBpp);
        C as_ = Expand(ColorA(s.Col, true), twoBpp), bs = Expand(ColorB(s.Col, true), twoBpp);
        static C Mix(C a, C b, int wa, int wb) => new((a.R * wa + b.R * wb) / 8, (a.G * wa + b.G * wb) / 8, (a.B * wa + b.B * wb) / 8, (a.A * wa + b.A * wb) / 8);
        // per texel (0..15): the 4 selectable colours
        C[][] sets =
        [
            [ap, Mix(ap, bp, 5, 3), Mix(ap, bp, 3, 5), bp],
            [ap, bp, aq, bq], [ap, bp, aq, bq], [ap, bp, aq, bq],
            [ap, bp, ar, br], [ap, bp, aq, br], [ap, bp, aq, bq], [as_, bp, aq, bq],
            [ap, bp, ar, br], [ap, bp, ar, br], [ap, bs, ar, bq], [as_, bs, aq, bq],
            [ap, bp, ar, br], [ap, bs, ar, br], [as_, bs, ar, br], [as_, bs, ar, bq],
        ];
        var pal = new C[4 * 16];
        for (int t = 0; t < 16; t++)
            for (int i = 0; i < 4; i++) pal[i * 16 + t] = sets[t][i];
        return pal;
    }
}
