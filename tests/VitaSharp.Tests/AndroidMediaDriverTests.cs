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
        /// <summary>A paused device: a blocking write on a full buffer finds no room (nothing drains).</summary>
        public bool Frozen;
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
            if (room <= 0 && timeoutNanos > 0 && !Frozen)
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

    /// <summary>
    /// A load state pauses the emulation (the stream pauses, the device stops draining) and then stops the guest
    /// threads: the audio thread blocked in Submit on the full buffer must return when its thread is asked to stop,
    /// or the load fails with "thread 'AudioOutput' did not stop" (F1 2011 on Android, during its opening films).
    /// A pause alone keeps it waiting, and the resume lets it write the grain.
    /// </summary>
    [Fact]
    public async Task SubmitWaitingWhilePausedReturnsWhenItsThreadIsStopped()
    {
        var hangGuard = TimeSpan.FromMinutes(2);
        var sink = new FakeSink(48000, 192) { Frozen = true }; // capacity 4 grains, start 2
        bool stop = false;
        using var s = new BufferedPcmStream(_ => sink, 48000, 2, 256, stopRequested: () => Volatile.Read(ref stop));
        for (int i = 0; i < 4; i++)
        {
            s.Submit(Grain(256));
        }

        Assert.Equal(4 * 256, sink.FramesWritten); // the device buffer is full
        s.Pause();
        Task blocked = Task.Factory.StartNew(() => s.Submit(Grain(256)), TaskCreationOptions.LongRunning);
        await Task.WhenAny(blocked, Task.Delay(300));
        Assert.False(blocked.IsCompleted); // paused: waits for the resume (only a false pass is possible here)
        Volatile.Write(ref stop, true);
        await blocked.WaitAsync(hangGuard);
        Assert.Equal(4 * 256, sink.FramesWritten); // the grain was dropped

        // a pause without a stop: the resume (the device drains again) lets the waiting grain through
        Volatile.Write(ref stop, false);
        Task resumed = Task.Factory.StartNew(() => s.Submit(Grain(256)), TaskCreationOptions.LongRunning);
        await Task.WhenAny(resumed, Task.Delay(300));
        Assert.False(resumed.IsCompleted);
        sink.Frozen = false;
        s.Resume();
        await resumed.WaitAsync(hangGuard);
        Assert.Equal(5 * 256, sink.FramesWritten);
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

    /// <summary>A pacing clock the test moves: a wait jumps the clock by the time asked (unless <see cref="Stopped"/>,
    /// a paused emulation), so the guest's pace is measured in this clock's time, never in the host's.</summary>
    private sealed class FakePacingClock : IPcmPacingClock
    {
        public double Now;
        public double Waited;
        public int Waits;
        public bool Turbo;
        /// <summary>The clock does not move (the emulation is paused): a wait passes no time.</summary>
        public bool Stopped;

        public double Seconds => Now;

        public bool SkipPacing => Turbo;

        public void Wait(object gate, double seconds)
        {
            Assert.True(Monitor.IsEntered(gate), "the wait releases the stream's lock, so it must hold it");
            Waits++;
            if (!Stopped)
            {
                Now += seconds;
                Waited += seconds;
            }
        }
    }

    [Fact]
    public void WithoutADeviceTheGuestIsPacedByTheClock()
    {
        var clock = new FakePacingClock();
        using var s = new BufferedPcmStream(_ => null, 48000, 2, 480, pacingClock: clock);
        // time between making the stream and its first grain is no credit: every grain still takes its 10 ms
        clock.Now += 0.05;
        for (int i = 0; i < 10; i++)
        {
            double before = clock.Now;
            s.Submit(Grain(480)); // 10 ms of audio
            Assert.InRange(clock.Now - before, 0.0099, 0.0101);
        }

        Assert.InRange(clock.Waited, 0.0999, 0.1001);
        Assert.Equal(0, s.PendingSamples);
        Assert.Contains("no output device", s.Describe());
    }

    [Fact]
    public void WithoutADeviceALateGuestGetsNoBurstAndTurboIsNotPaced()
    {
        var clock = new FakePacingClock();
        using var s = new BufferedPcmStream(_ => null, 48000, 2, 480, pacingClock: clock);
        s.Submit(Grain(480));
        // the guest came back a second late (a loading screen): it is not let through for that second, the schedule
        // restarts and each grain takes its 10 ms again
        clock.Now += 1.0;
        double waitedBefore = clock.Waited;
        for (int i = 0; i < 5; i++)
        {
            s.Submit(Grain(480));
        }

        Assert.InRange(clock.Waited - waitedBefore, 0.0499, 0.0501);
        // a guest slightly late (under the 0.1 s slack) catches up without waiting for that grain
        clock.Now += 0.02;
        waitedBefore = clock.Waited;
        s.Submit(Grain(480));
        s.Submit(Grain(480));
        s.Submit(Grain(480));
        Assert.InRange(clock.Waited - waitedBefore, 0.0099, 0.0101);
        // turbo: no pacing at all
        clock.Turbo = true;
        waitedBefore = clock.Waited;
        int waits = clock.Waits;
        for (int i = 0; i < 10; i++)
        {
            s.Submit(Grain(480));
        }

        Assert.Equal(waitedBefore, clock.Waited);
        Assert.Equal(waits, clock.Waits);
    }

    [Fact]
    public void WithoutADeviceAStoppedClockHoldsTheGrainUntilTheThreadIsStopped()
    {
        // the emulation is paused (the guest clock stops): the grain waits; a stop of the submitting thread (a save
        // state load) lets it go instead of keeping the audio thread there
        var clock = new FakePacingClock { Stopped = true };
        int asked = 0;
        using var s = new BufferedPcmStream(_ => null, 48000, 2, 480, stopRequested: () => ++asked > 3, pacingClock: clock);
        s.Submit(Grain(480));
        Assert.Equal(3, clock.Waits);
        Assert.Equal(0, clock.Waited);
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

    // ---------------------------------------------------------------- FFmpeg missing

    [Fact]
    public void FFmpegMissingWarningNamesOnlyWhatIsReallyOff()
    {
        // desktop without FFmpeg: no replacement, everything that uses it is off
        string desktop = FFmpegLoader.MissingMessage(false, false, false, false, false);
        Assert.Equal("FFmpeg libraries not found: videos (SceAvPlayer), H.264 decoding (SceAvcdec), MP4 reading (SceMp4), AAC decoding (SceAudiodec), "
            + "MP3 decoding (SceAudiodec), G.729 decoding (SceG729), H.264 encoding (camera videos) disabled", desktop);

        // Android installs MediaExtractor/MediaCodec replacements: movies and AAC are not off (Uncharted's log said
        // they were while its movie played through MediaCodec)
        string android = FFmpegLoader.MissingMessage(true, true, true, true, true);
        Assert.Equal("FFmpeg libraries not found: G.729 decoding (SceG729), H.264 encoding (camera videos) disabled; videos (SceAvPlayer), "
            + "H.264 decoding (SceAvcdec), MP4 reading (SceMp4), AAC decoding (SceAudiodec), MP3 decoding (SceAudiodec) through this platform's own decoders", android);
        Assert.DoesNotContain("SceAvPlayer)", android.Split(';')[0]);
    }

    // ---------------------------------------------------------------- MediaCodec input stalls

    [Fact]
    public void InputStallReopensTheDecoderThenTriesTheNextOne()
    {
        var stall = new InputStallRecovery();
        Assert.Equal(InputStallRecovery.Action.ReopenSameDecoder, stall.OnInputStall());
        // the reopened decoder stalled before any picture: the next decoder of the list
        Assert.Equal(InputStallRecovery.Action.NextDecoder, stall.OnInputStall());
        Assert.Equal(InputStallRecovery.Action.ReopenSameDecoder, stall.OnInputStall());
        stall.OnPicture();
        // it worked for a while: a later stall reopens it again
        Assert.Equal(InputStallRecovery.Action.ReopenSameDecoder, stall.OnInputStall());
        Assert.Equal(4, stall.Recoveries);
    }

    /// <summary>
    /// A MediaCodec stand-in with a fixed number of input buffers: a queued unit holds one until the decoder gives its
    /// picture out, and a stalled decoder never does (Android's software H.264 decoder in Killzone: Mercenary's movies,
    /// 13/11/2026: no input buffer came back and every later unit was dropped).
    /// </summary>
    private sealed class StallingCodec(int inputBuffers, int stallAfterUnits)
    {
        private readonly Queue<long> _held = new();
        private int _queued;

        public bool TryQueue(long pts)
        {
            if (_held.Count >= inputBuffers)
            {
                return false;
            }

            _held.Enqueue(pts);
            _queued++;
            return true;
        }

        /// <summary>The pictures the decoder gives out (none once it stalled).</summary>
        public IEnumerable<long> Drain()
        {
            while (_held.Count > 0 && _queued <= stallAfterUnits)
            {
                yield return _held.Dequeue();
            }
        }
    }

    /// <summary>MediaCodecVideoDecoder's input path (SendCore → Queue → RecoverInputStall → FallBack's replay) over
    /// <see cref="StallingCodec"/>: <paramref name="recover"/> false is the path before the recovery (the unit dropped,
    /// the same decoder kept).</summary>
    private static List<long> DecodeWithStalls(int units, Func<StallingCodec> openDecoder, bool recover)
    {
        var replay = new DecoderReplay();
        var stall = new InputStallRecovery();
        var pictures = new List<long>();
        StallingCodec codec = openDecoder();
        int skip = 0;

        void Send(byte[] unit, long pts)
        {
            replay.Add(unit, pts);
            bool queued = false;
            for (int attempt = 0; attempt < 20 && !queued; attempt++)
            {
                queued = codec.TryQueue(pts);
                Drain();
            }

            if (!queued && recover)
            {
                stall.OnInputStall();
                codec = openDecoder();
                (IReadOnlyList<(byte[] Data, long Pts)> again, int produced) = replay.TakeReplay();
                skip = produced;
                foreach ((byte[] data, long unitPts) in again)
                {
                    Send(data, unitPts);
                }
            }
        }

        void Drain()
        {
            foreach (long pts in codec.Drain())
            {
                replay.Produced();
                if (skip > 0)
                {
                    skip--;
                    continue;
                }

                pictures.Add(pts);
                stall.OnPicture();
            }
        }

        for (int i = 0; i < units; i++)
        {
            Send(i % 30 == 0 ? s_idr : s_pSlice, i);
        }

        Drain();
        return pictures;
    }

    [Fact]
    public void StalledDecoderIsReplacedAndNoPictureIsLost()
    {
        // the first decoder stalls after 100 units, its replacement never does: 300 units, 300 pictures in order
        int opened = 0;
        List<long> pictures = DecodeWithStalls(300, () => new StallingCodec(4, opened++ == 0 ? 100 : int.MaxValue), recover: true);
        Assert.Equal(Enumerable.Range(0, 300).Select(i => (long)i), pictures);
        Assert.Equal(2, opened);

        // without the recovery the movie freezes at the stall: the game polls for a picture that never comes
        opened = 0;
        List<long> frozen = DecodeWithStalls(300, () => new StallingCodec(4, opened++ == 0 ? 100 : int.MaxValue), recover: false);
        Assert.True(frozen.Count < 110, $"{frozen.Count} pictures");
    }

    /// <summary>
    /// A decoder that stalls for good once its input ring holds <c>stallAt</c> units (the c2.android.avc.decoder stalls
    /// seen in Killzone: Mercenary's movies, each with 14 units in flight), and otherwise gives one picture per poll
    /// (it decodes more slowly than the movie player feeds it).
    /// </summary>
    private sealed class RingStallCodec(int stallAt)
    {
        private readonly Queue<long> _ring = new();
        public bool Stalled { get; private set; }
        public List<long> Pictures { get; } = new();
        public long InFlight => _ring.Count;

        public void Queue(long pts)
        {
            _ring.Enqueue(pts);
            if (_ring.Count >= stallAt)
            {
                Stalled = true;
            }
        }

        public void Poll(long timeoutUs)
        {
            if (!Stalled && _ring.Count > 0)
            {
                Pictures.Add(_ring.Dequeue());
            }
        }
    }

    private static RingStallCodec FeedRingStallCodec(int maxInFlight)
    {
        var codec = new RingStallCodec(stallAt: 14);
        long clockUs = 0;
        for (int unit = 0; unit < 200; unit++)
        {
            Assert.True(InputPacing.WaitForRoom(maxInFlight, () => codec.InFlight, timeoutUs =>
            {
                clockUs += timeoutUs;
                codec.Poll(timeoutUs);
            }, () => clockUs));
            codec.Queue(unit);
            if (unit % 2 == 1)
            {
                codec.Poll(0); // the decoder keeps up with every other unit only
            }
        }

        while (codec.InFlight > 0 && !codec.Stalled)
        {
            codec.Poll(0);
        }

        return codec;
    }

    [Fact]
    public void InputPacingKeepsTheDecoderRingFromFillingUp()
    {
        // fed as fast as the player's queue allows, the ring fills to 14 and the decoder stalls
        RingStallCodec unpaced = FeedRingStallCodec(maxInFlight: 0);
        Assert.True(unpaced.Stalled);
        Assert.True(unpaced.Pictures.Count < 200);

        // at most 8 in flight: the next unit waits for a picture, the decoder never stalls, every picture in order
        RingStallCodec paced = FeedRingStallCodec(maxInFlight: 8);
        Assert.False(paced.Stalled);
        Assert.Equal(Enumerable.Range(0, 200).Select(i => (long)i), paced.Pictures);
    }

    [Fact]
    public void InputPacingGivesUpAfterItsLongestWait()
    {
        // a decoder that gives nothing back: the wait ends after MaxWaitUs and the unit is queued anyway
        long clockUs = 0;
        bool roomFound = InputPacing.WaitForRoom(4, () => 4, timeoutUs => clockUs += timeoutUs, () => clockUs);
        Assert.False(roomFound);
        Assert.InRange(clockUs, InputPacing.MaxWaitUs, InputPacing.MaxWaitUs + 10_000);
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
