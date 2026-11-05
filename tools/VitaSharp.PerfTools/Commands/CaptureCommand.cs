using VitaSharp.Core.Capture;

namespace VitaSharp.PerfTools;

/// <summary>
/// <c>capture &lt;folder|zip&gt; [--threshold 6] [--frames DIR] [--rerun]</c>: analyzes a "Gravar tudo" session capture
/// (docs/notes/session-capture.md): black frames, flashes, repeated / out-of-order presents, stalls and audio, lined up
/// with the GPU dump and the input (Core/Capture/CaptureAnalyzer.cs). Writes report.txt and findings/*.png in the
/// folder. <c>--frames DIR</c> also writes every presented frame as frame_PRESENT.png (for <c>flicker</c> or a
/// viewer); <c>--rerun</c> prints the re-run command line. Exit code 1 when a finding that is not (info) is reported.
/// </summary>
sealed class CaptureCommand : IPerfCommand
{
    public string Name => "capture";
    public string Summary => "analyze a Gravar tudo capture (frames, timing, GPU dump, input, audio)";

    public void PrintUsage()
    {
        Console.WriteLine("vitasharp-perf capture <folder|zip> [--threshold 6] [--frames <dir>] [--rerun]");
        Console.WriteLine("  writes <folder>/report.txt and <folder>/findings/*.png (a zip is extracted next to it first)");
        Console.WriteLine("  --frames <dir>  also write every presented frame as frame_<present>.png");
        Console.WriteLine("  --rerun         print the command that re-runs the session headless from its save state");
        Console.WriteLine("  --compare <original>  <folder> is a re-run (original/reruns/...): pair its frames with the original's by guest time");
    }

    public int Run(PerfArgs a)
    {
        if (a.Positional.Count < 1) throw new PerfArgsException("capture: a capture folder (or its .zip) is required");
        string path = Path.GetFullPath(a.Positional[0]);
        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            string to = Path.GetDirectoryName(path)!;
            string folder = Path.Combine(to, Path.GetFileNameWithoutExtension(path));
            if (!Directory.Exists(folder)) System.IO.Compression.ZipFile.ExtractToDirectory(path, to);
            path = folder;
        }
        if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "session.txt")))
            throw new PerfArgsException($"capture: {path} is not a capture folder (no session.txt)");
        string report = CaptureAnalyzer.Analyze(path, quiet: false, threshold: a.Double("--threshold", 6));
        if (a.Get("--frames") is { } framesDir)
        {
            Directory.CreateDirectory(framesDir);
            int n = 0;
            foreach (var (f, pic) in FramesFile.ReadAll(Path.Combine(path, "frames.vsfr")))
                if (pic is { } p) { CapturePng.Write(Path.Combine(framesDir, $"frame_{f.Info.Present}.png"), p.Rgba, p.W, p.H); n++; }
            Console.WriteLine($"{n} frames written to {Path.GetFullPath(framesDir)}");
        }
        if (a.Has("--rerun")) Console.WriteLine($"VitaSharp.Desktop rerun \"{path}\"");
        // a re-run folder (DIR/reruns/X) against the capture it re-ran
        if (a.Get("--compare") is { } original) Console.WriteLine(CaptureAnalyzer.Compare(Path.GetFullPath(original), path));
        bool problems = report.Split('\n').Any(l => l.TrimStart().StartsWith('[') && !l.Contains("(info)"));
        return problems ? 1 : 0;
    }
}
