using System.Collections.Concurrent;
using VitaSharp.Core.Media;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Media;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// Desktop-vs-Android media parity (<see cref="MediaContract"/>): the same synthetic clips through the guest-facing
/// decoder calls must give the same trace on every backend. The desktop side (FFmpeg) always runs and must match the
/// Vita's contract (sceAvcdecDecode yields the picture of the call's own unit, bit-exact; AAC 1024 and MP3 1152 samples
/// per frame); an asynchronous decoder like Android's MediaCodec, simulated here on real threads, must give the same
/// video trace through <see cref="SynchronousOutput"/>; and a trace recorded on an Android device or emulator
/// (build/android-media-parity.sh or .ps1, path in VITASHARP_ANDROID_MEDIA_TRACE) is compared with the desktop's.
/// <para>Audio clips (Fixtures/Media, generated, no game content):
/// <c>ffmpeg -f lavfi -i sine=frequency=440:sample_rate=48000:duration=0.5 -f lavfi -i sine=frequency=660:sample_rate=48000:duration=0.5
/// -filter_complex "[0][1]amerge=inputs=2" -c:a aac -b:a 96k -f adts tone48k-stereo.aac</c>, and the same at 44100 Hz with
/// <c>-c:a libmp3lame -b:a 128k -id3v2_version 0 -write_xing 0 -f mp3 tone44k-stereo.mp3</c>.</para>
/// </summary>
public sealed class MediaParityTests(ITestOutputHelper output)
{
    private static byte[] Clip(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name));

    private static List<string> DesktopTrace() =>
        MediaContract.Run(() => new FFmpegVideoBackend().CreateH264(new VideoDecoderOptions(64, 64)),
            () => AudioFrameDecoders.CreateAac(48000, 2, null), () => AudioFrameDecoders.CreateMp3(2),
            Clip(MediaContract.AacClipFile), Clip(MediaContract.Mp3ClipFile));

    [FFmpegFact]
    public void DesktopTraceIsTheVitaContract()
    {
        List<string> trace = DesktopTrace();
        List<SyntheticH264.Unit> clip = MediaContract.VideoClip();
        var video = trace.Where(line => line.StartsWith("video\t", StringComparison.Ordinal)).ToList();
        Assert.Equal(clip.Count + 1, video.Count);
        for (int call = 0; call < clip.Count; call++)
        {
            Assert.Equal($"video\t{MediaContract.VideoLabel}\tsend\t{call}\t{call}:64x64:{MediaContract.ExpectedHash(clip[call], 64, 64)}", video[call]);
        }
        Assert.Equal($"video\t{MediaContract.VideoLabel}\tflush\t{clip.Count}\t-", video[^1]);

        foreach ((string label, int frameSamples) in new[] { (MediaContract.AacLabel, 1024), (MediaContract.Mp3Label, 1152) })
        {
            var frames = trace.Where(line => line.StartsWith("audio\t" + label + "\t", StringComparison.Ordinal))
                .Select(line => line.Split('\t')).ToList();
            Assert.True(frames.Count >= 20, $"{label}: {frames.Count} frames");
            Assert.All(frames, fields => Assert.Equal(frameSamples.ToString(), fields[3]));
            // the tone is there (not silence) in the middle of the clip (lavfi's sine is at 1/8 of full scale: ~4096)
            byte[] middle = Convert.FromBase64String(frames[frames.Count / 2][4]);
            int peak = 0;
            for (int i = 0; i + 1 < middle.Length; i += 2)
            {
                peak = Math.Max(peak, Math.Abs((int)(short)(middle[i] | (middle[i + 1] << 8))));
            }
            Assert.True(peak > 2000, $"{label}: peak {peak}");
        }
    }

    /// <summary>
    /// An H.264 decoder that works like Android's MediaCodec: a worker thread decodes each queued unit (FFmpeg plus
    /// <paramref name="decodeMs"/> of work) and puts the picture in an output queue; the caller's side is the same as
    /// MediaCodecVideoDecoder's: take finished pictures, queue the unit, then wait for its picture through
    /// <see cref="SynchronousOutput"/> (or, <paramref name="legacyTiming"/>, the old 5 ms grace once a picture came out).
    /// </summary>
    private sealed class ThreadedDecoder : IVideoDecoder
    {
        private readonly IVideoDecoder _inner;
        private readonly int _decodeMs;
        private readonly bool _legacyTiming;
        private readonly BlockingCollection<(byte[] Unit, long Pts)> _input = new();
        private readonly BlockingCollection<DecodedPicture> _output = new();
        private readonly Queue<DecodedPicture> _ready = new();
        private readonly SynchronousOutput _sync;
        private readonly int _slowUnit;
        private readonly int _slowUnitMs;
        private int _decoded;
        private readonly Thread _worker;

        /// <param name="slowUnit">A unit whose decode takes <paramref name="slowUnitMs"/> instead (a busy machine).</param>
        public ThreadedDecoder(IVideoDecoder inner, int decodeMs, bool legacyTiming, bool pipelined = false, int slowUnit = -1, int slowUnitMs = 0)
        {
            _inner = inner;
            _decodeMs = decodeMs;
            _legacyTiming = legacyTiming;
            _slowUnit = slowUnit;
            _slowUnitMs = slowUnitMs;
            // the waits are for pictures, not for time: deadlines far beyond any decode, so a slow CI runner (the Windows
            // one gave a picture after the 40 ms deadline) cannot turn into a decoder that holds pictures back
            _sync = new SynchronousOutput { PipelinedAfterStart = pipelined, FirstDeadlineUs = 10_000_000, DeadlineUs = 10_000_000 };
            _worker = new Thread(Work) { IsBackground = true, Name = "simulated MediaCodec" };
            _worker.Start();
        }

        public string Name => "simulated-mediacodec";

        public bool IsHardware => false;

        public bool Asynchronous => true;

        private void Work()
        {
            foreach ((byte[] unit, long pts) in _input.GetConsumingEnumerable())
            {
                Thread.Sleep(_decoded == _slowUnit ? _slowUnitMs : _decodeMs);
                _decoded++;
                _inner.Send(unit, pts);
                while (_inner.Take() is { } picture)
                {
                    _output.Add(new DecodedPicture { Width = picture.Width, Height = picture.Height, Pts = picture.Pts, Y = picture.Y.ToArray(), U = picture.U.ToArray(), V = picture.V.ToArray() });
                    _inner.Recycle(picture);
                }
            }
        }

        private void Poll(long timeoutUs)
        {
            if (_output.TryTake(out DecodedPicture? picture, TimeSpan.FromMicroseconds(timeoutUs)))
            {
                _ready.Enqueue(picture);
                _sync.PictureOut();
                while (_output.TryTake(out DecodedPicture? more))
                {
                    _ready.Enqueue(more);
                    _sync.PictureOut();
                }
            }
        }

        private static long NowUs() => System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;

        public void Send(ReadOnlySpan<byte> accessUnit, long pts)
        {
            Poll(0);
            _input.Add((accessUnit.ToArray(), pts));
            _sync.UnitQueued(H264AnnexB.ContainsSlice(accessUnit));
            if (_legacyTiming)
            {
                Poll(0);
                if (_ready.Count == 0 && _sync.Outstanding > 0 && _sync.Started)
                {
                    Poll(5_000);
                }
            }
            else
            {
                _sync.Await(Poll, NowUs);
            }
        }

        public void Flush()
        {
            while (_sync.Outstanding > 0 && _output.TryTake(out DecodedPicture? picture, TimeSpan.FromSeconds(2)))
            {
                _ready.Enqueue(picture);
                _sync.PictureOut();
            }
            _sync.Restart();
        }

        public void Reset()
        {
            _ready.Clear();
            _sync.Restart();
        }

        public DecodedPicture? Take() => _ready.Count > 0 ? _ready.Dequeue() : null;

        public void Recycle(DecodedPicture picture)
        {
        }

        public void Dispose()
        {
            _input.CompleteAdding();
            _worker.Join();
            _inner.Dispose();
        }
    }

    [FFmpegFact]
    public void AnAsynchronousDecoderGivesTheDesktopVideoTrace()
    {
        var clip = MediaContract.VideoClip().Take(12).ToList();
        List<string> desktop;
        using (IVideoDecoder decoder = new FFmpegVideoBackend().CreateH264(new VideoDecoderOptions(64, 64))!)
        {
            desktop = MediaContract.VideoTrace(decoder, clip, MediaContract.VideoLabel);
        }

        List<string> synchronous;
        // unit 1 takes 120 ms, three times the default picture deadline: the trace must stay the desktop's
        using (var decoder = new ThreadedDecoder(new FFmpegVideoBackend().CreateH264(new VideoDecoderOptions(64, 64))!, 8, legacyTiming: false, slowUnit: 1, slowUnitMs: 120))
        {
            synchronous = MediaContract.VideoTrace(decoder, clip, MediaContract.VideoLabel, 20_000);
        }
        List<string> differences = MediaContract.Compare(desktop, synchronous);
        Assert.True(differences.Count == 0, string.Join("\n", differences));

        // the timing MediaCodecVideoDecoder had before (VITASHARP_MEDIACODEC_SYNC=0): the first call yields nothing and
        // every call after it the previous unit's picture, a contract the Vita's decoder never shows
        List<string> legacy;
        using (var decoder = new ThreadedDecoder(new FFmpegVideoBackend().CreateH264(new VideoDecoderOptions(64, 64))!, 8, legacyTiming: true))
        {
            legacy = MediaContract.VideoTrace(decoder, clip, MediaContract.VideoLabel, 20_000);
        }
        List<string> legacyDifferences = MediaContract.Compare(desktop, legacy);
        output.WriteLine(string.Join("\n", legacyDifferences.Take(4)));
        Assert.NotEmpty(legacyDifferences);
        Assert.EndsWith("\t-", legacy[0]);
        Assert.NotEmpty(MediaContract.CompareInOrder(desktop, legacy, out _));

        // MediaCodecVideoDecoder's default: the first call waits for its picture, the later ones take what is done; the
        // same pictures in the same order, the first one in the first call, the others at most one call late
        List<string> pipelined;
        using (var decoder = new ThreadedDecoder(new FFmpegVideoBackend().CreateH264(new VideoDecoderOptions(64, 64))!, 8, legacyTiming: false, pipelined: true))
        {
            pipelined = MediaContract.VideoTrace(decoder, clip, MediaContract.VideoLabel, 20_000);
        }
        List<string> pipelinedDifferences = MediaContract.CompareInOrder(desktop, pipelined, out int lag);
        Assert.True(pipelinedDifferences.Count == 0, string.Join("\n", pipelinedDifferences));
        output.WriteLine($"pipelined: pictures up to {lag} calls late"); // timing-dependent: order and the first call are the contract
    }

    [Fact]
    public void TraceFormatAndComparison()
    {
        var lines = new List<string> { "video\tv\tsend\t0\t0:64x64:AB", "audio\ta\t0\t2\t" + Convert.ToBase64String(new byte[] { 0, 1, 0, 2, 0, 3, 0, 4 }) };
        Assert.Equal(lines, MediaContract.Parse(MediaContract.Format(lines)));
        Assert.Throws<FormatException>(() => MediaContract.Parse("something else\n"));
        Assert.Empty(MediaContract.Compare(lines, lines));
        // a sample a little off is decoder rounding; a different count or a different picture is not
        var rounded = new List<string> { lines[0], "audio\ta\t0\t2\t" + Convert.ToBase64String(new byte[] { 10, 1, 0, 2, 0, 3, 0, 4 }) };
        Assert.Empty(MediaContract.Compare(lines, rounded));
        var louder = new List<string> { lines[0], "audio\ta\t0\t2\t" + Convert.ToBase64String(new byte[] { 0, 9, 0, 2, 0, 3, 0, 4 }) };
        Assert.Single(MediaContract.Compare(lines, louder));
        var shorter = new List<string> { lines[0], "audio\ta\t0\t1\t" + Convert.ToBase64String(new byte[] { 0, 1, 0, 2 }) };
        Assert.Single(MediaContract.Compare(lines, shorter));
        var late = new List<string> { "video\tv\tsend\t0\t-", lines[1] };
        Assert.Single(MediaContract.Compare(lines, late));
        Assert.Single(MediaContract.Compare(lines, lines.Take(1).ToList()));

        // in-order comparison: a picture one call late is fine, a missing first picture or a lost one is not
        var desktop = new List<string> { "video\tv\tsend\t0\t0:A", "video\tv\tsend\t1\t1:B", "video\tv\tflush\t2\t-" };
        var oneLate = new List<string> { "video\tv\tsend\t0\t0:A", "video\tv\tsend\t1\t-", "video\tv\tflush\t2\t1:B" };
        Assert.Empty(MediaContract.CompareInOrder(desktop, oneLate, out int lag));
        Assert.Equal(1, lag);
        var lateStart = new List<string> { "video\tv\tsend\t0\t-", "video\tv\tsend\t1\t0:A,1:B", "video\tv\tflush\t2\t-" };
        Assert.Single(MediaContract.CompareInOrder(desktop, lateStart, out _));
        var lost = new List<string> { "video\tv\tsend\t0\t0:A", "video\tv\tsend\t1\t-", "video\tv\tflush\t2\t-" };
        Assert.Single(MediaContract.CompareInOrder(desktop, lost, out _));
    }

    [Fact]
    public void AdtsHeadersGiveTheRawConfig()
    {
        // the ADTS clip's first header: AAC-LC, 48 kHz, stereo, no CRC -> AudioSpecificConfig 0x11 0x90, the one the raw
        // path builds from the same rate and channels (Android decodes ADTS frames as raw ones with it)
        byte[] clip = Clip(MediaContract.AacClipFile);
        Assert.True(AdtsHeader.TryParse(clip, out int headerLength, out byte[] config, out int rate, out int channels));
        Assert.Equal(7, headerLength);
        Assert.Equal(48000, rate);
        Assert.Equal(2, channels);
        Assert.Equal(FrameDecoder.AacConfig(48000, 2), config);
        // with a CRC the header is 9 bytes; anything else is not ADTS
        byte[] withCrc = clip.AsSpan(0, 16).ToArray();
        withCrc[1] &= 0xFE;
        Assert.True(AdtsHeader.TryParse(withCrc, out headerLength, out _, out _, out _));
        Assert.Equal(9, headerLength);
        Assert.False(AdtsHeader.TryParse(Clip(MediaContract.Mp3ClipFile), out _, out _, out _, out _));
        Assert.False(AdtsHeader.TryParse(new byte[] { 0xFF, 0xF1 }, out _, out _, out _, out _));
    }

    [AndroidMediaTraceFact]
    public void AndroidTraceMatchesTheDesktop()
    {
        string path = Environment.GetEnvironmentVariable(EnvVars.AndroidMediaTrace)!;
        List<string> android = MediaContract.Parse(File.ReadAllText(path));
        // pictures may come out up to a call late after the first one (SynchronousOutput.PipelinedAfterStart, the
        // default; VITASHARP_MEDIACODEC_SYNC=all in APP_ENV records the call-exact timing)
        List<string> differences = MediaContract.CompareInOrder(DesktopTrace(), android, out int lag);
        output.WriteLine($"{differences.Count} differences between the desktop trace and {path}; pictures up to {lag} calls late");
        foreach (string difference in differences.Take(40))
        {
            output.WriteLine(difference);
        }

        Assert.True(differences.Count == 0, string.Join("\n", differences.Take(40)));
    }
}

/// <summary>[Fact] skipped unless VITASHARP_ANDROID_MEDIA_TRACE names a trace pulled from an Android device or emulator
/// (build/android-media-parity.sh or .ps1) and FFmpeg is there for the desktop side.</summary>
public sealed class AndroidMediaTraceFactAttribute : FactAttribute
{
    public AndroidMediaTraceFactAttribute()
    {
        string? path = Environment.GetEnvironmentVariable(EnvVars.AndroidMediaTrace);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Skip = "no Android media trace: run build/android-media-parity.sh (or .ps1) with a device or the Android Emulator, then set VITASHARP_ANDROID_MEDIA_TRACE to the pulled trace";
        }
        else if (!FFmpegLoader.Available)
        {
            Skip = "FFmpeg not available (the desktop side of the comparison)";
        }
    }
}
