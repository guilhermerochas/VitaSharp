using VitaSharp.Core.Host;
using VitaSharp.Desktop.Host;
using VitaSharp.Mobile.Core.Audio;

namespace VitaSharp.Tests;

/// <summary>
/// Desktop-vs-Android parity of one audio port (the decoder side is <see cref="MediaParityTests"/>): the grains a game
/// gives sceAudioOutOutput, at the port's volume, must reach the output unchanged on both. Desktop: OpenAL Soft's own
/// mixer, read from a loopback device (<see cref="OpenAlAudio.TryCreateLoopback"/>, no sound card). Android:
/// <see cref="BufferedPcmStream"/>, which owns all the buffering and the volume before AAudio/AudioTrack, with a sink
/// that keeps what it is given. The PCM must match the guest's samples times the volume, and each other, within the
/// declared tolerance (one step of 16-bit rounding), with every frame there and in order.
/// </summary>
public sealed class AudioPortParityTests
{
    private const int Rate = 48000;
    private const int GrainFrames = 256;
    private const int Grains = 40;

    /// <summary>The largest difference allowed per sample: the backends round the volume product differently (one
    /// step of 16 bits), nothing more.</summary>
    private const int ToleranceLsb = 1;

    /// <summary>A stereo tone, different on each side (a swapped or merged channel shows), never zero at the start so
    /// the first frame of the content is found after the output's leading silence.</summary>
    private static short[] Tone()
    {
        short[] samples = new short[Grains * GrainFrames * 2];
        for (int frame = 0; frame < Grains * GrainFrames; frame++)
        {
            double time = frame / (double)Rate;
            samples[2 * frame] = (short)Math.Round(8000 * Math.Cos(2 * Math.PI * 440 * time));
            samples[2 * frame + 1] = (short)Math.Round(-6000 * Math.Cos(2 * Math.PI * 660 * time));
        }

        return samples;
    }

    private static short[] Expected(short[] tone, float volume)
    {
        short[] expected = new short[tone.Length];
        for (int i = 0; i < tone.Length; i++)
        {
            expected[i] = (short)Math.Clamp(Math.Round(tone[i] * (double)volume), short.MinValue, short.MaxValue);
        }

        return expected;
    }

    /// <summary>The frames from the first one that is not silent, at most <paramref name="length"/> samples.</summary>
    private static short[] Content(List<short> output, int length)
    {
        int start = output.FindIndex(sample => sample != 0);
        Assert.True(start >= 0, "the output is silent");
        start -= start % 2;
        Assert.True(output.Count - start >= length, $"only {(output.Count - start) / 2} frames after the silence, {length / 2} expected");
        return output.GetRange(start, length).ToArray();
    }

    private static List<short> DesktopOutput(short[] tone, float volume)
    {
        using OpenAlAudio audio = OpenAlAudio.TryCreateLoopback(Rate)!;
        using IAudioStream stream = audio.OpenStream(Rate, 2, GrainFrames);
        stream.Volume = volume;
        List<short> output = new();
        float[] block = new float[GrainFrames * 2];
        for (int grain = 0; grain < Grains; grain++)
        {
            stream.Submit(tone.AsSpan(grain * GrainFrames * 2, GrainFrames * 2));
            audio.RenderLoopback(block);
            AddAsShorts(output, block);
        }

        // the queued grains play out
        for (int extra = 0; extra < Grains; extra++)
        {
            audio.RenderLoopback(block);
            AddAsShorts(output, block);
        }

        return output;
    }

    /// <summary>The mixer's float frames back in 16-bit steps (full scale 32768), rounded.</summary>
    private static void AddAsShorts(List<short> output, float[] block)
    {
        foreach (float sample in block)
        {
            output.Add((short)Math.Clamp(Math.Round(sample * 32768.0), short.MinValue, short.MaxValue));
        }
    }

    /// <summary>A device that keeps what it is given and keeps up with it: one burst is always still queued (never
    /// drained, so no underrun and no re-buffering silence between grains).</summary>
    private sealed class KeepingSink : IPcmSink
    {
        public readonly List<short> Data = new();

        public int SampleRate => Rate;
        public int Channels => 2;
        public int FramesPerBurst => 192;
        public string Description => "keeping";
        public long FramesWritten => Data.Count / 2;
        public long FramesRead => Math.Max(0, FramesWritten - FramesPerBurst);
        public int XRunCount => 0;
        public bool Disconnected => false;

        public int SetBufferSize(int frames) => frames;

        public int Write(ReadOnlySpan<short> interleaved, long timeoutNanos)
        {
            foreach (short sample in interleaved)
            {
                Data.Add(sample);
            }

            return interleaved.Length / 2;
        }

        public void Start()
        {
        }

        public void Pause()
        {
        }

        public void Dispose()
        {
        }
    }

    private static List<short> AndroidOutput(short[] tone, float volume)
    {
        KeepingSink sink = new();
        using (BufferedPcmStream stream = new(_ => sink, Rate, 2, GrainFrames, stopRequested: () => false))
        {
            stream.Volume = volume;
            for (int grain = 0; grain < Grains; grain++)
            {
                stream.Submit(tone.AsSpan(grain * GrainFrames * 2, GrainFrames * 2));
            }
        }

        return sink.Data;
    }

    private static void AssertClose(short[] expected, short[] actual, string side)
    {
        int worst = 0;
        int worstAt = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int difference = Math.Abs(expected[i] - actual[i]);
            if (difference > worst)
            {
                worst = difference;
                worstAt = i;
            }
        }

        Assert.True(worst <= ToleranceLsb,
            $"{side}: sample {worstAt} (frame {worstAt / 2}, {(worstAt % 2 == 0 ? "left" : "right")}) is {actual[worstAt]}, expected {expected[worstAt]} (difference {worst} > {ToleranceLsb})");
    }

    [OpenAlLoopbackTheory]
    [InlineData(1.0f)]
    [InlineData(0.5f)]
    [InlineData(0.25f)]
    public void APortReachesTheOutputTheSameOnTheDesktopAndOnAndroid(float volume)
    {
        short[] tone = Tone();
        short[] expected = Expected(tone, volume);
        short[] desktop = Content(DesktopOutput(tone, volume), tone.Length);
        short[] android = Content(AndroidOutput(tone, volume), tone.Length);
        AssertClose(expected, desktop, "desktop (OpenAL)");
        AssertClose(expected, android, "Android (BufferedPcmStream)");
        AssertClose(desktop, android, "desktop vs Android");
    }
}

/// <summary>[Theory] skipped unless OpenAL Soft with ALC_SOFT_loopback loads (the desktop side of the audio parity).</summary>
public sealed class OpenAlLoopbackTheoryAttribute : TheoryAttribute
{
    private static readonly Lazy<bool> s_available = new(() =>
    {
        using var audio = OpenAlAudio.TryCreateLoopback(48000);
        return audio != null;
    });

    public OpenAlLoopbackTheoryAttribute()
    {
        if (!s_available.Value)
        {
            Skip = "OpenAL Soft with ALC_SOFT_loopback not available (the desktop side of the audio parity)";
        }
    }
}
