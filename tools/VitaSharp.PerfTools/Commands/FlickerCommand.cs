using System.Text;
using VitaSharp.Core.Util;

namespace VitaSharp.PerfTools;

/// <summary>
/// <c>flicker &lt;dir&gt; [--threshold 6]</c>: over consecutive frame_N.png (a replay-gpu output, a test run, or phone
/// screen-recording frames extracted with ffmpeg), flags frame N when it differs strongly from both neighbours (mean
/// channel difference above the threshold) while the neighbours match each other (below a third of it): a one-frame
/// flash or a stale frame, not motion. Writes flicker.txt; a flagged frame of a replay is bisected with
/// <c>replay-gpu --draw-limit=N@F --trace-draws=F</c>. Exit code 1 when a frame is flagged.
/// </summary>
sealed class FlickerCommand : IPerfCommand
{
    public string Name => "flicker";
    public string Summary => "flag one-frame flashes and stale frames in a frame_N.png sequence";

    public void PrintUsage()
    {
        Console.WriteLine("vitasharp-perf flicker <dir> [--threshold 6]");
        Console.WriteLine("  frame_N.png files in <dir> (consecutive N); writes <dir>/flicker.txt");
    }

    public int Run(PerfArgs a)
    {
        if (a.Positional.Count < 1) throw new PerfArgsException("flicker: a folder of frame_N.png files is required");
        string dir = Path.GetFullPath(a.Positional[0]);
        double threshold = a.Double("--threshold", 6);
        var files = Directory.GetFiles(dir, "frame_*.png")
            .Select(f => (Path: f, N: long.TryParse(Path.GetFileNameWithoutExtension(f)[6..], out long v) ? v : -1)).Where(x => x.N >= 0).OrderBy(x => x.N).ToArray();
        var sb = new StringBuilder("# frame prev-diff next-diff neighbours-diff\n");
        int flagged = 0;
        var img = new (byte[] Rgba, int W, int H)[3];
        for (int i = 1; i + 1 < files.Length; i++)
        {
            if (files[i + 1].N - files[i - 1].N != 2) continue; // only consecutive frames
            img[0] = PngReader.Read(files[i - 1].Path); img[1] = PngReader.Read(files[i].Path); img[2] = PngReader.Read(files[i + 1].Path);
            double prev = Mean(img[0], img[1]), next = Mean(img[1], img[2]), around = Mean(img[0], img[2]);
            if (prev > threshold && next > threshold && around < threshold / 3)
            {
                flagged++;
                sb.AppendLine($"frame_{files[i].N} {prev:F2} {next:F2} {around:F2}");
            }
        }
        string summary = $"FLICKER {files.Length} frames in {dir}: {flagged} flagged (threshold {threshold})";
        sb.Insert(0, "# " + summary + "\n");
        File.WriteAllText(Path.Combine(dir, "flicker.txt"), sb.ToString());
        Console.WriteLine(summary);
        return flagged == 0 ? 0 : 1;
    }

    static double Mean((byte[] Rgba, int W, int H) a, (byte[] Rgba, int W, int H) b)
    {
        if (a.W != b.W || a.H != b.H) return 255;
        long sum = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
            sum += Math.Abs(a.Rgba[i] - b.Rgba[i]) + Math.Abs(a.Rgba[i + 1] - b.Rgba[i + 1]) + Math.Abs(a.Rgba[i + 2] - b.Rgba[i + 2]);
        return sum / (3.0 * a.Rgba.Length / 4);
    }
}
