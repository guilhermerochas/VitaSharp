using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Host;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceAudioIn with a host microphone (SceAudioin 3.74: one grain per grain/rate seconds of guest time, S16 mono at the
/// port's rate). A scripted tone source stands for the host device, on a clock the test moves itself, so the samples
/// each read gets are decided by the test and never by the host's speed: each grain carries the tone's next samples,
/// resampled when the device runs at another rate; the guest clock moves one grain per read; mute, no microphone and
/// another process holding the microphone give silence; the host device is on only while the port is open; nothing of
/// the samples reaches a capture unless asked for.
/// </summary>
[Collection("guest")]
public sealed class MicrophoneTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Port = 0x80;

    private readonly HleHarness _harness = new(fixture, "mic");
    private readonly bool _turbo = GuestClock.Turbo;

    /// <summary>Grains of port time the scripted device has captured: the test's own clock.</summary>
    private long _capturedGrains;

    public void Dispose()
    {
        GuestClock.Turbo = _turbo;
        _harness.Dispose();
    }

    private Audio Audio => _harness.Module<Audio>();

    /// <summary>A tone source whose clock is "<see cref="_capturedGrains"/> grains of the port".</summary>
    private ToneMicrophone Tone(int grain, int portRate, int? deviceRate = null, double frequency = 1000)
    {
        var tone = new ToneMicrophone(frequency, 8000, deviceRate, () => _capturedGrains * grain / (double)portRate);
        _harness.Emu.Host.Microphone = tone;
        return tone;
    }

    /// <summary>One sceAudioInInput after the device captured one more grain; returns the grain the guest got.</summary>
    private short[] Input(uint dest, int grain, int advanceGrains = 1)
    {
        _capturedGrains += advanceGrains;
        Assert.Equal(0, Call(Audio, "sceAudioInInput", Port, dest));
        return GuestMemory.ReadSpan<short>(dest, grain).ToArray();
    }

    [Theory]
    [InlineData(0, 256, 16000)] // VOICE
    [InlineData(0, 512, 16000)]
    [InlineData(0x20, 256, 16000)] // the camera profile
    [InlineData(2, 256, 16000)] // RAW
    [InlineData(2, 768, 48000)]
    [InlineData(0x1F, 768, 48000)]
    public void EachGrainCarriesTheNextSamplesAtTheGrainPace(int type, int grain, int rate)
    {
        GuestClock.Turbo = true; // the waits move the guest clock instead of sleeping
        ToneMicrophone tone = Tone(grain, rate);
        Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", type, grain, rate, 0));
        uint dest = _harness.Alloc((uint)grain * 2);
        double start = GuestClock.Seconds;
        const int GrainCount = 6;
        for (int k = 0; k < GrainCount; k++)
        {
            short[] got = Input(dest, grain);
            for (int i = 0; i < grain; i++)
            {
                Assert.Equal(tone.Sample((long)k * grain + i, rate), got[i]);
            }
        }

        // the guest clock moved one grain per read: the hardware delivers one grain per grain/rate seconds
        double elapsed = GuestClock.Seconds - start;
        Assert.True(elapsed >= GrainCount * (double)grain / rate - 1e-6, $"{GrainCount} reads took {elapsed * 1000:F2} ms of guest time");
        Assert.Equal(0, Call(Audio, "sceAudioInReleasePort", Port));
    }

    [Fact]
    public void ADeviceAtThreeTimesThePortRateIsDecimated()
    {
        GuestClock.Turbo = true;
        ToneMicrophone tone = Tone(256, 16000, deviceRate: 48000);
        Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 0, 256, 16000, 0));
        uint dest = _harness.Alloc(512);
        for (int k = 0; k < 4; k++)
        {
            short[] got = Input(dest, 256);
            for (int i = 0; i < 256; i++)
            {
                Assert.Equal(tone.Sample(3L * (k * 256 + i), 48000), got[i]);
            }
        }
    }

    [Fact]
    public void ADeviceAt44100IsResampledToThePortRate()
    {
        GuestClock.Turbo = true;
        Tone(256, 16000, deviceRate: 44100, frequency: 440);
        Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 0, 256, 16000, 0));
        uint dest = _harness.Alloc(512);
        for (int k = 0; k < 20; k++)
        {
            short[] got = Input(dest, 256);
            for (int i = 0; i < 256; i++)
            {
                double expected = 8000 * Math.Sin(2 * Math.PI * 440 * (k * 256 + i) / 16000.0);
                Assert.True(Math.Abs(got[i] - expected) <= 40, $"grain {k} sample {i}: {got[i]} instead of {expected:F0}");
            }
        }
    }

    [Fact]
    public void MuteSilencesTheGrainsAndUnmuteGoesOnWithWhatIsSaidThen()
    {
        GuestClock.Turbo = true;
        ToneMicrophone tone = Tone(256, 16000);
        Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 0, 256, 16000, 0));
        uint dest = _harness.Alloc(512);
        Assert.NotEqual(0, Input(dest, 256).Max());
        Assert.Equal(1, Call(Audio, "sceAudioInSetMute", 1));
        Assert.All(Input(dest, 256), sample => Assert.Equal(0, sample));
        Assert.All(Input(dest, 256), sample => Assert.Equal(0, sample));
        Assert.Equal(0, Call(Audio, "sceAudioInSetMute", 2));
        short[] got = Input(dest, 256);
        // the muted grains were consumed: grain 3 of the device, not grain 1
        Assert.Equal(tone.Sample(3 * 256 + 17, 16000), got[17]);
    }

    [Fact]
    public void WithoutAMicrophoneThePortIsSilentAtTheSamePace()
    {
        GuestClock.Turbo = true;
        _harness.Emu.Host.Microphone = null;
        Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 2, 768, 48000, 0));
        uint dest = _harness.Alloc(768 * 2);
        GuestMemory.Fill(dest, 0x55, 768 * 2);
        double start = GuestClock.Seconds;
        for (int k = 0; k < 3; k++)
        {
            Assert.All(Input(dest, 768), sample => Assert.Equal(0, sample));
        }

        Assert.True(GuestClock.Seconds - start >= 3 * 0.016 - 1e-6);
    }

    [Fact]
    public void ADeviceThatCannotOpenGivesSilence()
    {
        GuestClock.Turbo = true;
        _harness.Emu.Host.Microphone = new NoDevice();
        Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 0, 256, 16000, 0));
        uint dest = _harness.Alloc(512);
        GuestMemory.Fill(dest, 0x55, 512);
        Assert.All(Input(dest, 256), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void AProcessTheMicrophoneIsNotAdoptedForHearsSilence()
    {
        GuestClock.Turbo = true;
        Tone(256, 16000);
        Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 0, 256, 16000, 0));
        uint dest = _harness.Alloc(512);
        Assert.NotEqual(0, Input(dest, 256).Max());
        Assert.Equal(0, Call(Audio, "ksceAudioInSetAdoptForPid", _harness.Emu.Pid + 1, 0u, 1u, 0));
        Assert.All(Input(dest, 256), sample => Assert.Equal(0, sample));
        Assert.Equal(0, Call(Audio, "ksceAudioInSetAdoptForPid", _harness.Emu.Pid, 0u, 0u, 0));
        Assert.NotEqual(0, Input(dest, 256).Max());
    }

    [Fact]
    public void AHostThatGotFarAheadGivesTheNewestSamples()
    {
        GuestClock.Turbo = true;
        ToneMicrophone tone = Tone(256, 16000);
        Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 0, 256, 16000, 0));
        uint dest = _harness.Alloc(512);
        Input(dest, 256);
        // the guest was away for a second (paused, a loading screen): 62 grains captured meanwhile
        short[] got = Input(dest, 256, advanceGrains: 63);
        long newest = 64L * 256;
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(tone.Sample(newest - 256 + i, 16000), got[i]);
        }

        // and it goes on from there
        got = Input(dest, 256);
        Assert.Equal(tone.Sample(newest + 5, 16000), got[5]);
    }

    [Fact]
    public void AHostThatFellBehindGivesASilentGrainAndLosesNothing()
    {
        GuestClock.Turbo = true;
        ToneMicrophone tone = Tone(256, 16000);
        Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 0, 256, 16000, 0));
        uint dest = _harness.Alloc(512);
        Input(dest, 256);
        Assert.All(Input(dest, 256, advanceGrains: 0), sample => Assert.Equal(0, sample));
        short[] got = Input(dest, 256);
        Assert.Equal(tone.Sample(256 + 9, 16000), got[9]);
    }

    [Fact]
    public void TheHostDeviceIsOnOnlyWhileThePortIsOpen()
    {
        GuestClock.Turbo = true;
        ToneMicrophone tone = Tone(256, 16000);
        Assert.Empty(tone.Streams);
        Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 0, 256, 16000, 0));
        ToneMicrophone.ToneStream stream = Assert.Single(tone.Streams);
        Assert.Equal(1, _harness.Emu.Host.OpenMicrophoneFeeds);
        Assert.False(stream.Disposed);
        Assert.Equal(0, Call(Audio, "sceAudioInReleasePort", Port));
        Assert.True(stream.Disposed);
        Assert.Equal(0, _harness.Emu.Host.OpenMicrophoneFeeds);

        // a process image replaced with its port open (LoadExec): the new image's modules close the old capture
        Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 0, 256, 16000, 0));
        Assert.Equal(1, _harness.Emu.Host.OpenMicrophoneFeeds);
        new Audio { Emu = _harness.Emu }.Initialize();
        Assert.True(tone.Streams[1].Disposed);
        Assert.Equal(0, _harness.Emu.Host.OpenMicrophoneFeeds);
    }

    [Theory]
    [InlineData(0, 256, 48000, 1, 0x80260103)] // the rate is checked before the parameter
    [InlineData(0, 300, 16000, 1, 0x80260102)] // and the grain too
    [InlineData(0, 256, 16000, 1, 0x80260106)]
    [InlineData(0x10, 256, 16000, 0, 0x80)] // any multiple of 16 below 0x400 is a VOICE-like port
    [InlineData(0x12, 768, 48000, 0, 0x80)] // RAW with the 0x10 bit
    [InlineData(0x12, 768, 44100, 0, 0x80260103)]
    [InlineData(0x1F, 768, 48000, 0, 0x80)]
    [InlineData(0x1F, 768, 16000, 0, 0x80260103)]
    [InlineData(0x1F, 256, 48000, 0, 0x80260102)]
    [InlineData(2, 512, 16000, 0, 0x80260102)]
    [InlineData(0x400, 256, 16000, 0, 0x80260104)]
    [InlineData(3, 256, 16000, 0, 0x80260104)]
    [InlineData(0x22, 256, 16000, 0, 0x80260104)]
    public void OpenChecksTypeRateGrainThenParameter(int type, int grain, int rate, int param, uint expected)
    {
        _harness.Emu.Host.Microphone = null;
        Assert.Equal(unchecked((int)expected), Call(Audio, "sceAudioInOpenPort", type, grain, rate, param));
        if (expected == Port)
        {
            Assert.Equal(0, Call(Audio, "sceAudioInReleasePort", Port));
        }
    }

    [Fact]
    public void ACaptureKeepsTheFormatAndPaceButNotTheSamplesUnlessAskedFor()
    {
        GuestClock.Turbo = true;
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-mic-" + Guid.NewGuid().ToString("N"));
        Tone(256, 16000);
        Assert.Equal(Port, Call(Audio, "sceAudioInOpenPort", 0, 256, 16000, 0));
        uint dest = _harness.Alloc(512);
        try
        {
            AudioDump.Enable(Path.Combine(dir, "default"));
            Input(dest, 256);
            Input(dest, 256);
            AudioDump.Disable();
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "default"), "*microphone*"));
            string report = AudioDiagnostics.Describe();
            Assert.Contains("grain 256 at 16000 Hz, source tone 1000 Hz at 16000 Hz", report);

            AudioDump.MicrophoneDumps = true;
            AudioDump.Enable(Path.Combine(dir, "asked"));
            Input(dest, 256);
            AudioDump.Disable();
            string wav = Assert.Single(Directory.GetFiles(Path.Combine(dir, "asked"), "*microphone_16000.wav"));
            Assert.Equal(44 + 256 * 4, new FileInfo(wav).Length);
        }
        finally
        {
            AudioDump.MicrophoneDumps = false;
            if (AudioDump.Enabled)
            {
                AudioDump.Disable();
            }

            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FillingAGrainAllocatesNothing()
    {
        long captured = 0;
        var tone = new ToneMicrophone(440, 8000, 44100, () => captured * 256 / 16000.0);
        using var feed = new MicrophoneFeed(tone.Open(16000), 16000, 256);
        short[] grain = new short[256];
        for (int k = 0; k < 8; k++)
        {
            captured++;
            feed.Fill(grain, muted: false);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 0; k < 100; k++)
        {
            captured++;
            feed.Fill(grain, muted: false);
        }

        // the scripted clock's closure is the test's; the feed itself allocates nothing per grain
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"{allocated} bytes allocated for 100 grains");
        Assert.Equal(0, feed.ShortGrains);
    }

    [Theory]
    [InlineData("", MicrophoneSpecKind.Default)]
    [InlineData("none", MicrophoneSpecKind.None)]
    [InlineData("NONE", MicrophoneSpecKind.None)]
    [InlineData("tone", MicrophoneSpecKind.Tone)]
    [InlineData("tone:1000", MicrophoneSpecKind.Tone)]
    [InlineData("USB Headset", MicrophoneSpecKind.Device)]
    public void TheSettingParses(string value, MicrophoneSpecKind kind)
    {
        var spec = MicrophoneSpec.Parse(value);
        Assert.Equal(kind, spec.Kind);
        if (value == "tone:1000")
        {
            Assert.Equal(1000, spec.ToneHz);
        }

        Assert.Null(MicrophoneSpec.None.CreateSource(_ => throw new InvalidOperationException("none opens no device")));
        Assert.IsType<ToneMicrophone>(MicrophoneSpec.Parse("tone").CreateSource(_ => null));
        string[] names = ["Built-in Microphone", "USB Headset Microphone"];
        Assert.Equal(1, MicrophoneSpec.Parse("usb headset").SelectDevice(names));
        Assert.Equal(0, MicrophoneSpec.Parse("0").SelectDevice(names));
        Assert.Equal(-1, MicrophoneSpec.Parse("webcam").SelectDevice(names));
        Assert.Equal(0, MicrophoneSpec.Default.SelectDevice(names));
    }

    [Fact]
    public void TheRingKeepsTheNewestSamplesWhenNobodyReads()
    {
        var ring = new MicrophoneRing(8);
        short[] first = [1, 2, 3, 4, 5];
        ring.Write(first);
        Assert.Equal(5, ring.Available);
        short[] got = new short[3];
        Assert.Equal(3, ring.Read(got));
        Assert.Equal([1, 2, 3], got);
        short[] more = [6, 7, 8, 9, 10, 11, 12, 13, 14];
        ring.Write(more);
        // 4, 5 and 6 were overwritten: the last eight samples are kept
        Assert.Equal(8, ring.Available);
        Assert.Equal(3, ring.Overwritten);
        short[] all = new short[10];
        Assert.Equal(8, ring.Read(all));
        Assert.Equal([7, 8, 9, 10, 11, 12, 13, 14], all[..8]);
        Assert.Equal(0, ring.Read(all));
    }

    [Fact]
    public void ThePcAndThePhoneSettingsUseTheSameValues()
    {
        var pc = new VitaSharp.Desktop.Launcher.LauncherSettings { Microphone = "none" };
        Assert.Contains("--microphone=none", pc.RunOptions());
        Assert.DoesNotContain(new VitaSharp.Desktop.Launcher.LauncherSettings().RunOptions(), o => o.StartsWith("--microphone", StringComparison.Ordinal));
        (List<string> values, _) = VitaSharp.Desktop.Launcher.SettingsPage.MicrophoneChoices(["USB Headset"], "Old Mic");
        Assert.Equal(["", "none", "tone", "USB Headset", "Old Mic"], values);
        IReadOnlyList<VitaSharp.Mobile.Core.Settings.Choice> phone = VitaSharp.Mobile.Core.Settings.MicrophoneSettings.Choices;
        Assert.Equal(
            [MicrophoneSpecKind.Default, MicrophoneSpecKind.None, MicrophoneSpecKind.Tone],
            phone.Select(choice => MicrophoneSpec.Parse(choice.Value).Kind));
        Assert.All(phone, choice => Assert.Equal(choice.Label, VitaSharp.Mobile.Core.Settings.MicrophoneSettings.Label(choice.Value)));
    }

    /// <summary>A host whose device does not open (no microphone, permission refused).</summary>
    private sealed class NoDevice : IMicrophoneSource
    {
        public IReadOnlyList<string> DeviceNames => [];

        public IMicrophoneStream? Open(int sampleRate) => null;

        public void Dispose()
        {
        }
    }
}
