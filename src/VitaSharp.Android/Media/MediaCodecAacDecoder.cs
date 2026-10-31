using Android.Media;
using VitaSharp.Core.Media;
using VitaSharp.Core.Util;
using JByteBuffer = Java.Nio.ByteBuffer;

namespace VitaSharp.Android.Media;

/// <summary>
/// SceAudiodec's AAC decoder on Android (<see cref="AudioFrameDecoders.AacBackend"/>), where the app carries no FFmpeg:
/// a MediaCodec AAC decoder driven synchronously, one frame per call. Raw frames get their exact length from what
/// SceMp4 handed out (<see cref="RawAudioFrameLengths"/>), since MediaCodec reports no bytes consumed and decodes the
/// stale bytes after a frame as more frames. A frame whose PCM is not out yet (decoder latency) is answered with
/// silence of the stream's frame size and its PCM goes out one call later, so every call returns one frame of samples.
/// </summary>
public sealed unsafe class MediaCodecAacDecoder : IAudioFrameDecoder
{
    const long InputTimeoutUs = 10_000, OutputWaitUs = 30_000;
    long _queued, _producedSamples;
    int _frames, _silent;
    // decoded interleaved PCM not handed out yet
    short[] _samples = new short[8192];
    int _fill;
    const int InfoOutputFormatChanged = -2, InfoOutputBuffersChanged = -3;
    MediaCodec? _codec;
    readonly MediaCodec.BufferInfo _info = new();
    int _outChannels, _frameSamples;
    long _pts;

    MediaCodecAacDecoder(MediaCodec codec, int channels)
    {
        _codec = codec;
        _outChannels = channels;
    }

    public static IAudioFrameDecoder? Create(int sampleRate, int channels, byte[]? audioSpecificConfig)
    {
        MediaCodec? codec = null;
        try
        {
            var fmt = MediaFormat.CreateAudioFormat(MediaFormat.MimetypeAudioAac, sampleRate > 0 ? sampleRate : 48000, Math.Max(1, channels));
            if (audioSpecificConfig != null) fmt.SetByteBuffer("csd-0", JByteBuffer.Wrap(audioSpecificConfig));
            else fmt.SetInteger(MediaFormat.KeyIsAdts, 1);
            codec = MediaCodec.CreateDecoderByType(MediaFormat.MimetypeAudioAac);
            codec.Configure(fmt, null, null, MediaCodecConfigFlags.None);
            codec.Start();
            Log.Info($"sceAudiodec: AAC through MediaCodec {codec.Name} ({sampleRate} Hz, {channels} ch, {(audioSpecificConfig != null ? "raw" : "ADTS")})");
            return new MediaCodecAacDecoder(codec, Math.Max(1, channels));
        }
        catch (Java.Lang.Exception e)
        {
            Log.Warn($"sceAudiodec: no MediaCodec AAC decoder ({e.Message})");
            try { codec?.Release(); } catch (Java.Lang.Exception) { }
            return null;
        }
    }

    public int RawFrameLength(byte* data, int maxSize)
    {
        int n = RawAudioFrameLengths.Find(new ReadOnlySpan<byte>(data, maxSize));
        if (n == 0) Log.WarnOnce("aac-rawlen", "sceAudiodec: raw AAC frame of unknown length (not from SceMp4): decoded with the whole input");
        return n > 0 ? n : maxSize;
    }

    public int Decode(byte* data, int size, Span<short> pcmOut, int outChannels = 0)
    {
        var c = _codec;
        _frames++;
        if (c == null) return 0;
        int oc = outChannels > 0 ? outChannels : _outChannels;
        try
        {
            int idx = c.DequeueInputBuffer(InputTimeoutUs);
            if (idx >= 0)
            {
                using var buf = c.GetInputBuffer(idx)!;
                int n = Math.Min(size, buf.Capacity());
                new ReadOnlySpan<byte>(data, n).CopyTo(new Span<byte>((void*)buf.GetDirectBufferAddress(), n));
                c.QueueInputBuffer(idx, 0, n, _pts, MediaCodecBufferFlags.None);
                _pts += 21_333;
            }
            else Log.WarnOnce("aac-in", "sceAudiodec: MediaCodec AAC input full, frame dropped");
            if (idx >= 0) _queued++;
            Drain(c, 0);
            // the decoder runs on its own thread: wait for PCM whenever a frame is in flight. Silence is only right
            // while the decoder primes (no output yet for a frame it holds back); answering silence for a late frame
            // inserted a 1024-sample gap into about one movie frame in four (F1 2011, Persona 4 Golden). Frames are
            // counted in samples: an output buffer may carry more than one frame.
            if (_fill < FrameSamples * _outChannels && _queued > _producedSamples / FrameSamples) Drain(c, OutputWaitUs);
        }
        catch (Java.Lang.Exception e)
        {
            Log.WarnOnce("aac-fail", $"sceAudiodec: MediaCodec AAC failed: {e.Message}");
            return Silence(pcmOut, oc);
        }
        int ch = _outChannels;
        if (_fill < FrameSamples * ch) return Silence(pcmOut, oc);
        // one frame per call, the decoder's own frame size
        int samples = Math.Min(FrameSamples, pcmOut.Length / oc);
        for (int i = 0; i < samples; i++)
            for (int o = 0; o < oc; o++)
                pcmOut[i * oc + o] = _samples[i * ch + Math.Min(o, ch - 1)];
        int used = FrameSamples * ch;
        Array.Copy(_samples, used, _samples, 0, _fill - used);
        _fill -= used;
        return samples;
    }

    /// <summary>Samples per channel in one frame: the first output buffer's (1024 for AAC-LC, 2048 with SBR).</summary>
    int FrameSamples => _frameSamples > 0 ? _frameSamples : 1024;

    int Silence(Span<short> pcmOut, int oc)
    {
        _silent++;
        return Zeros(pcmOut, oc);
    }

    int Zeros(Span<short> pcmOut, int oc)
    {
        int samples = Math.Min(FrameSamples, pcmOut.Length / oc);
        pcmOut[..(samples * oc)].Clear();
        return samples;
    }

    void Drain(MediaCodec c, long timeoutUs)
    {
        while (true)
        {
            int idx = c.DequeueOutputBuffer(_info, timeoutUs);
            if (idx == InfoOutputFormatChanged)
            {
                var f = c.OutputFormat;
                if (f.ContainsKey(MediaFormat.KeyChannelCount)) _outChannels = Math.Max(1, f.GetInteger(MediaFormat.KeyChannelCount));
                continue;
            }
            if (idx == InfoOutputBuffersChanged) continue;
            if (idx < 0) return;
            timeoutUs = 0;
            if (_info.Size > 0)
            {
                using var buf = c.GetOutputBuffer(idx)!;
                int n = _info.Size / 2;
                if (_frameSamples == 0) _frameSamples = Math.Clamp(n / _outChannels, 1, 2048);
                if (_samples.Length < _fill + n) Array.Resize(ref _samples, Math.Max(_fill + n, _samples.Length * 2));
                new ReadOnlySpan<byte>((byte*)buf.GetDirectBufferAddress() + _info.Offset, n * 2)
                    .CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_samples.AsSpan(_fill, n)));
                _fill += n;
                _producedSamples += n / _outChannels;
            }
            c.ReleaseOutputBuffer(idx, false);
        }
    }

    public void Flush()
    {
        try { _codec?.Flush(); } catch (Java.Lang.Exception) { }
        _queued = _producedSamples = 0;
        _fill = 0;
    }

    public void Dispose()
    {
        var c = _codec;
        _codec = null;
        if (c == null) return;
        Log.Info($"sceAudiodec: MediaCodec AAC decoder closed after {_frames} frames, {_silent} answered with silence (decoder late)");
        try { c.Stop(); } catch (Java.Lang.Exception) { }
        try { c.Release(); } catch (Java.Lang.Exception) { }
        c.Dispose();
        _info.Dispose();
    }
}
