using VitaSharp.Core.Shell;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Audio;

/// <summary>
/// Plays one short host sound (the trophy chime) on a device stream of its own, so the guest's audio ports are never
/// touched: the sound is converted to the stream's rate and channels, written with blocking writes, and the call
/// returns once the device has played it (or after a timeout). The caller opens and disposes the sink.
/// </summary>
public static class EffectPlayer
{
    public static void Play(IPcmSink sink, PcmSound pcm, float gain = 1f)
    {
        var data = TrophySound.Convert(pcm, sink.SampleRate, sink.Channels);
        if (data.Length == 0) return;
        if (gain != 1f)
            for (int i = 0; i < data.Length; i++) data[i] = (short)Math.Clamp(data[i] * gain, short.MinValue, short.MaxValue);
        int ch = Math.Max(1, sink.Channels);
        sink.SetBufferSize(Math.Max(sink.FramesPerBurst * 4, 1024));
        sink.Start();
        long start = sink.FramesWritten;
        int off = 0, stalls = 0;
        while (off < data.Length)
        {
            int n = sink.Write(data.AsSpan(off), 200_000_000); // 200 ms
            if (n < 0) { Log.WarnOnce("effect-write", $"Audio: the sound effect stopped (write {n})"); return; }
            if (n == 0 && ++stalls > 10) return;
            off += n * ch;
        }
        // let the device play what was queued before the stream is closed
        long target = start + data.Length / ch;
        var until = DateTime.UtcNow.AddSeconds(2);
        while (sink.FramesRead < target && DateTime.UtcNow < until && !sink.Disconnected) Thread.Sleep(10);
    }
}
