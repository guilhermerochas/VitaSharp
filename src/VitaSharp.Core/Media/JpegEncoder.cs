namespace VitaSharp.Core.Media;

/// <summary>
/// Managed baseline JPEG encoder (8-bit, Huffman with the typical Annex K tables) from planar YCbCr: 4:4:4, 4:2:2,
/// 4:2:0 (any 1/2/4 divisors) or grayscale. Edge MCUs replicate the last row/column.
/// </summary>
public static class JpegEncoder
{
    /// <summary>Quantization tables for a Vita compression ratio (0..255, higher = smaller file): the ratio maps to a
    /// scale 1..16 (as Vita3K's FFmpeg qscale) and the Annex K tables are multiplied by scale / 8, so the default ratio
    /// 64 (scale 4) is the classic "quality 75" table set.</summary>
    public static (byte[] Luma, byte[] Chroma) TablesForRatio(int ratio)
    {
        int scale = 1 + Math.Clamp(ratio, 0, 255) * 15 / 255;
        return (Scale(JpegTables.StdLumaQuant, scale), Scale(JpegTables.StdChromaQuant, scale));
    }

    static byte[] Scale(byte[] t, int scale)
    {
        var o = new byte[64];
        for (int i = 0; i < 64; i++) o[i] = (byte)Math.Clamp((t[i] * scale + 4) / 8, 1, 255);
        return o;
    }

    sealed class HuffCode
    {
        public readonly ushort[] Code = new ushort[256];
        public readonly byte[] Size = new byte[256];

        public HuffCode(byte[] bits, byte[] vals)
        {
            int code = 0, k = 0;
            for (int len = 1; len <= 16; len++)
            {
                for (int i = 0; i < bits[len - 1]; i++, k++, code++)
                {
                    Code[vals[k]] = (ushort)code;
                    Size[vals[k]] = (byte)len;
                }
                code <<= 1;
            }
        }
    }

    static readonly HuffCode DcY = new(JpegTables.DcLumaBits, JpegTables.DcLumaVals),
        AcY = new(JpegTables.AcLumaBits, JpegTables.AcLumaVals),
        DcC = new(JpegTables.DcChromaBits, JpegTables.DcChromaVals),
        AcC = new(JpegTables.AcChromaBits, JpegTables.AcChromaVals);

    sealed class Writer
    {
        public readonly MemoryStream Out = new();
        ulong _acc;
        int _n;

        public void Byte(int b) => Out.WriteByte((byte)b);
        public void Word(int w) { Byte(w >> 8); Byte(w & 0xFF); }

        public void Bits(int value, int count)
        {
            if (count == 0) return;
            _acc = (_acc << count) | ((uint)value & ((1u << count) - 1));
            _n += count;
            while (_n >= 8)
            {
                int b = (int)(_acc >> (_n - 8)) & 0xFF;
                Out.WriteByte((byte)b);
                if (b == 0xFF) Out.WriteByte(0);
                _n -= 8;
            }
        }

        /// <summary>Pads the last byte with 1 bits.</summary>
        public void Flush()
        {
            if (_n > 0) Bits(0x7F, 8 - _n);
            _acc = 0;
            _n = 0;
        }
    }

    /// <summary>Encodes the image. <paramref name="cb"/> empty = grayscale. Chroma planes are ceil(w / hdiv) x
    /// ceil(h / vdiv) with stride <paramref name="cStride"/>. <paramref name="writeHuffmanTables"/> = false omits APP0
    /// and DHT (MJPEG frame: decoders use the Annex K tables).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> y, int yStride, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int cStride,
        int width, int height, int hdiv, int vdiv, byte[] lumaQuant, byte[] chromaQuant, bool writeHuffmanTables = true,
        int restartInterval = 0)
    {
        if (width is < 1 or > 65535 || height is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(width));
        bool gray = cb.IsEmpty;
        if (gray) hdiv = vdiv = 1;
        var w = new Writer();
        w.Word(0xFFD8);
        if (writeHuffmanTables)
        {
            w.Word(0xFFE0); w.Word(16);
            foreach (char l in "JFIF") w.Byte(l);
            w.Byte(0); w.Byte(1); w.Byte(1); w.Byte(0); w.Word(1); w.Word(1); w.Byte(0); w.Byte(0);
        }
        w.Word(0xFFDB); w.Word(2 + 65 * (gray ? 1 : 2));
        w.Byte(0x00);
        for (int i = 0; i < 64; i++) w.Byte(lumaQuant[JpegTables.ZigZag[i]]);
        if (!gray)
        {
            w.Byte(0x01);
            for (int i = 0; i < 64; i++) w.Byte(chromaQuant[JpegTables.ZigZag[i]]);
        }
        int nc = gray ? 1 : 3;
        w.Word(0xFFC0); w.Word(8 + 3 * nc); w.Byte(8); w.Word(height); w.Word(width); w.Byte(nc);
        w.Byte(1); w.Byte((hdiv << 4) | vdiv); w.Byte(0);
        if (!gray)
        {
            w.Byte(2); w.Byte(0x11); w.Byte(1);
            w.Byte(3); w.Byte(0x11); w.Byte(1);
        }
        if (restartInterval > 0) { w.Word(0xFFDD); w.Word(4); w.Word(restartInterval); }
        if (writeHuffmanTables)
        {
            void Dht(int cls, byte[] bits, byte[] vals)
            {
                w.Word(0xFFC4); w.Word(2 + 17 + vals.Length); w.Byte(cls);
                foreach (var b in bits) w.Byte(b);
                foreach (var v in vals) w.Byte(v);
            }
            Dht(0x00, JpegTables.DcLumaBits, JpegTables.DcLumaVals);
            Dht(0x10, JpegTables.AcLumaBits, JpegTables.AcLumaVals);
            if (!gray)
            {
                Dht(0x01, JpegTables.DcChromaBits, JpegTables.DcChromaVals);
                Dht(0x11, JpegTables.AcChromaBits, JpegTables.AcChromaVals);
            }
        }
        w.Word(0xFFDA); w.Word(6 + 2 * nc); w.Byte(nc);
        w.Byte(1); w.Byte(0x00);
        if (!gray) { w.Byte(2); w.Byte(0x11); w.Byte(3); w.Byte(0x11); }
        w.Byte(0); w.Byte(63); w.Byte(0);

        int mcuW = 8 * hdiv, mcuH = 8 * vdiv;
        int mcusX = (width + mcuW - 1) / mcuW, mcusY = (height + mcuH - 1) / mcuH;
        int cw = (width + hdiv - 1) / hdiv, ch = (height + vdiv - 1) / vdiv;
        int predY = 0, predCb = 0, predCr = 0, sinceRestart = 0, rst = 0;
        Span<float> samples = stackalloc float[64];
        Span<float> coefs = stackalloc float[64];
        for (int my = 0; my < mcusY; my++)
            for (int mx = 0; mx < mcusX; mx++)
            {
                if (restartInterval > 0 && sinceRestart == restartInterval)
                {
                    w.Flush();
                    w.Word(0xFFD0 + (rst++ & 7));
                    predY = predCb = predCr = 0;
                    sinceRestart = 0;
                }
                sinceRestart++;
                for (int v = 0; v < vdiv; v++)
                    for (int h = 0; h < hdiv; h++)
                    {
                        Load(y, yStride, width, height, (mx * hdiv + h) * 8, (my * vdiv + v) * 8, samples);
                        Block(w, samples, coefs, lumaQuant, ref predY, DcY, AcY);
                    }
                if (gray) continue;
                Load(cb, cStride, cw, ch, mx * 8, my * 8, samples);
                Block(w, samples, coefs, chromaQuant, ref predCb, DcC, AcC);
                Load(cr, cStride, cw, ch, mx * 8, my * 8, samples);
                Block(w, samples, coefs, chromaQuant, ref predCr, DcC, AcC);
            }
        w.Flush();
        w.Word(0xFFD9);
        return w.Out.ToArray();
    }

    static void Load(ReadOnlySpan<byte> p, int stride, int pw, int ph, int x0, int y0, Span<float> dst)
    {
        for (int yy = 0; yy < 8; yy++)
        {
            int sy = Math.Min(y0 + yy, ph - 1) * stride;
            for (int xx = 0; xx < 8; xx++) dst[yy * 8 + xx] = p[sy + Math.Min(x0 + xx, pw - 1)] - 128f;
        }
    }

    static int Category(int v)
    {
        v = Math.Abs(v);
        int n = 0;
        while (v != 0) { n++; v >>= 1; }
        return n;
    }

    static void Block(Writer w, Span<float> samples, Span<float> coefs, byte[] q, ref int pred, HuffCode dc, HuffCode ac)
    {
        JpegTables.Fdct(samples, coefs);
        Span<int> zz = stackalloc int[64];
        for (int i = 0; i < 64; i++)
        {
            int n = JpegTables.ZigZag[i];
            zz[i] = (int)MathF.Round(coefs[n] / q[n], MidpointRounding.AwayFromZero);
        }
        zz[0] = Math.Clamp(zz[0], -2047, 2047);
        int diff = zz[0] - pred;
        pred = zz[0];
        int cat = Category(diff);
        w.Bits(dc.Code[cat], dc.Size[cat]);
        w.Bits(diff < 0 ? diff - 1 : diff, cat);
        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int c = Math.Clamp(zz[k], -1023, 1023);
            if (c == 0) { run++; continue; }
            while (run > 15) { w.Bits(ac.Code[0xF0], ac.Size[0xF0]); run -= 16; }
            int s = Category(c), sym = (run << 4) | s;
            w.Bits(ac.Code[sym], ac.Size[sym]);
            w.Bits(c < 0 ? c - 1 : c, s);
            run = 0;
        }
        if (run > 0) w.Bits(ac.Code[0x00], ac.Size[0x00]);
    }
}
