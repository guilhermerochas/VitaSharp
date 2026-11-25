using System.Diagnostics;
using System.Text;
using VitaSharp.Core.Capture;

namespace VitaSharp.Desktop;

/// <summary>
/// <c>verify &lt;capture folder|zip&gt; [--out=DIR] [--rerun] [replay options]</c>: is the problem a "Gravar tudo"
/// capture recorded still there with this build? The capture's GPU dump is replayed by this build (a child
/// <c>replay-gpu --findings</c>: the renderer switches are read once per process), the capture report's picture rules
/// and the NaN/Inf scan run over the replayed frames, and every finding of the recording inside the dump is set against
/// them (<see cref="CaptureVerify.Compare"/>): corrigido / ainda presente / mudou, finding by finding and kind by kind.
/// <c>--rerun</c> also re-runs the game logic from the capture's state (<c>rerun</c>) and compares the reports kind by
/// kind (stalls, order, audio gaps and clicks). Nothing of the capture is changed: everything goes into
/// <c>--out</c> (default <c>&lt;capture&gt;/verify</c>). Exit code 0 fixed, 1 still present, 2 changed, 3 nothing to
/// check, 4 the replay failed. See docs/captures.md, "Checking a fix".
/// </summary>
static class VerifyCapture
{
    public static void PrintUsage()
    {
        Console.WriteLine("  verify <capture folder|zip> [--out=<dir>] [--rerun] [replay options]  is the capture's problem still there with this build?");
        Console.WriteLine("                           replays its GPU dump and compares the findings (flash, black frames, NaN/Inf) with the recording's:");
        Console.WriteLine("                           corrigido / ainda presente / mudou (exit 0 / 1 / 2; 3 nothing to check); --rerun also re-runs the logic");
        Console.WriteLine("                           (stalls, order, audio); replay options (--res-scale=, --env=K=V, --msaa=...) go to the replay");
    }

    static readonly string[] s_ownOptions = ["--out=", "--rerun", "--keep-frames"];

    public static int Run(string capture, string[] options)
    {
        string dir = Path.GetFullPath(capture);
        string outDir = Runner.Opt(options, "--out") is { } o ? Path.GetFullPath(o) : "";
        if (File.Exists(dir) && dir.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            outDir = outDir.Length > 0 ? outDir : Path.Combine(Path.GetDirectoryName(dir)!, Path.GetFileNameWithoutExtension(dir) + "_verify");
            string extracted = Path.Combine(outDir, "capture");
            if (!File.Exists(Path.Combine(extracted, "session.txt")))
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(dir, extracted, overwriteFiles: true);
            }

            dir = extracted;
        }

        if (!File.Exists(Path.Combine(dir, "session.txt")))
        {
            Console.Error.WriteLine($"verify: {dir} is not a capture folder (no session.txt)");
            return 3;
        }

        outDir = outDir.Length > 0 ? outDir : Path.Combine(dir, "verify");
        Directory.CreateDirectory(outDir);
        string[] replayOptions = options.Where(a => !s_ownOptions.Any(a.StartsWith)).ToArray();
        var report = new StringBuilder();
        report.AppendLine($"verify {Path.GetFileName(dir)} with {Diagnostics.BuildVersion()}");
        List<CaptureVerify.Item> recorded = RecordedFindings(dir);
        report.AppendLine($"gravação: {recorded.Count} achados ({string.Join(", ", recorded.GroupBy(r => r.Kind).Select(g => $"{g.Count()} {g.Key}"))})");
        var verdicts = new List<CaptureVerify.Verdict>();
        string dump = Path.Combine(dir, "gpu.vsgd");
        if (File.Exists(dump))
        {
            string replayOut = Path.Combine(outDir, "replay");
            (int rc, CaptureVerify.Result? result) = ReplayAndCompare(dump, replayOut, recorded, replayOptions, keepFrames: options.Contains("--keep-frames"));
            if (result == null)
            {
                report.AppendLine($"replay do dump da GPU: falhou (código {rc}; {Path.Combine(replayOut, "log.txt")})");
                Console.Write(report);
                File.WriteAllText(Path.Combine(outDir, "verify.txt"), report.ToString());
                return 4;
            }

            report.AppendLine($"replay do dump da GPU com este build ({result.Recorded} achados da gravação dentro do dump):");
            foreach (string line in result.Lines)
            {
                report.AppendLine(line);
            }

            report.AppendLine($"  replay: {CaptureVerify.Word(result.Verdict)}" + (result.Verdict == CaptureVerify.Verdict.StillPresent
                ? " (o replay executa os comandos gravados: se o build que gravou também o mostra no replay, a causa está no renderizador; se a causa vem antes da GPU, nos comandos, use --rerun)"
                : ""));
            verdicts.Add(result.Verdict);
        }
        else
        {
            report.AppendLine("replay do dump da GPU: a captura não tem gpu.vsgd");
        }

        if (options.Contains("--rerun"))
        {
            CaptureVerify.Result rerun = Rerun(dir, outDir, replayOptions, report);
            verdicts.Add(rerun.Verdict);
        }

        CaptureVerify.Verdict verdict = Combine(verdicts);
        report.AppendLine($"resultado: {CaptureVerify.Word(verdict)}");
        Console.Write(report);
        File.WriteAllText(Path.Combine(outDir, "verify.txt"), report.ToString());
        Console.WriteLine($"-> {Path.Combine(outDir, "verify.txt")}");
        return verdict switch
        {
            CaptureVerify.Verdict.Fixed => 0,
            CaptureVerify.Verdict.StillPresent => 1,
            CaptureVerify.Verdict.Changed => 2,
            _ => 3,
        };
    }

    /// <summary>Still present if any part says so; fixed if a part is fixed and none changed; nothing when no part had anything.</summary>
    internal static CaptureVerify.Verdict Combine(IReadOnlyList<CaptureVerify.Verdict> verdicts)
    {
        if (verdicts.Contains(CaptureVerify.Verdict.StillPresent))
        {
            return CaptureVerify.Verdict.StillPresent;
        }

        if (verdicts.Contains(CaptureVerify.Verdict.Changed))
        {
            return CaptureVerify.Verdict.Changed;
        }

        return verdicts.Contains(CaptureVerify.Verdict.Fixed) ? CaptureVerify.Verdict.Fixed : CaptureVerify.Verdict.NothingToCheck;
    }

    /// <summary>The recording's findings (the report's rules over frames.vsfr, (info) left out) and the NaN/Inf
    /// anomalies of its log.</summary>
    internal static List<CaptureVerify.Item> RecordedFindings(string dir)
    {
        var items = new List<CaptureVerify.Item>();
        string frames = Path.Combine(dir, "frames.vsfr");
        if (File.Exists(frames))
        {
            List<InputEvent> input = File.Exists(Path.Combine(dir, "input.tsv"))
                ? File.ReadLines(Path.Combine(dir, "input.tsv")).Select(InputEvent.Parse).OfType<InputEvent>().ToList()
                : new List<InputEvent>();
            items.AddRange(FrameFindings.OfFramesFile(frames, 6, input).Where(f => !f.Info).Select(CaptureVerify.ToItem));
        }

        items.AddRange(CaptureVerify.NanEvents(Path.Combine(dir, "log.txt")));
        return items;
    }

    /// <summary>Replays <paramref name="dump"/> in a child process with --findings into <paramref name="outDir"/> and
    /// compares its findings with <paramref name="recorded"/>; null when the replay wrote no findings.txt.</summary>
    internal static (int ExitCode, CaptureVerify.Result? Result) ReplayAndCompare(string dump, string outDir, IReadOnlyList<CaptureVerify.Item> recorded, string[] replayOptions, bool keepFrames)
    {
        (int rc, (long First, long Last, List<CaptureVerify.Item> Items)? replayed) = Replay(dump, outDir, replayOptions, keepFrames);
        if (replayed is not { } r)
        {
            return (rc, null);
        }

        return (rc, CaptureVerify.Compare(recorded, r.Items, r.First, r.Last));
    }

    /// <summary>One <c>replay-gpu DUMP --findings --fast --out=DIR</c> in a child process (its renderer switches are its
    /// own); its findings.txt, or null.</summary>
    internal static (int ExitCode, (long First, long Last, List<CaptureVerify.Item> Items)? Findings) Replay(string dump, string outDir, IEnumerable<string> replayOptions, bool keepFrames)
    {
        Directory.CreateDirectory(outDir);
        string findings = Path.Combine(outDir, CaptureVerify.FindingsFileName);
        File.Delete(findings);
        // no PNG of every frame unless asked: the findings come from the present tap
        var args = new List<string> { "replay-gpu", dump, $"--out={outDir}", "--findings", "--fast" };
        if (!keepFrames)
        {
            args.Add("--dump-every=1000000000");
        }

        args.AddRange(replayOptions);
        int rc = RunSelf(args, Path.Combine(outDir, "console.txt"));
        return (rc, File.Exists(findings) ? CaptureVerify.ReadFindings(findings) : null);
    }

    /// <summary>Runs this program again with <paramref name="args"/> (the apphost, or dotnet with this assembly), its
    /// output into <paramref name="consoleFile"/>.</summary>
    internal static int RunSelf(IEnumerable<string> args, string consoleFile)
    {
        string self = Environment.ProcessPath ?? throw new InvalidOperationException("no executable path");
        var psi = new ProcessStartInfo(self) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "VitaSharp.Desktop.dll"));
        }

        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{self} did not start");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        lock (output)
        {
            File.WriteAllText(consoleFile, output.ToString());
        }

        return process.ExitCode;
    }

    /// <summary>--rerun: the capture's logic again from its state (<c>rerun</c>, a child process), its report against
    /// the recording's, kind by kind.</summary>
    static CaptureVerify.Result Rerun(string dir, string outDir, string[] extra, StringBuilder report)
    {
        string reruns = Path.Combine(dir, "reruns");
        var before = Directory.Exists(reruns) ? Directory.GetDirectories(reruns).ToHashSet() : new HashSet<string>();
        int rc = RunSelf(new[] { "rerun", dir }.Concat(extra), Path.Combine(outDir, "rerun_console.txt"));
        string? latest = Directory.Exists(reruns)
            ? Directory.GetDirectories(reruns).Where(d => !before.Contains(d) && File.Exists(Path.Combine(d, "report.txt"))).OrderBy(Directory.GetLastWriteTimeUtc).LastOrDefault()
            : null;
        if (latest == null)
        {
            report.AppendLine($"re-run: falhou (código {rc}; sem report.txt em {reruns})");
            return new CaptureVerify.Result(CaptureVerify.Verdict.NothingToCheck, new List<string>(), 0, 0, 0, 0, 0);
        }

        SortedDictionary<string, int> recorded = CaptureVerify.ReportCounts(Path.Combine(dir, "report.txt"));
        SortedDictionary<string, int> again = CaptureVerify.ReportCounts(Path.Combine(latest, "report.txt"));
        CaptureVerify.Result result = CaptureVerify.CompareRerun(recorded, again);
        report.AppendLine($"re-run da lógica a partir do estado ({latest}):");
        foreach (string line in result.Lines)
        {
            report.AppendLine(line);
        }

        report.AppendLine($"  re-run: {CaptureVerify.Word(result.Verdict)}");
        return result;
    }
}
