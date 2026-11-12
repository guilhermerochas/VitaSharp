using VitaSharp.Core.Media;

namespace VitaSharp.Tests;

/// <summary>
/// <see cref="H264ReorderLimit"/>: an SPS without a reorder limit gets VUI bitstream_restriction with
/// max_num_reorder_frames (Android's software decoder otherwise holds a whole DPB of pictures back: the black and late
/// pictures of the movies on the phone); the stream still decodes to the same pictures; an SPS that has the limit is
/// left alone; B slices are recognised.
/// </summary>
public sealed class H264ReorderLimitTests
{
    [Fact]
    public void AnSpsWithoutVuiGetsTheLimitAndStillParses()
    {
        List<SyntheticH264.Unit> stream = SyntheticH264.Stream(3);
        byte[] idr = stream[0].AccessUnit;
        Assert.True(H264ReorderLimit.ContainsSps(idr));
        Assert.False(H264ReorderLimit.ContainsSps(stream[1].AccessUnit));
        byte[] limited = H264ReorderLimit.Apply(idr, 0);
        Assert.NotSame(idr, limited);
        Assert.True(limited.Length > idr.Length);
        // the slice and the PPS are untouched, and a second pass finds the limit already there
        Assert.True(limited.AsSpan().EndsWith(idr.AsSpan(idr.Length - 200)));
        Assert.Same(limited, H264ReorderLimit.Apply(limited, 0));
        // non-IDR units have no SPS: the same array back
        Assert.Same(stream[1].AccessUnit, H264ReorderLimit.Apply(stream[1].AccessUnit, 0));
    }

    [FFmpegFact]
    public void TheLimitedStreamDecodesToTheSamePictures()
    {
        List<SyntheticH264.Unit> stream = SyntheticH264.Stream(4);
        using IVideoDecoder decoder = new FFmpegVideoBackend().CreateH264(new VideoDecoderOptions(64, 64))!;
        for (int i = 0; i < stream.Count; i++)
        {
            decoder.Send(H264ReorderLimit.Apply(stream[i].AccessUnit, 0), i);
            DecodedPicture? picture = decoder.Take();
            Assert.NotNull(picture);
            Assert.Equal(MediaContract.ExpectedHash(stream[i], 64, 64), MediaContract.PictureHash(picture!));
            decoder.Recycle(picture!);
        }
    }

    [Fact]
    public void AnSpsWithVuiButNoRestrictionKeepsItsVuiFields()
    {
        // baseline SPS with VUI: video signal type (full range, BT.709 colour description) and timing info, no restriction
        byte[] sps = SpsWithVui();
        byte[] limited = H264ReorderLimit.Apply(sps, 1);
        Assert.NotSame(sps, limited);
        Assert.Same(limited, H264ReorderLimit.Apply(limited, 1));
        // the VUI fields before the restriction are copied bit for bit: the rewritten SPS starts with the same bytes up
        // to the last whole byte before pic_struct_present_flag
        Assert.True(limited.AsSpan(0, 12).SequenceEqual(sps.AsSpan(0, 12)));
    }

    [Fact]
    public void TheStatedReorderDepthIsRead()
    {
        // F1 2011's movies state 2 (their first picture comes after two more units on the Vita); no limit reads as null
        byte[] idr = SyntheticH264.Stream(1)[0].AccessUnit;
        Assert.Null(H264ReorderLimit.StatedReorderFrames(idr));
        Assert.Equal(2, H264ReorderLimit.StatedReorderFrames(H264ReorderLimit.Apply(idr, 2)));
        Assert.Equal(0, H264ReorderLimit.StatedReorderFrames(H264ReorderLimit.Apply(idr, 0)));
        Assert.Null(H264ReorderLimit.StatedReorderFrames(SpsWithVui()));
        Assert.Equal(1, H264ReorderLimit.StatedReorderFrames(H264ReorderLimit.Apply(SpsWithVui(), 1)));
        Assert.Null(H264ReorderLimit.StatedReorderFrames(SyntheticH264.Stream(2)[1].AccessUnit));
    }

    [Fact]
    public void BSlicesAreRecognised()
    {
        // first_mb_in_slice 0 ('1'), slice_type ue: B = 1 ('010'), P = 0 ('1'), I = 7 ('0001000'), B = 6 ('00111')
        Assert.True(H264ReorderLimit.IsBSlice([0, 0, 1, 0x01, 0xA0]));
        Assert.False(H264ReorderLimit.IsBSlice([0, 0, 1, 0x41, 0xC0]));
        Assert.False(H264ReorderLimit.IsBSlice([0, 0, 1, 0x65, 0x88, 0x80]));
        Assert.True(H264ReorderLimit.IsBSlice([0, 0, 0, 1, 0x09, 0xF0, 0, 0, 1, 0x01, 0x9C]));
        Assert.False(H264ReorderLimit.IsBSlice(SyntheticH264.ParameterSets()));
    }

    /// <summary>An SPS (Annex B) with VUI but no bitstream_restriction.</summary>
    private static byte[] SpsWithVui()
    {
        var bits = new List<int>();
        void U(int count, uint value)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add((int)(value >> i) & 1);
            }
        }

        void Ue(uint value)
        {
            uint codeNum = value + 1;
            int length = 31 - System.Numerics.BitOperations.LeadingZeroCount(codeNum);
            U(length, 0);
            U(length + 1, codeNum);
        }

        U(8, 66);
        U(8, 0x40);
        U(8, 31);
        Ue(0);                       // sps id
        Ue(0);                       // log2_max_frame_num - 4
        Ue(0);                       // poc type 0
        Ue(2);                       // log2_max_poc_lsb - 4
        Ue(1);                       // max_num_ref_frames
        U(1, 0);
        Ue(59);                      // 960 wide
        Ue(33);                      // 544 high
        U(1, 1);
        U(1, 1);
        U(1, 0);                     // no cropping
        U(1, 1);                     // VUI
        U(1, 0);                     // aspect ratio
        U(1, 0);                     // overscan
        U(1, 1);                     // video signal type
        U(3, 5);
        U(1, 1);                     // full range
        U(1, 1);                     // colour description
        U(24, 0x010101);             // BT.709
        U(1, 0);                     // chroma location
        U(1, 1);                     // timing info
        U(32, 1001);
        U(32, 60000);
        U(1, 1);
        U(1, 0);                     // nal hrd
        U(1, 0);                     // vcl hrd
        U(1, 0);                     // pic struct
        U(1, 0);                     // bitstream_restriction_flag
        U(1, 1);                     // stop bit
        while (bits.Count % 8 != 0)
        {
            bits.Add(0);
        }

        var nal = new List<byte> { 0, 0, 0, 1, 0x67 };
        int zeros = 0;
        for (int i = 0; i < bits.Count; i += 8)
        {
            byte value = 0;
            for (int j = 0; j < 8; j++)
            {
                value = (byte)((value << 1) | bits[i + j]);
            }

            if (zeros >= 2 && value <= 3)
            {
                nal.Add(3);
                zeros = 0;
            }
            nal.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return nal.ToArray();
    }
}
