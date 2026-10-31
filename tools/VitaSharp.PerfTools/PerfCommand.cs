using System.Globalization;

namespace VitaSharp.PerfTools;

/// <summary>One vitasharp-perf command (Commands/*.cs), registered in Program.cs.</summary>
interface IPerfCommand
{
    string Name { get; }
    string Summary { get; }
    void PrintUsage();
    int Run(PerfArgs args);
}

sealed class PerfArgsException(string message) : Exception(message);

/// <summary>Positional arguments and <c>--name value</c> / <c>--name=value</c> / <c>--flag</c> options.</summary>
sealed class PerfArgs
{
    readonly List<string> _positional = new();
    readonly Dictionary<string, string?> _options = new();

    public PerfArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith('-')) { _positional.Add(a); continue; }
            int eq = a.IndexOf('=');
            if (eq > 0) _options[a[..eq]] = a[(eq + 1)..];
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) _options[a] = args[++i];
            else _options[a] = null;
        }
    }

    public IReadOnlyList<string> Positional => _positional;
    public bool Has(string name) => _options.ContainsKey(name);
    public string? Get(string name) => _options.GetValueOrDefault(name);
    public IEnumerable<string> Names => _options.Keys;

    public double Double(string name, double def)
    {
        if (Get(name) is not { } v) return def;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : throw new PerfArgsException($"{name}: '{v}' is not a positive number");
    }

    /// <summary><c>a-b</c>, <c>a-</c> or <c>a</c> (present numbers, inclusive).</summary>
    public (long First, long Last) Range(string name)
    {
        if (Get(name) is not { } v) return (0, long.MaxValue);
        var parts = v.Split('-');
        bool ok = long.TryParse(parts[0], out var a);
        long b = parts.Length == 1 ? a : parts[1].Length == 0 ? long.MaxValue : long.TryParse(parts[1], out var bb) ? bb : -1;
        if (!ok || parts.Length > 2 || b < a) throw new PerfArgsException($"{name}: '{v}' is not a range a-b");
        return (a, b);
    }
}
