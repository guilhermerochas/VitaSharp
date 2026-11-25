using VitaSharp.Core.Capture;
using VitaSharp.Desktop;

namespace VitaSharp.Tests;

/// <summary>
/// <c>run --flicker-check</c> (the measure of a flicker from a save state, without playing): the presented frames go
/// through the capture report's rules (<see cref="FrameFindings"/>), and the end of the run reports the flashes and
/// writes findings.txt in the format of <c>replay-gpu --findings</c>. The case that made it: a wall of Assassin's Creed III
/// Liberation (one part of the picture) dark in every other frame.
/// </summary>
public sealed class FlickerCheckTests : IDisposable
{
    private const int Width = 96, Height = 54;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-flicker-" + Guid.NewGuid().ToString("N"));

    public FlickerCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A still gradient; with <paramref name="darkWall"/> the top-right quarter (a wall) is black.</summary>
    private static byte[] Picture(bool darkWall)
    {
        byte[] px = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Width + x) * 4;
                bool wall = darkWall && x >= Width / 2 && y < Height / 4;
                px[o] = wall ? (byte)0 : (byte)(60 + x);
                px[o + 1] = wall ? (byte)0 : (byte)(40 + y * 2);
                px[o + 2] = wall ? (byte)0 : (byte)90;
                px[o + 3] = 255;
            }
        }

        return px;
    }

    private static FrameFindings Run(int frames, Func<int, bool> darkWall)
    {
        var findings = new FrameFindings();
        for (int i = 0; i < frames; i++)
        {
            findings.Add(new PresentInfo { Present = 1000 + i, Flip = -1, PresentNs = i * 33_333_333L }, Picture(darkWall(i)), Width, Height);
        }

        return findings;
    }

    [Fact]
    public void AWallDarkInEveryOtherFrameIsCountedAsFlashesAndWrittenToFindings()
    {
        FrameFindings findings = Run(40, i => i % 2 == 1);

        string summary = Runner.ReportFlicker(findings, 6, _dir);

        // in an A-B-A-B run every frame differs from both neighbours, which match each other: all but the first and the
        // last (one neighbour each) are flashes, as in the capture report
        Assert.StartsWith("FLICKER 40 frames: 38 flash(es)", summary);
        Assert.Contains("presents 1001 1002 1003", summary);
        string written = File.ReadAllText(Path.Combine(_dir, CaptureVerify.FindingsFileName));
        Assert.Contains("flash", written);
        Assert.Contains("1001", written);
    }

    [Fact]
    public void AStillPictureHasNoFlashes()
    {
        FrameFindings findings = Run(40, _ => false);

        string summary = Runner.ReportFlicker(findings, 6, null);

        Assert.StartsWith("FLICKER 40 frames: 0 flash(es)", summary);
        Assert.False(File.Exists(Path.Combine(_dir, CaptureVerify.FindingsFileName)));
    }

    [Fact]
    public void ASingleDarkFrameIsOneFlashAtItsPresent()
    {
        FrameFindings findings = Run(30, i => i == 12);

        string summary = Runner.ReportFlicker(findings, 6, null);

        Assert.StartsWith("FLICKER 30 frames: 1 flash(es)", summary);
        Assert.EndsWith("presents 1012", summary);
    }
}
