using System.Diagnostics;
using System.IO.Compression;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Media;
using VitaSharp.Mobile.Core.Audio;
using VitaSharp.Mobile.Core.Gpu;
using VitaSharp.Mobile.Core.Media;
using VitaSharp.Mobile.Core.Storage;

namespace VitaSharp.Tests;

/// <summary>The platform-neutral parts of the Android audio, MediaCodec and GPU driver backends
/// (src/VitaSharp.Mobile.Core): buffering policy, resampling and device recovery of the audio streams, decoder output
/// copies and fallback replay, driver packages, the driver screen and the game process marker.</summary>
public sealed class AndroidMediaDriverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-am-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (DirectoryNotFoundException) { }
    }

    // ---------------------------------------------------------------- audio

    /// <summary>A device stream that records what it is given; the "device" consumes frames when the test says so, or
    /// by itself when a blocking write finds the buffer full.</summary>
    private sealed class FakeSink(int rate, int burst, int channels = 2) : IPcmSink
    {
        public readonly List<short> Data = new();
        public int BufferFrames = int.MaxValue, Starts, Pauses, DisconnectOnWrite = -1, Writes;
        public bool Lost, Disposed;
        public int XRuns;
        public long Read;
        public int SampleRate => rate;
        public int Channels => channels;
        public int FramesPerBurst => burst;
        public string Description => "fake";
        public int SetBufferSize(int frames) => BufferFrames = frames;
        public long FramesWritten => Data.Count / channels;
        public long FramesRead => Read;
        public int XRunCount => XRuns;
        public bool Disconnected => Lost;
        public void Start() => Starts++;
        public void Pause() => Pauses++;
        public void Dispose() => Disposed = true;

        public int Write(ReadOnlySpan<short> interleaved, long timeoutNanos)
        {
            if (Writes++ == DisconnectOnWrite)
            {
                Lost = true;
                return PcmSinkStatus.Disconnected;
            }
            long room = BufferFrames - (FramesWritten - Read);
            if (room <= 0 && timeoutNanos > 0)
            {
                Read = FramesWritten;
                room = BufferFrames;
            } // the device drained
            int n = (int)Math.Min(room, interleaved.Length / channels);
            if (n <= 0)
            {
                return 0;
            }

            foreach (short s in interleaved[..(n * channels)])
            {
                Data.Add(s);
            }

            return n;
        }

        public void ConsumeAll() => Read = FramesWritten;
    }

    private static short[] Grain(int frames, short value = 1000) => Enumerable.Repeat(value, frames * 2).ToArray();

    [Theory]
    [InlineData(256, 48000, 48000, 192, 4, 2)]   // a 4 ms burst: one grain per period
    [InlineData(256, 48000, 48000, 960, 10, 5)]  // a 20 ms period: four grains
    [InlineData(256, 48000, 44100, 441, 6, 3)]   // 441 frames at 44.1 kHz = 480 guest samples: two grains
    [InlineData(1024, 48000, 48000, 96, 4, 2)]   // never fewer than four grains
    public void BufferPolicyMatchesTheDesktop(int grain, int guest, int device, int burst, int capacity, int start)
    {
        var p = PcmBufferPolicy.For(grain, guest, device, burst);
        Assert.Equal(capacity, p.Capacity);
        Assert.Equal(start, p.StartGrains);
    }

    [Fact]
    public void StreamPrimesThenStartsAndSizesTheDeviceBuffer()
    {
        var sink = new FakeSink(48000, 960);
        using var s = new BufferedPcmStream(_ => sink, 48000, 2, 256);
        Assert.Equal(10 * 256, sink.BufferFrames); // capacity in device frames
        for (int i = 0; i < 4; i++)
        {
            s.Submit(Grain(256));
        }

        Assert.Equal(0, sink.Starts);              // below the start threshold (5 grains)
        s.Submit(Grain(256));
        Assert.Equal(1, sink.Starts);
        s.Submit(Grain(256));
        Assert.Equal(1, sink.Starts);
        Assert.Equal(6 * 256, sink.FramesWritten);
        Assert.Equal(6 * 256, s.PendingSamples);
        Assert.Equal(0, s.Underruns);
    }

    [Fact]
    public void UnderrunIsCountedOnceAndRebuffersWithSilence()
    {
        var sink = new FakeSink(48000, 192); // capacity 4, start 2
        using var s = new BufferedPcmStream(_ => sink, 48000, 2, 256);
        s.Submit(Grain(256));
        s.Submit(Grain(256));
        Assert.Equal(1, sink.Starts);
        // the device drained everything and glitched several times before the next grain
        sink.ConsumeAll();
        sink.XRuns += 3;
        long before = sink.FramesWritten;
        s.Submit(Grain(256));
        Assert.Equal(1, s.Underruns);
        // one grain of silence (start threshold 2 - 1) before the late grain, as OpenAL re-buffers before replaying
        Assert.Equal(before + 2 * 256, sink.FramesWritten);
        Assert.All(sink.Data.Skip((int)before * 2).Take(256 * 2), v => Assert.Equal(0, v));
        Assert.All(sink.Data.Skip((int)(before + 256) * 2), v => Assert.Equal(1000, v));
        // no new glitch: no new underrun
        s.Submit(Grain(256));
        Assert.Equal(1, s.Underruns);
    }

    [Fact]
    public void DisconnectedDeviceIsReopenedOnTheNextGrain()
    {
        var sinks = new List<FakeSink>();
        IPcmSink Open(int ch)
        {
            var f = new FakeSink(sinks.Count == 0 ? 48000 : 44100, 192);
            sinks.Add(f);
            return f;
        }
        using var s = new BufferedPcmStream(Open, 48000, 2, 256);
        s.Submit(Grain(256));
        s.Submit(Grain(256));
        sinks[0].Lost = true;                        // the error callback flagged it
        s.Submit(Grain(256));
        Assert.Equal(2, sinks.Count);
        Assert.True(sinks[0].Disposed);
        Assert.Equal(1, s.Reopens);
        // the new device runs at 44.1 kHz: the grain was resampled into it
        Assert.InRange(sinks[1].FramesWritten, 233, 236);
        // a write that reports the disconnect itself: reopened and the grain goes to the new device
        sinks[1].DisconnectOnWrite = sinks[1].Writes;
        s.Submit(Grain(256));
        Assert.Equal(3, sinks.Count);
        Assert.Equal(2, s.Reopens);
        Assert.True(sinks[2].FramesWritten > 0);
    }

    [Fact]
    public void WithoutADeviceTheGuestIsPacedByTheClock()
    {
        using var s = new BufferedPcmStream(_ => null, 48000, 2, 480);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            s.Submit(Grain(480)); // 10 x 10 ms
        }

        Assert.True(sw.ElapsedMilliseconds >= 60, $"{sw.ElapsedMilliseconds} ms");
        Assert.Equal(0, s.PendingSamples);
        Assert.Contains("no output device", s.Describe());
    }

    [Fact]
    public void ResamplerKeepsTheRateAcrossCalls()
    {
        var r = new LinearResampler(2);
        r.Reset(48000, 44100);
        Assert.True(r.Active);
        short[] out_ = new short[r.MaxOutputFrames(256) * 2];
        long total = 0;
        short last = short.MinValue;
        for (int call = 0; call < 100; call++)
        {
            // a rising ramp across the calls: the output must rise too (no jumps back at the boundaries)
            short[] input = new short[256 * 2];
            for (int i = 0; i < 256; i++)
            {
                input[2 * i] = input[2 * i + 1] = (short)(call * 256 + i - 16000);
            }

            int n = r.Process(input, out_);
            for (int i = 0; i < n; i++)
            {
                Assert.True(out_[2 * i] >= last, $"call {call} frame {i}: {out_[2 * i]} < {last}");
                last = out_[2 * i];
                Assert.Equal(out_[2 * i], out_[2 * i + 1]);
            }
            total += n;
        }
        Assert.InRange(total, 25600L * 44100 / 48000 - 2, 25600L * 44100 / 48000 + 1);
    }

    // ---------------------------------------------------------------- MediaCodec output and replay

    [Fact]
    public void SemiPlanarRawBufferIsCroppedIntoPlanes()
    {
        // 8x4 picture, stride 12, slice height 6, crop at (2, 2) 4x2; luma = x + 16 * y, U = 100 + x + 10y, V = 200 + ...
        int stride = 12, slice = 6;
        byte[] buf = new byte[stride * slice * 3 / 2];
        for (int y = 0; y < slice; y++)
        {
            for (int x = 0; x < stride; x++)
            {
                buf[y * stride + x] = (byte)(x + 16 * y);
            }
        }

        for (int y = 0; y < slice / 2; y++)
        {
            for (int x = 0; x < stride / 2; x++)
            {
                buf[stride * slice + y * stride + 2 * x] = (byte)(100 + x + 10 * y);
                buf[stride * slice + y * stride + 2 * x + 1] = (byte)(200 + x + 10 * y);
            }
        }

        var p = new DecodedPicture();
        Assert.True(YuvImageCopy.CopyRaw(buf, YuvImageCopy.ColorFormatYuv420SemiPlanar, stride, slice, 2, 2, 4, 2, p));
        Assert.Equal((4, 2), (p.Width, p.Height));
        Assert.Equal(new byte[] { 2 + 32, 3 + 32, 4 + 32, 5 + 32, 2 + 48, 3 + 48, 4 + 48, 5 + 48 }, p.Y);
        Assert.Equal(new byte[] { 111, 112 }, p.U);
        Assert.Equal(new byte[] { 211, 212 }, p.V);
        Assert.False(YuvImageCopy.CopyRaw(buf, 0x7FA30C04, stride, slice, 0, 0, 4, 2, p));
    }

    [Fact]
    public void PlanarLayoutsAndImagePlanes()
    {
        // I420 4x2, stride 4: U then V after the luma
        byte[] i420 = [1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 20, 21];
        var p = new DecodedPicture();
        Assert.True(YuvImageCopy.CopyRaw(i420, YuvImageCopy.ColorFormatYuv420Planar, 4, 2, 0, 0, 4, 2, p));
        Assert.Equal(new byte[] { 10, 11 }, p.U);
        Assert.Equal(new byte[] { 20, 21 }, p.V);
        // an Image with interleaved chroma planes (pixel stride 2, V before U in memory: NV21) and row padding
        byte[] luma = [1, 2, 9, 9, 3, 4, 9, 9];
        byte[] vu = [50, 60, 9, 9];
        var q = new DecodedPicture { Y = new byte[4] };
        byte[] yRef = q.Y;
        YuvImageCopy.Copy(new YuvImageCopy.Plane(luma, 4, 1), new YuvImageCopy.Plane(vu.AsSpan(1), 4, 2), new YuvImageCopy.Plane(vu, 4, 2), 0, 0, 2, 2, q);
        Assert.Same(yRef, q.Y); // planes of the right size are reused
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, q.Y);
        Assert.Equal(new byte[] { 60 }, q.U);
        Assert.Equal(new byte[] { 50 }, q.V);
    }

    private static readonly byte[] s_idr = [0, 0, 0, 1, 0x67, 1, 0, 0, 1, 0x68, 2, 0, 0, 1, 0x65, 3];
    private static readonly byte[] s_pSlice = [0, 0, 1, 0x41, 4, 5];
    private static readonly byte[] s_parameterSetsOnly = [0, 0, 0, 1, 0x67, 1, 0, 0, 1, 0x68, 2];

    [Fact]
    public void AnnexBHelpers()
    {
        Assert.True(H264AnnexB.ContainsIdr(s_idr));
        Assert.False(H264AnnexB.ContainsIdr(s_pSlice));
        Assert.True(H264AnnexB.ContainsSlice(s_idr));
        Assert.True(H264AnnexB.ContainsSlice(s_pSlice));
        Assert.False(H264AnnexB.ContainsSlice(s_parameterSetsOnly));
        Assert.True(H264AnnexB.StartsWithStartCode(s_pSlice));
        // MP4 sample with 4-byte lengths, SPS/PPS header in front of a key frame
        byte[] avcc = [0, 0, 0, 2, 0x65, 7, 0, 0, 0, 1, 0x06];
        byte[] header = [0, 0, 0, 1, 0x67, 0, 0, 0, 1, 0x68];
        byte[] buf = Array.Empty<byte>();
        int n = H264AnnexB.BuildAccessUnit(avcc, header, ref buf);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0, 0, 0, 1, 0x68, 0, 0, 0, 1, 0x65, 7, 0, 0, 0, 1, 0x06 }, buf[..n]);
        // already Annex B (what MediaExtractor gives): copied as is
        n = H264AnnexB.BuildAccessUnit(s_pSlice, [], ref buf);
        Assert.Equal(s_pSlice, buf[..n]);
    }

    [Fact]
    public void ReplayStartsAtTheLastIdrAndSkipsShownPictures()
    {
        var r = new DecoderReplay();
        r.Add(s_idr, 0);
        r.Produced();
        r.Add(s_pSlice, 1);
        r.Produced();
        r.Add(s_idr, 2);
        r.Produced();
        r.Add(s_pSlice, 3);
        r.Add(s_pSlice, 4);
        r.Produced();
        (IReadOnlyList<(byte[] Data, long Pts)>? units, int skip) = r.TakeReplay();
        Assert.Equal(new long[] { 2, 3, 4 }, units.Select(u => u.Pts));
        Assert.Equal(2, skip);
        Assert.Equal(0, r.Count);
        r.Add(s_pSlice, 5);
        r.Clear();
        Assert.Empty(r.TakeReplay().Units);
    }

    // ---------------------------------------------------------------- MediaCodec output timing (sceAvcdecDecode contract)

    /// <summary>
    /// A MediaCodec stand-in on a test clock: it decodes the queued units one after the other, each taking
    /// <c>decodeUs(index)</c>, and gives a finished picture out only once <c>heldPictures</c> later units were queued
    /// (a decoder that holds pictures back for reordering). <see cref="Poll"/> is what MediaCodecVideoDecoder's drain
    /// does: take every finished picture out, waiting at most the timeout for the next one.
    /// </summary>
    private sealed class SimulatedMediaCodec(SynchronousOutput sync, Func<int, long> decodeUs, int heldPictures = 0)
    {
        private readonly List<long> _finishUs = new();
        private int _pictureCount;

        public long NowUs;

        public int PictureCount => _pictureCount;

        public void Queue(bool makesPicture)
        {
            if (makesPicture)
            {
                long decodeStart = Math.Max(NowUs, _finishUs.Count > 0 ? _finishUs[^1] : 0);
                _finishUs.Add(decodeStart + decodeUs(_finishUs.Count));
            }
            sync.UnitQueued(makesPicture);
        }

        public void Poll(long timeoutUs)
        {
            int releasable = _finishUs.Count - heldPictures;
            if (_pictureCount >= releasable)
            {
                NowUs += timeoutUs;
                return;
            }

            long next = _finishUs[_pictureCount];
            if (next > NowUs + timeoutUs)
            {
                NowUs += timeoutUs;
                return;
            }

            NowUs = Math.Max(NowUs, next);
            while (_pictureCount < releasable && _finishUs[_pictureCount] <= NowUs)
            {
                _pictureCount++;
                sync.PictureOut();
            }
        }

        /// <summary>One sceAvcdecDecode call: earlier pictures out, the unit in, then the wait for its picture.</summary>
        public void Send(bool makesPicture = true)
        {
            Poll(0);
            Queue(makesPicture);
            sync.Await(Poll, () => NowUs);
        }
    }

    [Fact]
    public void EachDecodeCallGetsThePictureOfItsUnit()
    {
        // c2.android.avc.decoder on the Redmi Note 14: ~13 ms per Vita-sized picture, a 30 fps movie. The Vita's
        // sceAvcdecDecode answers every call, the first one included, with the picture of that call's unit; a 5 ms grace
        // (and none before the first picture) left F1 2011's and Uncharted's players with old or missing pictures
        var sync = new SynchronousOutput();
        var codec = new SimulatedMediaCodec(sync, _ => 13_000);
        for (int unit = 1; unit <= 60; unit++)
        {
            long callStart = codec.NowUs;
            codec.Send();
            Assert.Equal(unit, codec.PictureCount);
            Assert.True(codec.NowUs - callStart <= 13_000, $"call {unit} waited {codec.NowUs - callStart} us");
            codec.NowUs += 33_333 - (codec.NowUs - callStart);
        }
        Assert.Equal(0, sync.MissedDeadlines);
        Assert.Equal(0, sync.HeldPictures);

        // a flush to loop the movie: the first call of the new pass waits for its picture as well
        sync.Restart();
        var looped = new SimulatedMediaCodec(sync, _ => 30_000);
        looped.Send();
        Assert.Equal(1, looped.PictureCount);
    }

    [Fact]
    public void PipelinedOutputWaitsOnlyForTheFirstPicture()
    {
        // MediaCodecVideoDecoder's default: the call that starts a stream answers with its picture (as the Vita does:
        // no empty first calls, the black and stale frames at every movie start on the phone), later calls take what is
        // done (the decoder works during the game's frame) with a 5 ms grace, so the game thread does not wait the
        // decoder's 13 ms on every call: one picture per call, one call behind
        var sync = new SynchronousOutput { PipelinedAfterStart = true };
        var codec = new SimulatedMediaCodec(sync, _ => 13_000);
        codec.Send();
        Assert.Equal(1, codec.PictureCount);
        codec.NowUs += 33_333;
        for (int unit = 2; unit <= 40; unit++)
        {
            long callStart = codec.NowUs;
            codec.Send();
            Assert.Equal(unit - 1, codec.PictureCount);
            Assert.True(codec.NowUs - callStart <= SynchronousOutput.PipelinedGraceUs, $"call {unit} waited {codec.NowUs - callStart} us");
            codec.NowUs += 33_333;
        }
        Assert.Equal(0, sync.MissedDeadlines);

        // a flush to loop the movie: the first call of the new pass waits for its picture again
        sync.Restart();
        var looped = new SimulatedMediaCodec(sync, _ => 30_000);
        looped.Send();
        Assert.Equal(1, looped.PictureCount);
    }

    [Fact]
    public void UnitsWithoutASliceDoNotWait()
    {
        var sync = new SynchronousOutput();
        var codec = new SimulatedMediaCodec(sync, _ => 13_000);
        codec.Send(makesPicture: false); // SPS/PPS alone
        Assert.Equal(0, codec.NowUs);
        Assert.Equal(0, sync.MissedDeadlines);
        codec.Send();
        Assert.Equal(1, codec.PictureCount);
    }

    [Fact]
    public void ADecoderThatHoldsAPictureIsLearnedOnceNotWaitedForEveryCall()
    {
        // a reordering decoder gives picture n out only after unit n+1: the first call misses its deadline once, then
        // every call answers with one picture (one unit behind, as FFmpeg does for such a stream) without stalling
        var sync = new SynchronousOutput();
        var codec = new SimulatedMediaCodec(sync, _ => 13_000, heldPictures: 1);
        codec.Send();
        Assert.Equal(0, codec.PictureCount);
        Assert.Equal(1, sync.MissedDeadlines);
        Assert.Equal(1, sync.HeldPictures);
        for (int unit = 2; unit <= 30; unit++)
        {
            long callStart = codec.NowUs;
            codec.Send();
            Assert.Equal(unit - 1, codec.PictureCount);
            Assert.True(codec.NowUs - callStart <= 13_000, $"call {unit} waited {codec.NowUs - callStart} us");
            codec.NowUs += 33_333;
        }
        Assert.Equal(1, sync.MissedDeadlines);
    }

    [Fact]
    public void ADecoderFillingItsDpbStallsOnlyOnce()
    {
        // libavc without a reorder limit in the SPS gives nothing out before its DPB (8 pictures for a Vita movie) is
        // full: one missed first-picture deadline, then no waits while it fills, then one picture per call
        var sync = new SynchronousOutput();
        var codec = new SimulatedMediaCodec(sync, _ => 13_000, heldPictures: 8);
        long stalled = 0;
        for (int unit = 1; unit <= 30; unit++)
        {
            long callStart = codec.NowUs;
            codec.Send();
            stalled += codec.NowUs - callStart;
            Assert.Equal(Math.Max(0, unit - 8), codec.PictureCount);
            codec.NowUs += 33_333;
        }
        Assert.Equal(1, sync.MissedDeadlines);
        Assert.True(stalled <= SynchronousOutput.FirstPictureDeadlineUs + 30 * 13_000, $"stalled {stalled} us");
        Assert.Equal(8, sync.HeldPictures);
    }

    [Fact]
    public void OneSlowPictureDoesNotLeaveTheDecoderBehind()
    {
        // picture 5 takes 60 ms (the CPU was busy): that call misses its deadline, the next calls catch up and wait
        // for their own picture again
        var sync = new SynchronousOutput();
        var codec = new SimulatedMediaCodec(sync, index => index == 4 ? 60_000 : 13_000);
        for (int unit = 1; unit <= 12; unit++)
        {
            codec.Send();
            codec.NowUs += 20_000;
        }
        Assert.Equal(1, sync.MissedDeadlines);
        Assert.Equal(0, sync.HeldPictures);
        Assert.Equal(12, codec.PictureCount);
    }

    // ---------------------------------------------------------------- GPU driver packages and screen

    private string Package(string name, bool nested)
    {
        Directory.CreateDirectory(_root);
        string zip = Path.Combine(_root, name + ".zip");
        using ZipArchive z = ZipFile.Open(zip, ZipArchiveMode.Create);
        string prefix = nested ? name + "/" : "";
        using (var w = new StreamWriter(z.CreateEntry(prefix + "meta.json").Open()))
        {
            w.Write($$"""{"schemaVersion":1,"name":"{{name}}","vendor":"Mesa","driverVersion":"25.1.0","minApi":28,"libraryName":"vulkan.ad07XX.so","author":"a","packageVersion":"1"}""");
        }

        using (Stream w = z.CreateEntry(prefix + "vulkan.ad07XX.so").Open())
        {
            w.Write([0x7F, (byte)'E', (byte)'L', (byte)'F']);
        }

        using (Stream w = z.CreateEntry(prefix + "notgsl.so").Open())
        {
            w.Write([1]);
        }

        z.CreateEntry("../evil.so");
        return zip;
    }

    [Fact]
    public void DriverPackagesFlatOrInOneFolderInstallIntoInternalStorage()
    {
        var data = new DataLayout(Path.Combine(_root, "ext"), Path.Combine(_root, "int"));
        var screen = new GpuDriverScreen(data, null);
        Assert.StartsWith(data.InternalRoot, screen.Store.Root);
        foreach ((string? name, bool nested) in new[] { ("Turnip", false), ("Turnip-nested", true) })
        {
            using FileStream f = File.OpenRead(Package(name, nested));
            InstalledGpuDriver d = screen.Install(f, 34);
            Assert.True(File.Exists(d.LibraryPath));
            Assert.True(File.Exists(Path.Combine(d.Directory, "notgsl.so")));
            Assert.True(File.Exists(Path.Combine(d.Directory, "meta.json")));
            Assert.False(File.Exists(Path.Combine(_root, "int", "evil.so")));
        }
        Assert.Equal(["", "Turnip", "Turnip-nested"], screen.Options().Select(o => o.Id));
        using (FileStream old = File.OpenRead(Package("Old", false)))
        {
            Assert.Throws<InvalidDataException>(() => screen.Install(old, 27)); // minApi 28
        }
    }

    [Fact]
    public void DriverScreenAsksForARestartWhenTheRunningGameUsesAnotherConfiguration()
    {
        var data = new DataLayout(Path.Combine(_root, "ext"), Path.Combine(_root, "int"));
        var screen = new GpuDriverScreen(data, null);
        using (FileStream f = File.OpenRead(Package("Turnip", false)))
        {
            screen.Install(f, 34);
        }

        string systemConfig = screen.Config;
        var running = new GameProcessInfo(1234, "PCSB00001", "Jogo", systemConfig, DateTime.UtcNow);
        Assert.False(screen.RestartNeeded(running));
        Assert.False(screen.RestartNeeded(null));
        Assert.True(screen.Select("Turnip"));
        Assert.True(screen.RestartNeeded(running));
        Assert.False(screen.Select("Turnip"));
        // the options are part of the configuration too
        GameProcessInfo withTurnip = running with
        {
            GpuConfig = screen.Config
        };
        Assert.False(screen.RestartNeeded(withTurnip));
        screen.SetOptions(turbo: true);
        Assert.True(screen.RestartNeeded(withTurnip));
        // the settings file (external) and the package (internal) are where the loader looks
        var reloaded = GpuDriverSettings.Load(data.GpuDriverSettingsFile);
        Assert.Equal("Turnip", reloaded.Driver);
        Assert.Equal(screen.Config, GpuDriverScreen.ConfigOf(reloaded, new CustomDriverStore(data.GpuDrivers)));
        // removing the selected driver goes back to the system one
        Assert.True(screen.Remove("Turnip"));
        Assert.Equal("", screen.Settings.Driver);
    }

    [Fact]
    public void GameProcessMarkerRoundTripsAndDropsStaleOnes()
    {
        string path = Path.Combine(_root, "int", "run", "game.json");
        var info = new GameProcessInfo(4321, "PCSE00002", "Outro", "|bcn=1|turbo=0|redirect=0", new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc));
        GameProcessMarker.Write(path, info);
        Assert.Equal(info, GameProcessMarker.Read(path, pid => pid == 4321));
        GameProcessMarker.Clear(path, 1);            // another process's marker stays
        Assert.NotNull(GameProcessMarker.Read(path, _ => true));
        Assert.Null(GameProcessMarker.Read(path, _ => false)); // its process is gone: deleted
        Assert.False(File.Exists(path));
        GameProcessMarker.Write(path, info);
        GameProcessMarker.Clear(path, 4321);
        Assert.False(File.Exists(path));
        File.WriteAllText(path, "{ not json");
        Assert.Null(GameProcessMarker.Read(path, _ => true));
    }
}
