namespace VitaSharp.Core.Shell;

/// <summary>
/// The sound played with the in-game trophy notification. With the firmware installed it is the system's notice chime
/// from vs0:data/external/common/common_resource.rco: the RCOs of firmware 3.60-3.74 hold no sound tied to trophies, and
/// the only sounds system code plays by id (not through a control's soundindextable entry) are 0xE0D9F918 and
/// 0xD74A54E5. The shell, the trophy app (NPXS10008), netcheck, sign-in and the trophy setup dialog pick 0xE0D9F918
/// for a completed or positive event and 0xD74A54E5 for an error or a refusal (shell.self: the sound-type switch at
/// 0x8139392C maps type 1 to the first and type 2 to the second), so the positive one is used here.
/// Without firmware a short two-note chime synthesised here is played instead (no copyrighted asset is shipped).
/// </summary>
public static class TrophySound
{
    /// <summary>common_resource.rco sounddata id of the system's notice chime (0.36 s, mono VAG, 48 kHz).</summary>
    public const uint FirmwareNoticeSound = 0xE0D9F918;

    /// <summary>The trophy sound for this file-system root, and where it came from ("firmware" or "built-in").</summary>
    public static (PcmSound Pcm, string Source) Load(string fsRoot)
    {
        try
        {
            if (new ShellAssets(fsRoot).Sound(ShellSoundEffect.Notice) is { Samples.Length: > 0 } fw)
                return (fw, "firmware (common_resource.rco 0xE0D9F918)");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Util.Log.Warn($"trophy sound: firmware sound unavailable: {e.Message}");
        }
        return (Chime(), "built-in chime");
    }

    /// <summary>
    /// A bell-like two-note chime (E6 then B6, a rising fifth) of about 0.7 s: each note is a sine with two soft
    /// inharmonic partials, a 4 ms attack and an exponential decay; peaks at about -6 dBFS. Mono, 16-bit.
    /// </summary>
    public static PcmSound Chime(int rate = 48000)
    {
        int n = (int)(rate * 0.7);
        var mix = new double[n];
        void Note(double freq, double start, double gain)
        {
            int s0 = (int)(start * rate);
            for (int i = s0; i < n; i++)
            {
                double t = (i - s0) / (double)rate;
                double env = Math.Min(1, t / 0.004) * Math.Exp(-t / 0.16);
                double v = Math.Sin(2 * Math.PI * freq * t) + 0.35 * Math.Sin(2 * Math.PI * freq * 2.76 * t) * Math.Exp(-t / 0.05)
                         + 0.15 * Math.Sin(2 * Math.PI * freq * 5.4 * t) * Math.Exp(-t / 0.03);
                mix[i] += gain * env * v;
            }
        }
        Note(1318.51, 0, 0.8);
        Note(1975.53, 0.11, 1.0);
        double peak = mix.Max(Math.Abs);
        double scale = peak > 0 ? 0.5 / peak : 0;
        var samples = new short[n];
        // fade the last 20 ms so the tail never clicks
        int fade = rate / 50;
        for (int i = 0; i < n; i++)
        {
            double f = i >= n - fade ? (n - i) / (double)fade : 1;
            samples[i] = (short)Math.Round(mix[i] * scale * f * short.MaxValue);
        }
        return new PcmSound(rate, 1, samples);
    }

    /// <summary>The sound as interleaved 16-bit samples at <paramref name="rate"/> with <paramref name="channels"/>
    /// channels (linear interpolation; mono is copied to every channel, extra channels are dropped or averaged).</summary>
    public static short[] Convert(PcmSound pcm, int rate, int channels)
    {
        int inCh = Math.Max(1, pcm.Channels), inFrames = pcm.Samples.Length / inCh;
        if (inFrames == 0 || rate <= 0 || channels <= 0) return [];
        double Frame(int i, int c) => inCh == 1 ? pcm.Samples[i] : channels == 1 ? (pcm.Samples[i * inCh] + pcm.Samples[i * inCh + 1]) / 2.0
            : pcm.Samples[i * inCh + Math.Min(c, inCh - 1)];
        long outFrames = (long)inFrames * rate / Math.Max(1, pcm.SampleRate);
        var o = new short[outFrames * channels];
        double step = pcm.SampleRate / (double)rate;
        for (long f = 0; f < outFrames; f++)
        {
            double pos = f * step;
            int i = (int)pos;
            double frac = pos - i;
            int j = Math.Min(i + 1, inFrames - 1);
            for (int c = 0; c < channels; c++)
                o[f * channels + c] = (short)Math.Clamp(Math.Round(Frame(i, c) * (1 - frac) + Frame(j, c) * frac), short.MinValue, short.MaxValue);
        }
        return o;
    }
}
