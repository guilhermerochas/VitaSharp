namespace VitaSharp.Core.Media;

/// <summary>
/// A hand-built H.264 stream for decoder tests on every platform (the desktop's FFmpeg and Android's MediaCodec): a
/// constrained-baseline stream of I_PCM macroblocks with deblocking off, so every conforming decoder must give the
/// pictures back bit-exact; picture order count type 2 (output order = decoding order, no reordering), one IDR picture
/// first (with SPS and PPS in band, as the Vita's players send them) and P-type pictures after it. No game content.
/// </summary>
public static class SyntheticH264
{
    /// <summary>One access unit (Annex B) and the planes it must decode to (Y full size, U and V at half resolution).</summary>
    public sealed record Unit(byte[] AccessUnit, byte[] Y, byte[] U, byte[] V);

    /// <summary>The access units of <paramref name="pictures"/> pictures of <paramref name="width"/> x
    /// <paramref name="height"/> (multiples of 16).</summary>
    public static List<Unit> Stream(int pictures, int width = 64, int height = 64)
    {
        if (width <= 0 || height <= 0 || width % 16 != 0 || height % 16 != 0)
        {
            throw new ArgumentException("width and height must be positive multiples of 16");
        }

        var units = new List<Unit>(pictures);
        for (int index = 0; index < pictures; index++)
        {
            byte[] accessUnit = Picture(index, width, height, out byte[] y, out byte[] u, out byte[] v);
            units.Add(new Unit(accessUnit, y, u, v));
        }
        return units;
    }

    /// <summary>The SPS and PPS NAL units (Annex B), e.g. for an MP4 sample description.</summary>
    public static byte[] ParameterSets(int width = 64, int height = 64) => [.. Sps(width, height), .. Pps()];

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _current;
        private int _count;

        public void U(int bits, uint value)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                Bit((int)(value >> i) & 1);
            }
        }

        public void Bit(int bit)
        {
            _current = (_current << 1) | bit;
            _count++;
            if (_count == 8)
            {
                _bytes.Add((byte)_current);
                _current = 0;
                _count = 0;
            }
        }

        public void Ue(uint value)
        {
            uint codeNum = value + 1;
            int length = 31 - System.Numerics.BitOperations.LeadingZeroCount(codeNum);
            U(length, 0);
            U(length + 1, codeNum);
        }

        public void Se(int value) => Ue(value <= 0 ? (uint)(-2 * value) : (uint)(2 * value - 1));

        public void Align()
        {
            while (_count != 0)
            {
                Bit(0);
            }
        }

        public void Trailing()
        {
            Bit(1);
            Align();
        }

        public byte[] ToArray() => _bytes.ToArray();
    }

    /// <summary>A NAL unit with a 4-byte start code and emulation prevention bytes.</summary>
    private static byte[] Nal(int refIdc, int type, byte[] rbsp)
    {
        var output = new List<byte>(rbsp.Length + rbsp.Length / 64 + 8) { 0, 0, 0, 1, (byte)((refIdc << 5) | type) };
        int zeros = 0;
        foreach (byte value in rbsp)
        {
            if (zeros >= 2 && value <= 3)
            {
                output.Add(3);
                zeros = 0;
            }
            output.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return output.ToArray();
    }

    private static byte[] Sps(int width, int height)
    {
        var bits = new BitWriter();
        bits.U(8, 66);                              // profile_idc: baseline
        bits.U(8, 0x40);                            // constraint_set1: constrained baseline (hwaccel rejects plain baseline)
        bits.U(8, width / 16 * (height / 16) > 1620 ? 31u : 30u); // level 3.0, 3.1 above its 1620 macroblocks
        bits.Ue(0);                                 // sps id
        bits.Ue(0);                                 // log2_max_frame_num - 4
        bits.Ue(2);                                 // pic_order_cnt_type 2: output order = decoding order
        bits.Ue(1);                                 // max_num_ref_frames
        bits.Bit(0);                                // gaps_in_frame_num_value_allowed
        bits.Ue((uint)(width / 16 - 1));
        bits.Ue((uint)(height / 16 - 1));
        bits.Bit(1);                                // frame_mbs_only
        bits.Bit(1);                                // direct_8x8_inference
        bits.Bit(0);                                // no cropping
        bits.Bit(0);                                // no VUI
        bits.Trailing();
        return Nal(3, 7, bits.ToArray());
    }

    private static byte[] Pps()
    {
        var bits = new BitWriter();
        bits.Ue(0);                                 // pps id
        bits.Ue(0);                                 // sps id
        bits.Bit(0);                                // CAVLC
        bits.Bit(0);                                // no bottom field pic order
        bits.Ue(0);                                 // slice groups
        bits.Ue(0);                                 // ref idx l0
        bits.Ue(0);                                 // ref idx l1
        bits.Bit(0);                                // no weighted prediction
        bits.U(2, 0);                               // no weighted bipred
        bits.Se(0);                                 // pic_init_qp
        bits.Se(0);                                 // pic_init_qs
        bits.Se(0);                                 // chroma qp offset
        bits.Bit(1);                                // deblocking filter control present
        bits.Bit(0);                                // constrained intra off
        bits.Bit(0);                                // no redundant pic cnt
        bits.Trailing();
        return Nal(3, 8, bits.ToArray());
    }

    /// <summary>One picture of I_PCM macroblocks with deterministic sample values.</summary>
    private static byte[] Picture(int index, int width, int height, out byte[] y, out byte[] u, out byte[] v)
    {
        y = new byte[width * height];
        u = new byte[width * height / 4];
        v = new byte[width * height / 4];
        for (int i = 0; i < y.Length; i++)
        {
            y[i] = (byte)(16 + (i * 7 + index * 31) % 220);
        }

        for (int i = 0; i < u.Length; i++)
        {
            u[i] = (byte)(16 + (i * 3 + index * 17) % 220);
            v[i] = (byte)(16 + (i * 5 + index * 13) % 220);
        }

        bool idr = index == 0;
        var bits = new BitWriter();
        bits.Ue(0);                                 // first_mb_in_slice
        bits.Ue(7);                                 // slice_type: I (all slices)
        bits.Ue(0);                                 // pps id
        bits.U(4, (uint)index);                     // frame_num (4 bits, wraps)
        if (idr)
        {
            bits.Ue(0);                             // idr_pic_id
            bits.Bit(0);                            // no_output_of_prior_pics
            bits.Bit(0);                            // long_term_reference
        }
        else
        {
            bits.Bit(0);                            // adaptive_ref_pic_marking_mode
        }

        bits.Se(0);                                 // slice_qp_delta
        bits.Ue(1);                                 // disable_deblocking_filter_idc = 1
        int macroblocksPerRow = width / 16;
        int macroblocks = macroblocksPerRow * (height / 16);
        int chromaWidth = width / 2;
        for (int macroblock = 0; macroblock < macroblocks; macroblock++)
        {
            int mx = macroblock % macroblocksPerRow;
            int my = macroblock / macroblocksPerRow;
            bits.Ue(25);                            // mb_type I_PCM
            bits.Align();                           // pcm_alignment_zero_bits
            for (int row = 0; row < 16; row++)
            {
                for (int column = 0; column < 16; column++)
                {
                    bits.U(8, y[(my * 16 + row) * width + mx * 16 + column]);
                }
            }

            for (int row = 0; row < 8; row++)
            {
                for (int column = 0; column < 8; column++)
                {
                    bits.U(8, u[(my * 8 + row) * chromaWidth + mx * 8 + column]);
                }
            }

            for (int row = 0; row < 8; row++)
            {
                for (int column = 0; column < 8; column++)
                {
                    bits.U(8, v[(my * 8 + row) * chromaWidth + mx * 8 + column]);
                }
            }
        }
        bits.Trailing();
        byte[] slice = Nal(3, idr ? 5 : 1, bits.ToArray());
        return idr ? [.. Sps(width, height), .. Pps(), .. slice] : slice;
    }
}
