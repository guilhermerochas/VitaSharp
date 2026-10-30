using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>Debug-mode per-frame anomaly rules (FrameAnomalyDetector) and the colour metrics they run on.</summary>
public class FrameAnomalyDetectorTests
{
    static FrameMetrics Normal(float mean = 0.4f, ulong hash = 0) => new() { Mean = mean, Black = 0.01f, Hash = hash };
    static FrameMetrics Green(ulong hash) => new() { Mean = 0.4f, Green = 0.5f, Hash = hash };

    static List<FrameAnomalyDetector.Event> Run(FrameAnomalyDetector d, IEnumerable<FrameMetrics> frames, long first = 1)
    {
        var all = new List<FrameAnomalyDetector.Event>();
        long p = first;
        foreach (var m in frames) all.AddRange(d.Feed(p++, m));
        return all;
    }

    static IEnumerable<FrameMetrics> Seq(int n, Func<int, FrameMetrics> f) => Enumerable.Range(0, n).Select(f);

    [Fact]
    public void TwoFrameSaturatedSpikeCaptures()
    {
        var d = new FrameAnomalyDetector();
        var frames = Seq(10, i => Normal(hash: (ulong)i)).Concat([Green(100), Green(101)]).Concat(Seq(10, i => Normal(hash: (ulong)(200 + i))));
        var ev = Run(d, frames);
        var hit = Assert.Single(ev, e => e.Kind == "saturated" && e.Capture);
        Assert.Equal(12, hit.Present); // confirmed on the second green frame
        Assert.Contains(ev, e => e.Kind == "saturated" && e.Detail == "cleared");
    }

    [Fact]
    public void SingleFrameSpikeIsIgnored()
    {
        var d = new FrameAnomalyDetector();
        var ev = Run(d, Seq(10, i => Normal(hash: (ulong)i)).Concat([Green(50)]).Concat(Seq(10, i => Normal(hash: (ulong)(60 + i)))));
        Assert.DoesNotContain(ev, e => e.Capture);
    }

    [Fact]
    public void AlternatingSpikeCaptures()
    {
        var d = new FrameAnomalyDetector();
        var ev = Run(d, [Normal(hash: 1), Green(2), Normal(hash: 3), Green(4), Normal(hash: 5)]);
        Assert.Single(ev, e => e.Kind == "saturated" && e.Capture);
    }

    [Fact]
    public void MagentaSpikeCaptures()
    {
        var d = new FrameAnomalyDetector();
        var pink = new FrameMetrics { Mean = 0.5f, Magenta = 0.3f };
        var ev = Run(d, [Normal(hash: 1), pink with { Hash = 2 }, pink with { Hash = 3 }]);
        Assert.Single(ev, e => e.Kind == "magenta" && e.Capture);
    }

    [Fact]
    public void TransientBlackFlashIsFlickerButCutIsNot()
    {
        var black = new FrameMetrics { Mean = 0f, Black = 1f };
        var d = new FrameAnomalyDetector();
        var ev = Run(d, Seq(5, i => Normal(0.5f, (ulong)i)).Concat([black with { Hash = 10 }, black with { Hash = 11 }]).Concat(Seq(5, i => Normal(0.5f, (ulong)(20 + i)))));
        Assert.Single(ev, e => e.Kind == "flicker" && e.Capture);

        // a cut to a much darker scene that stays is a scene change
        d = new FrameAnomalyDetector();
        ev = Run(d, Seq(5, i => Normal(0.7f, (ulong)i)).Concat(Seq(20, i => Normal(0.1f, (ulong)(30 + i)))));
        Assert.DoesNotContain(ev, e => e.Kind == "flicker");
    }

    /// <summary>A bright still image (Sonic Mania's SEGA screen: a white background with the logo, identical for two
    /// seconds after its fade-in) is not an exposure bug; the same brightness on changing frames is.</summary>
    [Fact]
    public void BrightStillImageIsNotBlownOut()
    {
        var d = new FrameAnomalyDetector();
        var logo = new FrameMetrics { Mean = 0.805f, White = 0.81f };
        // fade-in (changing, getting brighter), then the still logo for 200 frames, then a fade-out
        var fadeIn = Seq(30, i => new FrameMetrics { Mean = 0.02f * i, Hash = (ulong)(100 + i) });
        var still = Seq(200, _ => logo with { Hash = 42 });
        var fadeOut = Seq(30, i => new FrameMetrics { Mean = 0.8f - 0.02f * i, Hash = (ulong)(500 + i) });
        var ev = Run(d, fadeIn.Concat(still).Concat(fadeOut));
        Assert.DoesNotContain(ev, e => e.Kind == "blown-out");

        // a moving scene washed out to white (wrong exposure) still fires
        d = new FrameAnomalyDetector();
        ev = Run(d, Seq(FrameAnomalyDetector.BrightFrames + 10, i => logo with { Hash = (ulong)i }));
        Assert.Single(ev, e => e.Kind == "blown-out" && e.Capture);

        // a mostly still bright screen with an occasional change counts only the changing frames
        d = new FrameAnomalyDetector();
        ev = Run(d, Seq(FrameAnomalyDetector.BrightFrames * 3, i => logo with { Hash = (ulong)(i / 4) }));
        Assert.DoesNotContain(ev, e => e.Kind == "blown-out");
    }

    [Fact]
    public void SlowConditionsNeedToLast()
    {
        var d = new FrameAnomalyDetector();
        var bright = new FrameMetrics { Mean = 0.95f, White = 0.9f };
        var ev = Run(d, Seq(FrameAnomalyDetector.BrightFrames - 1, i => bright with { Hash = (ulong)i }));
        Assert.DoesNotContain(ev, e => e.Kind == "blown-out");
        ev = Run(d, [bright with { Hash = 999 }], FrameAnomalyDetector.BrightFrames);
        Assert.Single(ev, e => e.Kind == "blown-out" && e.Capture);

        d = new FrameAnomalyDetector();
        ev = Run(d, Seq(FrameAnomalyDetector.FrozenFrames + 5, _ => Normal(hash: 7)));
        var frozen = Assert.Single(ev, e => e.Kind == "frozen");
        Assert.False(frozen.Capture);
    }

    [Fact]
    public unsafe void MeasureCountsColours()
    {
        const int w = 8, h = 8;
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            bool top = i < w * h / 2;
            px[i * 4] = 0; px[i * 4 + 1] = (byte)(top ? 255 : 0); px[i * 4 + 2] = 0; px[i * 4 + 3] = 255;
        }
        fixed (byte* p = px)
        {
            var m = FrameMetrics.Measure(p, w, h, step: 1);
            Assert.Equal(0.5f, m.Green, 3);
            Assert.Equal(0.5f, m.Black, 3);
            Assert.True(m.Hits.HasFlag(FrameMetrics.Hit.Saturated));
        }
    }
}
