using VitaSharp.Core.Shell;

namespace VitaSharp.Tests;

/// <summary>The trophy notification sound: the built-in chime and the format conversion the front-ends use.</summary>
public sealed class TrophySoundTests
{
    [Fact]
    public void WithoutFirmwareTheBuiltInChimeIsUsed()
    {
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-trophysound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (pcm, source) = TrophySound.Load(root);
            Assert.Equal("built-in chime", source);
            Assert.Equal(48000, pcm.SampleRate);
            Assert.Equal(1, pcm.Channels);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ChimeIsShortAndNotClipped()
    {
        var pcm = TrophySound.Chime();
        Assert.InRange(pcm.Seconds, 0.5, 1.0);
        int peak = pcm.Samples.Max(s => Math.Abs((int)s));
        Assert.InRange(peak, 15000, 17500);              // about -6 dBFS
        Assert.True(Math.Abs((int)pcm.Samples[^1]) < 200); // faded out: no click at the end
    }

    sealed class FakeSink : VitaSharp.Android.Audio.IPcmSink
    {
        public readonly List<short> Written = new();
        public bool Started;
        public int SampleRate => 44100;
        public int Channels => 2;
        public int FramesPerBurst => 192;
        public string Description => "fake";
        public int SetBufferSize(int frames) => frames;
        public int Write(ReadOnlySpan<short> interleaved, long timeoutNanos)
        {
            int frames = Math.Min(interleaved.Length / 2, 1000); // partial writes, as a full device would do
            Written.AddRange(interleaved[..(frames * 2)].ToArray());
            return frames;
        }
        public long FramesWritten => Written.Count / 2;
        public long FramesRead => FramesWritten;
        public int XRunCount => 0;
        public bool Disconnected => false;
        public void Start() => Started = true;
        public void Pause() { }
        public void Dispose() { }
    }

    [Fact]
    public void AndroidEffectPlayerWritesTheWholeSoundAtTheDeviceFormat()
    {
        var sink = new FakeSink();
        var pcm = TrophySound.Chime();
        VitaSharp.Android.Audio.EffectPlayer.Play(sink, pcm);
        Assert.True(sink.Started);
        Assert.Equal(TrophySound.Convert(pcm, 44100, 2).Length, sink.Written.Count);
    }

    [Fact]
    public void ConvertResamplesAndUpmixes()
    {
        var pcm = new PcmSound(24000, 1, [0, 1000, 2000, 3000]);
        var o = TrophySound.Convert(pcm, 48000, 2);
        Assert.Equal(16, o.Length);                       // 8 frames x 2 channels
        Assert.Equal(new short[] { 0, 0, 500, 500, 1000, 1000 }, o[..6]);
        var stereo = new PcmSound(48000, 2, [100, 300, 100, 300]);
        Assert.Equal(new short[] { 200, 200 }, TrophySound.Convert(stereo, 48000, 1));
    }
}
