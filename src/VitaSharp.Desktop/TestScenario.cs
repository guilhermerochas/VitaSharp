using System.Text;

namespace VitaSharp.Desktop;

/// <summary>
/// Automated test scenario: a text file of key=value lines
///   title=PCSC00045            title id (or folder)
///   data=C:\path\to\data       optional data root (default: the normal one)
///   args=--turbo --frames=4000 --dump-every=200 --input=tap@f600:0.36:0.84,...
/// Runs headless and writes into &lt;data&gt;/tests/&lt;scenario name&gt;/: the dumped frames, sheet.png (all frames on one
/// image) and report.txt (run speed plus every distinct warning, error and unimplemented call).
/// </summary>
static class TestScenario
{
    /// <param name="extraArgs">run options from the command line (test &lt;scenario&gt; [options]); they win over the scenario's args=</param>
    public static int Run(string file, string[]? extraArgs = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(file))
        {
            var l = line.Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            int eq = l.IndexOf('=');
            if (eq > 0) values[l[..eq].Trim()] = l[(eq + 1)..].Trim();
        }
        if (!values.TryGetValue("title", out var title)) { Console.Error.WriteLine("scenario needs title="); return 1; }
        // --data=<folder> on the command line wins over the scenario's data= (scenarios name Windows folders; on
        // another OS, or to run against a copy of the data, pass --data); a data= folder that does not exist on this
        // machine is ignored as well
        if (values.TryGetValue("data", out var data))
        {
            if (Program.DataFromCommandLine) Console.WriteLine($"Scenario data={data} overridden by --data={Program.DataRoot}");
            else if (!Path.IsPathFullyQualified(data) || !Directory.Exists(data)) Console.WriteLine($"Scenario data={data} not found here, using {Program.DataRoot}");
            else Environment.SetEnvironmentVariable("VITASHARP_DATA", data);
        }

        string name = Path.GetFileNameWithoutExtension(file);
        string outDir = Path.Combine(Program.DataRoot, "tests", name);
        Directory.CreateDirectory(outDir);
        // options are looked up first-match-wins: command-line options go first so they override the scenario's
        var args = (extraArgs ?? []).Concat(SplitArgs(values.GetValueOrDefault("args", ""))).ToList();
        if (!args.Any(a => a.StartsWith("--dump-frames"))) args.Add("--dump-frames=" + outDir);
        if (!args.Contains("--headless")) args.Add("--headless");
        string log = Path.Combine(outDir, "log.txt");
        if (!args.Any(a => a.StartsWith("--log="))) args.Add("--log=" + log);

        int rc = Runner.Run(Program.ResolveApp(title), args.ToArray());
        WriteReport(log, Path.Combine(outDir, "report.txt"));
        Console.WriteLine($"Results in {outDir}");
        return rc;
    }

    static IEnumerable<string> SplitArgs(string s)
    {
        var cur = new StringBuilder();
        bool quoted = false;
        foreach (char c in s)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted) { if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); } continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) yield return cur.ToString();
    }

    /// <summary>Distinct warnings/errors (timestamps stripped) plus the summary lines.</summary>
    static void WriteReport(string log, string report)
    {
        if (!File.Exists(log)) return;
        var seen = new HashSet<string>();
        var sb = new StringBuilder();
        using var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line)
        {
            int b = line.IndexOf("] [", StringComparison.Ordinal);
            string body = b >= 0 ? line[(b + 2)..] : line;
            bool interesting = body.StartsWith("[Warn") || body.StartsWith("[Error") || body.Contains("Unimplemented")
                               || body.Contains("Run finished") || body.Contains("FPS per") || body.Contains("Contact sheet")
                               || body.Contains("  thread '") || body.Contains("METRICS") || body.Contains("PERF ") || body.Contains("ALLOC frames") || body.Contains("CPU backend") || body.Contains("SUSPECT") || body.Contains("Applied patch") || body.Contains("LoadExec") || body.Contains("[shadercache]");
            if (interesting && seen.Add(body)) sb.AppendLine(body);
        }
        File.WriteAllText(report, sb.ToString());
    }
}
