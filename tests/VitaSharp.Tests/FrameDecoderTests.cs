using FFmpeg.AutoGen;
using VitaSharp.Core.Media;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// The per-frame audio decoder behind sceAudiodec: like the Vita's hardware decoder it must turn every compressed frame
/// into a full frame of PCM, including the first one. FFmpeg trims the encoder delay of libfaac streams by itself
/// (it spots the "libfaac x.y" fill element in the first frame) and returned 0 samples for that frame; Persona 4
/// Golden's movie player then derived a 0 Hz output rate from the empty frame and never managed to play a movie again.
/// </summary>
public sealed class FrameDecoderTests(ITestOutputHelper output)
{
    sealed class Bits
    {
        readonly List<byte> _bytes = new();
        int _cur, _n;
        public void U(int bits, uint v) { for (int i = bits - 1; i >= 0; i--) { _cur = (_cur << 1) | (int)((v >> i) & 1); if (++_n == 8) { _bytes.Add((byte)_cur); _cur = _n = 0; } } }
        public byte[] ToArray() { while (_n != 0) U(1, 0); return _bytes.ToArray(); }
    }

    /// <summary>A raw AAC-LC mono frame: a FIL element carrying the libfaac encoder tag (as libfaac writes it into the
    /// first frame), then a silent SCE (global gain 0, long window, max_sfb 0, no tools) and END.</summary>
    static byte[] Frame(bool encoderTag)
    {
        var b = new Bits();
        if (encoderTag)
        {
            b.U(3, 6); b.U(4, 15); b.U(8, 2);  // ID_FIL, 15 + 2 - 1 = 16 payload bytes
            b.U(4, 0); b.U(13, 0);            // EXT_FILL; the fill data starts with 13 bits FFmpeg skips
            foreach (char c in "libfaac 1.28\0") b.U(8, c);
            b.U(7, 0);
        }
        b.U(3, 0); b.U(4, 0); b.U(8, 0);      // ID_SCE, tag 0, global gain
        b.U(1, 0); b.U(2, 0); b.U(1, 0);      // ics_reserved, ONLY_LONG_SEQUENCE, window shape
        b.U(6, 0); b.U(1, 0);                 // max_sfb 0, no prediction
        b.U(1, 0); b.U(1, 0); b.U(1, 0);      // no pulse, TNS or gain control data
        b.U(3, 7);                            // ID_END
        return b.ToArray();
    }

    [Fact]
    public unsafe void AacFirstFrameIsAFullFrame()
    {
        if (!FFmpegLoader.Available) { output.WriteLine("FFmpeg not available: skipped"); return; }
        using var d = FrameDecoder.Create(AVCodecID.AV_CODEC_ID_AAC, 48000, 1, FrameDecoder.AacConfig(48000, 1));
        Assert.NotNull(d);
        var pcm = new short[4096];
        for (int frame = 0; frame < 3; frame++)
        {
            var data = Frame(encoderTag: frame == 0);
            int samples;
            fixed (byte* p = data) samples = d!.Decode(p, data.Length, pcm, 1);
            Assert.Equal(1024, samples);
        }
    }
}

/// <summary>
/// Raw (MP4-style) AAC has no frame header, so the hardware decoder parses one raw_data_block and reports the bytes it
/// used. Persona 4 Golden's movie player puts each access unit at the start of a 0x200-byte slot and passes the slot
/// with maxEsSize 1536: handing FFmpeg the whole 1536 bytes made it decode the next slots as extra frames (errors,
/// broken overlap state, garbage or silent movie audio).
/// </summary>
public sealed class RawAacFrameLengthTests(ITestOutputHelper output)
{
    static unsafe List<byte[]> EncodeSine(int frames, out byte[] config)
    {
        config = [];
        var packets = new List<byte[]>();
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec == null) return packets;
        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        ctx->sample_rate = 48000;
        ctx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        ctx->bit_rate = 128000;
        ffmpeg.av_channel_layout_default(&ctx->ch_layout, 2);
        ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        if (ffmpeg.avcodec_open2(ctx, codec, null) < 0) { ffmpeg.avcodec_free_context(&ctx); return packets; }
        config = new Span<byte>(ctx->extradata, ctx->extradata_size).ToArray();
        var frame = ffmpeg.av_frame_alloc();
        var pkt = ffmpeg.av_packet_alloc();
        long t = 0;
        for (int f = 0; f <= frames; f++)
        {
            if (f < frames)
            {
                frame->nb_samples = ctx->frame_size;
                frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                frame->sample_rate = 48000;
                ffmpeg.av_channel_layout_copy(&frame->ch_layout, &ctx->ch_layout);
                ffmpeg.av_frame_get_buffer(frame, 0);
                for (int i = 0; i < frame->nb_samples; i++, t++)
                {
                    ((float*)frame->extended_data[0u])[i] = 0.4f * MathF.Sin(t * 2 * MathF.PI * 440 / 48000);
                    ((float*)frame->extended_data[1u])[i] = 0.3f * MathF.Sin(t * 2 * MathF.PI * 660 / 48000);
                }
                ffmpeg.avcodec_send_frame(ctx, frame);
                ffmpeg.av_frame_unref(frame);
            }
            else ffmpeg.avcodec_send_frame(ctx, null);
            while (ffmpeg.avcodec_receive_packet(ctx, pkt) == 0)
            {
                packets.Add(new Span<byte>(pkt->data, pkt->size).ToArray());
                ffmpeg.av_packet_unref(pkt);
            }
        }
        ffmpeg.av_packet_free(&pkt);
        ffmpeg.av_frame_free(&frame);
        ffmpeg.avcodec_free_context(&ctx);
        return packets;
    }

    [Fact]
    public unsafe void FramesInFixedSlotsDecodeLikeExactPackets()
    {
        if (!FFmpegLoader.Available) { output.WriteLine("FFmpeg not available: skipped"); return; }
        var packets = EncodeSine(40, out var config);
        if (packets.Count == 0) { output.WriteLine("no AAC encoder: skipped"); return; }
        const int Slot = 0x200, MaxEs = 1536;
        Assert.All(packets, p => Assert.True(p.Length < Slot));
        // the game's layout: each access unit at the start of its slot, the rest of the slot stale (non-zero) bytes
        var es = new byte[packets.Count * Slot + MaxEs];
        new Random(1).NextBytes(es);
        for (int i = 0; i < packets.Count; i++) packets[i].CopyTo(es, i * Slot);

        using var exact = FrameDecoder.Create(AVCodecID.AV_CODEC_ID_AAC, 48000, 2, config);
        using var slotted = FrameDecoder.Create(AVCodecID.AV_CODEC_ID_AAC, 48000, 2, config);
        Assert.NotNull(exact);
        Assert.NotNull(slotted);
        var want = new short[8192];
        var got = new short[8192];
        fixed (byte* e = es)
            for (int i = 0; i < packets.Count; i++)
            {
                byte* slot = e + i * Slot;
                int len = slotted!.RawFrameLength(slot, MaxEs);
                Assert.Equal(packets[i].Length, len);
                int n1, n2;
                fixed (byte* p = packets[i]) n1 = exact!.Decode(p, packets[i].Length, want, 2);
                n2 = slotted.Decode(slot, len, got, 2);
                Assert.Equal(n1, n2);
                Assert.True(want.AsSpan(0, n1 * 2).SequenceEqual(got.AsSpan(0, n2 * 2)), $"frame {i} differs");
            }
    }
}
