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
