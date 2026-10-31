using System.Globalization;

namespace VitaSharp.Android.Performance;

/// <summary>Cores of one kind (same capacity and maximum frequency).</summary>
public sealed record CpuCluster(int[] Cpus, long MaxFreqKhz, int Capacity)
{
    public ulong Mask => CpuTopology.MaskOf(Cpus);

    public override string ToString() => FormattableString.Invariant($"{CpuTopology.Ranges(Cpus)} @{MaxFreqKhz / 1e6:0.0#}GHz");
}

/// <summary>
/// The phone's CPU clusters, read at run time from sysfs (never assumed: the Helio G99 is 6 x A55 + 2 x A76, other
/// SoCs are 4+4, 1+3+4, 2+3+3, ...). Cores are grouped by <c>cpu_capacity</c> (the scheduler's relative speed, which
/// tells an A55 and an A76 apart even at the same clock) and <c>cpufreq/cpuinfo_max_freq</c>; clusters are ordered from
/// the slowest to the fastest, so <see cref="Big"/> is the last one.
/// </summary>
public sealed class CpuTopology
{
    public IReadOnlyList<CpuCluster> Clusters { get; }

    CpuTopology(List<CpuCluster> clusters) { Clusters = clusters; }

    /// <summary>Every known core.</summary>
    public ulong AllMask => Clusters.Aggregate(0UL, (m, c) => m | c.Mask);

    /// <summary>The fastest cluster, when there is more than one kind of core.</summary>
    public CpuCluster? Big => Clusters.Count > 1 ? Clusters[^1] : null;

    /// <summary>The second fastest cluster when there are three or more (1+3+4 layouts: the A7x "mid" cores).</summary>
    public CpuCluster? Mid => Clusters.Count > 2 ? Clusters[^2] : null;

    /// <summary>Every core but the fastest cluster's.</summary>
    public ulong NotBigMask => Big is { } b ? AllMask & ~b.Mask : AllMask;

    /// <summary>"0-5 @2.0GHz, 6-7 @2.2GHz".</summary>
    public string Describe() => Clusters.Count == 0 ? "unknown" : string.Join(", ", Clusters);

    /// <summary>Groups cores by (capacity, max frequency). Capacity 0 = unknown (then the frequency alone ranks them).</summary>
    public static CpuTopology Build(IEnumerable<(int Cpu, long MaxFreqKhz, int Capacity)> cores)
    {
        var list = cores.Where(c => c.Cpu is >= 0 and < 64 && (c.MaxFreqKhz > 0 || c.Capacity > 0)).ToList();
        bool useCapacity = list.Count > 0 && list.All(c => c.Capacity > 0);
        var clusters = list
            .GroupBy(c => (Capacity: useCapacity ? c.Capacity : 0, c.MaxFreqKhz))
            .Select(g => new CpuCluster(g.Select(c => c.Cpu).Order().ToArray(), g.Key.MaxFreqKhz, g.Key.Capacity))
            .OrderBy(c => c.Capacity).ThenBy(c => c.MaxFreqKhz).ThenBy(c => c.Cpus[0])
            .ToList();
        return new CpuTopology(clusters);
    }

    /// <summary>Reads <c>&lt;root&gt;/cpuN/cpu_capacity</c> and <c>&lt;root&gt;/cpuN/cpufreq/cpuinfo_max_freq</c> for
    /// every <c>cpuN</c> folder (Linux and Android: <c>/sys/devices/system/cpu</c>). Unreadable files count as unknown.</summary>
    public static CpuTopology FromSysfs(string root = "/sys/devices/system/cpu")
    {
        var cores = new List<(int, long, int)>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "cpu*"))
            {
                var name = Path.GetFileName(dir);
                if (name.Length < 4 || !int.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int cpu)) continue;
                long freq = ReadLong(Path.Combine(dir, "cpufreq", "cpuinfo_max_freq"));
                int cap = (int)ReadLong(Path.Combine(dir, "cpu_capacity"));
                cores.Add((cpu, freq, cap));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Build(cores);
    }

    static long ReadLong(string path)
    {
        try { return long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    public static ulong MaskOf(IEnumerable<int> cpus) => cpus.Aggregate(0UL, (m, c) => m | 1UL << c);

    /// <summary>The cores of a mask as ranges: "0-5", "6-7", "0,2,4".</summary>
    public static string Ranges(ulong mask) => Ranges(Enumerable.Range(0, 64).Where(i => (mask >> i & 1) != 0).ToArray());

    public static string Ranges(int[] cpus)
    {
        if (cpus.Length == 0) return "none";
        var parts = new List<string>();
        int start = cpus[0], prev = cpus[0];
        foreach (int c in cpus.Skip(1).Append(int.MinValue))
        {
            if (c == prev + 1) { prev = c; continue; }
            parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
            start = prev = c;
        }
        return string.Join(",", parts);
    }
}
