using VitaSharp.Core.Capture;
using VitaSharp.Core.Gpu;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// Pixel -> draw attribution (<see cref="DrawAttribution"/>): the draws of the flashing frame lined up with the
/// neighbour's name the draw responsible: a draw missing in the flashing frame, one only it has, one that wrote the
/// region in one frame and nothing in the other (with the state that differs: depth, stencil, blend; and the depth or
/// stencil under the region before it), one with another state; none when every draw into the shown surface is the
/// same (the cause is a surface drawn earlier, or the buffer presented).
/// </summary>
public sealed class DrawAttributionTests
{
    private const ulong Vs = 0x1111, Background = 0xB0, Hud = 0x4D;

    private static DrawAttribution.Probed P(long present, long draw, ulong fs, int changed, string state = "depth=7/True st=0/0", double depth = 0.5) =>
        new(present, draw, 0x60000000, Vs, fs, changed, 128, depth, 0, state);

    [Fact]
    public void ADrawMissingInTheFlashingFrameIsResponsible()
    {
        DrawAttribution.Result r = DrawAttribution.Attribute([P(9, 1, Background, 256), P(9, 2, Hud, 128), P(10, 3, Background, 256), P(11, 4, Background, 256), P(11, 5, Hud, 128)], 10);
        Assert.Equal((Hud, 2L), (r.Responsible?.Fs, r.Responsible?.Draw));
        Assert.Contains("falta no quadro do pisca", r.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void ADrawThatWroteNothingNamesTheStateAndTheDepthThatDiffer()
    {
        DrawAttribution.Result r = DrawAttribution.Attribute(
            [P(9, 1, Background, 256), P(9, 2, Hud, 128), P(10, 3, Background, 256), P(10, 4, Hud, 0, "depth=0/True st=0/0", depth: 0.25)], 10);
        Assert.Equal((Hud, 4L), (r.Responsible?.Fs, r.Responsible?.Draw));
        Assert.Contains("não escreveu na região", r.Why, StringComparison.Ordinal);
        Assert.Contains("depth 0/True (vizinho 7/True)", r.Why, StringComparison.Ordinal);
        Assert.Contains("profundidade antes 0.2500 (vizinho 0.5000)", r.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void ADrawOnlyTheFlashingFrameHasOrWithAnotherStateIsResponsible()
    {
        DrawAttribution.Result extra = DrawAttribution.Attribute([P(9, 1, Background, 256), P(10, 2, Background, 256), P(10, 3, 0xEE, 40)], 10);
        Assert.Equal(0xEEUL, extra.Responsible?.Fs);
        Assert.Contains("só no quadro do pisca", extra.Why, StringComparison.Ordinal);
        DrawAttribution.Result blend = DrawAttribution.Attribute([P(9, 1, Hud, 128, "blend=none"), P(10, 2, Hud, 128, "blend=F:0/6/7")], 10);
        Assert.Contains("estado diferente", blend.Why, StringComparison.Ordinal);
        Assert.Contains("blend F:0/6/7 (vizinho none)", blend.Why, StringComparison.Ordinal);
        // the neighbour after, when the flashing frame is the dump's first
        DrawAttribution.Result after = DrawAttribution.Attribute([P(10, 1, Background, 256), P(11, 2, Background, 256), P(11, 3, Hud, 128)], 10);
        Assert.Equal(Hud, after.Responsible?.Fs);
    }

    [Fact]
    public void TheSameDrawsInBothFramesNameNoDraw()
    {
        DrawAttribution.Result r = DrawAttribution.Attribute([P(9, 1, Background, 256), P(10, 2, Background, 256)], 10);
        Assert.Null(r.Responsible);
        Assert.Contains(r.Lines, l => l.Contains("nenhum draw", StringComparison.Ordinal));
    }

    [Fact]
    public void ProbeFileRoundTripsAndCellsCoverThePicture()
    {
        string path = Path.Combine(Path.GetTempPath(), "vitasharp-probe-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            DrawAttribution.Write(path, "probe of present 10\nregion 0,0 16x16", [P(10, 3, Hud, 0, "depth=0/True t3=0x81000000:0C000000", 0.25)]);
            DrawAttribution.Probed back = Assert.Single(DrawAttribution.Read(path));
            Assert.Equal((10L, 3L, Hud, 0, 0.25, "depth=0/True t3=0x81000000:0C000000"), (back.Present, back.Draw, back.Fs, back.Changed, back.Depth, back.State));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal((240u, 136u, 240u, 136u), DrawAttribution.CellRegion(5, 960, 544));
        Assert.Equal((0u, 0u, 960u, 544u), DrawAttribution.CellRegion(-1, 960, 544));
    }
}

/// <summary>
/// The draw probe on the software Vulkan device: a GPU dump of the synthetic scene whose HUD draw is rejected by the
/// depth test for one frame, replayed with the probe around the flashing present: the probe sees the HUD write its
/// region in the neighbour and nothing in the flashing frame, and the attribution names it with its depth function.
/// </summary>
[Collection("guest")]
public sealed class DrawProbeRenderTests(GuestMemoryFixture fx)
{
    [SoftwareVulkanFact]
    public void TheProbeNamesTheHudDrawTheDepthTestRejected()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "probe");
        string dump = Path.Combine(h.Root, "depth.vsgd");
        var recorded = new FrameFindings();
        var frames = new List<FlashScene.Hud>();
        for (int i = 0; i < 10; i++)
        {
            frames.Add(i == 5 ? FlashScene.Hud.DepthNever : FlashScene.Hud.Drawn);
        }

        FlashScene.Record(h, new VulkanRenderer(FlashScene.Options(recorded)), dump, frames);
        CaptureAnalyzer.Finding flash = Assert.Single(recorded.Findings(), f => f.Kind == "flash");

        var index = GpuDumpFrameIndex.Build(dump);
        var probe = DrawProbe.Around(index, flash.Present, flash.Cell, out Func<long, long, bool> executeDraw);
        Assert.NotNull(probe);
        using (var reader = new GpuDumpReader(dump))
        {
            VulkanRendererOptions options = FlashScene.Options();
            options.FirstPresent = reader.Header.Long("first-present");
            var renderer = new VulkanRenderer(options) { Probe = probe };
            try
            {
                GpuDumpPlayer.Play(reader, renderer, executeDraw: executeDraw);
                renderer.Execute(new FinishCommand());
            }
            finally
            {
                renderer.Dispose();
            }
        }

        List<DrawAttribution.Probed> probed = probe!.Results;
        // the neighbour: the background and the HUD both write the cell (the HUD repaints it red over the blue)
        Assert.Equal(2, probed.Count(p => p.Present == flash.Present - 1 && p.Changed > 0));
        DrawAttribution.Result result = DrawAttribution.Attribute(probed, flash.Present);
        Assert.NotNull(result.Responsible);
        Assert.Equal(flash.Present, result.Responsible!.Value.Present);
        Assert.StartsWith("depth=0/", result.Responsible.Value.State, StringComparison.Ordinal);
        Assert.Contains("não escreveu na região", result.Why, StringComparison.Ordinal);
        Assert.Contains("depth 0/", result.Why, StringComparison.Ordinal);
    }
}
