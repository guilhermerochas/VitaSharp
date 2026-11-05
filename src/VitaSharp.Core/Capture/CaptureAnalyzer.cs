using System.Globalization;
using System.IO.Hashing;
using System.Text;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Capture;

/// <summary>
/// Offline analysis of a session capture (<c>vitasharp-perf capture DIR</c>, and automatically at the end of every
/// recording): black frames and black screens, one-frame flashes (whole picture or a part of it), repeated and
/// out-of-order presents, stalls with their cause, audio gaps and clicks. Every finding is lined up with the GPU dump
/// (which dump frame shows it, or that it happened in the pre-roll, before the dump) and with the input the guest read
/// just before. Writes report.txt and, under findings/, a strip of the frames around each finding.
/// </summary>
public static class CaptureAnalyzer
{
    public sealed record Finding(string Kind, int Index, int Count, string Detail);

    sealed class FrameStat
    {
        public FramesFile.Frame F = null!;
        public double Luma, Dark;
        public ulong Hash;
        public byte[]? Thumb; // 96x54 RGBA
    }

    const int TW = VulkanThumbW, TH = VulkanThumbH;
    const int VulkanThumbW = 96, VulkanThumbH = 54;

    public static string Analyze(string dir, bool quiet = false, double threshold = 6)
    {
        dir = Path.GetFullPath(dir);
        string framesPath = Path.Combine(dir, "frames.vsfr");
        var session = ReadSession(Path.Combine(dir, "session.txt"));
        long startNs = long.TryParse(session.GetValueOrDefault("start-ns"), out var sn) ? sn : 0;
        var sb = new StringBuilder();
        sb.AppendLine($"VitaSharp capture report: {Path.GetFileName(dir)}");
        sb.AppendLine($"title: {session.GetValueOrDefault("title-id", "?")} {session.GetValueOrDefault("title", "")}");
        if (session.GetValueOrDefault("recorded") is { } rec) sb.AppendLine($"recorded: {rec}");
        var findings = new List<Finding>();
        var stats = new List<FrameStat>();
        if (File.Exists(framesPath))
        {
            foreach (var (f, pic) in FramesFile.ReadAll(framesPath))
            {
                var st = new FrameStat { F = f };
                if (pic is { } pv)
                {
                    var (px, w, h) = pv;
                    st.Hash = XxHash64.HashToUInt64(px);
                    (st.Luma, st.Dark) = Brightness(px);
                    st.Thumb = Downscale(px, w, h, TW, TH);
                }
                stats.Add(st);
            }
            FindFrames(stats, findings, threshold, ReadInput(dir));
        }
        else sb.AppendLine("no frames.vsfr: no presented frames in this capture");

        // the GPU dump's presents
        var dumpPresents = new Dictionary<long, int>();
        string dumpPath = Path.Combine(dir, "gpu" + GpuDumpFormat.Extension);
        if (File.Exists(dumpPath))
        {
            try
            {
                using var r = new GpuDumpReader(dumpPath);
                int k = 0;
                while (r.Next())
                    if (r.Kind == GpuDumpRecord.Present) dumpPresents[r.PresentInfo.Present] = ++k;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException) { sb.AppendLine($"GPU dump unreadable past frame {dumpPresents.Count}: {e.Message}"); }
        }

        var input = ReadInput(dir);


        // overview
        if (stats.Count > 0)
        {
            var live = stats.Where(s => !s.F.PreRoll).ToList();
            int pre = stats.Count - live.Count;
            var iv = stats.Skip(1).Select((s, i) => (s.F.Info.PresentNs - stats[i].F.Info.PresentNs) / 1e6).Where(v => v > 0).OrderBy(v => v).ToList();
            double med = iv.Count > 0 ? iv[iv.Count / 2] : 0;
            int restarts = stats.Skip(1).Select((s, i) => s.F.Info.Present < stats[i].F.Info.Present ? 1 : 0).Sum();
            double span = (stats[^1].F.Info.PresentNs - stats[0].F.Info.PresentNs) / 1e9;
            sb.AppendLine(FormattableString.Invariant($"frames: {stats.Count} presented ({pre} in the pre-roll, before the recording start), {span:F1} s, median interval {med:F1} ms ({(med > 0 ? 1000 / med : 0):F1} fps), presents {stats[0].F.Info.Present}-{stats[^1].F.Info.Present}{(restarts > 0 ? $" (the renderer restarted {restarts} times: LoadExec or state loads; present numbers count again from 1)" : "")}, flips {stats[0].F.Info.Flip}-{stats[^1].F.Info.Flip}"));
            sb.AppendLine(dumpPresents.Count > 0
                ? $"GPU dump: {dumpPresents.Count} frames, presents {dumpPresents.Keys.Min()}-{dumpPresents.Keys.Max()}"
                : "GPU dump: none");
            sb.AppendLine($"input: {input.Count(e => e.Kind == "pad")} pad, {input.Count(e => e.Kind == "touch")} touch, {input.Count(e => e.Kind == "motion")} motion, {input.Count(e => e.Kind == "rng")} random, {input.Count(e => e.Kind == "key")} hotkey events");
            sb.AppendLine(FpsLine(stats));
        }

        sb.AppendLine();
        sb.AppendLine($"Findings ({findings.Count}):");
        if (findings.Count == 0) sb.AppendLine("  none in the presented frames");
        string findDir = Path.Combine(dir, "findings");
        if (Directory.Exists(findDir)) foreach (var old in Directory.GetFiles(findDir, "*.png")) File.Delete(old);
        var strips = new List<(Finding F, string Path)>();
        foreach (var f in findings.OrderBy(f => f.Index))
        {
            var s = stats[f.Index];
            var p = s.F.Info;
            double atS = (p.PresentNs - startNs) / 1e9;
            // present numbers restart with a new renderer (LoadExec, state load): pre-roll frames never match the dump
            string where = !s.F.PreRoll && dumpPresents.TryGetValue(p.Present, out int dk) ? $"GPU dump frame {dk} (replay-gpu --frame={dk})"
                : s.F.PreRoll ? "pre-roll, before the GPU dump: re-run from state_preroll.vss to reproduce it"
                : dumpPresents.Count > 0 && p.Present > dumpPresents.Keys.Max() ? "after the GPU dump ended"
                : "not in the GPU dump";
            sb.AppendLine(FormattableString.Invariant($"  [{f.Kind}] present {p.Present} flip {p.Flip} at {atS:+0.000;-0.000} s{(f.Count > 1 ? $", {f.Count} frames" : "")}: {f.Detail}"));
            sb.AppendLine($"      {where}");
            if (f.Kind == "stall" && f.Index > 0)
            {
                long a = stats[f.Index - 1].F.Info.PresentNs;
                var during = input.Where(e => e.Kind is "mark" or "key" && e.Ns > a - 150_000_000 && e.Ns <= p.PresentNs).Select(e => $"{e.Kind} {e.Data}").ToList();
                if (during.Count > 0) sb.AppendLine($"      during: {string.Join(", ", during)} (an emulator action, not the game)");
            }
            if (NearInput(input, p.PresentNs) is { } ni) sb.AppendLine($"      input: {ni}");
            if (s.Thumb != null && strips.Count < 40) strips.Add((f, Path.Combine(findDir, $"{f.Kind}_p{p.Present}.png")));
        }

        if (strips.Count > 0)
        {
            Directory.CreateDirectory(findDir);
            WriteStrips(framesPath, strips);
        }

        // audio
        string audioDir = Path.Combine(dir, "audio");
        var wavs = Directory.Exists(audioDir) ? Directory.GetFiles(audioDir, "*.wav").OrderBy(x => x).ToList() : new List<string>();
        if (File.Exists(Path.Combine(dir, "audio_mix.wav"))) wavs.Insert(0, Path.Combine(dir, "audio_mix.wav"));
        sb.AppendLine();
        sb.AppendLine($"Audio ({wavs.Count} files):");
        foreach (var w in wavs.Take(64))
        {
            try
            {
                var c = CaptureAudio.Analyze(w);
                sb.Append(FormattableString.Invariant($"  {c.File}: {c.Seconds:F1} s at {c.Rate} Hz, peak {c.PeakDb:F1} dBFS, rms {c.RmsDb:F1} dBFS"));
                if (c.ClippedPct > 0.01) sb.Append(FormattableString.Invariant($", CLIPPED {c.ClippedPct:F2}%"));
                if (c.Gaps > 0) sb.Append(FormattableString.Invariant($", {c.Gaps} silent gaps inside the sound (longest {c.LongestGapMs:F0} ms; at {string.Join(" ", c.GapTimesS.Take(8).Select(t => t.ToString("F2", CultureInfo.InvariantCulture)))} s)"));
                if (c.Clicks > 0) sb.Append(FormattableString.Invariant($", {c.Clicks} clicks (at {string.Join(" ", c.ClickTimesS.Take(8).Select(t => t.ToString("F2", CultureInfo.InvariantCulture)))} s)"));
                if (c.RmsDb < -90) sb.Append(", SILENT");
                sb.AppendLine();
            }
            catch (Exception e) when (e is IOException or InvalidDataException) { sb.AppendLine($"  {Path.GetFileName(w)}: {e.Message}"); }
        }

        sb.AppendLine();
        sb.AppendLine("Re-run (desktop, headless): VitaSharp.Desktop rerun \"" + dir + "\"" +
                      (File.Exists(Path.Combine(dir, "state_preroll.vss")) ? "   (--from=start to begin at the recording start instead of the pre-roll)" : ""));
        if (File.Exists(dumpPath)) sb.AppendLine($"GPU replay: VitaSharp.Desktop replay-gpu \"{dumpPath}\"");
        string text = sb.ToString();
        File.WriteAllText(Path.Combine(dir, "report.txt"), text);
        if (!quiet) Console.Write(text);
        return text;
    }

    /// <summary>
    /// A re-run against the capture it re-ran: each original frame from the re-run's start on is paired with the
    /// re-run frame presented closest to the same guest time, and the pictures compared (mean channel difference of
    /// 96x54 thumbnails). Writes compare.txt into the re-run folder and returns its summary line.
    /// </summary>
    public static string Compare(string original, string rerun, double threshold = 6)
    {
        static List<(long Us, long Present, byte[] T)> Load(string dir) =>
            FramesFile.ReadAll(Path.Combine(dir, "frames.vsfr"))
                .Where(x => x.Picture != null && x.Frame.Info.Flip >= 0)
                .Select(x => (x.Frame.Info.GuestUs, x.Frame.Info.Present, Downscale(x.Picture!.Value.Rgba, x.Picture.Value.W, x.Picture.Value.H, TW, TH)))
                .ToList();
        var a = Load(original);
        var b = Load(rerun);
        var sb = new StringBuilder("# originalPresent rerunPresent guestTimeS dtMs diff\n");
        if (a.Count == 0 || b.Count == 0) return "compare: no frames";
        long from = b[0].Us, to = b[^1].Us;
        int n = 0, same = 0;
        double firstDiverge = double.NaN;
        var diffs = new List<double>();
        int j = 0;
        foreach (var (us, present, t) in a)
        {
            if (us < from || us > to) continue;
            while (j + 1 < b.Count && Math.Abs(b[j + 1].Us - us) <= Math.Abs(b[j].Us - us)) j++;
            double d = Diff(t, b[j].T);
            n++;
            diffs.Add(d);
            if (d < threshold) same++;
            else if (double.IsNaN(firstDiverge)) firstDiverge = us / 1e6;
            sb.AppendLine(FormattableString.Invariant($"{present} {b[j].Present} {us / 1e6:F3} {(b[j].Us - us) / 1000.0:F1} {d:F2}"));
        }
        diffs.Sort();
        string summary = n == 0 ? "compare: the re-run does not overlap the capture in guest time"
            : FormattableString.Invariant($"compare: {n} original frames from guest time {from / 1e6:F2} s to {to / 1e6:F2} s; {100.0 * same / n:F0}% look the same in the re-run (mean difference < {threshold}), median difference {diffs[n / 2]:F2}; ") +
              (double.IsNaN(firstDiverge) ? "no divergence" : FormattableString.Invariant($"first different picture at guest time {firstDiverge:F2} s"));
        sb.Insert(0, "# " + summary + "\n");
        File.WriteAllText(Path.Combine(rerun, "compare.txt"), sb.ToString());
        return summary;
    }

    static List<InputEvent> ReadInput(string dir) => File.Exists(Path.Combine(dir, "input.tsv"))
        ? File.ReadLines(Path.Combine(dir, "input.tsv")).Select(InputEvent.Parse).Where(e => e != null).Select(e => e!.Value).ToList()
        : new List<InputEvent>();

    static Dictionary<string, string> ReadSession(string path)
    {
        var d = new Dictionary<string, string>();
        if (!File.Exists(path)) return d;
        foreach (var line in File.ReadLines(path))
        {
            int c = line.IndexOf(": ", StringComparison.Ordinal);
            if (c > 0 && !line.StartsWith(' ')) d.TryAdd(line[..c], line[(c + 2)..]);
        }
        return d;
    }

    /// <summary>Mean luma (0-255) and the fraction of pixels darker than 16.</summary>
    static (double Luma, double Dark) Brightness(byte[] px)
    {
        long sum = 0, dark = 0, n = 0;
        for (int i = 0; i < px.Length; i += 8) // every other pixel
        {
            int l = (px[i] * 77 + px[i + 1] * 150 + px[i + 2] * 29) >> 8;
            sum += l;
            if (l < 16) dark++;
            n++;
        }
        return (n == 0 ? 0 : sum / (double)n, n == 0 ? 0 : dark / (double)n);
    }

    static byte[] Downscale(byte[] src, int w, int h, int tw, int th)
    {
        var dst = new byte[tw * th * 4];
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                int x0 = x * w / tw, x1 = Math.Max(x0 + 1, (x + 1) * w / tw), y0 = y * h / th, y1 = Math.Max(y0 + 1, (y + 1) * h / th);
                int r = 0, g = 0, b = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int o = (sy * w + sx) * 4;
                        r += src[o]; g += src[o + 1]; b += src[o + 2]; n++;
                    }
                int d = (y * tw + x) * 4;
                dst[d] = (byte)(r / n); dst[d + 1] = (byte)(g / n); dst[d + 2] = (byte)(b / n); dst[d + 3] = 255;
            }
        return dst;
    }

    static double Diff(byte[]? a, byte[]? b)
    {
        if (a == null || b == null || a.Length != b.Length) return -1;
        long s = 0;
        for (int i = 0; i < a.Length; i += 4) s += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        return s / (3.0 * a.Length / 4);
    }

    static bool IsBlack(FrameStat s) => s.Thumb != null && s.Luma < 10 && s.Dark > 0.97;

    static void FindFrames(List<FrameStat> st, List<Finding> findings, double threshold, List<InputEvent> input)
    {
        int n = st.Count;
        // black frames: a run of 1-3 between normal frames is a black flash, a longer run a black screen
        for (int i = 0; i < n;)
        {
            if (!IsBlack(st[i])) { i++; continue; }
            int j = i;
            while (j + 1 < n && IsBlack(st[j + 1])) j++;
            int count = j - i + 1;
            bool before = i > 0 && !IsBlack(st[i - 1]) && st[i - 1].Thumb != null, after = j + 1 < n && st[j + 1].Thumb != null;
            double ms = (st[j].F.Info.PresentNs - st[i].F.Info.PresentNs) / 1e6 + st[j].F.Info.IntervalMs;
            if (count <= 3 && before && after)
                findings.Add(new Finding("black-flash", i, count, FormattableString.Invariant($"{count} black frame(s) between normal pictures (luma {st[i].Luma:F1})")));
            else if (ms < 1000 && before && after)
                findings.Add(new Finding("black-transition", i, count, FormattableString.Invariant($"(info) black for {count} frames ({ms / 1000:F2} s) between pictures: a fade or a loading cut, unless it should not be there")));
            else
                findings.Add(new Finding("black-screen", i, count, FormattableString.Invariant($"black for {count} frames ({ms / 1000:F2} s){(i == 0 ? ", from the first captured frame" : "")}{(j == n - 1 ? ", to the last captured frame" : "")}")));
            i = j + 1;
        }

        // one-frame flashes (the whole picture or one cell of a 4x4 grid): the rule of vitasharp-perf flicker
        var det = new FlickerDetector(threshold);
        for (int i = 0; i < n; i++)
        {
            if (st[i].Thumb is not { } t) continue;
            if (det.Add(i, t, TW, TH) is { } f)
            {
                int k = (int)f.Frame;
                if (IsBlack(st[k])) continue; // reported as a black flash
                findings.Add(new Finding("flash", k, 1, $"differs from both neighbours, which match each other: {det.Describe(f).Replace($"frame {f.Frame}: ", "")}"));
            }
        }

        // presentation order: guest flip numbers going back, a flip presented twice, flips the renderer skipped
        for (int i = 1; i < n; i++)
        {
            var a = st[i - 1].F.Info; var b = st[i].F.Info;
            if (a.Flip < 0 || b.Flip < 0) continue;
            if (b.Flip < a.Flip) findings.Add(new Finding("out-of-order", i, 1, $"guest flip {b.Flip} presented after flip {a.Flip}"));
            else if (b.Flip == a.Flip) findings.Add(new Finding("re-presented", i, 1, $"guest flip {b.Flip} presented twice (renderer presents {a.Present} and {b.Present})"));
            else if (b.Flip > a.Flip + 1 && b.Present == a.Present + 1)
                findings.Add(new Finding("flips-skipped", i, (int)(b.Flip - a.Flip - 1), $"guest flips {a.Flip + 1}-{b.Flip - 1} never reached the screen"));
        }

        // pictures out of order: swapping frames i and i+1 makes the sequence much smoother (a decode-order or
        // buffer-order bug), while the frames do move
        for (int i = 1; i + 2 < n; i++)
        {
            double d01 = Diff(st[i - 1].Thumb, st[i].Thumb), d12 = Diff(st[i].Thumb, st[i + 1].Thumb), d23 = Diff(st[i + 1].Thumb, st[i + 2].Thumb);
            double d02 = Diff(st[i - 1].Thumb, st[i + 1].Thumb), d13 = Diff(st[i].Thumb, st[i + 2].Thumb);
            if (d01 < 0 || d12 < 0 || d23 < 0 || d02 < 0 || d13 < 0) continue;
            double asIs = d01 + d12 + d23, swapped = d02 + d12 + d13;
            if (asIs > 3 * threshold && swapped < 0.6 * asIs && d02 < d01 * 0.5 && d13 < d23 * 0.5)
            {
                findings.Add(new Finding("picture-order", i, 2, FormattableString.Invariant($"presents {st[i].F.Info.Present} and {st[i + 1].F.Info.Present} look swapped (path {asIs:F1} as shown, {swapped:F1} swapped)")));
                i++;
            }
        }

        // the same picture for a long time while flips go on (a frozen picture), and stalls (a long present interval)
        for (int i = 0; i < n;)
        {
            int j = i;
            while (j + 1 < n && st[j + 1].Thumb != null && st[i].Thumb != null && st[j + 1].Hash == st[i].Hash) j++;
            double ms = (st[j].F.Info.PresentNs - st[i].F.Info.PresentNs) / 1e6;
            if (ms >= 1500 && !IsBlack(st[i]) && st[j].F.Info.Flip - st[i].F.Info.Flip >= 30)
            {
                long a = st[i].F.Info.PresentNs, b = st[j].F.Info.PresentNs;
                int presses = input.Count(e => e.Kind is "pad" or "touch" && e.Ns > a && e.Ns < b);
                findings.Add(presses > 0
                    ? new Finding("frozen-picture", i, j - i + 1, FormattableString.Invariant($"the same picture for {ms / 1000:F1} s while the guest flipped {st[j].F.Info.Flip - st[i].F.Info.Flip} times and read {presses} input changes: the game ignored the input, or it does not draw what changed"))
                    : new Finding("static-picture", i, j - i + 1, FormattableString.Invariant($"(info) the same picture for {ms / 1000:F1} s ({st[j].F.Info.Flip - st[i].F.Info.Flip} flips), no input meanwhile")));
            }
            i = j + 1;
        }
        var iv = new List<double>();
        for (int i = 1; i < n; i++) iv.Add((st[i].F.Info.PresentNs - st[i - 1].F.Info.PresentNs) / 1e6);
        var sorted = iv.Where(v => v > 0).OrderBy(v => v).ToList();
        double med = sorted.Count > 0 ? sorted[sorted.Count / 2] : 16.7;
        double limit = Math.Max(3 * med, med + 50);
        for (int i = 1; i < n; i++)
        {
            double d = iv[i - 1];
            if (d < limit) continue;
            var p = st[i].F.Info;
            bool restarted = p.Present < st[i - 1].F.Info.Present;
            // a cause longer than the interval is a ledger total carried over a renderer change, not this frame's
            string cause = restarted ? "the renderer was replaced here (LoadExec or a state load): boot of the new process or the load itself"
                : string.IsNullOrEmpty(p.Hitch) || p.HitchMs > d + 50 ? "no stall cause recorded on the GPU worker (the guest itself was slow, or the host)"
                : p.Hitch == "guestGpuWait" ? FormattableString.Invariant($"the guest waited for the GPU {p.HitchMs:F1} ms (GPU work or the renderer's thread was slow)")
                : FormattableString.Invariant($"{p.Hitch} {p.HitchMs:F1} ms");
            findings.Add(new Finding("stall", i, 1, FormattableString.Invariant($"{d:F0} ms between presents (median {med:F1} ms); GPU {p.GpuMs:F1} ms, {p.Draws} draws, new shaders {p.NewShaders}, new pipelines {p.NewPipelines}; cause: {cause}")));
        }
    }

    static string FpsLine(List<FrameStat> st)
    {
        // frames presented in each second (the dips)
        long t0 = st[0].F.Info.PresentNs;
        var perSecond = st.GroupBy(s => (s.F.Info.PresentNs - t0) / 1_000_000_000).OrderBy(g => g.Key).Select(g => g.Count()).ToList();
        if (perSecond.Count > 2) perSecond = perSecond.Skip(1).SkipLast(1).ToList();
        if (perSecond.Count == 0) return "fps per second: -";
        return $"fps per second: min {perSecond.Min()}, median {perSecond.OrderBy(x => x).ElementAt(perSecond.Count / 2)}, max {perSecond.Max()}: {string.Join(' ', perSecond.Take(120))}";
    }

    static string? NearInput(List<InputEvent> input, long ns)
    {
        InputEvent? best = null;
        foreach (var e in input)
        {
            if (e.Kind is "motion" or "rng") continue;
            if (e.Ns > ns) break;
            if (ns - e.Ns <= 2_000_000_000) best = e;
        }
        if (best is not { } b) return null;
        return FormattableString.Invariant($"{(ns - b.Ns) / 1e6:F0} ms after {b.Kind} {DescribeInput(b)} (guest frame {b.Flip})");
    }

    static string DescribeInput(InputEvent e)
    {
        if (e.Kind != "pad") return e.Data;
        var p = e.Data.Split(' ');
        if (p.Length < 6 || !uint.TryParse(p[1], NumberStyles.HexNumber, null, out uint btn)) return e.Data;
        var names = Enum.GetValues<Host.VitaButtons>().Where(v => v != 0 && ((uint)v & ((uint)v - 1)) == 0 && (btn & (uint)v) != 0).Select(v => v.ToString()).Distinct();
        return $"port {p[0]} buttons [{string.Join(' ', names)}] sticks {p[2]},{p[3]} {p[4]},{p[5]}";
    }

    /// <summary>Each finding's frame with two before and two after, side by side (240x136 each), the finding in the
    /// middle with a red border. One pass over the file decodes the pictures needed.</summary>
    static void WriteStrips(string framesPath, List<(Finding F, string Path)> strips)
    {
        const int cw = 240, ch = 136, cols = 5;
        var need = new HashSet<int>(strips.SelectMany(s => Enumerable.Range(s.F.Index - 2, cols)));
        var cells = new Dictionary<int, byte[]>();
        int i = 0;
        foreach (var (_, pic) in FramesFile.ReadAll(framesPath))
        {
            if (need.Contains(i) && pic is { } p) cells[i] = Downscale(p.Rgba, p.W, p.H, cw, ch);
            i++;
        }
        foreach (var (f, path) in strips)
        {
            var img = new byte[cw * cols * ch * 4];
            for (int c = 0; c < cols; c++)
            {
                if (!cells.TryGetValue(f.Index - 2 + c, out var cell)) continue;
                for (int y = 0; y < ch; y++)
                    cell.AsSpan(y * cw * 4, cw * 4).CopyTo(img.AsSpan((y * cw * cols + c * cw) * 4));
                if (c == 2)
                    for (int y = 0; y < ch; y++)
                        for (int x = 0; x < cw; x++)
                            if (y < 3 || y >= ch - 3 || x < 3 || x >= cw - 3)
                            {
                                int o = (y * cw * cols + c * cw + x) * 4;
                                img[o] = 255; img[o + 1] = 0; img[o + 2] = 0;
                            }
            }
            CapturePng.Write(path, img, cw * cols, ch);
        }
    }
}