using VitaSharp.Core.Capture;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// A synthetic "game" for the F8 tools on the software Vulkan device: every frame one scene draws a blue background
/// over the whole 64x64 surface and a small red HUD triangle in one corner (one cell of the analyzer's 4x4 grid), and
/// presents it. A frame can leave the HUD out, draw it with the depth test NEVER (it then writes nothing) or paint the
/// whole picture red: one-frame flashes the capture report finds, of a part or of the whole picture.
/// </summary>
internal sealed class FlashScene
{
    public const uint W = 64, H = 64;
    /// <summary>SCE_GXM_TEXTURE_FORMAT_U8U8U8U8_ABGR.</summary>
    private const uint TexAbgr = 0x0C000000;
    /// <summary>SCE_GXM_DEPTH_FUNC_ALWAYS and SCE_GXM_DEPTH_FUNC_NEVER (the function in bits 22-24).</summary>
    public const uint DepthAlways = 0x01C00000, DepthNever = 0;

    private readonly Gxm _g;
    private readonly HleHarness _h;
    public readonly RendererTextureTests.Workload Workload;
    public readonly uint Blue, Red, HudVerts;

    public enum Hud
    {
        Drawn,
        Missing,
        DepthNever,
        WholeRed,
    }

    public FlashScene(HleHarness h)
    {
        _h = h;
        _g = h.Module<Gxm>();
        Workload = new RendererTextureTests.Workload(h);
        Blue = Solid(0xFFFF0000u);
        Red = Solid(0xFF0000FFu);
        // a right triangle over half of the top-left 16x16 cell (clip x, y from -1 to -0.5)
        HudVerts = h.Alloc(48);
        float[] v = [-1, -1, 0.5f, 1, -0.5f, -1, 0.5f, 1, -1, -0.5f, 0.5f, 1];
        for (int i = 0; i < v.Length; i++)
        {
            GuestMemory.Write(HudVerts + (uint)i * 4, v[i]);
        }
    }

    private uint Solid(uint abgr)
    {
        uint texture = _h.Alloc(16), texels = _h.Alloc(8 * 8 * 4);
        for (uint i = 0; i < 64; i++)
        {
            GuestMemory.W32(texels + i * 4, abgr);
        }

        Assert.Equal(0, Call(_g, "sceGxmTextureInitLinear", texture, texels, TexAbgr, 8u, 8u, 1u));
        return texture;
    }

    /// <summary>One frame: the scene and its present.</summary>
    public void Frame(Hud hud = Hud.Drawn)
    {
        RendererTextureTests.Workload w = Workload;
        Assert.Equal(0, Call(_g, "sceGxmBeginScene", w.Ctx, 0u, w.Rt, 0u, 0u, 0u, w.Color, 0u));
        Invoke(_g, "sceGxmSetVertexProgram", w.Ctx, w.Vp);
        Invoke(_g, "sceGxmSetFragmentProgram", w.Ctx, w.Fp);
        Invoke(_g, "sceGxmSetFrontDepthFunc", w.Ctx, DepthAlways);
        Invoke(_g, "sceGxmSetCullMode", w.Ctx, 0u);
        Assert.Equal(0, Call(_g, "sceGxmSetVertexStream", w.Ctx, 0u, w.Verts));
        Assert.Equal(0, Call(_g, "sceGxmSetFragmentTexture", w.Ctx, 3u, hud == Hud.WholeRed ? Red : Blue));
        Assert.Equal(0, Call(_g, "sceGxmDraw", w.Ctx, 0u, 0u, w.Idx, 3u));
        if (hud is Hud.Drawn or Hud.DepthNever)
        {
            Invoke(_g, "sceGxmSetFrontDepthFunc", w.Ctx, hud == Hud.DepthNever ? DepthNever : DepthAlways);
            Assert.Equal(0, Call(_g, "sceGxmSetVertexStream", w.Ctx, 0u, HudVerts));
            Assert.Equal(0, Call(_g, "sceGxmSetFragmentTexture", w.Ctx, 3u, Red));
            Assert.Equal(0, Call(_g, "sceGxmDraw", w.Ctx, 0u, 0u, w.Idx, 3u));
        }

        Assert.Equal(0, Call(_g, "sceGxmEndScene", w.Ctx, 0u, 0u));
        _h.Emu.Gpu.Submit(Present());
    }

    public PresentCommand Present() => new() { Frame = new DisplayFrame(Workload.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) };

    /// <summary>Records <paramref name="frames"/> (one <see cref="Hud"/> per frame) on <paramref name="renderer"/> into
    /// the GPU dump <paramref name="dump"/>; the renderer's present tap (if any) sees them as a recording does.</summary>
    public static void Record(HleHarness h, VulkanRenderer renderer, string dump, IReadOnlyList<Hud> frames, Action<FlashScene>? before = null)
    {
        try
        {
            h.Emu.Gpu.Backend = renderer;
            var scene = new FlashScene(h);
            before?.Invoke(scene);
            scene.Frame(); // a frame before the recording: the recorder starts at the next present
            var done = new ManualResetEventSlim();
            var rec = GpuDumpRecorder.Start(new GpuDumpOptions { Path = dump, MaxFrames = 0, Header = new GpuDumpHeader { ["title-id"] = "PCSA00000" }, Finished = _ => done.Set() });
            Assert.NotNull(rec);
            h.Emu.Gpu.Submit(scene.Present());
            foreach (Hud hud in frames)
            {
                scene.Frame(hud);
            }

            h.Emu.Gpu.WaitIdle(); // the recorder stops at the next present the worker executes: after these frames
            rec!.Stop();
            h.Emu.Gpu.Submit(scene.Present());
            h.Emu.Gpu.WaitIdle();
            Assert.True(done.Wait(30_000), "the dump was not finished");
            Assert.Equal(0, Call(h.Module<Gxm>(), "sceGxmFinish", scene.Workload.Ctx));
            scene.Workload.Terminate();
        }
        finally
        {
            h.Emu.Gpu.WaitIdle();
            h.Emu.Gpu.Backend = null;
            renderer.Dispose();
        }
    }

    /// <summary>Replays <paramref name="dump"/> in this process (the guest memory of the harness; replay-gpu does the
    /// same in a process of its own) on a renderer made from <paramref name="options"/>, with the present numbers of
    /// the recording.</summary>
    public static GpuDumpPlayStats Replay(string dump, VulkanRendererOptions options, Action<VulkanRenderer>? before = null)
    {
        using var reader = new GpuDumpReader(dump);
        options.FirstPresent = reader.Header.Long("first-present");
        var renderer = new VulkanRenderer(options);
        try
        {
            before?.Invoke(renderer);
            GpuDumpPlayStats stats = GpuDumpPlayer.Play(reader, renderer);
            renderer.Execute(new FinishCommand());
            return stats;
        }
        finally
        {
            renderer.Dispose();
        }
    }

    /// <summary>A present tap that writes what it sees as a recording's frames.vsfr (and keeps the findings too).</summary>
    public sealed class FramesWriter : IPresentTap, IDisposable
    {
        private readonly FileStream _file;
        private readonly FramesFile.Encoder _encoder = new();
        public readonly FrameFindings Findings = new();

        public FramesWriter(string path)
        {
            _file = File.Create(path);
            FramesFile.WriteHeader(_file);
        }

        public void OnPresent(in PresentInfo info, ReadOnlySpan<byte> rgba, int w, int h)
        {
            Findings.Add(info, rgba, w, h);
            (byte[] data, byte flags) = _encoder.Encode(rgba, w, h);
            lock (_file)
            {
                FramesFile.WriteRecord(_file, info, flags, data);
            }
        }

        public void Dispose() => _file.Dispose();
    }

    /// <summary>A capture folder of the synthetic scene: session.txt, frames.vsfr (what the recording renderer showed)
    /// and gpu.vsgd.</summary>
    public static FrameFindings CaptureFolder(HleHarness h, string dir, IReadOnlyList<Hud> frames)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "session.txt"), "title-id: PCSA00000\ntitle: FlashScene\nstart-ns: 0\n");
        using var writer = new FramesWriter(Path.Combine(dir, "frames.vsfr"));
        Record(h, new VulkanRenderer(Options(writer)), Path.Combine(dir, "gpu.vsgd"), frames);
        return writer.Findings;
    }

    public static VulkanRendererOptions Options(IPresentTap? tap = null) => new()
    {
        VSync = false,
        AsyncShaders = false,
        AsyncPipelines = false,
        PresentTap = tap,
    };
}

/// <summary>
/// <c>verify</c> over a GPU dump (the replay's frames through the capture report's rules, set against the
/// recording's): a flash the commands themselves draw is still there in the replay; a flash the recording showed but
/// the commands do not draw (a renderer bug since fixed) is gone; a finding the recording did not have is new.
/// </summary>
[Collection("guest")]
public sealed class CaptureVerifyRenderTests(GuestMemoryFixture fx)
{
    /// <summary>The report's texts in Portuguese whatever the runner's language (the assertions read them).</summary>
    private static readonly Dictionary<string, string> s_portuguese = new() { [VitaSharp.Core.Util.EnvVars.Language] = "pt-BR" };

    private static List<FlashScene.Hud> Frames(int flashAt, FlashScene.Hud flash)
    {
        var frames = new List<FlashScene.Hud>();
        for (int i = 0; i < 12; i++)
        {
            frames.Add(i == flashAt ? flash : FlashScene.Hud.Drawn);
        }

        return frames;
    }

    private static List<CaptureVerify.Item> Items(FrameFindings findings) => findings.Findings().Where(f => !f.Info).Select(CaptureVerify.ToItem).ToList();

    [SoftwareVulkanFact]
    public void AFlashTheCommandsDrawIsStillPresentInTheReplay()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "verify1");
        string dump = Path.Combine(h.Root, "flash.vsgd");
        var recorded = new FrameFindings();
        FlashScene.Record(h, new VulkanRenderer(FlashScene.Options(recorded)), dump, Frames(6, FlashScene.Hud.Missing));
        List<CaptureVerify.Item> live = Items(recorded);
        CaptureVerify.Item flash = Assert.Single(live, f => f.Kind == "flash");
        Assert.True(flash.Cell >= 0, $"a part-picture flash expected: {flash.Detail}");

        var replayed = new FrameFindings();
        GpuDumpPlayStats stats = FlashScene.Replay(dump, FlashScene.Options(replayed));
        (long first, long last) = replayed.Presents;
        Assert.True(replayed.Count >= 12, $"{replayed.Count} frames tapped of {stats.Frames} replayed ({stats.Draws} draws), presents {first}-{last}");
        (long recordedFirst, long recordedLast) = recorded.Presents;
        Assert.True(flash.Present >= first && flash.Present <= last, $"flash at {flash.Present}, recorded presents {recordedFirst}-{recordedLast}, replayed {first}-{last}"); // the replay numbers its presents as the recording did
        CaptureVerify.Result result = CaptureVerify.Compare(live, Items(replayed), first, last);
        Assert.Equal(CaptureVerify.Verdict.StillPresent, result.Verdict);
        Assert.Equal(1, result.StillThere);
    }

    /// <summary>The front-end's verify on a capture folder: the replay in a child process (replay-gpu --findings), its
    /// findings.txt read back and set against the recording's frames.vsfr: "ainda presente", exit code 1, and the
    /// capture folder left as it was (everything in --out).</summary>
    [SoftwareVulkanFact]
    public void VerifyCommandSaysTheFlashIsStillPresent()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "verify3");
        string capture = Path.Combine(h.Root, "PCSA00000_20261125_000000_tudo"), output = Path.Combine(h.Root, "verify-out");
        FrameFindings recorded = FlashScene.CaptureFolder(h, capture, Frames(6, FlashScene.Hud.Missing));
        Assert.Single(recorded.Findings(), f => f.Kind == "flash");
        string[] before = Directory.GetFiles(capture);
        DesktopProcess.Result r = DesktopProcess.Run(h.Root, ["verify", capture, $"--out={output}", "--no-nan-scan"], timeoutMs: 180_000, env: s_portuguese);
        Assert.True(r.ExitCode == 1, r.Output);
        Assert.Contains("ainda presente", r.Output, StringComparison.Ordinal);
        Assert.Contains("resultado: ainda presente", File.ReadAllText(Path.Combine(output, "verify.txt")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, "replay", CaptureVerify.FindingsFileName)));
        // the draw probe names the HUD draw the flashing frame lacks
        Assert.Contains("draw responsável por [flash]", r.Output, StringComparison.Ordinal);
        Assert.Contains("falta no quadro do pisca", r.Output, StringComparison.Ordinal);
        Assert.Equal(before, Directory.GetFiles(capture));
    }

    /// <summary>verify --ab: the replay with a switch that does not touch the scene keeps the flash ("igual"); one that
    /// paints every pixel of the scene's fragment program one colour (a VITASHARP_DEBUG_FS override, given with
    /// --ab-env) removes it, and the report names it.</summary>
    [SoftwareVulkanFact]
    public void VerifyAbNamesTheSwitchThatRemovesTheFlash()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "verify4");
        string capture = Path.Combine(h.Root, "PCSA00000_20261125_000001_tudo"), output = Path.Combine(h.Root, "verify-ab");
        FlashScene.CaptureFolder(h, capture, Frames(6, FlashScene.Hud.Missing));
        ulong fragmentHash = 0;
        using (var reader = new GpuDumpReader(Path.Combine(capture, "gpu.vsgd")))
        {
            while (fragmentHash == 0 && reader.Next())
            {
                if (reader.Command is DrawCommand { FragmentProgram.Info: { } info })
                {
                    fragmentHash = info.Program.Hash;
                }
            }
        }

        Assert.NotEqual(0UL, fragmentHash);
        string solid = $"{VitaSharp.Core.Util.EnvVars.DebugFs}={fragmentHash:X16}:vec4(0.0, 0.0, 1.0, 1.0)";
        DesktopProcess.Result r = DesktopProcess.Run(h.Root, ["verify", capture, $"--out={output}", "--no-nan-scan", "--ab=depth-seed-off", $"--ab-env={solid}"], timeoutMs: 300_000, env: s_portuguese);
        Assert.True(r.ExitCode == 1, r.Output);
        Assert.Contains("depth-seed-off (VITASHARP_DEPTH_SEED=0: ", r.Output, StringComparison.Ordinal);
        Assert.Contains("): igual", r.Output, StringComparison.Ordinal);
        Assert.Contains($"o achado some com: {solid}", r.Output, StringComparison.Ordinal);
    }

    [SoftwareVulkanFact]
    public void AFlashTheCommandsDoNotDrawIsFixedAndANewOneIsAChange()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "verify2");
        string dump = Path.Combine(h.Root, "steady.vsgd");
        var recorded = new FrameFindings();
        FlashScene.Record(h, new VulkanRenderer(FlashScene.Options(recorded)), dump, Frames(-1, FlashScene.Hud.Drawn));
        Assert.DoesNotContain(Items(recorded), f => f.Kind == "flash");
        (long recordedFirst, long recordedLast) = recorded.Presents;

        var replayed = new FrameFindings();
        FlashScene.Replay(dump, FlashScene.Options(replayed));
        (long first, long last) = replayed.Presents;
        // what the player saw (a renderer bug of the build that recorded): a flash at a present the dump holds
        long at = (recordedFirst + recordedLast) / 2;
        CaptureVerify.Result fixedResult = CaptureVerify.Compare([new CaptureVerify.Item("flash", at, 0, 1, "recorded")], Items(replayed), first, last);
        Assert.True(fixedResult.Verdict == CaptureVerify.Verdict.Fixed, $"{fixedResult.Verdict}: flash at {at}, recorded presents {recordedFirst}-{recordedLast}, replayed {first}-{last}");
        // a whole-picture flash the replay shows and the recording did not have
        string flashing = Path.Combine(h.Root, "whole.vsgd");
        FlashScene.Record(h, new VulkanRenderer(FlashScene.Options()), flashing, Frames(5, FlashScene.Hud.WholeRed));
        var again = new FrameFindings();
        FlashScene.Replay(flashing, FlashScene.Options(again));
        (first, last) = again.Presents;
        CaptureVerify.Result changed = CaptureVerify.Compare([], Items(again), first, last);
        Assert.Equal(CaptureVerify.Verdict.Changed, changed.Verdict);
        Assert.Contains(changed.Entries, e => e.Outcome == CaptureVerify.Outcome.New);
    }
}
