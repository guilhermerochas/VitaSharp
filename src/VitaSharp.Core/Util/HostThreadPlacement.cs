using System.Globalization;

namespace VitaSharp.Core.Util;

/// <summary>
/// Where a host thread runs on Linux and Android: the core it last ran on, that core's maximum clock (a phone's small
/// cores run at about half its big ones) and the thread's nice value, from /proc/self/task/TID/stat (fields 39 and 19)
/// and /sys/devices/system/cpu/cpuN/cpufreq/cpuinfo_max_freq. Guest threads that wake late or run slowly on the phone
/// show up there on a small core or with a high nice. Null on other systems or when the files cannot be read.
/// </summary>
public static class HostThreadPlacement
{
    public static string? Describe(int tid)
    {
        if (tid <= 0 || !(OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()))
        {
            return null;
        }

        try
        {
            string stat = File.ReadAllText($"/proc/self/task/{tid}/stat");
            return Parse(stat, core => MaxFrequencyKhz(core));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The description from a stat line; <paramref name="maxKhz"/> gives a core's maximum clock (0: unknown).</summary>
    internal static string? Parse(string stat, Func<int, long> maxKhz)
    {
        int close = stat.LastIndexOf(')');
        if (close < 0)
        {
            return null;
        }

        // after "pid (comm)" the fields start at 3 (state): field N is at index N - 3
        string[] fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 36)
        {
            return null;
        }

        int nice = int.Parse(fields[16], CultureInfo.InvariantCulture);
        int core = int.Parse(fields[36], CultureInfo.InvariantCulture);
        long khz = maxKhz(core);
        string clock = khz > 0 ? FormattableString.Invariant($" (max {khz / 1000} MHz)") : "";
        return FormattableString.Invariant($"host: core {core}{clock}, nice {nice}, state {fields[0]}");
    }

    private static long MaxFrequencyKhz(int core)
    {
        try
        {
            string text = File.ReadAllText($"/sys/devices/system/cpu/cpu{core}/cpufreq/cpuinfo_max_freq").Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long khz) ? khz : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
