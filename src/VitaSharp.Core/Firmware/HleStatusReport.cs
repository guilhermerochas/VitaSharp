using System.Globalization;
using System.Text;

namespace VitaSharp.Core.Firmware;

/// <summary>
/// The HLE status shown in the README: badges, a progress map (one cell per firmware library, area = exported
/// functions, colour = share implemented) and a per-library table (docs/hle/progress.md). Everything is rendered from
/// a small per-library summary (docs/hle/data/hle-status.tsv) that <c>hle-coverage --status=&lt;repo&gt;</c> writes from
/// the installed firmware and games; the test HleStatusReportTests renders it again and compares, so the committed
/// files cannot drift from the summary.
/// </summary>
public static class HleStatusReport
{
    /// <summary>One firmware library: exported functions, how many have an HLE implementation, and the split of both
    /// into the ones the installed games import and the ones only the system programs import. The *Default counts are
    /// the part of the implemented ones that only return the firmware's documented result (no hardware or service to
    /// emulate: <see cref="Hle.HleAttribute.Default"/>); Lle the ones covered by running the real module
    /// (<see cref="Hle.LleLibraries"/>). Impl includes both.</summary>
    public sealed record Library(string Name, string Module, int Total, int Impl, int Games, int GamesImpl, int System, int SystemImpl,
        int Default = 0, int GamesDefault = 0, int SystemDefault = 0, int Lle = 0);

    public sealed record Status(string Firmware, int GameTitles, int VitaSdk, int VitaSdkImpl, IReadOnlyList<Library> Libraries, int VitaSdkDefault = 0,
        int VitaSdkLle = 0)
    {
        public int Lle => Libraries.Sum(l => l.Lle);
        public int Default => Libraries.Sum(l => l.Default);
        public int GamesDefault => Libraries.Sum(l => l.GamesDefault);
        public int SystemDefault => Libraries.Sum(l => l.SystemDefault);
        public int Total => Libraries.Sum(l => l.Total);
        public int Impl => Libraries.Sum(l => l.Impl);
        public int Games => Libraries.Sum(l => l.Games);
        public int GamesImpl => Libraries.Sum(l => l.GamesImpl);
        public int System => Libraries.Sum(l => l.System);
        public int SystemImpl => Libraries.Sum(l => l.SystemImpl);
    }

    public const string TsvPath = "docs/hle/data/hle-status.tsv";
    /// <summary>The kernel export census (<c>VitaSharp.Desktop kernel-census</c>): every function the kernel modules export
    /// to other kernel code, with its status. The kernel badge and section of the status come from it.</summary>
    public const string KernelTsvPath = "docs/hle/data/kernel-exports.tsv";
    public const string ProgressPath = "docs/hle/progress.md";
    public const string AssetDir = "docs/assets/status";

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    /// <summary>One kernel library of the census: exported functions, the covered ones (HLE, defaults included), the
    /// defaults, and the same counts for the functions some kernel module (or installed kernel plugin) imports.</summary>
    public sealed record KernelLibrary(string Name, int Total, int Impl, int Default, int Imported, int ImportedImpl);

    /// <summary>The per-library kernel counts of the census table (library, nid, name, status impl/default/missing,
    /// method, importers).</summary>
    public static IReadOnlyList<KernelLibrary> ParseKernel(string tsv)
    {
        var rows = new List<(string Library, string Status, bool Imported)>();
        foreach (string raw in tsv.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0 || raw[0] == '#' || raw.StartsWith("library\t", StringComparison.Ordinal))
            {
                continue;
            }

            string[] c = raw.Split('\t');
            rows.Add((c[0], c[3], c.Length > 5 && c[5].Length > 0));
        }

        return rows.GroupBy(r => r.Library, StringComparer.Ordinal)
            .Select(g => new KernelLibrary(g.Key, g.Count(), g.Count(r => r.Status != "missing"), g.Count(r => r.Status == "default"),
                g.Count(r => r.Imported), g.Count(r => r.Imported && r.Status != "missing")))
            .OrderBy(l => l.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The kernel badge: the census's covered exports (HLE and defaults) of every exported kernel function.</summary>
    public static (string Label, int Impl, int Total, int Default) KernelBadge(string firmware, IReadOnlyList<KernelLibrary> kernel) =>
        ($"firmware {firmware} kernel exports", kernel.Sum(l => l.Impl), kernel.Sum(l => l.Total), kernel.Sum(l => l.Default));

    /// <summary>Per-library summary from hle-coverage rows (group: games/system/general; rows of NIDs no firmware
    /// module exports are not passed in).</summary>
    public static Status Build(string firmware, int gameTitles, int vitaSdk, int vitaSdkImpl,
        IEnumerable<(string Library, string Module, string Group, bool Impl, bool Default, bool Lle)> rows, int vitaSdkDefault = 0, int vitaSdkLle = 0)
    {
        var libs = rows.GroupBy(r => r.Library, StringComparer.Ordinal).Select(g => new Library(g.Key,
                g.GroupBy(r => r.Module).OrderByDescending(m => m.Count()).ThenBy(m => m.Key, StringComparer.Ordinal).First().Key,
                g.Count(), g.Count(r => r.Impl),
                g.Count(r => r.Group == "games"), g.Count(r => r.Group == "games" && r.Impl),
                g.Count(r => r.Group == "system"), g.Count(r => r.Group == "system" && r.Impl),
                g.Count(r => r.Impl && r.Default), g.Count(r => r.Group == "games" && r.Impl && r.Default),
                g.Count(r => r.Group == "system" && r.Impl && r.Default), g.Count(r => r.Impl && r.Lle)))
            .OrderBy(l => l.Name, StringComparer.Ordinal).ToList();
        return new Status(firmware, gameTitles, vitaSdk, vitaSdkImpl, libs, vitaSdkDefault, vitaSdkLle);
    }

    public static string ToTsv(Status s)
    {
        var b = new StringBuilder();
        b.Append("# HLE status per firmware library, written by VitaSharp.Desktop hle-coverage --status (build/hle-status.ps1)\n");
        b.Append(s_inv, $"# firmware={s.Firmware} games={s.GameTitles} vitasdk={s.VitaSdkImpl}/{s.VitaSdk} vitasdk_default={s.VitaSdkDefault} vitasdk_lle={s.VitaSdkLle}\n");
        b.Append("library\tmodule\ttotal\timpl\tgames\tgames_impl\tsystem\tsystem_impl\tdefault\tgames_default\tsystem_default\tlle\n");
        foreach (Library l in s.Libraries)
        {
            b.Append(s_inv, $"{l.Name}\t{l.Module}\t{l.Total}\t{l.Impl}\t{l.Games}\t{l.GamesImpl}\t{l.System}\t{l.SystemImpl}\t{l.Default}\t{l.GamesDefault}\t{l.SystemDefault}\t{l.Lle}\n");
        }

        return b.ToString();
    }

    public static Status Parse(string tsv)
    {
        string firmware = "?";
        int games = 0, sdk = 0, sdkImpl = 0, sdkDefault = 0, sdkLle = 0;
        var libs = new List<Library>();
        foreach (string raw in tsv.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            if (raw.StartsWith("# firmware=", StringComparison.Ordinal))
            {
                foreach (string kv in raw[2..].Split(' '))
                {
                    string[] p = kv.Split('=');
                    if (p.Length != 2)
                    {
                        continue;
                    }

                    if (p[0] == "firmware")
                    {
                        firmware = p[1];
                    }
                    else if (p[0] == "games")
                    {
                        games = int.Parse(p[1], s_inv);
                    }
                    else if (p[0] == "vitasdk")
                    {
                        string[] f = p[1].Split('/');
                        sdkImpl = int.Parse(f[0], s_inv);
                        sdk = int.Parse(f[1], s_inv);
                    }
                    else if (p[0] == "vitasdk_default")
                    {
                        sdkDefault = int.Parse(p[1], s_inv);
                    }
                    else if (p[0] == "vitasdk_lle")
                    {
                        sdkLle = int.Parse(p[1], s_inv);
                    }
                }
                continue;
            }
            if (raw[0] == '#' || raw.StartsWith("library\t", StringComparison.Ordinal))
            {
                continue;
            }

            string[] c = raw.Split('\t');
            libs.Add(new Library(c[0], c[1], I(c[2]), I(c[3]), I(c[4]), I(c[5]), I(c[6]), I(c[7]),
                c.Length > 10 ? I(c[8]) : 0, c.Length > 10 ? I(c[9]) : 0, c.Length > 10 ? I(c[10]) : 0, c.Length > 11 ? I(c[11]) : 0));
        }
        return new Status(firmware, games, sdk, sdkImpl, libs, sdkDefault, sdkLle);
        static int I(string v) => int.Parse(v, s_inv);
    }

    internal static double Pct(int n, int total) => total == 0 ? 0 : 100.0 * n / total;

    /// <summary>"99.1%", never rounded up to 100% while something is missing.</summary>
    internal static string PctText(int n, int total)
    {
        double p = Math.Floor(Pct(n, total) * 10) / 10;
        return p.ToString(p == Math.Floor(p) ? "0" : "0.0", s_inv) + "%";
    }

    internal static string Count(int n) => n.ToString("N0", s_inv);

    /// <summary>Every generated file: repository-relative path (forward slashes) to its content (LF line ends).
    /// <paramref name="kernel"/>: the kernel census (<see cref="ParseKernel"/>), for the kernel badge and section.</summary>
    public static IReadOnlyDictionary<string, string> Render(Status s, IReadOnlyList<KernelLibrary>? kernel = null)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string? id, string? label, int n, int total, int d, int lle) in BadgesWithParts(s))
        {
            files[$"{AssetDir}/badge-{id}.svg"] = Badge(label, BadgeValue(n, total, d, lle), BadgeColor(Pct(n, total)));
        }

        if (kernel is { Count: > 0 })
        {
            (string label, int n, int total, int d) = KernelBadge(s.Firmware, kernel);
            files[$"{AssetDir}/badge-kernel.svg"] = Badge(label, BadgeValue(n, total, d, 0), BadgeColor(Pct(n, total)));
        }

        files[$"{AssetDir}/progress-map.svg"] = Map(s, kernel, dark: true);
        files[$"{AssetDir}/progress-map-light.svg"] = Map(s, kernel, dark: false);
        files[ProgressPath] = ProgressPage(s, kernel);
        return files;
    }

    private static string BadgeValue(int n, int total, int d, int lle) =>
        $"{PctText(n, total)} ({Count(n)}/{Count(total)}{(d > 0 ? $", {Count(d)} default" : "")}{(lle > 0 ? $", {Count(lle)} LLE" : "")})";

    public static IEnumerable<(string Id, string Label, int Impl, int Total)> Badges(Status s) =>
        BadgesWithDefault(s).Select(b => (b.Id, b.Label, b.Impl, b.Total));

    /// <summary>The badges with the default part of each implemented count.</summary>
    public static IEnumerable<(string Id, string Label, int Impl, int Total, int Default)> BadgesWithDefault(Status s) =>
        BadgesWithParts(s).Select(b => (b.Id, b.Label, b.Impl, b.Total, b.Default));

    /// <summary>The badges with the default and LLE parts of each covered count.</summary>
    public static IEnumerable<(string Id, string Label, int Impl, int Total, int Default, int Lle)> BadgesWithParts(Status s) =>
    [
        ("games", "games imports", s.GamesImpl, s.Games, s.GamesDefault, 0),
        ("system", "system imports", s.SystemImpl, s.System, s.SystemDefault, 0),
        ("vitasdk", "VitaSDK functions", s.VitaSdkImpl, s.VitaSdk, s.VitaSdkDefault, s.VitaSdkLle),
        ("overall", $"firmware {s.Firmware} exports", s.Impl, s.Total, s.Default, s.Lle),
    ];

    internal static string BadgeColor(double p) => p >= 95 ? "#4c1" : p >= 80 ? "#97ca00" : p >= 60 ? "#a4a61d" : p >= 40 ? "#dfb317" : p >= 20 ? "#fe7d37" : "#e05d44";

    /// <summary>Approximate advance of a string in Verdana 11 px (shields.io's font), enough to size the boxes.</summary>
    internal static double TextWidth(string t, double size = 11)
    {
        double w = 0;
        foreach (char ch in t)
        {
            w += ch switch
            {
                'i' or 'l' or 'j' or '.' or ',' or ':' or '\'' or '|' or '!' => 3.4,
                ' ' or '(' or ')' or '/' or 'f' or 't' or 'r' or 'I' => 4.6,
                'm' or 'w' or 'M' or 'W' or '%' => 10.5,
                >= 'A' and <= 'Z' => 7.6,
                >= '0' and <= '9' => 7.0,
                _ => 6.6,
            };
        }

        return w * size / 11;
    }

    internal static string Esc(string t) => t.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    internal static string F(double v) => Math.Round(v, 1).ToString("0.#", s_inv);

    /// <summary>A flat shields.io-style badge.</summary>
    internal static string Badge(string label, string value, string color)
    {
        double lw = Math.Ceiling(TextWidth(label) + 12), vw = Math.Ceiling(TextWidth(value) + 12), w = lw + vw;
        var b = new StringBuilder();
        b.Append(s_inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"20\" role=\"img\" aria-label=\"{Esc(label)}: {Esc(value)}\">\n");
        b.Append(s_inv, $"<title>{Esc(label)}: {Esc(value)}</title>\n");
        b.Append("<linearGradient id=\"s\" x2=\"0\" y2=\"100%\"><stop offset=\"0\" stop-color=\"#bbb\" stop-opacity=\".1\"/><stop offset=\"1\" stop-opacity=\".1\"/></linearGradient>\n");
        b.Append(s_inv, $"<clipPath id=\"r\"><rect width=\"{F(w)}\" height=\"20\" rx=\"3\" fill=\"#fff\"/></clipPath>\n");
        b.Append(s_inv, $"<g clip-path=\"url(#r)\"><rect width=\"{F(lw)}\" height=\"20\" fill=\"#555\"/><rect x=\"{F(lw)}\" width=\"{F(vw)}\" height=\"20\" fill=\"{color}\"/><rect width=\"{F(w)}\" height=\"20\" fill=\"url(#s)\"/></g>\n");
        b.Append("<g fill=\"#fff\" text-anchor=\"middle\" font-family=\"Verdana,Geneva,DejaVu Sans,sans-serif\" font-size=\"11\">\n");
        foreach ((double x, string? t) in new[] { (lw / 2, label), (lw + vw / 2, value) })
        {
            b.Append(s_inv, $"<text x=\"{F(x)}\" y=\"15\" fill=\"#010101\" fill-opacity=\".3\">{Esc(t)}</text>");
            b.Append(s_inv, $"<text x=\"{F(x)}\" y=\"14\">{Esc(t)}</text>\n");
        }
        b.Append("</g>\n</svg>\n");
        return b.ToString();
    }

    /// <summary>Cell colour: red (nothing) through amber to green (everything implemented).</summary>
    internal static string CellColor(double pct, bool dark)
    {
        double hue = 120 * pct / 100;
        return Hsl(hue, dark ? 0.55 : 0.60, dark ? 0.36 : 0.62);
    }

    private static string Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, hp = h / 60, x = c * (1 - Math.Abs(hp % 2 - 1)), m = l - c / 2;
        (double r, double g, double b) = hp < 1 ? (c, x, 0.0) : hp < 2 ? (x, c, 0.0) : (0.0, c, x);
        return string.Create(s_inv, $"#{(int)Math.Round((r + m) * 255):x2}{(int)Math.Round((g + m) * 255):x2}{(int)Math.Round((b + m) * 255):x2}");
    }

    /// <summary>Squarified treemap (Bruls, Huizing, van Wijk): rows of cells along the shorter side, each row grown
    /// while it keeps the worst aspect ratio from getting worse.</summary>
    internal static List<(MapCell L, double X, double Y, double W, double H)> Treemap(IReadOnlyList<MapCell> libs, double x, double y, double w, double h)
    {
        var cells = new List<(MapCell, double, double, double, double)>();
        double sum = libs.Sum(l => (double)l.Total);
        var items = libs.Select(l => (L: l, A: l.Total * w * h / sum)).ToList();
        int i = 0;
        while (i < items.Count)
        {
            double side = Math.Min(w, h), rowSum = 0, best = double.MaxValue;
            int j = i;
            while (j < items.Count)
            {
                double s = rowSum + items[j].A, max = items[i].A, min = items[j].A;
                double worst = Math.Max(side * side * max / (s * s), s * s / (side * side * min));
                if (j > i && worst > best)
                {
                    break;
                }

                rowSum = s;
                best = worst;
                j++;
            }
            if (w >= h)
            {
                double rw = rowSum / h, cy = y;
                for (int k = i; k < j; k++)
                {
                    double rh = items[k].A / rw;
                    cells.Add((items[k].L, x, cy, rw, rh));
                    cy += rh;
                }
                x += rw;
                w -= rw;
            }
            else
            {
                double rh = rowSum / w, cx = x;
                for (int k = i; k < j; k++)
                {
                    double rw = items[k].A / rh;
                    cells.Add((items[k].L, cx, y, rw, rh));
                    cx += rw;
                }
                y += rh;
                h -= rh;
            }
            i = j;
        }
        return cells;
    }

    /// <summary>One cell of the progress map: a library, its exported functions and the implemented (covered) ones.</summary>
    internal sealed record MapCell(string Name, int Impl, int Total, int Default);

    /// <summary>The progress map: one treemap region for the libraries user code imports and, with the kernel census
    /// (<paramref name="kernel"/>), one for the kernel libraries (exports to other kernel code), each region's area
    /// proportional to its exported functions and every cell on the same colour scale (implemented / total).</summary>
    private static string Map(Status s, IReadOnlyList<KernelLibrary>? kernel, bool dark)
    {
        const double W = 1000, Top = 44, Pad = 8, RegionLabel = 20, Gap = 6;
        string bg = dark ? "#0d1117" : "#ffffff", fg = dark ? "#e6edf3" : "#1f2328", muted = dark ? "#9198a1" : "#59636e";
        var user = s.Libraries.Where(l => l.Total > 0).Select(l => new MapCell(l.Name, l.Impl, l.Total, l.Default))
            .OrderByDescending(l => l.Total).ThenBy(l => l.Name, StringComparer.Ordinal).ToList();
        var kernelCells = (kernel ?? []).Where(l => l.Total > 0).Select(l => new MapCell(l.Name, l.Impl, l.Total, l.Default))
            .OrderByDescending(l => l.Total).ThenBy(l => l.Name, StringComparer.Ordinal).ToList();
        int kernelTotal = kernelCells.Sum(l => l.Total);
        int kernelImpl = kernelCells.Sum(l => l.Impl);
        // with the kernel region the map is taller and the kernel gets 45% of it (not its share of the functions, which
        // would leave its 114 libraries a thin band without names): the area is proportional within each region
        double H = kernelCells.Count > 0 ? 1100 : 600;
        string aria = kernelCells.Count > 0
            ? $"HLE progress map: {user.Count} user libraries and {kernelCells.Count} kernel libraries of the firmware"
            : $"HLE progress map: {user.Count} firmware libraries";
        var b = new StringBuilder();
        b.Append(s_inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(W)}\" height=\"{F(H)}\" viewBox=\"0 0 {F(W)} {F(H)}\" role=\"img\" aria-label=\"{Esc(aria)}\">\n");
        b.Append(s_inv, $"<rect width=\"{F(W)}\" height=\"{F(H)}\" rx=\"8\" fill=\"{bg}\"/>\n");
        b.Append("<g font-family=\"-apple-system,Segoe UI,Helvetica,Arial,sans-serif\">\n");
        b.Append(s_inv, $"<text x=\"{F(Pad + 4)}\" y=\"27\" font-size=\"17\" font-weight=\"600\" fill=\"{fg}\">HLE progress map</text>\n");
        string subtitle = kernelCells.Count > 0
            ? $"firmware {s.Firmware}: {user.Count} user + {kernelCells.Count} kernel libraries, area = exported functions"
            : $"{user.Count} libraries of firmware {s.Firmware}, area = exported functions, {PctText(s.Impl, s.Total)} implemented";
        b.Append(s_inv, $"<text x=\"{F(Pad + 4 + 158)}\" y=\"27\" font-size=\"12\" fill=\"{muted}\">{Esc(subtitle)}</text>\n");
        // legend: 0% .. 100%
        double lx = W - Pad - 4 - 5 * 26 - 60;
        b.Append(s_inv, $"<text x=\"{F(lx)}\" y=\"27\" font-size=\"11\" fill=\"{muted}\">0%</text>\n");
        for (int k = 0; k <= 4; k++)
        {
            b.Append(s_inv, $"<rect x=\"{F(lx + 22 + k * 26)}\" y=\"16\" width=\"24\" height=\"14\" rx=\"2\" fill=\"{CellColor(k * 25, dark)}\"/>\n");
        }

        b.Append(s_inv, $"<text x=\"{F(lx + 22 + 5 * 26 + 2)}\" y=\"27\" font-size=\"11\" fill=\"{muted}\">100%</text>\n");
        if (kernelCells.Count == 0)
        {
            MapRegion(b, user, Pad, Top, W - 2 * Pad, H - Top - Pad, dark);
        }
        else
        {
            // two regions stacked, each under its label band; within a region the cell areas follow the exported functions
            double cellsHeight = H - Top - Pad - 2 * RegionLabel - Gap;
            double userHeight = cellsHeight * UserMapShare;
            double y = Top;
            RegionTitle(b, $"User ({user.Count} libraries): {PctText(s.Impl, s.Total)} implemented ({Count(s.Impl)}/{Count(s.Total)})", Pad, y, fg);
            y += RegionLabel;
            MapRegion(b, user, Pad, y, W - 2 * Pad, userHeight, dark);
            y += userHeight + Gap;
            RegionTitle(b, $"Kernel ({kernelCells.Count} libraries): {PctText(kernelImpl, kernelTotal)} covered ({Count(kernelImpl)}/{Count(kernelTotal)})", Pad, y, fg);
            y += RegionLabel;
            MapRegion(b, kernelCells, Pad, y, W - 2 * Pad, cellsHeight - userHeight, dark);
        }

        b.Append("</g>\n</svg>\n");
        return b.ToString();
    }

    /// <summary>The share of the map's cell height the user region gets when the kernel region is drawn too.</summary>
    private const double UserMapShare = 0.55;

    /// <summary>The label of a cell: the library name without the "Sce" prefix and, when that does not fit, without the
    /// kernel export-table suffix too ("CtrlForDriver" -> "Ctrl"). By name because these are the firmware's fixed library
    /// names and this only shortens a label (nothing behaves differently).</summary>
    private static string CellName(string library, double size, double width)
    {
        string name = library.StartsWith("Sce", StringComparison.Ordinal) && library.Length > 3 ? library[3..] : library;
        if (TextWidth(name, size) + 6 <= width)
        {
            return name;
        }

        foreach (string suffix in s_kernelTableSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }

    private static readonly string[] s_kernelTableSuffixes = ["ForDriver", "ForKernel"];

    internal static void RegionTitle(StringBuilder b, string text, double x, double y, string fill)
    {
        b.Append(s_inv, $"<text x=\"{F(x + 4)}\" y=\"{F(y + 14)}\" font-size=\"13\" font-weight=\"600\" fill=\"{fill}\">{Esc(text)}</text>\n");
    }

    private static void MapRegion(StringBuilder b, IReadOnlyList<MapCell> libs, double regionX, double regionY, double regionWidth, double regionHeight, bool dark)
    {
        string stroke = dark ? "#0d1117" : "#ffffff";
        string label = dark ? "#ffffff" : "#1f2328";
        foreach ((MapCell? l, double x, double y, double w, double h) in Treemap(libs, regionX, regionY, regionWidth, regionHeight))
        {
            double p = Pct(l.Impl, l.Total);
            b.Append(s_inv, $"<g><title>{Esc(l.Name)}: {l.Impl}/{l.Total} ({PctText(l.Impl, l.Total)}){(l.Default > 0 ? $", {l.Default} default" : "")}</title>");
            b.Append(s_inv, $"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"{CellColor(p, dark)}\" stroke=\"{stroke}\" stroke-width=\"1\"/>");
            double size = Math.Clamp(Math.Min(h / 2.6, w / 7), 0, 13);
            string name = CellName(l.Name, size, w);
            if (size >= 7 && TextWidth(name, size) + 6 <= w)
            {
                b.Append(s_inv, $"<text x=\"{F(x + 3)}\" y=\"{F(y + size + 2)}\" font-size=\"{F(size)}\" fill=\"{label}\">{Esc(name)}</text>");
                if (h >= 2 * size + 8)
                {
                    b.Append(s_inv, $"<text x=\"{F(x + 3)}\" y=\"{F(y + 2 * size + 5)}\" font-size=\"{F(size * 0.85)}\" fill=\"{label}\" fill-opacity=\".8\">{PctText(l.Impl, l.Total)}</text>");
                }
            }
            b.Append("</g>\n");
        }
    }

    private static string ProgressPage(Status s, IReadOnlyList<KernelLibrary>? kernel)
    {
        var b = new StringBuilder();
        b.Append("# HLE progress\n\n");
        b.Append("<!-- Generated by build/hle-status.ps1 (VitaSharp.Desktop hle-coverage --status) from docs/hle/data/hle-status.tsv; do not edit by hand. -->\n\n");
        b.Append(s_inv, $"How many of the functions that the PS Vita firmware {s.Firmware} exports to user code (the export tables of its user modules and the syscall libraries of its kernel modules) have an HLE implementation in VitaSharp. ");
        b.Append(s_inv, $"\"Games\" are the exported functions that the {s.GameTitles} installed test games import, \"system\" the ones only the system software (shell, system apps and modules) imports, \"general\" the rest. ");
        b.Append("This is not a count of every PS Vita system function: functions that only kernel code calls, and imports no installed firmware module exports, are not counted. ");
        b.Append("The VitaSDK row counts the user functions of the VitaSDK NID list (`docs/hle/data/vitasdk-user-functions.tsv`). ");
        b.Append("A function counts as implemented when it has an `[Hle]` entry point. \"Default\" is the part of those that needs hardware or a service the emulator does not have (Bluetooth, USB, game card authentication, system update, PSP ad hoc, debug hooks...) and returns the result the firmware gives for that case (success or its specific error) without doing anything; \"real\" is the rest. \"LLE\" counts the functions of libraries the emulator provides by loading and running the real module (the game's own copy in app0:sce_module, or the firmware module the loader gives the programs that import a library the HLE does not emulate; found by scanning the installed modules, listed below), and the functions the HLE has only as a stand-in for a missing firmware module (libc, libpaf, sqlite...: system programs get the real module): they are covered, with no HLE.\n\n");
        b.Append("<picture>\n  <source media=\"(prefers-color-scheme: light)\" srcset=\"../assets/status/progress-map-light.svg\">\n");
        b.Append("  <img src=\"../assets/status/progress-map.svg\" alt=\"HLE progress map: one cell per firmware library, user and kernel\" width=\"100%\">\n</picture>\n\n");
        b.Append("| Group | Covered | Real | Default | LLE | Total | % |\n|---|---:|---:|---:|---:|---:|---:|\n");
        foreach ((string? label, int n, int d, int l, int total) in new[] { ("Games imports", s.GamesImpl, s.GamesDefault, 0, s.Games), ("System imports", s.SystemImpl, s.SystemDefault, 0, s.System),
                     ("General", s.Impl - s.GamesImpl - s.SystemImpl, s.Default - s.GamesDefault - s.SystemDefault, 0, s.Total - s.Games - s.System),
                     ($"All firmware {s.Firmware} exports", s.Impl, s.Default, s.Lle, s.Total), ("VitaSDK user functions", s.VitaSdkImpl, s.VitaSdkDefault, s.VitaSdkLle, s.VitaSdk) })
        {
            b.Append(s_inv, $"| {label} | {Count(n)} | {Count(n - d - l)} | {Count(d)} | {Count(l)} | {Count(total)} | {PctText(n, total)} |\n");
        }

        // the firmware libraries the measurement found LLE functions in (Hle.LleLibraries: installed modules that really
        // export them, found by scanning)
        b.Append("\n## Libraries provided by LLE\n\n| Library | Module | LLE functions |\n|---|---|---:|\n");
        foreach (Library? l in s.Libraries.Where(l => l.Lle > 0))
        {
            b.Append(s_inv, $"| {l.Name} | `{l.Module}` | {Count(l.Lle)} |\n");
        }

        b.Append(s_inv, $"\nVitaSDK user functions provided by LLE (the modules the installed games ship in app0:sce_module): {Count(s.VitaSdkLle)}.\n");

        if (kernel is { Count: > 0 })
        {
            KernelSection(b, s.Firmware, kernel);
        }

        b.Append(s_inv, $"\n## Per library ({s.Libraries.Count})\n\n");
        b.Append("| Library | Module | Implemented / total | % | Default | Games | System |\n|---|---|---:|---:|---:|---:|---:|\n");
        foreach (Library l in s.Libraries)
        {
            b.Append(s_inv, $"| {l.Name} | `{l.Module}` | {l.Impl} / {l.Total} | {PctText(l.Impl, l.Total)} | {(l.Default == 0 ? "-" : Count(l.Default))} | {(l.Games == 0 ? "-" : $"{l.GamesImpl} / {l.Games}")} | {(l.System == 0 ? "-" : $"{l.SystemImpl} / {l.System}")} |\n");
        }

        return b.ToString();
    }

    /// <summary>The kernel side (docs/hle/kernel-exports.md has the method and the history): totals and the table per
    /// library, generated from the census table.</summary>
    private static void KernelSection(StringBuilder b, string firmware, IReadOnlyList<KernelLibrary> kernel)
    {
        int total = kernel.Sum(l => l.Total);
        int impl = kernel.Sum(l => l.Impl);
        int def = kernel.Sum(l => l.Default);
        int imported = kernel.Sum(l => l.Imported);
        int importedImpl = kernel.Sum(l => l.ImportedImpl);
        b.Append(s_inv, $"\n## Kernel ({kernel.Count} libraries)\n\n");
        b.Append(s_inv, $"The functions the kernel modules of firmware {firmware} export to other kernel code (`*ForDriver`, `*ForKernel`...: what a kernel plugin can import), from the census in [kernel-exports.md](kernel-exports.md) (`VitaSharp.Desktop kernel-census`, `data/kernel-exports.tsv`). \"Imported\" are the ones some firmware kernel module or installed kernel plugin imports: the order of the work.\n\n");
        b.Append("| Kernel exports | Covered | Real | Default | Total | % |\n|---|---:|---:|---:|---:|---:|\n");
        b.Append(s_inv, $"| All | {Count(impl)} | {Count(impl - def)} | {Count(def)} | {Count(total)} | {PctText(impl, total)} |\n");
        b.Append(s_inv, $"| Imported | {Count(importedImpl)} | - | - | {Count(imported)} | {PctText(importedImpl, imported)} |\n");
        b.Append("\n| Library | Implemented / total | % | Default | Imported |\n|---|---:|---:|---:|---:|\n");
        foreach (KernelLibrary l in kernel)
        {
            b.Append(s_inv, $"| {l.Name} | {l.Impl} / {l.Total} | {PctText(l.Impl, l.Total)} | {(l.Default == 0 ? "-" : Count(l.Default))} | {(l.Imported == 0 ? "-" : $"{l.ImportedImpl} / {l.Imported}")} |\n");
        }
    }
}
