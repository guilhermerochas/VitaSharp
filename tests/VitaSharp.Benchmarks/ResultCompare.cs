using System.Globalization;

namespace VitaSharp.Benchmarks;

/// <summary>
/// Compares two result files of build/bench.* (BenchmarkDotNet's GitHub Markdown tables): one row per benchmark found
/// in both (same method and parameters), with both means, the ratio new/base and both allocations. Printed as a
/// Markdown table, ready to paste into a commit message or docs/roadmap.md.
/// </summary>
public static class ResultCompare
{
    private sealed record Row(string Key, double MeanNs, string Mean, string Allocated);

    public static int Run(string basePath, string newPath)
    {
        Dictionary<string, Row> a = Read(basePath);
        Dictionary<string, Row> b = Read(newPath);
        if (a.Count == 0 || b.Count == 0)
        {
            Console.Error.WriteLine("no benchmark table in one of the files");
            return 2;
        }
        Console.WriteLine($"| Benchmark | {Path.GetFileNameWithoutExtension(basePath)} | {Path.GetFileNameWithoutExtension(newPath)} | Ratio | Allocated (base -> new) |");
        Console.WriteLine("|---|---:|---:|---:|---|");
        foreach ((string key, Row nb) in b)
        {
            if (!a.TryGetValue(key, out Row? ob))
            {
                Console.WriteLine($"| {key} | - | {nb.Mean} | new | {nb.Allocated} |");
                continue;
            }
            string ratio = ob.MeanNs > 0 && nb.MeanNs > 0 ? (nb.MeanNs / ob.MeanNs).ToString("F3", CultureInfo.InvariantCulture) : "?";
            Console.WriteLine($"| {key} | {ob.Mean} | {nb.Mean} | {ratio} | {ob.Allocated} -> {nb.Allocated} |");
        }
        foreach (string key in a.Keys.Where(k => !b.ContainsKey(k)))
        {
            Console.WriteLine($"| {key} | {a[key].Mean} | - | gone | |");
        }

        return 0;
    }

    /// <summary>The rows of every table in a file, keyed by type/method and parameter columns.</summary>
    private static Dictionary<string, Row> Read(string path)
    {
        var rows = new Dictionary<string, Row>();
        string[]? header = null;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (!line.StartsWith('|'))
            {
                header = null;
                continue;
            }
            string[] cells = line.Trim('|').Split('|').Select(c => c.Trim().Trim('\'')).ToArray();
            if (header == null)
            {
                header = cells;
                continue;
            }
            if (cells.All(c => c.Length == 0 || c.All(ch => ch is '-' or ':')))
            {
                continue;
            }

            int mean = Array.IndexOf(header, "Mean");
            if (mean < 0 || cells.Length != header.Length)
            {
                continue;
            }
            // the identity: everything before Mean except the job and environment columns BenchmarkDotNet adds
            IEnumerable<string> id = Enumerable.Range(0, mean).Where(i => header[i] is not ("Job" or "Runtime" or "Toolchain" or "IterationCount"
                or "LaunchCount" or "WarmupCount" or "RunStrategy" or "Affinity" or "Error" or "StdDev"))
                .Select(i => header[i] == "Method" || header[i] == "Type" ? cells[i] : $"{header[i]}={cells[i]}");
            string key = string.Join(' ', id.Where(s => s.Length > 0));
            int alloc = Array.IndexOf(header, "Allocated");
            rows[key] = new Row(key, Nanoseconds(cells[mean]), cells[mean], alloc >= 0 ? cells[alloc] : "");
        }
        return rows;
    }

    private static double Nanoseconds(string mean)
    {
        string[] parts = mean.Replace(",", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            return 0;
        }

        return parts[1] switch { "ns" => v, "μs" or "us" => v * 1e3, "ms" => v * 1e6, "s" => v * 1e9, _ => 0 };
    }
}
