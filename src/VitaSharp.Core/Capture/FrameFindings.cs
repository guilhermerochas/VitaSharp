using System.IO.Hashing;

namespace VitaSharp.Core.Capture;

/// <summary>
/// The capture report's picture rules as an API: frames in, findings out. Each frame added (a present tap's picture,
/// a replay's frame, a frame of frames.vsfr) is reduced to the analyzer's 96x54 thumbnail, hash and brightness, and
/// <see cref="Findings"/> runs exactly the rules of <c>report.txt</c> (<see cref="CaptureAnalyzer"/>: black flashes
/// and screens, [flash] of the whole picture or one 4x4 cell, presentation order, frozen pictures, stalls). Used by the
/// analyzer itself, by <c>verify</c> (the findings of a replay of the capture's GPU dump against the recording's) and
/// by anything that measures a flicker from frames (a re-run, a run from a save state).
/// <para>It is a <see cref="IPresentTap"/>: set as <c>VulkanRendererOptions.PresentTap</c> it sees every presented
/// frame as a recording does (downscaled to fit 480x272). Thread-safe; ~21 KB kept per frame.</para>
/// </summary>
public sealed class FrameFindings : IPresentTap
{
    private readonly List<CaptureAnalyzer.FrameStat> _frames = new();
    private readonly object _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>The lowest and highest present number added (0 and -1 before the first frame).</summary>
    public (long First, long Last) Presents
    {
        get
        {
            lock (_lock)
            {
                if (_frames.Count == 0)
                {
                    return (0, -1);
                }

                long first = long.MaxValue, last = long.MinValue;
                foreach (CaptureAnalyzer.FrameStat frame in _frames)
                {
                    first = Math.Min(first, frame.F.Info.Present);
                    last = Math.Max(last, frame.F.Info.Present);
                }

                return (first, last);
            }
        }
    }

    public void OnPresent(in PresentInfo info, ReadOnlySpan<byte> rgba, int w, int h) => Add(info, rgba, w, h, preRoll: false);

    /// <summary>Adds the next presented frame (RGBA, <paramref name="w"/> x <paramref name="h"/>); an empty picture is a
    /// frame whose timing is known but whose picture is not (the recording's encoder fell behind).</summary>
    public void Add(in PresentInfo info, ReadOnlySpan<byte> rgba, int w, int h, bool preRoll = false)
    {
        var stat = new CaptureAnalyzer.FrameStat
        {
            F = new FramesFile.Frame(info, preRoll ? FramesFile.FlagPreRoll : (byte)0, 0, 0),
        };
        if (!rgba.IsEmpty && w > 0 && h > 0)
        {
            byte[] pixels = rgba[..(w * h * 4)].ToArray();
            stat.Hash = XxHash64.HashToUInt64(pixels);
            (stat.Luma, stat.Dark) = CaptureAnalyzer.Brightness(pixels);
            stat.Thumb = CaptureAnalyzer.Downscale(pixels, w, h, CaptureAnalyzer.TW, CaptureAnalyzer.TH);
        }

        lock (_lock)
        {
            _frames.Add(stat);
        }
    }

    /// <summary>The findings of the frames added so far, in frame order (<paramref name="threshold"/>: the mean channel
    /// difference of a flash, the report's default 6; <paramref name="input"/>: the input log, for frozen pictures and
    /// pauses).</summary>
    public List<CaptureAnalyzer.Finding> Findings(double threshold = 6, IReadOnlyList<InputEvent>? input = null)
    {
        List<CaptureAnalyzer.FrameStat> frames;
        lock (_lock)
        {
            frames = new List<CaptureAnalyzer.FrameStat>(_frames);
        }

        var findings = new List<CaptureAnalyzer.Finding>();
        CaptureAnalyzer.FindFrames(frames, findings, threshold, input ?? Array.Empty<InputEvent>());
        findings.Sort((a, b) => a.Index.CompareTo(b.Index));
        return findings;
    }

    /// <summary>The findings of a capture's frames.vsfr (pictures decoded one at a time).</summary>
    public static List<CaptureAnalyzer.Finding> OfFramesFile(string framesPath, double threshold = 6, IReadOnlyList<InputEvent>? input = null)
    {
        var findings = new FrameFindings();
        foreach ((FramesFile.Frame frame, (byte[] Rgba, int W, int H)? picture) in FramesFile.ReadAll(framesPath))
        {
            if (picture is { } p)
            {
                findings.Add(frame.Info, p.Rgba, p.W, p.H, frame.PreRoll);
            }
            else
            {
                findings.Add(frame.Info, ReadOnlySpan<byte>.Empty, 0, 0, frame.PreRoll);
            }
        }

        return findings.Findings(threshold, input);
    }
}
