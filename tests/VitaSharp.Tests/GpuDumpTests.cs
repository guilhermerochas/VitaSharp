using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>GPU dump format round trips (codec, chunks, header) and an end-to-end recording through a GpuQueue.</summary>
[Collection("guest")]
public sealed class GpuDumpTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-gpudump-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    private static GxmProgramInfo Info(bool fragment, byte seed)
    {
        byte[] data = new byte[0xC0];
        data[0x14] = fragment ? (byte)1 : (byte)0;
        data[0xBF] = seed;
        var p = new GxpProgram(data, 0x81000000u + seed);
        (List<UniformBufferSlot>? slots, int bytes) = ShaderGenerator.UniformLayout(p);
        return new GxmProgramInfo { Address = 0x81000000u + seed, Program = p, UniformSlots = slots, UniformBytes = bytes, TexturesUsed = 0, CubeSamplers = 0 };
    }

    private static readonly GxmVertexProgram s_vertexProgram = new()
    {
        Handle = 0x1234,
        Info = Info(false, 1),
        Attributes = [new GxmVertexAttribute(0, 0, 3, 3, 0), new GxmVertexAttribute(1, 12, 5, 2, 4)],
        Streams = [new GxmVertexStream(20, 0), new GxmVertexStream(8, 1)],
    };

    private static readonly GxmFragmentProgram s_fragmentProgram = new()
    {
        Handle = 0x5678,
        Info = Info(true, 2),
        Blend = new GxmBlendInfo(0xF, 1, 2, 3, 4, 5, 6),
        OutputFormat = 0x80000000,
        MultisampleMode = 2,
        // a ForGles remap whose last entry sets the packed value's high byte
        TexcoordRemap = GxmTexcoordRemap.Pack([3, 1, 2, 0, 4, 5, 6, 7, 8, 0]),
    };

    private static DrawCommand Draw(byte[] vu, byte[] fu, uint index)
    {
        var d = new DrawCommand { VertexProgram = s_vertexProgram, FragmentProgram = s_fragmentProgram };
        d.Reset();
        d.State.CullMode = 2;
        d.State.Front = SideState.Default;
        d.State.Back = SideState.Default with
        {
            DepthBiasUnits = -7
        };
        d.State.VpScaleX = 480.5f;
        d.State.ClipMaxY = 543;
        d.State.WClampValue = 0.25f;
        d.VertexUniforms = vu;
        d.VertexUniformsLength = vu.Length;
        d.FragmentUniforms = fu;
        d.FragmentUniformsLength = fu.Length;
        d.VertexUniformAddrs[0] = 0x81100000;
        d.FragmentUniformAddrs[14] = 0x62000000;
        d.FragmentTextures[3] = new GxmTextureDesc(1, 2, 0x81200000, 4);
        d.Streams[0] = index + 0x100;
        d.Streams[1] = index + 0x200;
        d.PrimitiveType = 3;
        d.Index32 = true;
        d.IndexAddress = index;
        d.IndexCount = 99;
        d.InstanceCount = 2;
        d.VisAddress = 0x81300004;
        d.VisIncrement = true;
        return d;
    }

    private static void AssertSameDraw(DrawCommand a, DrawCommand b)
    {
        Assert.Equal(a.VertexProgram.Handle, b.VertexProgram.Handle);
        Assert.Equal(a.VertexProgram.Attributes, b.VertexProgram.Attributes);
        Assert.Equal(a.VertexProgram.Streams, b.VertexProgram.Streams);
        Assert.Equal(a.VertexProgram.Info.Program.Data, b.VertexProgram.Info.Program.Data);
        Assert.Equal(a.FragmentProgram.Blend, b.FragmentProgram.Blend);
        Assert.Equal(a.FragmentProgram.MultisampleMode, b.FragmentProgram.MultisampleMode);
        Assert.Equal(a.FragmentProgram.IsMaskUpdate, b.FragmentProgram.IsMaskUpdate);
        Assert.Equal(a.FragmentProgram.TexcoordRemap, b.FragmentProgram.TexcoordRemap);
        Assert.Equal(a.State, b.State);
        Assert.Equal(a.VertexUniformSpan.ToArray(), b.VertexUniformSpan.ToArray());
        Assert.Equal(a.FragmentUniformSpan.ToArray(), b.FragmentUniformSpan.ToArray());
        Assert.Equal(a.VertexUniformAddrs, b.VertexUniformAddrs);
        Assert.Equal(a.FragmentUniformAddrs, b.FragmentUniformAddrs);
        Assert.Equal(a.FragmentTextures, b.FragmentTextures);
        Assert.Equal(a.VertexTextures, b.VertexTextures);
        Assert.Equal(a.Streams, b.Streams);
        Assert.Equal((a.PrimitiveType, a.Index32, a.IndexAddress, a.IndexCount, a.InstanceCount, a.VisAddress, a.VisIncrement),
                     (b.PrimitiveType, b.Index32, b.IndexAddress, b.IndexCount, b.InstanceCount, b.VisAddress, b.VisIncrement));
    }

    [Fact]
    public void CommandsRoundTripThroughAFile()
    {
        var rnd = new Random(5);
        byte[] Bytes(int n)
        {
            byte[] b = new byte[n];
            rnd.NextBytes(b);
            return b;
        }
        byte[] vu1 = Bytes(4096);
        byte[] vu2 = (byte[])vu1.Clone();
        vu2[100] ^= 0xFF;
        vu2[3000] ^= 1;     // small change: delta
        byte[] vu3 = Bytes(4096);                                               // all new: inline
        byte[] fu = Bytes(64);                                                  // repeated: references
        var cmds = new List<GpuCommand>
        {
            new BeginSceneCommand { Width = 960, Height = 544, Multisample = 2,
                Color = new ColorSurfaceInfo(0x60000000, 960, 544, 960, 0x1000, 2, true, 1),
                Depth = new DepthSurfaceInfo(0x61000000, 0, 960, 0x4000, 1, 0.5f, 7, true, false) },
            Draw(vu1, fu, 0x81400000), Draw(vu2, fu, 0x81500000), Draw(vu3, fu, 0x81600000), Draw(vu1, [], 0x81700000),
            new MidSceneFlushCommand(),
            new EndSceneCommand { Notifications = [(0x81000010, 5), (0x81000014, 6)] },
            // sceGxmBeginSceneEx with another load surface: its addresses survive the round trip
            new BeginSceneCommand { Width = 960, Height = 544,
                Depth = new DepthSurfaceInfo(0x61000000, 0x61200000, 960, 0x4000, 1, 0.5f, 7, true, true) { LoadDepthData = 0x61400000, LoadStencilData = 0x61600000 } },
            new EndSceneCommand(),
            // a second Ex scene, same store surface, another load surface (only its depth moves): read back as its own
            new BeginSceneCommand { Width = 960, Height = 544,
                Depth = new DepthSurfaceInfo(0x61000000, 0x61200000, 960, 0x4000, 1, 0.25f, 3, true, false) { LoadDepthData = 0x61800000, LoadStencilData = 0x61200000 } },
            new EndSceneCommand(),
            new CallbackCommand { WriteAddress = 0x62000000, WriteSize = 4096, Action = () => { } },
            new SignalCommand { Signal = () => { } },
            new FinishCommand(),
            new PresentCommand { Frame = new DisplayFrame(0x60000000, 960, 960, 544, DisplayPixelFormat.A2B10G10R10) },
        };
        byte[] memory = Bytes(300);

        var codec = new GpuDumpCodec();
        var body = new DumpBuffer();
        var defs = new DumpBuffer();
        var rec = new DumpBuffer();
        body.U8((byte)GpuDumpRecord.Memory);
        body.U32(0x81400000);
        body.Var((uint)memory.Length);
        body.Bytes(memory);
        foreach (GpuCommand c in cmds)
        {
            defs.Clear();
            rec.Clear();
            Assert.True(codec.Write(c, defs, rec));
            body.Append(defs);
            body.Append(rec);
            if (c is PresentCommand)
            {
                GpuDumpCodec.WritePresentInfo(body, 33_333, 4300, 4301);
            }
        }
        Assert.Equal((3L, 3L, 1L), (codec.UniformStats.Inline, codec.UniformStats.Refs, codec.UniformStats.Deltas));

        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "a.vsgd");
        var header = new GpuDumpHeader();
        header["title-id"] = "PCSC00045";
        header["renderer.0"] = "GPU: test\nsecond line";
        using (FileStream f = File.Create(path))
        {
            GpuDumpCodec.WriteFileHeader(f, header);
            byte[]? scratch = null;
            // split in two chunks (one compressed, one stored) at a record boundary
            GpuDumpCodec.WriteChunk(f, GpuDumpFormat.ChunkRecords, body.Span, 3, ref scratch);
            var tail = new DumpBuffer();
            tail.U8((byte)GpuDumpRecord.Idle);
            GpuDumpCodec.WriteChunk(f, GpuDumpFormat.ChunkRecords, tail.Span, 0, ref scratch);
            var end = new GpuDumpHeader { ["frames"] = "1" };
            GpuDumpCodec.WriteChunk(f, GpuDumpFormat.ChunkEnd, System.Text.Encoding.UTF8.GetBytes(end.Text), 3, ref scratch);
        }

        using var r = new GpuDumpReader(path);
        Assert.Equal("PCSC00045", r.Header["title-id"]);
        Assert.Equal("GPU: test | second line", r.Header["renderer.0"]);
        Assert.True(r.Next());
        Assert.Equal(GpuDumpRecord.Memory, r.Kind);
        Assert.Equal(0x81400000u, r.Address);
        Assert.Equal(memory, r.Data.ToArray());
        foreach (GpuCommand expected in cmds)
        {
            Assert.True(r.Next());
            GpuCommand got = r.Command!;
            Assert.Equal(expected.GetType(), got.GetType());
            switch (expected)
            {
                case DrawCommand d:
                    AssertSameDraw(d, (DrawCommand)got);
                    break;
                case BeginSceneCommand b:
                    var gb = (BeginSceneCommand)got;
                    Assert.Equal((b.Width, b.Height, b.Multisample, b.Color, b.Depth), (gb.Width, gb.Height, gb.Multisample, gb.Color, gb.Depth));
                    break;
                case EndSceneCommand e:
                    Assert.Equal(e.Notifications, ((EndSceneCommand)got).Notifications);
                    break;
                case CallbackCommand cb:
                    Assert.Equal((cb.WriteAddress, cb.WriteSize), (((CallbackCommand)got).WriteAddress, ((CallbackCommand)got).WriteSize));
                    break;
                case PresentCommand p:
                    Assert.Equal(p.Frame, ((PresentCommand)got).Frame);
                    Assert.Equal((33_333L, 4300L, 4301L), r.PresentInfo);
                    break;
            }
        }
        Assert.True(r.Next());
        Assert.Equal(GpuDumpRecord.Idle, r.Kind);
        Assert.False(r.Next());
        Assert.Equal("1", r.Summary?["frames"]);
    }

    [Fact]
    public void TruncatedFileReadsUpToTheCut()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "t.vsgd");
        using (FileStream f = File.Create(path))
        {
            GpuDumpCodec.WriteFileHeader(f, new GpuDumpHeader { ["title-id"] = "X" });
            byte[]? scratch = null;
            var b = new DumpBuffer();
            b.U8((byte)GpuDumpRecord.Finish);
            GpuDumpCodec.WriteChunk(f, GpuDumpFormat.ChunkRecords, b.Span, 3, ref scratch);
            GpuDumpCodec.WriteChunk(f, GpuDumpFormat.ChunkRecords, new byte[5000], 3, ref scratch);
        }
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^10]); // the last chunk is cut short
        using var r = new GpuDumpReader(path);
        Assert.True(r.Next());
        Assert.IsType<FinishCommand>(r.Command);
        Assert.False(r.Next());
        Assert.Null(r.Summary);
    }

    [Fact]
    public void RejectsOtherFilesAndNewerVersions()
    {
        Assert.Throws<InvalidDataException>(() => new GpuDumpReader(new MemoryStream(new byte[64])));
        var ms = new MemoryStream();
        GpuDumpCodec.WriteFileHeader(ms, new GpuDumpHeader());
        byte[] b = ms.ToArray();
        b[8] = (byte)(GpuDumpFormat.Version + 1);
        Assert.Throws<InvalidDataException>(() => new GpuDumpReader(new MemoryStream(b)));
    }

    private sealed class FakeBackend : IGpuBackend
    {
        public long Presents;
        public long PresentCount => Presents;
        public byte[]? ExportDumpState() => [1, 2, 3];
        public void Execute(GpuCommand command)
        {
            switch (command)
            {
                case DrawCommand d:
                    _ = GuestMemory.ReadSpan(d.IndexAddress, (int)d.IndexCount);
                    break; // the backend reads guest memory
                case PresentCommand:
                    Presents++;
                    break;
            }
        }
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// replay-gpu --async-shaders=on --prefetch-ahead=N (Renderer.Vulkan PrefetchLookahead, used by GpuDumpPlayer): the
    /// replay's submitting side hands command k of present interval p to the shader prefetch just before command k of
    /// interval p - N executes, as a game's submitting thread runs ahead of the GPU thread. Every command is prefetched
    /// once, in the recorded order, never after it executed, and the lead stays N presents however the intervals differ
    /// in length; at the end of the dump the lookahead stops. Without it the replay had no prefetch at all, so the
    /// first-use stalls it measured were those of a game whose GPU thread builds every pipeline itself.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReplayLookaheadPrefetchesEachCommandItsPresentsAhead(int frames)
    {
        uint buf = fixture.Memory.Alloc(0x1000, "gpudump lookahead");
        GuestMemory.Fill(buf, 0x33, 0x1000);
        using var queue = new GpuQueue();
        queue.Backend = new FakeBackend();
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, $"ahead{frames}.vsgd");
        var present = new PresentCommand { Frame = new DisplayFrame(0x60000000, 960, 960, 544, 0) };
        int[] drawsPerFrame = [3, 1, 4, 2];
        var rec = GpuDumpRecorder.Start(new GpuDumpOptions { Path = path, MaxFrames = drawsPerFrame.Length, Header = new GpuDumpHeader { ["title-id"] = "TEST" } });
        Assert.NotNull(rec);
        queue.Submit(present);
        for (int frame = 0; frame < drawsPerFrame.Length; frame++)
        {
            for (int k = 0; k < drawsPerFrame[frame]; k++)
            {
                DrawCommand draw = Draw([1, 2, 3], [], buf);
                draw.IndexCount = (uint)(frame * 10 + k + 1); // the draw's identity in the replay
                queue.Submit(draw);
            }
            queue.Submit(new PresentCommand { Frame = present.Frame });
        }
        queue.Submit(new PresentCommand { Frame = present.Frame });
        GpuDumpResult result = await rec!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result.Error);

        static string Name(GpuCommand command) => command is DrawCommand d ? $"draw {d.IndexCount}" : command.GetType().Name;
        // the recorded commands, each with its present interval and its 1-based place in it (the present is the last)
        var recorded = new List<(string Name, int Interval, int Place)>();
        using (var plain = new GpuDumpReader(path))
        {
            int interval = 0, place = 0;
            while (plain.Next())
            {
                if (plain.Command is not { } command)
                {
                    continue;
                }

                place++;
                recorded.Add((Name(command), interval, place));
                if (command is PresentCommand)
                {
                    interval++;
                    place = 0;
                }
            }
        }
        Assert.Contains(recorded, c => c.Name.StartsWith("draw", StringComparison.Ordinal));

        var prefetched = new List<string>();
        using var main = new GpuDumpReader(path);
        using var ahead = new GpuDumpReader(path);
        var lookahead = new PrefetchLookahead(ahead, command => prefetched.Add(Name(command)), frames);
        long presents = 0, frameCommands = 0;
        int index = 0;
        while (main.Next())
        {
            if (main.Command is not { } command)
            {
                continue;
            }

            frameCommands++;
            lookahead.Advance(presents, frameCommands);
            (string name, int interval, int place) = recorded[index];
            Assert.Equal(name, Name(command));
            // prefetched so far: every command up to place `place` of interval `interval + frames`, in the recorded order
            int expected = recorded.Count(c => c.Interval < interval + frames || c.Interval == interval + frames && c.Place <= place);
            Assert.True(expected > index, "the command about to execute is prefetched");
            Assert.Equal(recorded.Take(expected).Select(c => c.Name), prefetched);
            if (command is PresentCommand)
            {
                presents++;
                frameCommands = 0;
            }

            index++;
        }
        Assert.Equal(recorded.Select(c => c.Name), prefetched);
        Assert.Equal(recorded.Count, lookahead.Prefetched);
    }

    [Fact]
    public async Task RecorderStoresWhatTheBackendReadAndOnlyChangesLater()
    {
        uint buf = fixture.Memory.Alloc(0x3000, "gpudump test");
        GuestMemory.Fill(buf, 0x11, 0x3000);
        using var queue = new GpuQueue();
        var backend = new FakeBackend();
        queue.Backend = backend;
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "rec.vsgd");
        var present = new PresentCommand { Frame = new DisplayFrame(0x60000000, 960, 960, 544, 0) };
        // armed: starts after the first present, records 2 frames
        var rec = GpuDumpRecorder.Start(new GpuDumpOptions { Path = path, MaxFrames = 2, Header = new GpuDumpHeader { ["title-id"] = "TEST" } });
        Assert.NotNull(rec);
        DrawCommand d1 = Draw([1, 2, 3], [], buf + 0x100);
        d1.IndexCount = 0x40;
        queue.Submit(present);
        queue.Submit(d1);
        queue.WaitIdle();
        GuestMemory.Fill(buf + 0x120, 0x22, 4); // the CPU changes 4 bytes the next draw reads
        DrawCommand d2 = Draw([1, 2, 3], [], buf + 0x100);
        d2.IndexCount = 0x40;
        queue.Submit(d2);
        queue.Submit(new PresentCommand { Frame = present.Frame });
        queue.Submit(new PresentCommand { Frame = present.Frame });
        GpuDumpResult result = await rec!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result.Error);
        Assert.Equal(2, result.Frames);
        Assert.Null(GpuDumpRecorder.Current);

        using var r = new GpuDumpReader(path);
        Assert.Equal("1", r.Header["first-present"]);
        uint page = (buf + 0x100) & ~0xFFFu;
        var seen = new List<string>();
        while (r.Next())
        {
            seen.Add(r.Kind switch
            {
                GpuDumpRecord.Memory => $"mem {r.Address - page:X}+{r.Data.Length}={r.Data[0]:X2}",
                GpuDumpRecord.BackendState => $"state {r.Data.Length}",
                GpuDumpRecord.Present => $"present {r.PresentInfo.Present}",
                _ => r.Kind.ToString(),
            });
        }
        // first read: the whole page; second read: only the 4 changed bytes; idle points may come in between
        Assert.Equal(["state 3", "mem 0+4096=11", "Draw", $"mem {buf + 0x120 - page:X}+4=22", "Draw", "present 2", "present 3"], seen.Where(s => s != "Idle"));
    }

    /// <summary>
    /// --gpu-dump-at=F1:F2 arms the recorder before the title runs (no title id, no renderer yet), so it starts exactly
    /// after present F1-1 however fast the title's first frames come; the header (the title, the renderer) is built when
    /// it starts. It used to be armed by a thread that waited for the title and the renderer and built the header
    /// first: on a slow CI runner (macOS) the title's frames ran past F1 meanwhile, the dump started late and missed the
    /// frames it was asked for.
    /// </summary>
    [Fact]
    public async Task ScheduledDumpIsArmedBeforeTheTitleRunsAndStartsAtItsFrame()
    {
        var emu = new VitaSharp.Core.Emulator(new VitaSharp.Core.EmulatorOptions { FsRoot = _dir, Memory = fixture.Memory, EnableJit = false, CpuBackend = "interp" });
        try
        {
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, "scheduled.vsgd");
            VitaSharp.Desktop.GpuDumpControl.StartScheduled(emu, () => null, [$"--gpu-dump-at=3:4:{path}"]);
            GpuDumpRecorder? rec = GpuDumpRecorder.Current;
            Assert.NotNull(rec); // armed when the call returns, before the title is loaded
            Assert.Equal((2L, 2L), (rec.Options.StartAfterPresent, rec.Options.MaxFrames));
            emu.TitleId = "VSTS00001"; // the title loads after the recorder is armed

            using var queue = new GpuQueue();
            queue.Backend = new FakeBackend();
            var frame = new DisplayFrame(0x60000000, 960, 960, 544, 0);
            for (int i = 0; i < 6; i++)
            {
                queue.Submit(new PresentCommand { Frame = frame }); // the title's frames, as fast as they come
            }

            GpuDumpResult result = await rec.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(result.Error);
            Assert.Equal(2, result.Frames);
            using var r = new GpuDumpReader(path);
            Assert.Equal("2", r.Header["first-present"]); // presents 3 and 4
            Assert.Equal("VSTS00001", r.Header["title-id"]);
        }
        finally
        {
            GpuDumpRecorder.Current?.StopNow();
            emu.Dispose();
        }
    }

    /// <summary>A recording armed after the present it should start after starts at the next present and says so.</summary>
    [Fact]
    public async Task ARecordingArmedLateStartsAtTheNextPresent()
    {
        using var queue = new GpuQueue();
        var backend = new FakeBackend();
        queue.Backend = backend;
        var frame = new DisplayFrame(0x60000000, 960, 960, 544, 0);
        for (int i = 0; i < 3; i++)
        {
            queue.Submit(new PresentCommand { Frame = frame });
        }

        queue.WaitIdle();
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "late.vsgd");
        int headers = 0;
        var rec = GpuDumpRecorder.Start(new GpuDumpOptions
        {
            Path = "",
            PathAtStart = () => path,
            HeaderAtStart = () => new GpuDumpHeader { ["title-id"] = $"LATE{++headers}" },
            StartAfterPresent = 1,
            MaxFrames = 1,
        });
        Assert.NotNull(rec);
        Assert.Equal(0, headers); // built when the recording starts, not when it is armed
        queue.Submit(new PresentCommand { Frame = frame });
        queue.Submit(new PresentCommand { Frame = frame });
        GpuDumpResult result = await rec.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result.Error);
        Assert.Equal((path, 1L), (result.Path, result.Frames));
        using var r = new GpuDumpReader(path);
        Assert.Equal("4", r.Header["first-present"]);
        Assert.Equal("LATE1", r.Header["title-id"]);
    }
}
