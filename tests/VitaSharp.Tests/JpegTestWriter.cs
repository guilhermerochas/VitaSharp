using VitaSharp.Core.Media;

namespace VitaSharp.Tests;

/// <summary>
/// Independent JPEG writer for decoder tests: quantizes an image once, then writes the same coefficients either as one
/// baseline interleaved scan (SOF0) or as a progressive script (SOF2) with spectral selection, successive
/// approximation of DC and AC, non-interleaved chroma scans and EOB runs. Uses fixed-length Huffman codes (DC 4 bits,
/// AC 10 bits) so every symbol (including EOBn) exists and the decoder's long-code path is exercised.
/// </summary>
internal static class JpegTestWriter
{
    sealed class Comp
    {
        public int Id, H, V, BlocksX, BlocksY, CodedX, CodedY;
        public int[] Coefs = [];
    }

    sealed class Bits
    {
        public readonly List<byte> Out;
        ulong _acc;
        int _n;
        public Bits(List<byte> o) => Out = o;
        public void Put(int v, int n)
        {
            if (n == 0) return;
            _acc = (_acc << n) | ((uint)v & ((1u << n) - 1));
            _n += n;
            while (_n >= 8)
            {
                byte b = (byte)(_acc >> (_n - 8));
                Out.Add(b);
                if (b == 0xFF) Out.Add(0);
                _n -= 8;
            }
        }
        public void Flush() { if (_n > 0) Put(0x7F, 8 - _n); _acc = 0; _n = 0; }
    }

    const int DcLen = 4, AcLen = 10;

    static void Word(List<byte> o, int w) { o.Add((byte)(w >> 8)); o.Add((byte)w); }

    /// <summary>Planes: Y, Cb, Cr at chroma resolution ceil(w / hdiv) x ceil(h / vdiv) (or Y only when cb is null).</summary>
    public static byte[] Write(byte[] y, byte[]? cb, byte[]? cr, int w, int h, int hdiv, int vdiv, byte[] quant,
        bool progressive, int restart = 0)
    {
        bool gray = cb == null;
        if (gray) hdiv = vdiv = 1;
        int cw = (w + hdiv - 1) / hdiv, ch = (h + vdiv - 1) / vdiv;
        int mcusX = (w + 8 * hdiv - 1) / (8 * hdiv), mcusY = (h + 8 * vdiv - 1) / (8 * vdiv);
        var comps = new List<Comp> { Make(1, hdiv, vdiv, y, w, h, w, mcusX, mcusY, quant) };
        if (!gray)
        {
            comps.Add(Make(2, 1, 1, cb!, cw, ch, cw, mcusX, mcusY, quant));
            comps.Add(Make(3, 1, 1, cr!, cw, ch, cw, mcusX, mcusY, quant));
        }

        var o = new List<byte>();
        Word(o, 0xFFD8);
        Word(o, 0xFFDB); Word(o, 67); o.Add(0);
        for (int i = 0; i < 64; i++) o.Add(quant[JpegTables.ZigZag[i]]);
        Word(o, progressive ? 0xFFC2 : 0xFFC0); Word(o, 8 + 3 * comps.Count); o.Add(8); Word(o, h); Word(o, w); o.Add((byte)comps.Count);
        foreach (var c in comps) { o.Add((byte)c.Id); o.Add((byte)((c.H << 4) | c.V)); o.Add(0); }
        // DC: 12 symbols of 4 bits; AC: all 256 symbols of 10 bits
        Word(o, 0xFFC4); Word(o, 2 + 17 + 12); o.Add(0x00);
        for (int i = 1; i <= 16; i++) o.Add((byte)(i == DcLen ? 12 : 0));
        for (int i = 0; i < 12; i++) o.Add((byte)i);
        // counts are one byte each: 256 does not fit, so 255 codes of 10 bits + 1 of 11 bits
        Word(o, 0xFFC4); Word(o, 2 + 17 + 256); o.Add(0x10);
        for (int i = 1; i <= 16; i++) o.Add((byte)(i == AcLen ? 255 : i == AcLen + 1 ? 1 : 0));
        for (int i = 0; i < 256; i++) o.Add((byte)i);
        if (restart > 0) { Word(o, 0xFFDD); Word(o, 4); Word(o, restart); }

        if (!progressive) Scan(o, comps, mcusX, mcusY, 0, 63, 0, 0, restart, false);
        else
        {
            var all = comps;
            var lum = comps.Take(1).ToList();
            Scan(o, all, mcusX, mcusY, 0, 0, 0, 1, restart, true);
            Scan(o, lum, mcusX, mcusY, 1, 5, 0, 2, restart, true);
            if (!gray)
            {
                Scan(o, [comps[2]], mcusX, mcusY, 1, 63, 0, 1, restart, true);
                Scan(o, [comps[1]], mcusX, mcusY, 1, 63, 0, 1, restart, true);
            }
            Scan(o, lum, mcusX, mcusY, 6, 63, 0, 2, restart, true);
            Scan(o, lum, mcusX, mcusY, 1, 63, 2, 1, restart, true);
            Scan(o, all, mcusX, mcusY, 0, 0, 1, 0, restart, true);
            if (!gray)
            {
                Scan(o, [comps[2]], mcusX, mcusY, 1, 63, 1, 0, restart, true);
                Scan(o, [comps[1]], mcusX, mcusY, 1, 63, 1, 0, restart, true);
            }
            Scan(o, lum, mcusX, mcusY, 1, 63, 1, 0, restart, true);
        }
        Word(o, 0xFFD9);
        return o.ToArray();
    }

    static Comp Make(int id, int hf, int vf, byte[] plane, int pw, int ph, int stride, int mcusX, int mcusY, byte[] q)
    {
        var c = new Comp
        {
            Id = id, H = hf, V = vf, BlocksX = mcusX * hf, BlocksY = mcusY * vf,
            CodedX = (pw + 7) / 8, CodedY = (ph + 7) / 8,
        };
        c.Coefs = new int[c.BlocksX * c.BlocksY * 64];
        Span<float> s = stackalloc float[64], f = stackalloc float[64];
        for (int by = 0; by < c.BlocksY; by++)
            for (int bx = 0; bx < c.BlocksX; bx++)
            {
                for (int yy = 0; yy < 8; yy++)
                    for (int xx = 0; xx < 8; xx++)
                        s[yy * 8 + xx] = plane[Math.Min(by * 8 + yy, ph - 1) * stride + Math.Min(bx * 8 + xx, pw - 1)] - 128f;
                JpegTables.Fdct(s, f);
                for (int i = 0; i < 64; i++) c.Coefs[(by * c.BlocksX + bx) * 64 + i] = (int)MathF.Round(f[i] / q[i]);
            }
        return c;
    }

    static int Cat(int v) { v = Math.Abs(v); int n = 0; while (v != 0) { n++; v >>= 1; } return n; }

    static void Scan(List<byte> o, List<Comp> sc, int mcusX, int mcusY, int ss, int se, int ah, int al, int restart, bool progressive)
    {
        Word(o, 0xFFDA); Word(o, 6 + 2 * sc.Count); o.Add((byte)sc.Count);
        foreach (var c in sc) { o.Add((byte)c.Id); o.Add(0x00); }
        o.Add((byte)ss); o.Add((byte)se); o.Add((byte)((ah << 4) | al));
        var b = new Bits(o);
        var st = new State();
        int count = sc.Count == 1 ? sc[0].CodedX * sc[0].CodedY : mcusX * mcusY, since = 0, rst = 0;
        var preds = new int[sc.Count];
        for (int m = 0; m < count; m++)
        {
            if (restart > 0 && since == restart)
            {
                EmitEobRun(b, st);
                b.Flush();
                Word(o, 0xFFD0 + (rst++ & 7));
                Array.Clear(preds);
                since = 0;
            }
            since++;
            if (sc.Count == 1)
            {
                var c = sc[0];
                int bx = m % c.CodedX, by = m / c.CodedX;
                Block(b, st, c.Coefs.AsSpan((by * c.BlocksX + bx) * 64, 64), ref preds[0], ss, se, ah, al, progressive);
            }
            else
            {
                int mx = m % mcusX, my = m / mcusX;
                for (int i = 0; i < sc.Count; i++)
                {
                    var c = sc[i];
                    for (int v = 0; v < c.V; v++)
                        for (int h = 0; h < c.H; h++)
                            Block(b, st, c.Coefs.AsSpan(((my * c.V + v) * c.BlocksX + mx * c.H + h) * 64, 64), ref preds[i], ss, se, ah, al, progressive);
                }
            }
        }
        EmitEobRun(b, st);
        b.Flush();
    }

    sealed class State
    {
        public int EobRun;
        public readonly List<int> EobBits = new(); // correction bits buffered with the EOB run (refinement)
    }

    /// <summary>Canonical code of an AC symbol: 0..254 at 10 bits, 255 at 11 bits.</summary>
    static void PutAc(Bits b, int s)
    {
        if (s < 255) b.Put(s, AcLen);
        else b.Put(255 << 1, AcLen + 1);
    }

    static void EmitEobRun(Bits b, State st)
    {
        if (st.EobRun > 0)
        {
            int n = 0;
            for (int t = st.EobRun; (t >>= 1) != 0;) n++;
            PutAc(b, n << 4);
            b.Put(st.EobRun, n);
            st.EobRun = 0;
        }
        foreach (int bit in st.EobBits) b.Put(bit, 1);
        st.EobBits.Clear();
    }

    static void Block(Bits b, State st, Span<int> blk, ref int pred, int ss, int se, int ah, int al, bool progressive)
    {
        if (!progressive)
        {
            int dc = blk[0], diff = dc - pred;
            pred = dc;
            int cat = Cat(diff);
            b.Put(cat, DcLen);
            b.Put(diff < 0 ? diff - 1 : diff, cat);
            int run = 0;
            for (int k = 1; k < 64; k++)
            {
                int c = blk[JpegTables.ZigZag[k]];
                if (c == 0) { run++; continue; }
                while (run > 15) { PutAc(b, 0xF0); run -= 16; }
                int s = Cat(c);
                PutAc(b, (run << 4) | s);
                b.Put(c < 0 ? c - 1 : c, s);
                run = 0;
            }
            if (run > 0) PutAc(b, 0x00);
            return;
        }
        if (ss == 0)
        {
            if (ah == 0)
            {
                int v = blk[0] >> al, diff = v - pred;
                pred = v;
                int cat = Cat(diff);
                b.Put(cat, DcLen);
                b.Put(diff < 0 ? diff - 1 : diff, cat);
            }
            else b.Put((blk[0] >> al) & 1, 1);
            return;
        }
        if (ah == 0)
        {
            int r = 0;
            for (int k = ss; k <= se; k++)
            {
                int c = blk[JpegTables.ZigZag[k]];
                int t = c < 0 ? -c >> al : c >> al;
                if (t == 0) { r++; continue; }
                EmitEobRun(b, st);
                while (r > 15) { PutAc(b, 0xF0); r -= 16; }
                int s = Cat(t);
                PutAc(b, (r << 4) | s);
                b.Put(c < 0 ? ~t : t, s);
                r = 0;
            }
            if (r > 0)
            {
                st.EobRun++;
                if (st.EobRun == 0x7FFF) EmitEobRun(b, st);
            }
            return;
        }
        // AC refinement
        Span<int> abs = stackalloc int[64];
        int eob = 0;
        for (int k = ss; k <= se; k++)
        {
            int c = blk[JpegTables.ZigZag[k]];
            abs[k] = (c < 0 ? -c : c) >> al;
            if (abs[k] == 1) eob = k;
        }
        var br = new List<int>();
        int rr = 0;
        for (int k = ss; k <= se; k++)
        {
            int t = abs[k];
            if (t == 0) { rr++; continue; }
            while (rr > 15 && k <= eob)
            {
                EmitEobRun(b, st);
                PutAc(b, 0xF0);
                rr -= 16;
                foreach (int bit in br) b.Put(bit, 1);
                br.Clear();
            }
            if (t > 1) { br.Add(t & 1); continue; }
            EmitEobRun(b, st);
            PutAc(b, (rr << 4) | 1);
            b.Put(blk[JpegTables.ZigZag[k]] < 0 ? 0 : 1, 1);
            foreach (int bit in br) b.Put(bit, 1);
            br.Clear();
            rr = 0;
        }
        if (rr > 0 || br.Count > 0)
        {
            st.EobRun++;
            st.EobBits.AddRange(br);
            if (st.EobRun == 0x7FFF || st.EobBits.Count > 900) EmitEobRun(b, st);
        }
    }
}
