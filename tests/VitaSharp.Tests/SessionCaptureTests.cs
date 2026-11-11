using VitaSharp.Core.Capture;
using VitaSharp.Core.Host;

namespace VitaSharp.Tests;

/// <summary>"Gravar tudo" (Core/Capture): the picture codecs, the frames file, the input log and its playback, the
/// audio mix and the analyzer's findings on a synthetic capture.</summary>
public sealed class SessionCaptureTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-capture-" + Guid.NewGuid().ToString("N"));

    public SessionCaptureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    const int W = 120, H = 68;

    /// <summary>A moving gradient (frame <paramref name="t"/>), optionally with a solid block.</summary>
    static byte[] Picture(int t, (byte R, byte G, byte B)? fill = null)
    {
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int o = (y * W + x) * 4;
                if (fill is { } f)
                {
                    px[o] = f.R;
                    px[o + 1] = f.G;
                    px[o + 2] = f.B;
                }
                else
                {
                    px[o] = (byte)(x * 2 + t);
                    px[o + 1] = (byte)(y * 3);
                    px[o + 2] = (byte)(128 + t * 3);
                }
                px[o + 3] = 255;
            }
        }

        return px;
    }

    [Fact]
    public void Qoi_round_trips()
    {
        var rnd = new Random(7);
        var noise = new byte[W * H * 4];
        rnd.NextBytes(noise);
        for (int i = 3; i < noise.Length; i += 4)
        {
            noise[i] = 255;
        }

        foreach (var px in new[] { Picture(3), noise, Picture(0, (10, 20, 30)) })
        {
            var enc = Qoi.Encode(px, W, H);
            var (back, w, h) = Qoi.Decode(enc);
            Assert.Equal((W, H), (w, h));
            Assert.Equal(px, back);
        }
        // a solid picture is mostly runs
        Assert.True(Qoi.Encode(Picture(0, (1, 2, 3)), W, H).Length < 200);
    }

    [Fact]
    public void Frames_file_round_trips_key_and_delta_pictures()
    {
        string path = Path.Combine(_dir, "frames.vsfr");
        var pictures = new List<byte[]?>();
        using (var fs = File.Create(path))
        {
            FramesFile.WriteHeader(fs);
            var enc = new FramesFile.Encoder();
            int encoded = 0;
            for (int i = 0; i < 70; i++)
            {
                var info = new PresentInfo { Present = i + 1, Flip = i, PresentNs = i * 16_666_667L, Hitch = i == 5 ? "fileIo" : "" };
                if (i == 40)
                {
                    // no picture (the encoder fell behind): the chain goes on from the picture before
                    FramesFile.WriteRecord(fs, info, FramesFile.FlagNoPicture, []);
                    pictures.Add(null);
                    continue;
                }
                var px = Picture(i);
                var (data, flags) = enc.Encode(px, W, H);
                Assert.Equal(encoded++ % FramesFile.KeyInterval == 0, (flags & FramesFile.FlagKey) != 0);
                FramesFile.WriteRecord(fs, info, flags, data);
                pictures.Add(px);
            }
        }
        var all = FramesFile.ReadAll(path).ToList();
        Assert.Equal(70, all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            Assert.Equal(i + 1, all[i].Frame.Info.Present);
            if (pictures[i] is { } expected)
            {
                Assert.Equal(expected, all[i].Picture!.Value.Rgba);
            }
            else
            {
                Assert.Null(all[i].Picture);
            }
        }
        Assert.Equal("fileIo", all[5].Frame.Info.Hitch);
        var index = FramesFile.ReadIndex(path);
        Assert.Equal(pictures[55], FramesFile.ReadPicture(path, index, 55)!.Value.Rgba);
        // deltas of a slowly moving picture are much smaller than the key pictures
        Assert.True(index[2].QoiLength * 3 < index[0].QoiLength, $"delta {index[2].QoiLength} vs key {index[0].QoiLength}");
    }

    [Fact]
    public void Frames_file_cut_short_reads_complete_records()
    {
        string path = Path.Combine(_dir, "cut.vsfr");
        using (var fs = File.Create(path))
        {
            FramesFile.WriteHeader(fs);
            var enc = new FramesFile.Encoder();
            for (int i = 0; i < 5; i++)
            {
                var (d, f) = enc.Encode(Picture(i), W, H);
                FramesFile.WriteRecord(fs, new PresentInfo { Present = i }, f, d);
            }
        }
        using (var fs = new FileStream(path, FileMode.Open))
        {
            fs.SetLength(fs.Length - 10);
        }

        Assert.Equal(4, FramesFile.ReadIndex(path).Count);
    }

    [Fact]
    public void Input_events_round_trip_and_play_back()
    {
        var pad = new ControllerState { Buttons = VitaButtons.Cross | VitaButtons.Up, Lx = 10, Ly = 250, Rx = 128, Ry = 128 };
        var events = new[]
        {
            new InputEvent("pad", 100, 1_000_000, 10, InputRecorder.FormatPad(0, pad)),
            new InputEvent("touch", 200, 1_500_000, 12, "front 3:0.2500:0.7500"),
            new InputEvent("rng", 300, 900_000, 9, "0A0B0C0D"),
            new InputEvent("rng", 400, 2_000_000, 20, "01020304"),
            new InputEvent("mark", 500, 2_100_000, 21, "stop hotkey"),
        };
        var parsed = events.Select(e => InputEvent.Parse(e.ToString())).ToList();
        Assert.Equal(events, parsed.Select(p => p!.Value));
        Assert.Null(InputEvent.Parse(InputEvent.Header));

        var pb = new InputPlayback(parsed.Select(p => p!.Value));
        Assert.Equal(4, pb.Events); // the mark is not played back
        Assert.Equal(2_000_000, pb.LastUs);
        // inactive (the state is not loaded yet): idle pad, no recorded numbers
        Assert.Equal(ControllerState.Neutral.Buttons, pb.GetController(0).Buttons);
        Span<byte> r = stackalloc byte[4];
        Assert.False(pb.Replay(r));
        pb.Activate(1_000_000);
        // only the results recorded after the state's guest time are handed back, in order
        Assert.True(pb.Replay(r));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, r.ToArray());
        Assert.False(pb.Replay(r));
    }

    [Fact]
    public void Input_recorder_logs_changes_only()
    {
        var log = new List<InputEvent>();
        var src = new FakeInput();
        var rec = new InputRecorder(src, null, log.Add);
        rec.GetController(0);
        rec.GetController(0);
        src.State.Buttons = VitaButtons.Circle;
        rec.GetController(0);
        Span<TouchPoint> t = stackalloc TouchPoint[8];
        rec.GetTouches(false, t);
        src.Touches.Add(new TouchPoint(1, 0.5f, 0.5f));
        Assert.Equal(1, rec.GetTouches(false, t));
        rec.GetTouches(false, t);
        Assert.Equal(["pad", "pad", "touch"], log.Select(e => e.Kind));
        Assert.EndsWith("00002000 128 128 128 128", log[1].Data);
    }

    sealed class FakeInput : IInputBackend
    {
        public ControllerState State = ControllerState.Neutral;
        public readonly List<TouchPoint> Touches = new();
        public ControllerState GetController(int port) => State;
        public IReadOnlyList<TouchPoint> GetTouches(bool back) => back ? [] : Touches;
    }

    [Fact]
    public void Audio_mix_places_ports_at_their_start_and_checks_find_gaps()
    {
        string a = Path.Combine(_dir, "a.wav"), b = Path.Combine(_dir, "b.wav"), mix = Path.Combine(_dir, "mix.wav");
        var tone = new short[48000 * 2];
        for (int i = 0; i < 48000; i++)
        {
            tone[2 * i] = tone[2 * i + 1] = (short)(8000 * Math.Sin(i * 0.05));
        }
        // a 200 ms hole in the middle
        Array.Clear(tone, 24000 * 2, 9600 * 2);
        CaptureAudio.WriteWav(a, tone, 48000);
        CaptureAudio.WriteWav(b, new short[24000 * 2].Select((_, i) => (short)1000).ToArray(), 24000);
        CaptureAudio.Mix([(a, 0), (b, 500)], mix);
        var (m, rate) = CaptureAudio.ReadWav(mix);
        Assert.Equal(48000, rate);
        Assert.Equal(48000 + 24000, m.Length / 2); // b (1 s) starts at 0.5 s
        Assert.Equal(1000, m[2 * 60000]); // only b there
        var check = CaptureAudio.Analyze(a);
        Assert.Equal(1, check.Gaps);
        Assert.InRange(check.LongestGapMs, 150, 250);
    }

    /// <summary>A synthetic capture with one problem of each kind: the analyzer finds each one.</summary>
    [Fact]
    public void Analyzer_finds_black_frames_flashes_order_and_stalls()
    {
        string dir = Path.Combine(_dir, "cap");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "session.txt"), "title-id: TEST00000\ntitle: Test\nstart-ns: 0\n");
        File.WriteAllLines(Path.Combine(dir, "input.tsv"), [InputEvent.Header, new InputEvent("pad", 1_000_000_000, 1, 59, "0 00004000 128 128 128 128").ToString()]);
        using (var fs = File.Create(Path.Combine(dir, "frames.vsfr")))
        {
            FramesFile.WriteHeader(fs);
            var enc = new FramesFile.Encoder();
            long ns = 0;
            long flip = 0;
            for (int i = 0; i < 200; i++)
            {
                byte[] px;
                if (i is 60 or 61)
                {
                    px = Picture(0, (0, 0, 0));                  // black flash (2 frames)
                }
                else if (i == 100)
                {
                    px = Picture(i, (250, 0, 250));              // one-frame flash
                }
                else if (i is >= 150 and < 190)
                {
                    px = Picture(0, (0, 0, 0));     // black screen (40 frames... see the threshold)
                }
                else
                {
                    px = Picture(i / 8); // a picture that changes every 8 frames
                }

                long f = i == 121 ? flip - 2 : flip;                            // a flip presented out of order
                ns += i == 80 ? 400_000_000 : 16_666_667;                       // a 400 ms stall
                var (data, flags) = enc.Encode(px, W, H);
                FramesFile.WriteRecord(fs, new PresentInfo { Present = i + 1, Flip = f, PresentNs = ns, IntervalMs = 16.7f, Hitch = i == 80 ? "shaderCompile" : "", HitchMs = i == 80 ? 380 : 0 }, flags, data);
                flip++;
            }
        }
        string report = CaptureAnalyzer.Analyze(dir, quiet: true);
        Assert.Contains("[black-flash] present 61", report);
        Assert.Contains("[flash] present 101", report);
        Assert.Contains("[stall] present 81", report);
        Assert.Contains("shaderCompile 380.0 ms", report);
        Assert.Contains("[out-of-order] present 122", report);
        Assert.Contains("[black-transition] present 151", report); // 40 frames at 60 fps: 0.67 s, between pictures
        Assert.Contains("input: ", report);
        Assert.True(File.Exists(Path.Combine(dir, "findings", "flash_p101.png")));
        Assert.True(File.Exists(Path.Combine(dir, "report.txt")));
    }

    /// <summary>The report says where the player is (context.txt and the ctx events of input.tsv), draws a contact
    /// sheet, lists the input before the recording, names each port's owner and says whether music was playing.</summary>
    [Fact]
    public void Analyzer_reports_the_context_contact_sheet_input_before_and_audio_owners()
    {
        string dir = SimpleCapture("context", 120);
        File.WriteAllText(Path.Combine(dir, "session.txt"), "title-id: shell\ntitle: \nstart-ns: 0\nrecorded: 2.0 s, 120 guest frames (60.0 fps)\n");
        File.WriteAllLines(Path.Combine(dir, "timeline.tsv"), Enumerable.Range(0, 120).Select(i => $"{i + 1}\t{i}").Prepend("# present\tflip"));
        File.WriteAllLines(Path.Combine(dir, "input.tsv"),
        [
            InputEvent.Header,
            new InputEvent("mark", 0, 1, 0, "start hotkey").ToString(),
            new InputEvent("ctx", 0, 1, 0, "shell: LiveArea page of PCSA00017 'LittleBigPlanet'").ToString(),
            new InputEvent("ctx", 1_500_000_000, 2, 90, "game PCSA00017 'LittleBigPlanet' in front").ToString(),
        ]);
        File.WriteAllLines(Path.Combine(dir, "input_before.tsv"),
            [InputEvent.Header, new InputEvent("pad", -3_000_000_000, 0, -180, "0 00004000 128 128 128 128").ToString()]);
        File.WriteAllText(Path.Combine(dir, "context.txt"),
            "[start of the recording]\nwhere: shell: LiveArea page of PCSA00017 'LittleBigPlanet'\nprocess: shell shell pid 0x40010003: in front\n" +
            "audio: port 1 MAIN (port1_MAIN.wav): system sounds (the shell's UI sounds); opened by shell thread 'SceWaveMain'; 48000 Hz grain 1024; idle (last output 3.0 s ago)\n" +
            "audio: port 2 BGM (port2_BGM.wav): background music (the shell's BGM); opened by shell thread 'SceShellMusicCoreAudioOutput'; 48000 Hz grain 2048; outputting now\n" +
            "\n[stop of the recording]\nwhere: game PCSA00017 'LittleBigPlanet' in front\n" +
            "audio: BGM port held by shell (the shell's music player, SceShellMusicCore: the home screen / LiveArea music)\n");
        Directory.CreateDirectory(Path.Combine(dir, "audio"));
        var music = new short[48000 * 2 * 2];
        for (int i = 0; i < music.Length / 2; i++)
        {
            music[2 * i] = music[2 * i + 1] = (short)(6000 * Math.Sin(i * 0.03));
        }

        CaptureAudio.WriteWav(Path.Combine(dir, "audio", "02_port2_BGM_48000.wav"), music, 48000);
        var click = new short[48000 / 5 * 2];
        for (int i = 0; i < click.Length / 2; i++)
        {
            click[2 * i] = click[2 * i + 1] = (short)(6000 * Math.Sin(i * 0.2));
        }

        CaptureAudio.WriteWav(Path.Combine(dir, "audio", "01_port1_MAIN_48000.wav"), click, 48000);

        string report = CaptureAnalyzer.Analyze(dir, quiet: true);
        Assert.Contains("Where:", report);
        Assert.Contains("+0.0 s: shell: LiveArea page of PCSA00017 'LittleBigPlanet'", report);
        Assert.Contains("+1.5 s: game PCSA00017 'LittleBigPlanet' in front", report);
        Assert.Contains("at the stop: game PCSA00017", report);
        Assert.Contains("contact sheet: frames.png (12 frames", report);
        Assert.True(File.Exists(Path.Combine(dir, "frames.png")));
        Assert.Contains("-3.0 s: pad port 0 buttons [Cross]", report);
        Assert.Contains("02_port2_BGM_48000.wav", report);
        Assert.Contains("[background music (the shell's BGM), opened by shell thread 'SceShellMusicCoreAudioOutput']", report);
        Assert.Contains("continuous sound (music or a stream) on 02_port2_BGM_48000.wav", report);
        Assert.Contains("BGM port held by shell", report);

        // the same capture with only the short UI sound: no music
        File.Delete(Path.Combine(dir, "audio", "02_port2_BGM_48000.wav"));
        Assert.Contains("no continuous audio stream (music) during the recording", CaptureAnalyzer.Analyze(dir, quiet: true));
    }

    /// <summary>A port that is simply idle between short sounds (the shell's UI sounds) drains its host queue: that
    /// is not an underrun; a port that keeps outputting grains on time and still runs dry is.</summary>
    [Fact]
    public void Underrun_after_an_idle_pause_is_not_an_underrun()
    {
        var p = new VitaSharp.Core.Util.AudioDiagnostics.PortStats { Name = "port 1 MAIN", Grain = 1024, Freq = 48000, Channels = 2 };
        p.LastGap = 5.7;
        Assert.True(VitaSharp.Core.Util.AudioDiagnostics.ResumedAfterIdle(p));
        p.LastGap = 0.021;
        Assert.False(VitaSharp.Core.Util.AudioDiagnostics.ResumedAfterIdle(p));
        p.LastGap = 0.06; // a late grain of a running stream (3 grains of 21 ms): a real underrun
        Assert.False(VitaSharp.Core.Util.AudioDiagnostics.ResumedAfterIdle(p));
    }

    /// <summary>A capture folder of <paramref name="frames"/> moving pictures (the save's progress and zip tests).</summary>
    string SimpleCapture(string name, int frames)
    {
        string dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "session.txt"), "title-id: TEST00000\ntitle: Test\nstart-ns: 0\n");
        using var fs = File.Create(Path.Combine(dir, "frames.vsfr"));
        FramesFile.WriteHeader(fs);
        var enc = new FramesFile.Encoder();
        for (int i = 0; i < frames; i++)
        {
            var (data, flags) = enc.Encode(Picture(i), W, H);
            FramesFile.WriteRecord(fs, new PresentInfo { Present = i + 1, Flip = i, PresentNs = (i + 1) * 16_666_667L, IntervalMs = 16.7f, Hitch = "" }, flags, data);
        }
        return dir;
    }

    /// <summary>The analysis reports its progress (the "saving NN%" badge) and stops when the window closes while a
    /// recording is saved (the close waits only for the files).</summary>
    [Fact]
    public void Analyzer_reports_progress_and_can_be_cancelled()
    {
        string dir = SimpleCapture("progress", 300);
        var seen = new List<double>();
        CaptureAnalyzer.Analyze(dir, quiet: true, progress: seen.Add, expectedFrames: 300);
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.InRange(p, 0, 1));
        Assert.True(seen.Max() >= 0.5, $"progress reached only {seen.Max():F2}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => CaptureAnalyzer.Analyze(SimpleCapture("cancelled", 50), quiet: true, cancel: cts.Token));
    }

    /// <summary>The zip stores frames.vsfr (its pictures are compressed already: deflating 2 GB of them only cost time)
    /// and reports the bytes written.</summary>
    [Fact]
    public void Zip_stores_the_frames_and_reports_progress()
    {
        string dir = SimpleCapture("zipped", 120);
        File.WriteAllText(Path.Combine(dir, "log.txt"), string.Concat(Enumerable.Repeat("a log line that compresses well\n", 2000)));
        string zip = dir + ".zip";
        var seen = new List<double>();
        SessionCapture.ZipFolder(dir, zip, seen.Add);
        Assert.Equal(1.0, seen[^1], 3);
        Assert.True(seen.Zip(seen.Skip(1)).All(p => p.Second >= p.First), "progress goes backwards");
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        var frames = z.GetEntry("zipped/frames.vsfr")!;
        Assert.Equal(new FileInfo(Path.Combine(dir, "frames.vsfr")).Length, frames.Length);
        Assert.Equal(frames.Length, frames.CompressedLength); // stored
        var log = z.GetEntry("zipped/log.txt")!;
        Assert.True(log.CompressedLength < log.Length / 4); // deflated
    }
}
