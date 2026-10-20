namespace VitaSharp.Core.Media;

/// <summary>Frame header of a JPEG stream.</summary>
public sealed class JpegInfo
{
    public int Width, Height;
    public bool Progressive;
    /// <summary>Single component (luma only).</summary>
    public bool Gray;
    /// <summary>Chroma subsampling divisors (luma samples per chroma sample); 1/1 for grayscale.</summary>
    public int HDiv = 1, VDiv = 1;
    /// <summary>MCU size in luma pixels.</summary>
    public int McuWidth => 8 * HDiv;
    public int McuHeight => 8 * VDiv;
    /// <summary>SCE_JPEG_COLORSPACE_* code: 0x10101 grayscale, else 0x20000 | (hdiv &lt;&lt; 8) | vdiv.</summary>
    public int ColorSpace => Gray ? 0x10101 : 0x20000 | (HDiv << 8) | VDiv;
}

/// <summary>One decoded component. <see cref="Data"/> holds whole blocks (MCU padded); the samples that belong to the
/// image are the top-left <see cref="Width"/> x <see cref="Height"/>.</summary>
public sealed class JpegPlane
{
    public required byte[] Data;
    public int Stride, Rows, Width, Height;
}

public sealed class JpegImage
{
    public required JpegInfo Info;
    /// <summary>Y (and Cb, Cr unless grayscale).</summary>
    public required JpegPlane[] Planes;
}

/// <summary>
/// Managed JPEG decoder: baseline and extended sequential (8-bit, Huffman), progressive (spectral selection and
/// successive approximation), grayscale or YCbCr with any subsampling where both chroma components share the factors
/// (4:4:4, 4:4:0, 4:4:1, 4:2:2, 4:2:0, 4:1:1), restart intervals. Streams without DHT (MJPEG) use the Annex K tables.
/// Output is planar YCbCr at native component resolution, nothing is color converted.
/// </summary>
public static class JpegDecoder
{
    public static JpegInfo ReadInfo(ReadOnlySpan<byte> data) => new Parser(data.ToArray()).Run(headerOnly: true).Info!;

    public static JpegImage Decode(ReadOnlySpan<byte> data)
    {
        var p = new Parser(data.ToArray()).Run(headerOnly: false);
        return p.BuildImage();
    }

    static JpegFormatException Fail(JpegError e, string m) => new(e, m);

    sealed class Huffman
    {
        // fast path: 9-bit prefix -> (length << 8) | symbol, length 0 = longer code
        public readonly ushort[] Fast = new ushort[1 << FastBits];
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValOffset = new int[17];
        public readonly byte[] Values;
        public const int FastBits = 9;

        public Huffman(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
        {
            Values = values.ToArray();
            int code = 0, k = 0;
            for (int len = 1; len <= 16; len++)
            {
                int n = bits[len - 1];
                ValOffset[len] = k - code;
                for (int i = 0; i < n; i++, k++, code++)
                {
                    if (len <= FastBits)
                    {
                        int shift = FastBits - len;
                        for (int j = 0; j < 1 << shift; j++) Fast[(code << shift) | j] = (ushort)((len << 8) | Values[k]);
                    }
                }
                MaxCode[len] = n > 0 ? code - 1 : -1;
                if (code > 1 << len) throw Fail(JpegError.BadDhtCounts, "DHT: code space overflow");
                code <<= 1;
            }
            MaxCode[17] = int.MaxValue;
        }
    }

    sealed class Component
    {
        public int Id, H, V, Tq;
        public int BlocksX, BlocksY;         // MCU padded
        public int CodedBlocksX, CodedBlocksY; // blocks covering the component (non-interleaved scans)
        public int Width, Height;            // samples covering the image
        public int[] Coefs = [];
        public int DcPred, DcTable, AcTable;
    }

    sealed class Parser(byte[] d)
    {
        readonly byte[] _d = d;
        int _pos;
        readonly ushort[]?[] _quant = new ushort[4][];
        readonly Huffman?[] _dc = new Huffman[4], _ac = new Huffman[4];
        Component[] _comps = [];
        int _hmax = 1, _vmax = 1, _mcusX, _mcusY, _restart;
        bool _sof, _anyScan;
        public JpegInfo? Info;

        // entropy decoder state
        uint _bits;
        int _nbits;
        bool _marker;
        int _eobrun;

        public Parser Run(bool headerOnly)
        {
            if (_d.Length < 2) throw Fail(JpegError.ImageEmpty, "empty stream");
            if (_d[0] != 0xFF || _d[1] != 0xD8) throw Fail(JpegError.NoSoi, "no SOI");
            _pos = 2;
            while (true)
            {
                int m = NextMarker();
                if (m < 0)
                {
                    // truncated stream without EOI: accept what was decoded
                    if (_anyScan || (headerOnly && _sof)) return this;
                    throw Fail(_sof ? JpegError.DecodeError : JpegError.BadSofMarker, "stream ended before the first scan");
                }
                switch (m)
                {
                    case 0xD8: throw Fail(JpegError.UnexpectedMarker, "SOI inside the stream");
                    case 0xD9:
                        if (!_anyScan) throw Fail(JpegError.DecodeError, "EOI before the first scan");
                        return this;
                    case 0xC4: ReadDht(Segment()); break;
                    case 0xDB: ReadDqt(Segment()); break;
                    case 0xDD:
                    {
                        var s = Segment();
                        if (s.Length != 2) throw Fail(JpegError.BadDriMarker, "DRI length");
                        _restart = (s[0] << 8) | s[1];
                        break;
                    }
                    case 0xC0 or 0xC1 or 0xC2: ReadSof(Segment(), m == 0xC2); break;
                    case 0xC3 or 0xC7 or 0xCB or 0xCF: throw Fail(JpegError.NoLosslessSupport, "lossless JPEG");
                    case 0xC9 or 0xCA or 0xCD or 0xCE or 0xCC: throw Fail(JpegError.NoArithSupport, "arithmetic coding");
                    case 0xC5 or 0xC6: throw Fail(JpegError.NoLosslessSupport, "hierarchical JPEG");
                    case 0xDA:
                    {
                        if (!_sof) throw Fail(JpegError.BadSosMarker, "SOS before SOF");
                        if (headerOnly) return this;
                        var s = Segment();
                        ReadScan(s);
                        break;
                    }
                    case (>= 0xD0 and <= 0xD7) or 0x01: break; // stray restart marker / TEM: no payload
                    case 0xDC or 0xFE or (>= 0xE0 and <= 0xEF) or 0xF0 or (>= 0xF1 and <= 0xFD): Segment(); break;
                    default: throw Fail(JpegError.UnknownMarker, $"marker 0x{m:X2}");
                }
            }
        }

        /// <summary>Next marker code at or after the current position (skipping garbage and fill bytes), or -1 at the end.</summary>
        int NextMarker()
        {
            while (_pos + 1 < _d.Length)
            {
                if (_d[_pos] != 0xFF) { _pos++; continue; }
                int c = _d[_pos + 1];
                if (c == 0xFF) { _pos++; continue; }
                if (c == 0x00) { _pos += 2; continue; }
                _pos += 2;
                return c;
            }
            _pos = _d.Length;
            return -1;
        }

        ReadOnlySpan<byte> Segment()
        {
            if (_pos + 2 > _d.Length) throw Fail(JpegError.BadMarkerLength, "truncated segment");
            int len = (_d[_pos] << 8) | _d[_pos + 1];
            if (len < 2 || _pos + len > _d.Length) throw Fail(JpegError.BadMarkerLength, "bad segment length");
            var s = new ReadOnlySpan<byte>(_d, _pos + 2, len - 2);
            _pos += len;
            return s;
        }

        void ReadDqt(ReadOnlySpan<byte> s)
        {
            while (s.Length > 0)
            {
                int pq = s[0] >> 4, tq = s[0] & 15;
                if (tq > 3) throw Fail(JpegError.BadDqtIndex, "DQT index");
                if (pq > 1) throw Fail(JpegError.BadDqtMarker, "DQT precision");
                int need = 1 + 64 * (pq + 1);
                if (s.Length < need) throw Fail(JpegError.BadDqtMarker, "DQT length");
                var q = new ushort[64];
                for (int i = 0; i < 64; i++)
                    q[JpegTables.ZigZag[i]] = pq == 0 ? s[1 + i] : (ushort)((s[1 + 2 * i] << 8) | s[2 + 2 * i]);
                _quant[tq] = q;
                s = s[need..];
            }
        }

        void ReadDht(ReadOnlySpan<byte> s)
        {
            while (s.Length > 0)
            {
                if (s.Length < 17) throw Fail(JpegError.BadDhtMarker, "DHT length");
                int tc = s[0] >> 4, th = s[0] & 15;
                if (tc > 1 || th > 3) throw Fail(JpegError.BadDhtIndex, "DHT index");
                int total = 0;
                for (int i = 0; i < 16; i++) total += s[1 + i];
                if (total > 256) throw Fail(JpegError.BadDhtCounts, "DHT counts");
                if (s.Length < 17 + total) throw Fail(JpegError.BadDhtMarker, "DHT length");
                var h = new Huffman(s.Slice(1, 16), s.Slice(17, total));
                (tc == 0 ? _dc : _ac)[th] = h;
                s = s[(17 + total)..];
            }
        }

        void ReadSof(ReadOnlySpan<byte> s, bool progressive)
        {
            if (_sof) throw Fail(JpegError.SofDuplicate, "second SOF");
            if (s.Length < 6) throw Fail(JpegError.BadSofMarker, "SOF length");
            if (s[0] != 8) throw Fail(JpegError.BadPrecision, $"{s[0]}-bit samples");
            int h = (s[1] << 8) | s[2], w = (s[3] << 8) | s[4], nf = s[5];
            if (s.Length != 6 + 3 * nf) throw Fail(JpegError.BadSofMarker, "SOF length");
            if (nf != 1 && nf != 3) throw Fail(JpegError.ComponentCount, $"{nf} components");
            if (w == 0 || h == 0) throw Fail(JpegError.ImageEmpty, "zero image size (DNL unsupported)");
            var comps = new Component[nf];
            for (int i = 0; i < nf; i++)
            {
                var c = new Component { Id = s[6 + 3 * i], H = s[7 + 3 * i] >> 4, V = s[7 + 3 * i] & 15, Tq = s[8 + 3 * i] };
                if (c.H is < 1 or > 4 || c.V is < 1 or > 4) throw Fail(JpegError.BadMcuSize, "sampling factor");
                if (c.Tq > 3) throw Fail(JpegError.BadDqtIndex, "component quant table");
                for (int j = 0; j < i; j++) if (comps[j].Id == c.Id) throw Fail(JpegError.BadComponentId, "duplicate component id");
                comps[i] = c;
            }
            var info = new JpegInfo { Width = w, Height = h, Progressive = progressive, Gray = nf == 1 };
            if (nf == 1) comps[0].H = comps[0].V = 1; // factors are meaningless for a single component
            else
            {
                var y = comps[0];
                var cb = comps[1];
                var cr = comps[2];
                // luma must carry the largest factors and both chroma components the same ones
                // (2x2,2x2,2x2 is 4:4:4)
                if (cb.H != cr.H || cb.V != cr.V || y.H % cb.H != 0 || y.V % cb.V != 0)
                    throw Fail(JpegError.UnsupportedSampling, "unsupported subsampling");
                int hs = y.H / cb.H, vs = y.V / cb.V;
                if ((hs, vs) is not ((1, 1) or (1, 2) or (1, 4) or (2, 1) or (2, 2) or (4, 1)))
                    throw Fail(JpegError.UnsupportedSampling, $"subsampling {hs}x{vs}");
                info.HDiv = hs;
                info.VDiv = vs;
            }
            _hmax = comps.Max(c => c.H);
            _vmax = comps.Max(c => c.V);
            if (nf == 3 && (comps[0].H != _hmax || comps[0].V != _vmax)) throw Fail(JpegError.UnsupportedSampling, "luma is not the largest component");
            _mcusX = (w + 8 * _hmax - 1) / (8 * _hmax);
            _mcusY = (h + 8 * _vmax - 1) / (8 * _vmax);
            foreach (var c in comps)
            {
                c.Width = (w * c.H + _hmax - 1) / _hmax;
                c.Height = (h * c.V + _vmax - 1) / _vmax;
                c.CodedBlocksX = (c.Width + 7) / 8;
                c.CodedBlocksY = (c.Height + 7) / 8;
                c.BlocksX = _mcusX * c.H;
                c.BlocksY = _mcusY * c.V;
            }
            _comps = comps;
            _sof = true;
            Info = info;
        }

        void ReadScan(ReadOnlySpan<byte> s)
        {
            if (s.Length < 1) throw Fail(JpegError.BadSosMarker, "SOS length");
            int ns = s[0];
            if (ns < 1 || ns > 4 || s.Length != 4 + 2 * ns) throw Fail(JpegError.BadSosMarker, "SOS length");
            var sc = new Component[ns];
            for (int i = 0; i < ns; i++)
            {
                int id = s[1 + 2 * i], t = s[2 + 2 * i];
                var c = Array.Find(_comps, x => x.Id == id) ?? throw Fail(JpegError.BadComponentId, $"scan component {id}");
                if (Array.IndexOf(sc, c) >= 0) throw Fail(JpegError.BadComponentId, "component twice in a scan");
                c.DcTable = t >> 4;
                c.AcTable = t & 15;
                if (c.DcTable > 3 || c.AcTable > 3) throw Fail(JpegError.BadDhtIndex, "scan table index");
                sc[i] = c;
            }
            int ss = s[1 + 2 * ns], se = s[2 + 2 * ns], ah = s[3 + 2 * ns] >> 4, al = s[3 + 2 * ns] & 15;
            bool progressive = Info!.Progressive;
            if (progressive)
            {
                bool ok = ss == 0 ? se == 0 : se >= ss && se <= 63 && ns == 1;
                if (!ok || al > 13 || (ah != 0 && ah != al + 1)) throw Fail(JpegError.BadProgressiveParam, "progressive scan parameters");
            }
            else { ss = 0; se = 63; ah = al = 0; }
            if (ns > 1)
            {
                int blocks = 0;
                foreach (var c in sc) blocks += c.H * c.V;
                if (blocks > 10) throw Fail(JpegError.BadMcuSize, "more than 10 blocks per MCU");
            }
            foreach (var c in sc)
            {
                if (c.Coefs.Length == 0) c.Coefs = new int[c.BlocksX * c.BlocksY * 64];
                bool needDc = ss == 0 && ah == 0, needAc = se > 0;
                if (!progressive) { needDc = true; needAc = true; }
                if (needDc) EnsureTable(_dc, c.DcTable, true);
                if (needAc) EnsureTable(_ac, c.AcTable, false);
                c.DcPred = 0;
            }

            _bits = 0; _nbits = 0; _marker = false; _eobrun = 0;
            int mcuCount = ns == 1 ? sc[0].CodedBlocksX * sc[0].CodedBlocksY : _mcusX * _mcusY;
            int sinceRestart = 0;
            for (int mcu = 0; mcu < mcuCount; mcu++)
            {
                if (_restart > 0 && sinceRestart == _restart)
                {
                    Restart(sc);
                    sinceRestart = 0;
                }
                sinceRestart++;
                if (ns == 1)
                {
                    var c = sc[0];
                    int bx = mcu % c.CodedBlocksX, by = mcu / c.CodedBlocksX;
                    DecodeBlock(c, by * c.BlocksX + bx, ss, se, ah, al, progressive);
                }
                else
                {
                    int mx = mcu % _mcusX, my = mcu / _mcusX;
                    foreach (var c in sc)
                        for (int v = 0; v < c.V; v++)
                            for (int h = 0; h < c.H; h++)
                                DecodeBlock(c, (my * c.V + v) * c.BlocksX + mx * c.H + h, ss, se, ah, al, progressive);
                }
            }
            // the bit reader never moves past a marker: NextMarker() skips the rest of the entropy data
            _anyScan = true;
        }

        void EnsureTable(Huffman?[] set, int index, bool dc)
        {
            if (set[index] != null) return;
            // MJPEG streams omit DHT: tables 0/1 default to the typical luminance/chrominance tables
            if (index > 1) throw Fail(JpegError.NoHuffTable, $"missing Huffman table {index}");
            set[index] = dc
                ? index == 0 ? new Huffman(JpegTables.DcLumaBits, JpegTables.DcLumaVals) : new Huffman(JpegTables.DcChromaBits, JpegTables.DcChromaVals)
                : index == 0 ? new Huffman(JpegTables.AcLumaBits, JpegTables.AcLumaVals) : new Huffman(JpegTables.AcChromaBits, JpegTables.AcChromaVals);
        }

        void Restart(Component[] sc)
        {
            _bits = 0; _nbits = 0;
            if (!_marker)
            {
                // skip to the next marker
                while (_pos + 1 < _d.Length && !(_d[_pos] == 0xFF && _d[_pos + 1] != 0 && _d[_pos + 1] != 0xFF)) _pos++;
            }
            while (_pos + 1 < _d.Length && _d[_pos] == 0xFF && _d[_pos + 1] == 0xFF) _pos++;
            if (_pos + 1 < _d.Length && _d[_pos] == 0xFF && _d[_pos + 1] is >= 0xD0 and <= 0xD7) _pos += 2;
            else if (_pos + 1 < _d.Length) throw Fail(JpegError.BadRestartMarker, "expected RSTn");
            _marker = false;
            _eobrun = 0;
            foreach (var c in sc) c.DcPred = 0;
        }

        // ---------------------------------------------------------------- bit reader

        void Fill()
        {
            while (_nbits <= 24)
            {
                int b = 0;
                if (!_marker && _pos < _d.Length)
                {
                    b = _d[_pos];
                    if (b == 0xFF)
                    {
                        int n = _pos + 1 < _d.Length ? _d[_pos + 1] : 0xD9;
                        if (n == 0) _pos += 2;
                        else { _marker = true; b = 0; }
                    }
                    else _pos++;
                }
                _bits |= (uint)b << (24 - _nbits);
                _nbits += 8;
            }
        }

        int Bits(int n)
        {
            if (n == 0) return 0;
            if (_nbits < n) Fill();
            int v = (int)(_bits >> (32 - n));
            _bits <<= n;
            _nbits -= n;
            return v;
        }

        int Bit() => Bits(1);

        static int Extend(int v, int n) => v < 1 << (n - 1) ? v - (1 << n) + 1 : v;

        int Receive(int n) => n == 0 ? 0 : Extend(Bits(n), n);

        int DecodeSymbol(Huffman h)
        {
            if (_nbits < 16) Fill();
            int e = h.Fast[_bits >> (32 - Huffman.FastBits)];
            if (e != 0)
            {
                int len = e >> 8;
                _bits <<= len;
                _nbits -= len;
                return e & 0xFF;
            }
            for (int len = Huffman.FastBits + 1; len <= 16; len++)
            {
                int code = (int)(_bits >> (32 - len));
                if (code <= h.MaxCode[len])
                {
                    _bits <<= len;
                    _nbits -= len;
                    int idx = code + h.ValOffset[len];
                    if ((uint)idx >= (uint)h.Values.Length) throw Fail(JpegError.DecodeError, "bad Huffman code");
                    return h.Values[idx];
                }
            }
            throw Fail(JpegError.DecodeError, "bad Huffman code");
        }

        // ---------------------------------------------------------------- blocks

        void DecodeBlock(Component c, int block, int ss, int se, int ah, int al, bool progressive)
        {
            var blk = c.Coefs.AsSpan(block * 64, 64);
            if (!progressive)
            {
                int t = DecodeSymbol(_dc[c.DcTable]!);
                if (t > 16) throw Fail(JpegError.DecodeError, "DC magnitude");
                c.DcPred += Receive(t);
                blk[0] = c.DcPred;
                var ac = _ac[c.AcTable]!;
                for (int k = 1; k < 64;)
                {
                    int rs = DecodeSymbol(ac), r = rs >> 4, s = rs & 15;
                    if (s == 0)
                    {
                        if (r != 15) break;
                        k += 16;
                        continue;
                    }
                    k += r;
                    if (k > 63) throw Fail(JpegError.DecodeError, "AC index past 63");
                    blk[JpegTables.ZigZag[k]] = Receive(s);
                    k++;
                }
                return;
            }

            if (ss == 0)
            {
                if (ah == 0)
                {
                    int t = DecodeSymbol(_dc[c.DcTable]!);
                    if (t > 16) throw Fail(JpegError.DecodeError, "DC magnitude");
                    c.DcPred += Receive(t);
                    blk[0] = c.DcPred * (1 << al);
                }
                else if (Bit() != 0) blk[0] |= 1 << al;
                return;
            }

            var h = _ac[c.AcTable]!;
            if (ah == 0)
            {
                if (_eobrun > 0) { _eobrun--; return; }
                for (int k = ss; k <= se; k++)
                {
                    int rs = DecodeSymbol(h), r = rs >> 4, s = rs & 15;
                    if (s == 0)
                    {
                        if (r < 15)
                        {
                            _eobrun = (1 << r) - 1;
                            if (r > 0) _eobrun += Bits(r);
                            break;
                        }
                        k += 15;
                        continue;
                    }
                    k += r;
                    if (k > 63) throw Fail(JpegError.DecodeError, "AC index past 63");
                    blk[JpegTables.ZigZag[k]] = Receive(s) * (1 << al);
                }
                return;
            }

            // AC successive approximation refinement (G.1.2.3)
            int p1 = 1 << al, m1 = -1 << al;
            int kk = ss;
            if (_eobrun == 0)
            {
                for (; kk <= se; kk++)
                {
                    int rs = DecodeSymbol(h), r = rs >> 4, s = rs & 15;
                    int val = 0;
                    if (s != 0)
                    {
                        if (s != 1) throw Fail(JpegError.DecodeError, "refinement magnitude");
                        val = Bit() != 0 ? p1 : m1;
                    }
                    else if (r != 15)
                    {
                        _eobrun = 1 << r;
                        if (r > 0) _eobrun += Bits(r);
                        break;
                    }
                    while (kk <= se)
                    {
                        int z = JpegTables.ZigZag[kk];
                        if (blk[z] != 0)
                        {
                            if (Bit() != 0 && (blk[z] & p1) == 0) blk[z] += blk[z] >= 0 ? p1 : m1;
                        }
                        else
                        {
                            if (r == 0) break;
                            r--;
                        }
                        kk++;
                    }
                    if (val != 0 && kk <= se) blk[JpegTables.ZigZag[kk]] = val;
                }
            }
            if (_eobrun > 0)
            {
                for (; kk <= se; kk++)
                {
                    int z = JpegTables.ZigZag[kk];
                    if (blk[z] != 0 && Bit() != 0 && (blk[z] & p1) == 0) blk[z] += blk[z] >= 0 ? p1 : m1;
                }
                _eobrun--;
            }
        }

        // ---------------------------------------------------------------- output

        public JpegImage BuildImage()
        {
            var info = Info!;
            var planes = new JpegPlane[_comps.Length];
            Span<int> tmp = stackalloc int[64];
            for (int ci = 0; ci < _comps.Length; ci++)
            {
                var c = _comps[ci];
                var q = _quant[c.Tq] ?? throw Fail(JpegError.NoQuantTable, $"missing quant table {c.Tq}");
                int stride = c.BlocksX * 8, rows = c.BlocksY * 8;
                var data = new byte[stride * rows];
                if (c.Coefs.Length == 0) c.Coefs = new int[c.BlocksX * c.BlocksY * 64]; // never scanned: mid grey
                for (int by = 0; by < c.BlocksY; by++)
                    for (int bx = 0; bx < c.BlocksX; bx++)
                    {
                        var blk = c.Coefs.AsSpan((by * c.BlocksX + bx) * 64, 64);
                        for (int i = 0; i < 64; i++) tmp[i] = blk[i] * q[i];
                        JpegTables.Idct(tmp, data.AsSpan(by * 8 * stride + bx * 8), stride);
                    }
                planes[ci] = new JpegPlane { Data = data, Stride = stride, Rows = rows, Width = c.Width, Height = c.Height };
            }
            return new JpegImage { Info = info, Planes = planes };
        }
    }
}
