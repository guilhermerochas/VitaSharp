using System.Globalization;
using System.Text;

namespace VitaSharp.Core.Firmware;

/// <summary>
/// The per-function tables that <c>hle-coverage --status</c> writes next to the per-library summary
/// (<see cref="HleStatusReport.TsvPath"/>), all from the same census so they never disagree with it:
/// the missing functions of each status group (<see cref="MissingPath"/>), the user-side holes that reach the HLE
/// (<see cref="UserExportsPath"/>, method in docs/hle/user-exports.md) and every NID the HLE implements
/// (<see cref="ImplementedPath"/>). Each table is only data: comment lines start with '#', then a header row.
/// </summary>
public static class HleCensusTables
{
    public const string MissingPath = "docs/hle/data/missing-by-group.tsv";
    public const string UserExportsPath = "docs/hle/data/user-exports.tsv";
    public const string ImplementedPath = "docs/hle/data/vitasharp-implemented.tsv";

    /// <summary>The status groups, in the order of the README status: the functions the installed games import, the
    /// ones only the system programs/modules import, the rest the firmware exports, and the VitaSDK NID list.</summary>
    public static readonly string[] Groups = ["games", "system", "general", "vitasdk"];

    /// <summary>One firmware function of the census: <paramref name="Group"/> games/system/general (or vitasdk for a
    /// VitaSDK list row), covered = implemented by the HLE (real or default) or by a real module (LLE).</summary>
    public readonly record struct Function(uint Nid, string Name, string Library, string Module, string Group, bool Covered,
        int GameImporters, int SystemImporters, string Importer);

    /// <summary>Every function of <paramref name="functions"/> that is not covered, by group (in <see cref="Groups"/>
    /// order), then library and NID.</summary>
    public static string MissingByGroup(string firmware, IEnumerable<Function> functions)
    {
        var missing = functions.Where(f => !f.Covered).ToList();
        var b = new StringBuilder();
        b.Append("# Missing functions of each HLE status group, written by VitaSharp.Desktop hle-coverage --status (build/hle-status.ps1)\n");
        b.Append(CultureInfo.InvariantCulture, $"# firmware={firmware}");
        foreach (string group in Groups)
        {
            b.Append(CultureInfo.InvariantCulture, $" {group}={missing.Count(f => f.Group == group)}");
        }

        b.Append('\n');
        b.Append("# group: games/system/general as in docs/hle/data/hle-status.tsv, vitasdk = the VitaSDK NID list; games/system: importer counts\n");
        b.Append("group\tlibrary\tnid\tname\tmodule\tgames\tsystem\timporter\n");
        foreach (Function f in missing.OrderBy(f => Array.IndexOf(Groups, f.Group)).ThenBy(f => f.Library, StringComparer.Ordinal).ThenBy(f => f.Nid))
        {
            b.Append(CultureInfo.InvariantCulture,
                $"{f.Group}\t{f.Library}\t0x{f.Nid:X8}\t{f.Name}\t{f.Module}\t{f.GameImporters}\t{f.SystemImporters}\t{f.Importer}\n");
        }

        return b.ToString();
    }

    /// <summary>How many rows of each group a <see cref="MissingByGroup"/> table has.</summary>
    public static IReadOnlyDictionary<string, int> ParseMissingCounts(string tsv)
    {
        Dictionary<string, int> counts = Groups.ToDictionary(g => g, _ => 0, StringComparer.Ordinal);
        foreach (string raw in tsv.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0 || raw[0] == '#' || raw.StartsWith("group\t", StringComparison.Ordinal))
            {
                continue;
            }

            string group = raw[..raw.IndexOf('\t')];
            counts[group] = counts.GetValueOrDefault(group) + 1;
        }

        return counts;
    }

    /// <summary>Where a firmware function without an [Hle] entry would be served from, as the user-side census
    /// (docs/hle/user-exports.md) names it, or null when the real module serves it: kernel-syscall (a syscall
    /// export of a kernel module, the bootimage's included) and os0-user (an os0:us module) always reach the HLE,
    /// vs0-user only when the HLE serves its library (<paramref name="firmwareServesLibrary"/> false).</summary>
    public static string? HoleKind(string module, string library, Func<string, bool> firmwareServesLibrary)
    {
        if (module.StartsWith(Io.FirmwareLayout.KernelModules.Guest() + "/", StringComparison.Ordinal))
        {
            return "kernel-syscall";
        }

        if (module.StartsWith(Io.FirmwareLayout.UserModules.Guest() + "/", StringComparison.Ordinal))
        {
            return "os0-user";
        }

        if (module.StartsWith("vs0:", StringComparison.Ordinal) && !firmwareServesLibrary(library))
        {
            return "vs0-user";
        }

        return null;
    }

    /// <summary>The user-side holes: every exported function with no [Hle] entry (<paramref name="hasHleEntry"/>)
    /// that reaches the HLE (<see cref="HoleKind"/>), by library and NID.</summary>
    public static string UserExports(IEnumerable<Function> functions, Func<uint, bool> hasHleEntry, Func<string, bool> firmwareServesLibrary,
        Func<uint, bool> inVitaSdk)
    {
        var b = new StringBuilder();
        b.Append("# User-side HLE holes of FW 3.74 (VitaSharp.Desktop hle-coverage --status; method in docs/hle/user-exports.md):\n");
        b.Append("# the functions the firmware exports to user code that have no [Hle] entry and can reach the HLE. kind: kernel-syscall\n");
        b.Append($"# (syscall export of a kernel module) and os0-user ({Io.FirmwareLayout.UserModules.Guest()} module) always reach it; vs0-user rows are the missing functions of the vs0\n");
        b.Append("# libraries the HLE serves, which reach it only from games (system software that imports one gets the real module).\n");
        b.Append("# games/system: importers among the installed games / system programs and modules; vitasdk: in the VitaSDK NID list.\n");
        b.Append("library\tnid\tname\tkind\tgames\tsystem\tvitasdk\n");
        foreach (Function f in functions.Where(f => !hasHleEntry(f.Nid)).OrderBy(f => f.Library, StringComparer.Ordinal).ThenBy(f => f.Nid))
        {
            string? kind = HoleKind(f.Module, f.Library, firmwareServesLibrary);
            if (kind == null)
            {
                continue;
            }

            b.Append(CultureInfo.InvariantCulture,
                $"{f.Library}\t0x{f.Nid:X8}\t{f.Name}\t{kind}\t{f.GameImporters}\t{f.SystemImporters}\t{(inVitaSdk(f.Nid) ? "yes" : "-")}\n");
        }

        return b.ToString();
    }

    /// <summary>One line per NID with an [Hle] entry (nid, name, HLE module, library, "vh" when the VitaSDK lists it,
    /// "default" when it only returns the firmware's documented result), by library ("~" sorts the unknown last)
    /// and name.</summary>
    public static string Implemented(IEnumerable<(uint Nid, string Name, string Module, string? Library, bool Default)> entries, Func<uint, bool> inVitaSdk)
    {
        var b = new StringBuilder();
        foreach ((uint Nid, string Name, string Module, string? Library, bool Default) e in entries.OrderBy(e => e.Library ?? "~", StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            b.Append(CultureInfo.InvariantCulture, $"0x{e.Nid:X8}\t{e.Name}\t{e.Module}\t{e.Library ?? "-"}\t{(inVitaSdk(e.Nid) ? "vh" : "-")}");
            if (e.Default)
            {
                b.Append("\tdefault");
            }

            b.Append('\n');
        }

        return b.ToString();
    }
}
