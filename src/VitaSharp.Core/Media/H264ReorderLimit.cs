namespace VitaSharp.Core.Media;

/// <summary>
/// Gives an H.264 stream's sequence parameter sets an explicit reorder limit (VUI bitstream_restriction with
/// max_num_reorder_frames), for decoders that otherwise hold a whole DPB of pictures back.
/// <para>A stream whose SPS has no bitstream_restriction does not say how far its pictures are reordered, so a strictly
/// conforming decoder outputs a picture only when its DPB is full (H.264 C.4.5.3 "bumping"): Android's own software
/// decoder (libavc in c2.android.avc.decoder) takes the reorder depth from the DPB size then (16 pictures for the
/// 64x64 parity clip on the Redmi Note 14: its first picture came out in call 18, in call 3 with the limit; 8 for a
/// 960x544 level 3.1 stream). The Vita's decoder answers each sceAvcdecDecode call with the picture of that call's unit
/// for such streams, like FFmpeg, which assumes no reordering until it sees some. A stream without B slices never
/// reorders, so stating max_num_reorder_frames = 0 changes nothing in what is decoded, only when it comes out. (F1
/// 2011's movies have B frames and state their own limit, 2: they are left as they are.)</para>
/// <para>Only parameter sets are rewritten; an SPS that already has bitstream_restriction, or that cannot be parsed, is
/// left as it is. The caller decides whether the stream may reorder (B slices: <see cref="IsBSlice"/>).</para>
/// </summary>
public static class H264ReorderLimit
{
    /// <summary>
    /// <paramref name="accessUnit"/> with every SPS NAL unit lacking a reorder limit rewritten to state
    /// <paramref name="maxReorderFrames"/>; the same array when nothing changed.
    /// </summary>
    public static byte[] Apply(byte[] accessUnit, int maxReorderFrames)
    {
        List<(int Start, int End)> nals = NalUnits(accessUnit, stopAtSlice: true);
        List<byte>? output = null;
        int copied = 0;
        foreach ((int start, int end) in nals)
        {
            if ((accessUnit[start] & 0x1F) != 7)
            {
                continue;
            }

            byte[]? rewritten = RewriteSps(accessUnit.AsSpan(start, end - start), maxReorderFrames);
            if (rewritten == null)
            {
                continue;
            }

            output ??= new List<byte>(accessUnit.Length + 16);
            output.AddRange(accessUnit.AsSpan(copied, start - copied).ToArray());
            output.AddRange(rewritten);
            copied = end;
        }

        if (output == null)
        {
            return accessUnit;
        }

        output.AddRange(accessUnit.AsSpan(copied).ToArray());
        return output.ToArray();
    }

    /// <summary>
    /// The max_num_reorder_frames the access unit's first SPS states (VUI bitstream_restriction), or null when it has no
    /// SPS, states no limit or does not parse. A stream that states more than 0 reorders (B-frames): the Vita's decoder
    /// gives its first picture only after that many units, like FFmpeg.
    /// </summary>
    public static int? StatedReorderFrames(ReadOnlySpan<byte> accessUnit)
    {
        foreach ((int start, int end) in NalUnits(accessUnit, stopAtSlice: true))
        {
            if ((accessUnit[start] & 0x1F) != 7)
            {
                continue;
            }

            byte[] rbsp = ToRbsp(accessUnit.Slice(start + 1, end - start - 1));
            var reader = new BitReader(rbsp);
            if (!TryParseUpToVui(reader, out _, out int vuiFlagPosition, out bool hasVui) || !hasVui)
            {
                return null;
            }

            reader.Position = vuiFlagPosition + 1;
            if (!TrySkipVuiUpToRestriction(reader, out bool hasRestriction) || !hasRestriction)
            {
                return null;
            }

            // bitstream_restriction_flag, motion_vectors_over_pic_boundaries_flag, max_bytes_per_pic_denom,
            // max_bits_per_mb_denom, log2_max_mv_length_horizontal and _vertical, then max_num_reorder_frames
            if (!reader.TryU(2, out _) || !reader.TryUe(out _) || !reader.TryUe(out _) || !reader.TryUe(out _) || !reader.TryUe(out _)
                || !reader.TryUe(out uint reorderFrames))
            {
                return null;
            }

            return (int)Math.Min(reorderFrames, 16u);
        }
        return null;
    }

    /// <summary>True when the access unit has an SPS NAL unit (type 7).</summary>
    public static bool ContainsSps(ReadOnlySpan<byte> accessUnit)
    {
        foreach ((int start, int _) in NalUnits(accessUnit, stopAtSlice: true))
        {
            if ((accessUnit[start] & 0x1F) == 7)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True when the access unit's first slice is a B slice (slice_type 1 or 6): the stream reorders.</summary>
    public static bool IsBSlice(ReadOnlySpan<byte> accessUnit)
    {
        foreach ((int start, int end) in NalUnits(accessUnit, stopAtSlice: true))
        {
            int type = accessUnit[start] & 0x1F;
            if (type != 1 && type != 5)
            {
                continue;
            }

            byte[] rbsp = ToRbsp(accessUnit.Slice(start + 1, Math.Min(end - start - 1, 16)));
            var reader = new BitReader(rbsp);
            if (!reader.TryUe(out _) || !reader.TryUe(out uint sliceType))
            {
                return false;
            }

            return sliceType % 5 == 1;
        }
        return false;
    }

    /// <summary>The NAL units of an Annex B access unit: (first byte = NAL header, end), start codes excluded.
    /// <paramref name="stopAtSlice"/>: up to the first slice (its end is then the end of the data, its contents are not
    /// scanned): parameter sets come before it.</summary>
    internal static List<(int Start, int End)> NalUnits(ReadOnlySpan<byte> data, bool stopAtSlice = false)
    {
        var units = new List<(int Start, int End)>();
        int start = -1;
        int i = 0;
        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0)
                {
                    int end = i;
                    while (end > start && data[end - 1] == 0)
                    {
                        end--;
                    }
                    units.Add((start, end));
                }
                start = i + 3;
                i += 3;
                if (stopAtSlice && start < data.Length && (data[start] & 0x1F) is 1 or 5)
                {
                    break;
                }

                continue;
            }
            i++;
        }

        if (start >= 0 && start < data.Length)
        {
            units.Add((start, data.Length));
        }

        return units;
    }

    private static byte[] ToRbsp(ReadOnlySpan<byte> escaped)
    {
        var rbsp = new List<byte>(escaped.Length);
        int zeros = 0;
        foreach (byte value in escaped)
        {
            if (zeros >= 2 && value == 3)
            {
                zeros = 0;
                continue;
            }
            rbsp.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return rbsp.ToArray();
    }

    private static byte[] ToEscaped(byte header, byte[] rbsp)
    {
        var escaped = new List<byte>(rbsp.Length + rbsp.Length / 64 + 2) { header };
        int zeros = 0;
        foreach (byte value in rbsp)
        {
            if (zeros >= 2 && value <= 3)
            {
                escaped.Add(3);
                zeros = 0;
            }
            escaped.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return escaped.ToArray();
    }

    /// <summary>The SPS NAL unit (header byte first, no start code) with a reorder limit, or null when it already has one
    /// or does not parse.</summary>
    internal static byte[]? RewriteSps(ReadOnlySpan<byte> nal, int maxReorderFrames)
    {
        byte[] rbsp = ToRbsp(nal[1..]);
        var reader = new BitReader(rbsp);
        if (!TryParseUpToVui(reader, out uint maxRefFrames, out int vuiFlagPosition, out bool hasVui))
        {
            return null;
        }

        var writer = new BitWriter();
        writer.Copy(rbsp, 0, vuiFlagPosition);
        writer.Bit(1); // vui_parameters_present_flag
        if (hasVui)
        {
            reader.Position = vuiFlagPosition + 1;
            if (!TrySkipVuiUpToRestriction(reader, out bool hasRestriction) || hasRestriction)
            {
                return null;
            }

            writer.Copy(rbsp, vuiFlagPosition + 1, reader.Position);
        }
        else
        {
            writer.Bit(0);                          // aspect_ratio_info_present_flag
            writer.Bit(0);                          // overscan_info_present_flag
            writer.Bit(0);                          // video_signal_type_present_flag
            writer.Bit(0);                          // chroma_loc_info_present_flag
            writer.Bit(0);                          // timing_info_present_flag
            writer.Bit(0);                          // nal_hrd_parameters_present_flag
            writer.Bit(0);                          // vcl_hrd_parameters_present_flag
            writer.Bit(0);                          // pic_struct_present_flag
        }

        writer.Bit(1);                              // bitstream_restriction_flag
        writer.Bit(1);                              // motion_vectors_over_pic_boundaries_flag (the default)
        writer.Ue(2);                               // max_bytes_per_pic_denom (the default)
        writer.Ue(1);                               // max_bits_per_mb_denom (the default)
        writer.Ue(16);                              // log2_max_mv_length_horizontal (the default)
        writer.Ue(16);                              // log2_max_mv_length_vertical (the default)
        writer.Ue((uint)maxReorderFrames);          // max_num_reorder_frames
        writer.Ue(Math.Max(maxRefFrames, (uint)maxReorderFrames)); // max_dec_frame_buffering
        writer.Bit(1);                              // rbsp_stop_one_bit
        writer.Align();
        return ToEscaped(nal[0], writer.ToArray());
    }

    private static readonly int[] s_highProfiles = [100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135];

    private static bool TryParseUpToVui(BitReader reader, out uint maxRefFrames, out int vuiFlagPosition, out bool hasVui)
    {
        maxRefFrames = 0;
        vuiFlagPosition = 0;
        hasVui = false;
        if (!reader.TryU(8, out uint profile) || !reader.TryU(16, out _) || !reader.TryUe(out _))
        {
            return false;
        }

        if (Array.IndexOf(s_highProfiles, (int)profile) >= 0)
        {
            if (!reader.TryUe(out uint chromaFormat))
            {
                return false;
            }

            if (chromaFormat == 3 && !reader.TryU(1, out _))
            {
                return false;
            }

            if (!reader.TryUe(out _) || !reader.TryUe(out _) || !reader.TryU(1, out _) || !reader.TryU(1, out uint scalingMatrix))
            {
                return false;
            }

            if (scalingMatrix == 1)
            {
                int lists = chromaFormat == 3 ? 12 : 8;
                for (int list = 0; list < lists; list++)
                {
                    if (!reader.TryU(1, out uint present))
                    {
                        return false;
                    }

                    if (present == 1 && !TrySkipScalingList(reader, list < 6 ? 16 : 64))
                    {
                        return false;
                    }
                }
            }
        }

        if (!reader.TryUe(out _) || !reader.TryUe(out uint pocType))
        {
            return false;
        }

        if (pocType == 0)
        {
            if (!reader.TryUe(out _))
            {
                return false;
            }
        }
        else if (pocType == 1)
        {
            if (!reader.TryU(1, out _) || !reader.TrySe(out _) || !reader.TrySe(out _) || !reader.TryUe(out uint cycle) || cycle > 255)
            {
                return false;
            }

            for (uint i = 0; i < cycle; i++)
            {
                if (!reader.TrySe(out _))
                {
                    return false;
                }
            }
        }

        if (!reader.TryUe(out maxRefFrames) || !reader.TryU(1, out _) || !reader.TryUe(out _) || !reader.TryUe(out _)
            || !reader.TryU(1, out uint frameMbsOnly))
        {
            return false;
        }

        if (frameMbsOnly == 0 && !reader.TryU(1, out _))
        {
            return false;
        }

        if (!reader.TryU(1, out _) || !reader.TryU(1, out uint cropping))
        {
            return false;
        }

        if (cropping == 1)
        {
            for (int i = 0; i < 4; i++)
            {
                if (!reader.TryUe(out _))
                {
                    return false;
                }
            }
        }

        vuiFlagPosition = reader.Position;
        if (!reader.TryU(1, out uint vui))
        {
            return false;
        }

        hasVui = vui == 1;
        return true;
    }

    private static bool TrySkipScalingList(BitReader reader, int size)
    {
        int lastScale = 8;
        int nextScale = 8;
        for (int j = 0; j < size; j++)
        {
            if (nextScale != 0)
            {
                if (!reader.TrySe(out int delta))
                {
                    return false;
                }

                nextScale = (lastScale + delta + 256) % 256;
            }
            lastScale = nextScale == 0 ? lastScale : nextScale;
        }
        return true;
    }

    private static bool TrySkipVuiUpToRestriction(BitReader reader, out bool hasRestriction)
    {
        hasRestriction = false;
        if (!reader.TryU(1, out uint aspect))
        {
            return false;
        }

        if (aspect == 1)
        {
            if (!reader.TryU(8, out uint aspectIdc))
            {
                return false;
            }

            if (aspectIdc == 255 && !reader.TryU(32, out _))
            {
                return false;
            }
        }

        if (!reader.TryU(1, out uint overscan) || (overscan == 1 && !reader.TryU(1, out _)))
        {
            return false;
        }

        if (!reader.TryU(1, out uint signal))
        {
            return false;
        }

        if (signal == 1)
        {
            if (!reader.TryU(4, out _) || !reader.TryU(1, out uint colourDescription) || (colourDescription == 1 && !reader.TryU(24, out _)))
            {
                return false;
            }
        }

        if (!reader.TryU(1, out uint chromaLocation) || (chromaLocation == 1 && (!reader.TryUe(out _) || !reader.TryUe(out _))))
        {
            return false;
        }

        if (!reader.TryU(1, out uint timing) || (timing == 1 && !reader.TryU(32, out _)) || (timing == 1 && !reader.TryU(32, out _)) || (timing == 1 && !reader.TryU(1, out _)))
        {
            return false;
        }

        if (!reader.TryU(1, out uint nalHrd) || (nalHrd == 1 && !TrySkipHrd(reader)))
        {
            return false;
        }

        if (!reader.TryU(1, out uint vclHrd) || (vclHrd == 1 && !TrySkipHrd(reader)))
        {
            return false;
        }

        if ((nalHrd == 1 || vclHrd == 1) && !reader.TryU(1, out _))
        {
            return false;
        }

        if (!reader.TryU(1, out _))
        {
            return false;
        }

        int restrictionPosition = reader.Position;
        if (!reader.TryU(1, out uint restriction))
        {
            return false;
        }

        hasRestriction = restriction == 1;
        reader.Position = restrictionPosition;
        return true;
    }

    private static bool TrySkipHrd(BitReader reader)
    {
        if (!reader.TryUe(out uint cpbCount) || cpbCount > 31 || !reader.TryU(8, out _))
        {
            return false;
        }

        for (uint i = 0; i <= cpbCount; i++)
        {
            if (!reader.TryUe(out _) || !reader.TryUe(out _) || !reader.TryU(1, out _))
            {
                return false;
            }
        }
        return reader.TryU(20, out _);
    }

    private sealed class BitReader(byte[] data)
    {
        public int Position;

        public bool TryU(int bits, out uint value)
        {
            value = 0;
            if (Position + bits > data.Length * 8)
            {
                return false;
            }

            for (int i = 0; i < bits; i++)
            {
                value = (value << 1) | (uint)((data[Position >> 3] >> (7 - (Position & 7))) & 1);
                Position++;
            }
            return true;
        }

        public bool TryUe(out uint value)
        {
            value = 0;
            int zeros = 0;
            while (true)
            {
                if (!TryU(1, out uint bit))
                {
                    return false;
                }

                if (bit == 1)
                {
                    break;
                }

                zeros++;
                if (zeros > 31)
                {
                    return false;
                }
            }

            if (!TryU(zeros, out uint suffix))
            {
                return false;
            }

            value = (uint)((1UL << zeros) - 1 + suffix);
            return true;
        }

        public bool TrySe(out int value)
        {
            value = 0;
            if (!TryUe(out uint codeNum))
            {
                return false;
            }

            value = (codeNum & 1) == 1 ? (int)((codeNum + 1) / 2) : -(int)(codeNum / 2);
            return true;
        }
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _current;
        private int _count;

        public void Bit(int bit)
        {
            _current = (_current << 1) | (bit & 1);
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
            for (int i = 0; i < length; i++)
            {
                Bit(0);
            }

            for (int i = length; i >= 0; i--)
            {
                Bit((int)(codeNum >> i) & 1);
            }
        }

        public void Copy(byte[] source, int fromBit, int toBit)
        {
            for (int position = fromBit; position < toBit; position++)
            {
                Bit((source[position >> 3] >> (7 - (position & 7))) & 1);
            }
        }

        public void Align()
        {
            while (_count != 0)
            {
                Bit(0);
            }
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
