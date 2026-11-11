using BenchmarkDotNet.Attributes;
using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.Hle.Audio.Atrac9;
using VitaSharp.Core.Util;

namespace VitaSharp.Benchmarks;

/// <summary>
/// ATRAC9 superframe decoding (sceAtrac, sceAudiodec, the NGS ATRAC9 voices; PERF ngs splits it into unpack, dequant,
/// IMDCT and PCM). With <see cref="EnvVars.BenchAt9"/> (an .at9 file of a game) it decodes that stream's superframes;
/// without it, seeded superframes of random bits under valid block headers that the decoder accepts (48 kHz stereo,
/// 256-byte frames, 4 per superframe): the audio is noise, but unpack, dequantisation, IMDCT and PCM conversion all run.
/// </summary>
public class Atrac9Benchmarks
{
    readonly Atrac9Decoder _decoder = new();
    readonly List<byte[]> _superframes = [];
    short[][] _pcm = [];
    int _next;

    [GlobalSetup]
    public void Setup()
    {
        if (Fixtures.At9Path is { } path)
        {
            var file = File.ReadAllBytes(path);
            if (At9Container.Parse(file, out var info) != At9ParseResult.Ok)
                throw new InvalidOperationException($"{path}: not an ATRAC9 .at9 file");
            _decoder.Initialize(info.Config);
            int sf = _decoder.Config.SuperframeBytes;
            for (long at = info.DataOffset; at + sf <= info.DataOffset + info.DataSize && _superframes.Count < 2048; at += sf)
                _superframes.Add(file.AsSpan((int)at, sf).ToArray());
        }
        else
        {
            _decoder.Initialize([0xFE, 0x74, 0x1F, 0xF0]); // 48 kHz, stereo, 256-byte frames, 4 frames per superframe
            var rng = new Random(9);
            var pcm = NewPcm();
            for (int tries = 0; tries < 200_000 && _superframes.Count < 256; tries++)
            {
                var sf = Superframe(rng, _decoder.Config.FrameBytes, _decoder.Config.SuperframeBytes);
                try
                {
                    _decoder.Decode(sf, pcm);
                    _superframes.Add(sf);
                }
                catch (Exception) { } // an invalid bitstream: try another
            }
            _decoder.Initialize([0xFE, 0x74, 0x1F, 0xF0]);
        }
        if (_superframes.Count == 0)
            throw new InvalidOperationException("no ATRAC9 superframe to decode");
        _pcm = NewPcm();
    }

    /// <summary>A stereo superframe of random bits under valid block headers (first-in-superframe flag, band counts,
    /// gradient, no band extension, scale factors coded directly in the first frame), so most of the bits reach the
    /// scale factor and spectrum decoding.</summary>
    static byte[] Superframe(Random rng, int frameBytes, int superframeBytes)
    {
        var sf = new byte[superframeBytes];
        rng.NextBytes(sf);
        for (int f = 0; f * frameBytes < superframeBytes; f++)
        {
            int bit = f * frameBytes * 8;
            void Put(int value, int bits)
            {
                for (int i = bits - 1; i >= 0; i--, bit++)
                {
                    int mask = 0x80 >> (bit & 7);
                    if ((value >> i & 1) != 0)
                        sf[bit >> 3] |= (byte)mask;
                    else
                        sf[bit >> 3] &= (byte)~mask;
                }
            }
            Put(f == 0 ? 0 : 1, 1);       // first in superframe (inverted)
            Put(0, 1);                    // band parameters follow
            int band = rng.Next(0, 10);   // + 3 (48 kHz): at most 12 bands
            Put(band, 4);
            Put(rng.Next(0, band + 1), 4); // stereo band <= band count
            Put(0, 1);                    // no band extension
            Put(1, 2);                    // gradient mode 1: start unit and value
            Put(rng.Next(1, 31), 5);
            Put(rng.Next(0, 32), 5);
            Put(0, 4);                    // gradient boundary
            Put(rng.Next(0, 2), 1);       // primary channel
            Put(0, 1);                    // no joint stereo signs
            Put(0, 1);                    // no extension data
            Put(0, 2);                    // channel 0: scale factors coded directly
        }
        return sf;
    }

    short[][] NewPcm() => [.. Enumerable.Range(0, _decoder.Config.ChannelCount).Select(_ => new short[_decoder.Config.SuperframeSamples])];

    /// <summary>One superframe (1024 samples per channel at 48 kHz: 21.3 ms of audio).</summary>
    [Benchmark]
    public void DecodeSuperframe()
    {
        _decoder.Decode(_superframes[_next], _pcm);
        if (++_next == _superframes.Count)
            _next = 0;
    }
}
