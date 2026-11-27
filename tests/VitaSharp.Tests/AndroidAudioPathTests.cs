using System.Globalization;
using System.Text.RegularExpressions;
using VitaSharp.Core.Capture;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Audio;

namespace VitaSharp.Tests;

/// <summary>
/// The Android audio path on the PC, without the phone: everything before AAudio is ours and runs here. A guest's
/// sceAudioOutOutput grains go through the HLE port (grain, channels, volume), the port's <see cref="BufferedPcmStream"/>
/// (buffering, resampling to the device rate, underrun accounting) and reach <see cref="FakeAAudioDevice"/>, a fake
/// AAudio with the phone's profile (48000 Hz shared, capacity 4104 frames, 2052-frame bursts), whose shared mixer
/// records what it played as a WAV in memory. Time is the test's: the device plays a burst when a blocking write waits
/// for it or when the test lets time pass with no grain written (the guest is late).
/// </summary>
[Collection("guest")]
public sealed partial class AndroidAudioPathTests : IDisposable
{
    private const int Main = 0;
    private const int Bgm = 1;
    private const int Stereo = 1;

    private readonly GuestMemoryFixture _fixture;
    private readonly HleHarness _harness;
    private readonly FakeAAudioDevice _device = new();
    private readonly Audio _audio;
    private readonly Func<string>? _oldDeviceInfo;

    public AndroidAudioPathTests(GuestMemoryFixture fixture)
    {
        _fixture = fixture;
        _harness = new HleHarness(fixture, "aaudio");
        _harness.Emu.Host.Audio = _device;
        _audio = _harness.Module<Audio>();
        _oldDeviceInfo = AudioDiagnostics.DeviceInfo;
        AudioDiagnostics.DeviceInfo = _device.Describe;
    }

    public void Dispose()
    {
        AudioDiagnostics.DeviceInfo = _oldDeviceInfo;
        _harness.Dispose();
    }

    // ------------------------------------------------------------------ the guest side

    /// <summary>A stereo tone, a different frequency on each side (a swapped or dropped channel shows), with no sample
    /// exactly zero (a zero frame in the device's output is then silence the path added).</summary>
    private static short[] Tone(int frames, int rate, double leftHz, double rightHz, double amplitude = 8000, double phase = 0.3)
    {
        short[] samples = new short[frames * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            double time = frame / (double)rate;
            samples[2 * frame] = NonZero(amplitude * Math.Cos(2 * Math.PI * leftHz * time + phase));
            samples[2 * frame + 1] = NonZero(-0.75 * amplitude * Math.Cos(2 * Math.PI * rightHz * time + phase));
        }

        return samples;
    }

    private static short NonZero(double value)
    {
        short rounded = (short)Math.Round(value);
        return rounded == 0 ? (short)(value < 0 ? -1 : 1) : rounded;
    }

    /// <summary>What the HLE port makes of a guest sample at a volume (sceAudioOutOutput: float product, truncated).</summary>
    private static short[] AtVolume(short[] samples, float volume)
    {
        short[] result = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            result[i] = (short)Math.Clamp(samples[i] * volume, short.MinValue, short.MaxValue);
        }

        return result;
    }

    private int OpenPort(int type, int grain, int rate)
    {
        int port = HleHarness.Call(_audio, "sceAudioOutOpenPort", type, grain, rate, Stereo);
        Assert.True(port > 0, $"sceAudioOutOpenPort failed: 0x{port:X8}");
        return port;
    }

    private void SetVolume(int port, float volume)
    {
        uint volumes = _harness.Alloc(8);
        GuestMemory.W32(volumes, (uint)Math.Round(volume * 32768));
        GuestMemory.W32(volumes + 4, (uint)Math.Round(volume * 32768));
        Assert.Equal(0, HleHarness.Call(_audio, "sceAudioOutSetVolume", port, 3u, volumes));
    }

    /// <summary>A guest output buffer: the port's grains are copied in before each sceAudioOutOutput.</summary>
    private sealed class GuestGrains(AndroidAudioPathTests owner, int port, int grain, short[] samples)
    {
        private readonly uint _buffer = owner._harness.Alloc((uint)(grain * 4));
        private int _next;

        public int Count => samples.Length / (grain * 2);
        public bool Done => _next >= Count;

        public void Output()
        {
            GuestMemory.Span<short>(_buffer, grain * 2).Clear();
            samples.AsSpan(_next * grain * 2, grain * 2).CopyTo(GuestMemory.Span<short>(_buffer, grain * 2));
            _next++;
            Assert.Equal(grain, HleHarness.Call(owner._audio, "sceAudioOutOutput", port, _buffer));
        }
    }

    private AudioDiagnostics.PortStats Stats(int port)
    {
        object portObject = HleHarness.Invoke(_audio, "Get", port)!;
        return (AudioDiagnostics.PortStats)portObject.GetType().GetField("Stats")!.GetValue(portObject)!;
    }

    private BufferedPcmStream PortStream(int port)
    {
        object portObject = HleHarness.Invoke(_audio, "Get", port)!;
        return (BufferedPcmStream)portObject.GetType().GetField("Stream")!.GetValue(portObject)!;
    }

    /// <summary>The device plays what is still queued (its own time: the guest wrote everything).</summary>
    private void PlayOut() => _device.Run(FakeAAudioDevice.CapacityFrames + 2 * FakeAAudioDevice.BurstFrames);

    private short[] Played()
    {
        short[] samples = FakeAAudioDevice.ReadWav(_device.ToWav(), out int rate, out int channels);
        Assert.Equal(FakeAAudioDevice.DeviceRate, rate);
        Assert.Equal(2, channels);
        return samples;
    }

    // ------------------------------------------------------------------ analysis

    /// <summary>The played frames with the silence before and after the content removed, and every run of two or more
    /// silent frames inside it (gaps; the tones never have two zero frames in a row) cut out and counted.</summary>
    private static short[] WithoutSilence(short[] played, out int gaps)
    {
        int frames = played.Length / 2;
        int first = 0;
        while (first < frames && IsSilent(played, first))
        {
            first++;
        }

        int last = frames - 1;
        while (last >= first && IsSilent(played, last))
        {
            last--;
        }

        List<short> content = [];
        gaps = 0;
        int frame = first;
        while (frame <= last)
        {
            int run = 0;
            while (IsSilent(played, frame + run))
            {
                run++;
            }

            if (run >= 2)
            {
                gaps++;
                frame += run;
                continue;
            }

            content.Add(played[2 * frame]);
            content.Add(played[2 * frame + 1]);
            frame++;
        }

        return [.. content];
    }

    private static bool IsSilent(short[] played, int frame) => played[2 * frame] == 0 && played[2 * frame + 1] == 0;

    private static void AssertSame(short[] expected, short[] actual, string what)
    {
        Assert.True(actual.Length == expected.Length, $"{what}: {actual.Length / 2} frames played, {expected.Length / 2} expected");
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                Assert.Fail($"{what}: frame {i / 2} ({(i % 2 == 0 ? "left" : "right")}) is {actual[i]}, expected {expected[i]}");
            }
        }
    }

    /// <summary>Upward zero crossings of one channel per second.</summary>
    private static double Frequency(short[] interleaved, int channel, int rate)
    {
        int frames = interleaved.Length / 2;
        int first = -1;
        int last = -1;
        int crossings = 0;
        for (int frame = 1; frame < frames; frame++)
        {
            if (interleaved[2 * (frame - 1) + channel] < 0 && interleaved[2 * frame + channel] >= 0)
            {
                if (first < 0)
                {
                    first = frame;
                }
                else
                {
                    crossings++;
                }

                last = frame;
            }
        }

        return crossings * (double)rate / (last - first);
    }

    private static double Rms(short[] interleaved, int channel)
    {
        double sum = 0;
        int frames = interleaved.Length / 2;
        for (int frame = 0; frame < frames; frame++)
        {
            double sample = interleaved[2 * frame + channel];
            sum += sample * sample;
        }

        return Math.Sqrt(sum / frames);
    }

    // ------------------------------------------------------------------ (1) a tone arrives whole

    /// <summary>A 48 kHz port on the phone's 48 kHz device: every guest sample, times the port's volume, is played
    /// bit for bit, in order, with no silence inside and nothing lost (the device rate needs no resampler).</summary>
    [Theory]
    [InlineData(256, 1.0f)]
    [InlineData(1024, 0.5f)]
    public void AToneAt48kHzReachesThePhoneWhole(int grain, float volume)
    {
        const int Grains = 120;
        int port = OpenPort(Main, grain, 48000);
        SetVolume(port, volume);
        short[] tone = Tone(Grains * grain, 48000, 1000, 750);
        GuestGrains guest = new(this, port, grain, tone);
        while (!guest.Done)
        {
            guest.Output();
        }

        Assert.Equal(0, PortStream(port).Underruns);
        Assert.Equal(0, _device.Opened[^1].XRunCount);
        PlayOut();

        short[] content = WithoutSilence(Played(), out int gaps);
        Assert.Equal(0, gaps);
        AssertSame(AtVolume(tone, volume), content, "48 kHz port");
        Assert.Equal(0, HleHarness.Call(_audio, "sceAudioOutReleasePort", port));
    }

    /// <summary>A 44.1 kHz BGM port on the 48 kHz device: the stream is resampled to the device's rate (AAudio stays on
    /// its own rate, no resampler of its own). The played tone has every frame (the 48/44.1 count), its frequency and
    /// its level, and follows the continuous tone sample by sample (a lost or repeated grain shifts its phase).</summary>
    [Fact]
    public void ATone441kHzIsResampledToThePhoneRateWithNothingLost()
    {
        const int Grain = 256;
        const int Grains = 160;
        const double LeftHz = 1000;
        const double RightHz = 750;
        const double Amplitude = 8000;
        int port = OpenPort(Bgm, Grain, 44100);
        short[] tone = Tone(Grains * Grain, 44100, LeftHz, RightHz, Amplitude);
        GuestGrains guest = new(this, port, Grain, tone);
        while (!guest.Done)
        {
            guest.Output();
        }

        Assert.Equal(0, PortStream(port).Underruns);
        PlayOut();
        short[] content = WithoutSilence(Played(), out int gaps);
        Assert.Equal(0, gaps);

        // every input frame becomes 48000/44100 output frames (the resampler holds the last frame back)
        double expectedFrames = Grains * Grain * 48000.0 / 44100;
        Assert.InRange(content.Length / 2, expectedFrames - 3, expectedFrames + 1);
        Assert.InRange(Frequency(content, 0, 48000), LeftHz * 0.998, LeftHz * 1.002);
        Assert.InRange(Frequency(content, 1, 48000), RightHz * 0.998, RightHz * 1.002);
        Assert.InRange(Rms(content, 0), Amplitude / Math.Sqrt(2) * 0.99, Amplitude / Math.Sqrt(2) * 1.01);
        Assert.InRange(Rms(content, 1), 0.75 * Amplitude / Math.Sqrt(2) * 0.99, 0.75 * Amplitude / Math.Sqrt(2) * 1.01);
        // the continuous tone at the device rate: linear interpolation of a 1 kHz tone sampled at 44.1 kHz is within
        // A*(pi*f/fs)^2/2 (~20 steps) of it
        for (int frame = 0; frame < content.Length / 2; frame++)
        {
            double time = frame / 48000.0;
            double left = Amplitude * Math.Cos(2 * Math.PI * LeftHz * time + 0.3);
            double right = -0.75 * Amplitude * Math.Cos(2 * Math.PI * RightHz * time + 0.3);
            Assert.True(Math.Abs(content[2 * frame] - left) <= 32 && Math.Abs(content[2 * frame + 1] - right) <= 32,
                $"frame {frame}: {content[2 * frame]}/{content[2 * frame + 1]}, the tone is {left:F0}/{right:F0}");
        }

        Assert.Equal(0, HleHarness.Call(_audio, "sceAudioOutReleasePort", port));
    }

    // ------------------------------------------------------------------ (2) BGM + MAIN

    /// <summary>A MAIN and a BGM port play together: each is its own AAudio stream, the shared mixer adds them, and
    /// the device plays the exact sum of the two ports' samples at their volumes, aligned frame for frame.</summary>
    [Fact]
    public void BgmAndMainPortsAreMixedOnThePhone()
    {
        const int Grain = 256;
        const int Grains = 120;
        int main = OpenPort(Main, Grain, 48000);
        int bgm = OpenPort(Bgm, Grain, 48000);
        SetVolume(bgm, 0.5f);
        short[] effects = Tone(Grains * Grain, 48000, 1500, 500, 6000);
        short[] music = Tone(Grains * Grain, 48000, 440, 660, 9000);
        GuestGrains mainGuest = new(this, main, Grain, effects);
        GuestGrains bgmGuest = new(this, bgm, Grain, music);
        while (!mainGuest.Done)
        {
            mainGuest.Output();
            bgmGuest.Output();
        }

        Assert.Equal(2, _device.Opened.Count);
        Assert.Equal(0, PortStream(main).Underruns);
        Assert.Equal(0, PortStream(bgm).Underruns);
        PlayOut();

        short[] mainPart = effects;
        short[] bgmPart = AtVolume(music, 0.5f);
        short[] expected = new short[mainPart.Length];
        for (int i = 0; i < expected.Length; i++)
        {
            expected[i] = (short)Math.Clamp(mainPart[i] + bgmPart[i], short.MinValue, short.MaxValue);
        }

        short[] content = WithoutSilence(Played(), out int gaps);
        Assert.Equal(0, gaps);
        AssertSame(expected, content, "MAIN + BGM");
        Assert.Equal(0, HleHarness.Call(_audio, "sceAudioOutReleasePort", main));
        Assert.Equal(0, HleHarness.Call(_audio, "sceAudioOutReleasePort", bgm));
    }

    // ------------------------------------------------------------------ (3) underruns

    /// <summary>The guest stops outputting for a while in the middle of a tone. A pause the phone's queue covers (a
    /// grain, one 42.8 ms burst: the stream keeps ~4100 frames, ~85 ms, queued) is no underrun and no gap; a longer one
    /// (three bursts) drains the stream: the device counts the xrun, the stream counts one underrun, and the tone
    /// comes back after the gap with every guest sample in order, none lost or repeated.</summary>
    [Theory]
    [InlineData(256, 0)]
    [InlineData(FakeAAudioDevice.BurstFrames, 0)]
    [InlineData(3 * FakeAAudioDevice.BurstFrames, 1)]
    public void ALateGuestUnderrunsOnlyPastTheQueueAndTheSoundComesBack(int lateFrames, int underruns)
    {
        const int Grain = 256;
        const int Grains = 120;
        int port = OpenPort(Main, Grain, 48000);
        short[] tone = Tone(Grains * Grain, 48000, 1000, 750);
        GuestGrains guest = new(this, port, Grain, tone);
        for (int grain = 0; grain < Grains / 2; grain++)
        {
            guest.Output();
        }

        _device.Run(lateFrames); // the guest is late: the device plays on
        while (!guest.Done)
        {
            guest.Output();
        }

        FakeAAudioStream sink = _device.Opened[^1];
        BufferedPcmStream stream = PortStream(port);
        AudioDiagnostics.PortStats stats = Stats(port);
        Assert.Equal(underruns, sink.DrySpells);
        Assert.Equal(underruns, stream.Underruns);
        Assert.Equal(underruns, stats.Underruns + stats.IdleResumes);
        Assert.True(underruns == 0 || sink.XRunCount > 0, "the device counted no xrun");
        PlayOut();

        short[] content = WithoutSilence(Played(), out int gaps);
        Assert.Equal(underruns, gaps);
        AssertSame(tone, content, $"late by {lateFrames} frames");
        Assert.Equal(0, HleHarness.Call(_audio, "sceAudioOutReleasePort", port));
    }

    /// <summary>A guest slower than the device (a grain every 1.25 grains of device time, an emulation at 80% speed):
    /// the stream runs dry again and again. Every dry burst the device counts is one underrun of the stream (none
    /// missed, none counted twice), the stream re-buffers after each, and every guest sample is still played in
    /// order.</summary>
    [Fact]
    public void ASlowGuestCountsEveryDryBurstAndLosesNoSample()
    {
        const int Grain = 256;
        const int Grains = 400;
        int port = OpenPort(Main, Grain, 48000);
        short[] tone = Tone(Grains * Grain, 48000, 1000, 750);
        GuestGrains guest = new(this, port, Grain, tone);
        while (!guest.Done)
        {
            guest.Output();
            _device.Run(Grain + Grain / 4);
        }

        FakeAAudioStream sink = _device.Opened[^1];
        BufferedPcmStream stream = PortStream(port);
        Assert.True(sink.XRunCount > 1, $"{sink.XRunCount} xruns: the guest was not slow enough");
        Assert.Equal(sink.XRunCount, stream.Underruns);
        PlayOut();

        short[] content = WithoutSilence(Played(), out _);
        AssertSame(tone, content, "slow guest");
        Assert.Equal(0, HleHarness.Call(_audio, "sceAudioOutReleasePort", port));
    }

    // ------------------------------------------------------------------ (4) the AUDIO summary line

    [GeneratedRegex(@"(\d+) outputs in .* underruns (\d+) .* silent (\d+) \(sound from output (\d+)\)(?:, (\d+) starts after an idle pause)?")]
    private static partial Regex SummaryPattern();

    /// <summary>The end-of-session "AUDIO summary" line of a port agrees with what the device received: the outputs
    /// (every frame the stream wrote is a guest grain or the re-buffering silence of an underrun), the silent grains
    /// before the sound, the output the sound started at and the underruns.</summary>
    [Fact]
    public void TheAudioSummaryLineMatchesWhatThePhoneReceived()
    {
        const int Grain = 320;
        const int SilentGrains = 5;
        const int SoundGrains = 100;
        int port = OpenPort(Main, Grain, 48000);
        short[] tone = new short[(SilentGrains + SoundGrains) * Grain * 2];
        Tone(SoundGrains * Grain, 48000, 1000, 750).CopyTo(tone, SilentGrains * Grain * 2);
        GuestGrains guest = new(this, port, Grain, tone);
        for (int grain = 0; grain < SilentGrains + SoundGrains / 2; grain++)
        {
            guest.Output();
        }

        _device.Run(3 * FakeAAudioDevice.BurstFrames);
        while (!guest.Done)
        {
            guest.Output();
        }

        AudioDiagnostics.PortStats stats = Stats(port);
        BufferedPcmStream stream = PortStream(port);
        FakeAAudioStream sink = _device.Opened[^1];
        string prefix = "AUDIO summary: " + stats.Describe() + ":";
        string line = AudioDiagnostics.SummaryLines().Last(l => l.StartsWith(prefix, StringComparison.Ordinal));
        Match match = SummaryPattern().Match(line);
        Assert.True(match.Success, line);
        long outputs = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        long underruns = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        long silent = long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        long soundFrom = long.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        long idle = match.Groups[5].Success ? long.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture) : 0;

        // what the sink received
        int firstSoundFrame = sink.Written.FindIndex(sample => sample != 0) / 2;
        long padFrames = (long)(stream.Policy.StartGrains - 1) * Grain;
        Assert.Equal(1, sink.DrySpells);
        Assert.Equal(sink.DrySpells, underruns + idle);
        Assert.Equal(sink.Written.Count / 2, outputs * Grain + (underruns + idle) * padFrames);
        Assert.Equal(SilentGrains + SoundGrains, outputs);
        Assert.Equal(firstSoundFrame / Grain, silent);
        Assert.Equal(firstSoundFrame / Grain + 1, soundFrom);
        Assert.Contains("device fake AAudio (phone profile), device 48000 Hz", string.Join("\n", AudioDiagnostics.SummaryLines()), StringComparison.Ordinal);
        Assert.Equal(0, HleHarness.Call(_audio, "sceAudioOutReleasePort", port));
    }

    // ------------------------------------------------------------------ F8: the backend's state in the capture

    /// <summary>The F8 capture's context.txt says what the audio backend is doing: the device (rate, burst, the
    /// stream's buffering) and, per port, the host queue, the underruns and the silent grains, so a report of
    /// crackling or missing sound on the phone is diagnosed from the capture alone.</summary>
    [Fact]
    public void TheCaptureContextHasTheAudioBackendState()
    {
        const int Grain = 448; // no other test uses it: ports other tests left open are listed too
        int port = OpenPort(Main, Grain, 48000);
        short[] tone = Tone(80 * Grain, 48000, 1000, 750);
        GuestGrains guest = new(this, port, Grain, tone);
        for (int grain = 0; grain < 40; grain++)
        {
            guest.Output();
        }

        _device.Run(3 * FakeAAudioDevice.BurstFrames);
        while (!guest.Done)
        {
            guest.Output();
        }

        AudioDiagnostics.PortStats stats = Stats(port);
        List<string> lines = CaptureContext.AudioLines(_harness.Emu);
        string device = Assert.Single(lines, l => l.StartsWith("audio: device ", StringComparison.Ordinal));
        Assert.Contains("48000 Hz", device, StringComparison.Ordinal);
        Assert.Contains($"burst {FakeAAudioDevice.BurstFrames} frames", device, StringComparison.Ordinal);
        Assert.Contains($"capacity {FakeAAudioDevice.CapacityFrames} frames", device, StringComparison.Ordinal);
        string portLine = Assert.Single(lines, l => l.StartsWith($"audio: {stats.Name} ", StringComparison.Ordinal) && l.Contains($"grain {Grain};", StringComparison.Ordinal));
        Assert.Matches(@"host queue \d+ samples", portLine);
        Assert.Contains($"underruns {stats.Underruns}", portLine, StringComparison.Ordinal);
        Assert.Contains($"silent 0/{stats.Outputs}", portLine, StringComparison.Ordinal);
        Assert.Equal(0, HleHarness.Call(_audio, "sceAudioOutReleasePort", port));
    }
}
