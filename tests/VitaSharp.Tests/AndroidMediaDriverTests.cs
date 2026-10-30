using System.Diagnostics;
using System.IO.Compression;
using VitaSharp.Android.Audio;
using VitaSharp.Android.Gpu;
using VitaSharp.Android.Media;
using VitaSharp.Android.Storage;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Media;

namespace VitaSharp.Tests;

/// <summary>The platform-neutral parts of the Android audio, MediaCodec and GPU driver backends
/// (src/VitaSharp.Android.Common): buffering policy, resampling and device recovery of the audio streams, decoder output
/// copies and fallback replay, driver packages, the driver screen and the game process marker.</summary>
public sealed class AndroidMediaDriverTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-am-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { }
    }

    // ---------------------------------------------------------------- audio

    /// <summary>A device stream that records what it is given; the "device" consumes frames when the test says so, or
    /// by itself when a blocking write finds the buffer full.</summary>
    sealed class FakeSink(int rate, int burst, int channels = 2) : IPcmSink
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
            if (Writes++ == DisconnectOnWrite) { Lost = true; return PcmSinkStatus.Disconnected; }
            long room = BufferFrames - (FramesWritten - Read);
            if (room <= 0 && timeoutNanos > 0) { Read = FramesWritten; room = BufferFrames; } // the device drained
            int n = (int)Math.Min(room, interleaved.Length / channels);
            if (n <= 0) return 0;
            foreach (var s in interleaved[..(n * channels)]) Data.Add(s);
            return n;
        }

        public void ConsumeAll() => Read = FramesWritten;
    }

    static short[] Grain(int frames, short value = 1000) => Enumerable.Repeat(value, frames * 2).ToArray();

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
        for (int i = 0; i < 4; i++) s.Submit(Grain(256));
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
        IPcmSink Open(int ch) { var f = new FakeSink(sinks.Count == 0 ? 48000 : 44100, 192); sinks.Add(f); return f; }
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
        for (int i = 0; i < 10; i++) s.Submit(Grain(480)); // 10 x 10 ms
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
        var out_ = new short[r.MaxOutputFrames(256) * 2];
        long total = 0;
        short last = short.MinValue;
        for (int call = 0; call < 100; call++)
        {
            // a rising ramp across the calls: the output must rise too (no jumps back at the boundaries)
            var input = new short[256 * 2];
            for (int i = 0; i < 256; i++) input[2 * i] = input[2 * i + 1] = (short)(call * 256 + i - 16000);
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
        var buf = new byte[stride * slice * 3 / 2];
        for (int y = 0; y < slice; y++) for (int x = 0; x < stride; x++) buf[y * stride + x] = (byte)(x + 16 * y);
        for (int y = 0; y < slice / 2; y++)
            for (int x = 0; x < stride / 2; x++)
            {
                buf[stride * slice + y * stride + 2 * x] = (byte)(100 + x + 10 * y);
                buf[stride * slice + y * stride + 2 * x + 1] = (byte)(200 + x + 10 * y);
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
        var yRef = q.Y;
        YuvImageCopy.Copy(new YuvImageCopy.Plane(luma, 4, 1), new YuvImageCopy.Plane(vu.AsSpan(1), 4, 2), new YuvImageCopy.Plane(vu, 4, 2), 0, 0, 2, 2, q);
        Assert.Same(yRef, q.Y); // planes of the right size are reused
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, q.Y);
        Assert.Equal(new byte[] { 60 }, q.U);
        Assert.Equal(new byte[] { 50 }, q.V);
    }

    static readonly byte[] Idr = [0, 0, 0, 1, 0x67, 1, 0, 0, 1, 0x68, 2, 0, 0, 1, 0x65, 3];
    static readonly byte[] PSlice = [0, 0, 1, 0x41, 4, 5];

    [Fact]
    public void AnnexBHelpers()
    {
        Assert.True(H264AnnexB.ContainsIdr(Idr));
        Assert.False(H264AnnexB.ContainsIdr(PSlice));
        Assert.True(H264AnnexB.StartsWithStartCode(PSlice));
        // MP4 sample with 4-byte lengths, SPS/PPS header in front of a key frame
        byte[] avcc = [0, 0, 0, 2, 0x65, 7, 0, 0, 0, 1, 0x06];
        byte[] header = [0, 0, 0, 1, 0x67, 0, 0, 0, 1, 0x68];
        var buf = Array.Empty<byte>();
        int n = H264AnnexB.BuildAccessUnit(avcc, header, ref buf);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0, 0, 0, 1, 0x68, 0, 0, 0, 1, 0x65, 7, 0, 0, 0, 1, 0x06 }, buf[..n]);
        // already Annex B (what MediaExtractor gives): copied as is
        n = H264AnnexB.BuildAccessUnit(PSlice, [], ref buf);
        Assert.Equal(PSlice, buf[..n]);
    }

    [Fact]
    public void ReplayStartsAtTheLastIdrAndSkipsShownPictures()
    {
        var r = new DecoderReplay();
        r.Add(Idr, 0); r.Produced();
        r.Add(PSlice, 1); r.Produced();
        r.Add(Idr, 2); r.Produced();
        r.Add(PSlice, 3);
        r.Add(PSlice, 4); r.Produced();
        var (units, skip) = r.TakeReplay();
        Assert.Equal(new long[] { 2, 3, 4 }, units.Select(u => u.Pts));
        Assert.Equal(2, skip);
        Assert.Equal(0, r.Count);
        r.Add(PSlice, 5);
        r.Clear();
        Assert.Empty(r.TakeReplay().Units);
    }

    // ---------------------------------------------------------------- GPU driver packages and screen

    string Package(string name, bool nested)
    {
        Directory.CreateDirectory(_root);
        string zip = Path.Combine(_root, name + ".zip");
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        string prefix = nested ? name + "/" : "";
        using (var w = new StreamWriter(z.CreateEntry(prefix + "meta.json").Open()))
            w.Write($$"""{"schemaVersion":1,"name":"{{name}}","vendor":"Mesa","driverVersion":"25.1.0","minApi":28,"libraryName":"vulkan.ad07XX.so","author":"a","packageVersion":"1"}""");
        using (var w = z.CreateEntry(prefix + "vulkan.ad07XX.so").Open()) w.Write([0x7F, (byte)'E', (byte)'L', (byte)'F']);
        using (var w = z.CreateEntry(prefix + "notgsl.so").Open()) w.Write([1]);
        z.CreateEntry("../evil.so");
        return zip;
    }

    [Fact]
    public void DriverPackagesFlatOrInOneFolderInstallIntoInternalStorage()
    {
        var data = new DataLayout(Path.Combine(_root, "ext"), Path.Combine(_root, "int"));
        var screen = new GpuDriverScreen(data, null);
        Assert.StartsWith(data.InternalRoot, screen.Store.Root);
        foreach (var (name, nested) in new[] { ("Turnip", false), ("Turnip-nested", true) })
        {
            using var f = File.OpenRead(Package(name, nested));
            var d = screen.Install(f, 34);
            Assert.True(File.Exists(d.LibraryPath));
            Assert.True(File.Exists(Path.Combine(d.Directory, "notgsl.so")));
            Assert.True(File.Exists(Path.Combine(d.Directory, "meta.json")));
            Assert.False(File.Exists(Path.Combine(_root, "int", "evil.so")));
        }
        Assert.Equal(["", "Turnip", "Turnip-nested"], screen.Options().Select(o => o.Id));
        using (var old = File.OpenRead(Package("Old", false)))
            Assert.Throws<InvalidDataException>(() => screen.Install(old, 27)); // minApi 28
    }

    [Fact]
    public void DriverScreenAsksForARestartWhenTheRunningGameUsesAnotherConfiguration()
    {
        var data = new DataLayout(Path.Combine(_root, "ext"), Path.Combine(_root, "int"));
        var screen = new GpuDriverScreen(data, null);
        using (var f = File.OpenRead(Package("Turnip", false))) screen.Install(f, 34);
        string systemConfig = screen.Config;
        var running = new GameProcessInfo(1234, "PCSB00001", "Jogo", systemConfig, DateTime.UtcNow);
        Assert.False(screen.RestartNeeded(running));
        Assert.False(screen.RestartNeeded(null));
        Assert.True(screen.Select("Turnip"));
        Assert.True(screen.RestartNeeded(running));
        Assert.False(screen.Select("Turnip"));
        // the options are part of the configuration too
        var withTurnip = running with { GpuConfig = screen.Config };
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
