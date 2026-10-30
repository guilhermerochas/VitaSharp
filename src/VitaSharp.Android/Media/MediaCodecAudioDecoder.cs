using Android.Media;
using VitaSharp.Core.Media;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Media;

/// <summary>
/// Built into the app, not run on a device yet. SceAudiodec's MP3 and AAC on Android, where the app carries no FFmpeg:
/// an android.media.MediaCodec audio decoder ("audio/mpeg", "audio/mp4a-latm" in ADTS mode) behind the core's
/// <see cref="FrameAlignedAudioDecoder"/>, which hands the game exactly one frame of PCM per frame as FFmpeg does
/// (raw AAC frames get an ADTS header there). Installed by EmulationSession as <c>AudioFrameDecoders.Backend</c>.
/// </summary>
public sealed class MediaCodecAudioFrameBackend : IAudioFrameDecoderBackend
{
    public string Name => "mediacodec";

    public IAudioFrameDecoder? Create(AudioFrameDecoderOptions o)
    {
        var codec = MediaCodecAudioCodec.TryCreate(o);
        return codec == null ? null : new FrameAlignedAudioDecoder(codec, o);
    }
}

/// <summary>One MediaCodec audio decoder used synchronously (dequeue with timeouts), output 16-bit PCM.</summary>
public sealed unsafe class MediaCodecAudioCodec : IAsyncAudioCodec
{
    const string MimeMp3 = "audio/mpeg", MimeAac = "audio/mp4a-latm";
    readonly MediaCodec _codec;
    readonly MediaCodec.BufferInfo _info = new();
    int _channels, _disposed;

    public string Name { get; }
    public int OutputSampleRate { get; private set; }

    MediaCodecAudioCodec(MediaCodec codec, int channels)
    {
        _codec = codec;
        _channels = channels;
        Name = "mediacodec " + (codec.Name ?? "?");
    }

    public static MediaCodecAudioCodec? TryCreate(AudioFrameDecoderOptions o)
    {
        string mime = o.Codec == AudioFrameCodec.Mp3 ? MimeMp3 : MimeAac;
        int channels = Math.Clamp(o.Channels, 1, 2);
        // the decoder takes the real rate and layout from the stream (output format change); configure needs a value
        int rate = o.SampleRate > 0 ? (o.Sbr ? o.SampleRate / 2 : o.SampleRate) : 48000;
        MediaCodec? codec = null;
        try
        {
            var fmt = MediaFormat.CreateAudioFormat(mime, rate, channels);
            // AAC always as ADTS: FrameAlignedAudioDecoder wraps raw frames, so every input buffer is one whole frame
            if (o.Codec == AudioFrameCodec.Aac) fmt.SetInteger(MediaFormat.KeyIsAdts, 1);
            codec = MediaCodec.CreateDecoderByType(mime);
            codec.Configure(fmt, null, null, MediaCodecConfigFlags.None);
            codec.Start();
            var c = new MediaCodecAudioCodec(codec, channels);
            Log.Info($"Audiodec: {(o.Codec == AudioFrameCodec.Mp3 ? "MP3" : "AAC")} through {c.Name}" +
                     (o.Codec == AudioFrameCodec.Aac ? $" ({(o.Adts ? "ADTS" : "raw, ADTS-wrapped")}, {o.SampleRate} Hz{(o.Sbr ? ", SBR" : "")})" : ""));
            return c;
        }
        catch (Java.Lang.Exception e)
        {
            Log.Warn($"Audiodec: no MediaCodec decoder for {mime}: {e.Message}");
            try { codec?.Release(); } catch (Java.Lang.Exception) { }
            return null;
        }
    }

    public bool Queue(ReadOnlySpan<byte> frame, PcmFifo output)
    {
        try
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                int idx = _codec.DequeueInputBuffer(5_000);
                if (idx < 0) { Drain(output, 0); continue; } // every input buffer held: make room
                using var buf = _codec.GetInputBuffer(idx)!;
                int n = Math.Min(frame.Length, buf.Capacity());
                frame[..n].CopyTo(new Span<byte>((void*)buf.GetDirectBufferAddress(), n));
                _codec.QueueInputBuffer(idx, 0, n, 0, 0);
                return true;
            }
            Log.WarnOnce("audiodec-mc-input", $"Audiodec ({Name}): no input buffer became free, frame dropped");
        }
        catch (Java.Lang.Exception e) { Log.WarnOnce("audiodec-mc-queue", $"Audiodec ({Name}): queueing failed: {e.Message}"); }
        return false;
    }

    public int Drain(PcmFifo output, int waitUs)
    {
        int added = 0;
        try
        {
            for (int spins = 0; spins < 64; spins++)
            {
                int idx = _codec.DequeueOutputBuffer(_info, added == 0 ? waitUs : 0);
                if (idx == -2) // INFO_OUTPUT_FORMAT_CHANGED
                {
                    var f = _codec.OutputFormat;
                    _channels = f.GetInteger(MediaFormat.KeyChannelCount);
                    OutputSampleRate = f.GetInteger(MediaFormat.KeySampleRate);
                    continue;
                }
                if (idx == -3) continue; // INFO_OUTPUT_BUFFERS_CHANGED
                if (idx < 0) break;      // INFO_TRY_AGAIN_LATER
                if (_info.Size > 0 && _channels > 0)
                {
                    using var buf = _codec.GetOutputBuffer(idx)!;
                    // 16-bit PCM, the decoders' default output encoding
                    var pcm = new ReadOnlySpan<short>((byte*)buf.GetDirectBufferAddress() + _info.Offset, _info.Size / 2);
                    output.Append(pcm, _channels);
                    added += pcm.Length / _channels;
                }
                _codec.ReleaseOutputBuffer(idx, false);
            }
        }
        catch (Java.Lang.Exception e) { Log.WarnOnce("audiodec-mc-drain", $"Audiodec ({Name}): output failed: {e.Message}"); }
        return added;
    }

    public void Flush()
    {
        try { _codec.Flush(); } catch (Java.Lang.Exception) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _codec.Stop(); _codec.Release(); } catch (Java.Lang.Exception) { }
        _info.Dispose();
    }
}
