using VitaSharp.Core.Capture;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// A GPU dump taken at a resolution scale carries the render targets at that scale (VulkanRenderer.GpuDump.cs,
/// renderer state v2): a replay at the same scale starts from exactly the pixels the recording showed, one at another
/// scale from them resampled. Before, a dump taken at a scale carried no target at all and its replay started from
/// whatever guest memory held (here: zeros, a black first picture).
/// </summary>
[Collection("guest")]
public sealed class GpuDumpResScaleTests(GuestMemoryFixture fx)
{
    /// <summary>Keeps the last picture a present tap saw.</summary>
    private sealed class LastPicture : IPresentTap
    {
        public byte[]? Rgba;
        public int W, H;
        public int Count;

        public void OnPresent(in PresentInfo info, ReadOnlySpan<byte> rgba, int w, int h)
        {
            Rgba = rgba[..(w * h * 4)].ToArray();
            W = w;
            H = h;
            Count++;
        }
    }

    private static VulkanRendererOptions At(float scale, IPresentTap tap)
    {
        VulkanRendererOptions options = FlashScene.Options(tap);
        options.ResScale = scale;
        return options;
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long sum = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        }

        return sum / (3.0 * a.Length / 4);
    }

    [SoftwareVulkanTheory]
    [InlineData(2f)]
    [InlineData(1f)]
    public void ADumpAtTwiceTheResolutionReplaysFromItsScaledTargets(float replayScale)
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "resscale");
        string dump = Path.Combine(h.Root, "scaled.vsgd");
        var live = new LastPicture();
        uint data = 0;
        // no frame inside the dump: its pictures are the targets carried from the start
        FlashScene.Record(h, new VulkanRenderer(At(2f, live)), dump, [], scene => data = scene.Workload.Data);
        Assert.NotNull(live.Rgba);
        // guest memory holds nothing of the picture: only the carried targets can show it
        GuestMemory.Fill(data, 0, FlashScene.W * FlashScene.H * 4);

        var replayed = new LastPicture();
        FlashScene.Replay(dump, At(replayScale, replayed));
        Assert.True(replayed.Count > 0, "the replay presented nothing");
        Assert.Equal((live.W, live.H), (replayed.W, replayed.H));
        double difference = MeanDifference(live.Rgba!, replayed.Rgba!);
        // the same scale: the same pixels; native: the scaled pixels resampled (the HUD's edges move by a pixel)
        Assert.True(replayScale == 2f ? difference < 0.5 : difference < 8, $"mean channel difference {difference:F2} at the replay scale {replayScale}");
        // the picture itself, not a black one: blue background, red HUD in a corner
        int blue = 0, red = 0;
        for (int i = 0; i < replayed.Rgba!.Length; i += 4)
        {
            if (replayed.Rgba[i + 2] > 200 && replayed.Rgba[i] < 60)
            {
                blue++;
            }
            else if (replayed.Rgba[i] > 200 && replayed.Rgba[i + 2] < 60)
            {
                red++;
            }
        }

        Assert.True(blue > replayed.Rgba.Length / 4 / 2 && red > 20, $"{blue} blue and {red} red pixels");
    }
}
