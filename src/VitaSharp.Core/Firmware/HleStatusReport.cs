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
    public const string ProgressPath = "docs/hle/progress.md";
    public const string AssetDir = "docs/assets/status";

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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
        b.Append(Inv, $"# firmware={s.Firmware} games={s.GameTitles} vitasdk={s.VitaSdkImpl}/{s.VitaSdk} vitasdk_default={s.VitaSdkDefault} vitasdk_lle={s.VitaSdkLle}\n");
        b.Append("library\tmodule\ttotal\timpl\tgames\tgames_impl\tsystem\tsystem_impl\tdefault\tgames_default\tsystem_default\tlle\n");
        foreach (var l in s.Libraries)
        {
            b.Append(Inv, $"{l.Name}\t{l.Module}\t{l.Total}\t{l.Impl}\t{l.Games}\t{l.GamesImpl}\t{l.System}\t{l.SystemImpl}\t{l.Default}\t{l.GamesDefault}\t{l.SystemDefault}\t{l.Lle}\n");
        }

        return b.ToString();
    }

    public static Status Parse(string tsv)
    {
        string firmware = "?";
        int games = 0, sdk = 0, sdkImpl = 0, sdkDefault = 0, sdkLle = 0;
        var libs = new List<Library>();
        foreach (var raw in tsv.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            if (raw.StartsWith("# firmware=", StringComparison.Ordinal))
            {
                foreach (var kv in raw[2..].Split(' '))
                {
                    var p = kv.Split('=');
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
                        games = int.Parse(p[1], Inv);
                    }
                    else if (p[0] == "vitasdk")
                    {
                        var f = p[1].Split('/');
                        sdkImpl = int.Parse(f[0], Inv);
                        sdk = int.Parse(f[1], Inv);
                    }
                    else if (p[0] == "vitasdk_default")
                    {
                        sdkDefault = int.Parse(p[1], Inv);
                    }
                    else if (p[0] == "vitasdk_lle")
                    {
                        sdkLle = int.Parse(p[1], Inv);
                    }
                }
                continue;
            }
            if (raw[0] == '#' || raw.StartsWith("library\t", StringComparison.Ordinal))
            {
                continue;
            }

            var c = raw.Split('\t');
            libs.Add(new Library(c[0], c[1], I(c[2]), I(c[3]), I(c[4]), I(c[5]), I(c[6]), I(c[7]),
                c.Length > 10 ? I(c[8]) : 0, c.Length > 10 ? I(c[9]) : 0, c.Length > 10 ? I(c[10]) : 0, c.Length > 11 ? I(c[11]) : 0));
        }
        return new Status(firmware, games, sdk, sdkImpl, libs, sdkDefault, sdkLle);
        static int I(string v) => int.Parse(v, Inv);
    }

    static double Pct(int n, int total) => total == 0 ? 0 : 100.0 * n / total;

    /// <summary>"99.1%", never rounded up to 100% while something is missing.</summary>
    static string PctText(int n, int total)
    {
        double p = Math.Floor(Pct(n, total) * 10) / 10;
        return p.ToString(p == Math.Floor(p) ? "0" : "0.0", Inv) + "%";
    }

    static string Count(int n) => n.ToString("N0", Inv);

    /// <summary>Every generated file: repository-relative path (forward slashes) to its content (LF line ends).</summary>
    public static IReadOnlyDictionary<string, string> Render(Status s)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, label, n, total, d, lle) in BadgesWithParts(s))
        {
            files[$"{AssetDir}/badge-{id}.svg"] = Badge(label, $"{PctText(n, total)} ({Count(n)}/{Count(total)}{(d > 0 ? $", {Count(d)} default" : "")}{(lle > 0 ? $", {Count(lle)} LLE" : "")})", BadgeColor(Pct(n, total)));
        }

        files[$"{AssetDir}/progress-map.svg"] = Map(s, dark: true);
        files[$"{AssetDir}/progress-map-light.svg"] = Map(s, dark: false);
        files[ProgressPath] = ProgressPage(s);
        return files;
    }

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

    static string BadgeColor(double p) => p >= 95 ? "#4c1" : p >= 80 ? "#97ca00" : p >= 60 ? "#a4a61d" : p >= 40 ? "#dfb317" : p >= 20 ? "#fe7d37" : "#e05d44";

    /// <summary>Approximate advance of a string in Verdana 11 px (shields.io's font), enough to size the boxes.</summary>
    static double TextWidth(string t, double size = 11)
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

    static string Esc(string t) => t.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    static string F(double v) => Math.Round(v, 1).ToString("0.#", Inv);

    /// <summary>A flat shields.io-style badge.</summary>
    static string Badge(string label, string value, string color)
    {
        double lw = Math.Ceiling(TextWidth(label) + 12), vw = Math.Ceiling(TextWidth(value) + 12), w = lw + vw;
        var b = new StringBuilder();
        b.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"20\" role=\"img\" aria-label=\"{Esc(label)}: {Esc(value)}\">\n");
        b.Append(Inv, $"<title>{Esc(label)}: {Esc(value)}</title>\n");
        b.Append("<linearGradient id=\"s\" x2=\"0\" y2=\"100%\"><stop offset=\"0\" stop-color=\"#bbb\" stop-opacity=\".1\"/><stop offset=\"1\" stop-opacity=\".1\"/></linearGradient>\n");
        b.Append(Inv, $"<clipPath id=\"r\"><rect width=\"{F(w)}\" height=\"20\" rx=\"3\" fill=\"#fff\"/></clipPath>\n");
        b.Append(Inv, $"<g clip-path=\"url(#r)\"><rect width=\"{F(lw)}\" height=\"20\" fill=\"#555\"/><rect x=\"{F(lw)}\" width=\"{F(vw)}\" height=\"20\" fill=\"{color}\"/><rect width=\"{F(w)}\" height=\"20\" fill=\"url(#s)\"/></g>\n");
        b.Append("<g fill=\"#fff\" text-anchor=\"middle\" font-family=\"Verdana,Geneva,DejaVu Sans,sans-serif\" font-size=\"11\">\n");
        foreach (var (x, t) in new[] { (lw / 2, label), (lw + vw / 2, value) })
        {
            b.Append(Inv, $"<text x=\"{F(x)}\" y=\"15\" fill=\"#010101\" fill-opacity=\".3\">{Esc(t)}</text>");
            b.Append(Inv, $"<text x=\"{F(x)}\" y=\"14\">{Esc(t)}</text>\n");
        }
        b.Append("</g>\n</svg>\n");
        return b.ToString();
    }

    /// <summary>Cell colour: red (nothing) through amber to green (everything implemented).</summary>
    static string CellColor(double pct, bool dark)
    {
        double hue = 120 * pct / 100;
        return Hsl(hue, dark ? 0.55 : 0.60, dark ? 0.36 : 0.62);
    }

    static string Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, hp = h / 60, x = c * (1 - Math.Abs(hp % 2 - 1)), m = l - c / 2;
        var (r, g, b) = hp < 1 ? (c, x, 0.0) : hp < 2 ? (x, c, 0.0) : (0.0, c, x);
        return string.Create(Inv, $"#{(int)Math.Round((r + m) * 255):x2}{(int)Math.Round((g + m) * 255):x2}{(int)Math.Round((b + m) * 255):x2}");
    }

    /// <summary>Squarified treemap (Bruls, Huizing, van Wijk): rows of cells along the shorter side, each row grown
    /// while it keeps the worst aspect ratio from getting worse.</summary>
    static List<(Library L, double X, double Y, double W, double H)> Treemap(IReadOnlyList<Library> libs, double x, double y, double w, double h)
    {
        var cells = new List<(Library, double, double, double, double)>();
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

    static string Map(Status s, bool dark)
    {
        const double W = 1000, H = 600, Top = 44, Pad = 8;
        string bg = dark ? "#0d1117" : "#ffffff", fg = dark ? "#e6edf3" : "#1f2328", muted = dark ? "#9198a1" : "#59636e",
            stroke = bg, label = dark ? "#ffffff" : "#1f2328";
        var libs = s.Libraries.Where(l => l.Total > 0).OrderByDescending(l => l.Total).ThenBy(l => l.Name, StringComparer.Ordinal).ToList();
        var b = new StringBuilder();
        b.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(W)}\" height=\"{F(H)}\" viewBox=\"0 0 {F(W)} {F(H)}\" role=\"img\" aria-label=\"HLE progress map: {libs.Count} firmware libraries\">\n");
        b.Append(Inv, $"<rect width=\"{F(W)}\" height=\"{F(H)}\" rx=\"8\" fill=\"{bg}\"/>\n");
        b.Append("<g font-family=\"-apple-system,Segoe UI,Helvetica,Arial,sans-serif\">\n");
        b.Append(Inv, $"<text x=\"{F(Pad + 4)}\" y=\"27\" font-size=\"17\" font-weight=\"600\" fill=\"{fg}\">HLE progress map</text>\n");
        b.Append(Inv, $"<text x=\"{F(Pad + 4 + 158)}\" y=\"27\" font-size=\"12\" fill=\"{muted}\">{libs.Count} libraries of firmware {Esc(s.Firmware)}, area = exported functions, {Esc(PctText(s.Impl, s.Total))} implemented</text>\n");
        // legend: 0% .. 100%
        double lx = W - Pad - 4 - 5 * 26 - 60;
        b.Append(Inv, $"<text x=\"{F(lx)}\" y=\"27\" font-size=\"11\" fill=\"{muted}\">0%</text>\n");
        for (int k = 0; k <= 4; k++)
        {
            b.Append(Inv, $"<rect x=\"{F(lx + 22 + k * 26)}\" y=\"16\" width=\"24\" height=\"14\" rx=\"2\" fill=\"{CellColor(k * 25, dark)}\"/>\n");
        }

        b.Append(Inv, $"<text x=\"{F(lx + 22 + 5 * 26 + 2)}\" y=\"27\" font-size=\"11\" fill=\"{muted}\">100%</text>\n");
        foreach (var (l, x, y, w, h) in Treemap(libs, Pad, Top, W - 2 * Pad, H - Top - Pad))
        {
            double p = Pct(l.Impl, l.Total);
            b.Append(Inv, $"<g><title>{Esc(l.Name)}: {l.Impl}/{l.Total} ({PctText(l.Impl, l.Total)}){(l.Default > 0 ? $", {l.Default} default" : "")}</title>");
            b.Append(Inv, $"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"{CellColor(p, dark)}\" stroke=\"{stroke}\" stroke-width=\"1\"/>");
            double size = Math.Clamp(Math.Min(h / 2.6, w / 7), 0, 13);
            string name = l.Name.StartsWith("Sce", StringComparison.Ordinal) && l.Name.Length > 3 ? l.Name[3..] : l.Name;
            if (size >= 7 && TextWidth(name, size) + 6 <= w)
            {
                b.Append(Inv, $"<text x=\"{F(x + 3)}\" y=\"{F(y + size + 2)}\" font-size=\"{F(size)}\" fill=\"{label}\">{Esc(name)}</text>");
                if (h >= 2 * size + 8)
                {
                    b.Append(Inv, $"<text x=\"{F(x + 3)}\" y=\"{F(y + 2 * size + 5)}\" font-size=\"{F(size * 0.85)}\" fill=\"{label}\" fill-opacity=\".8\">{PctText(l.Impl, l.Total)}</text>");
                }
            }
            b.Append("</g>\n");
        }
        b.Append("</g>\n</svg>\n");
        return b.ToString();
    }

    static string ProgressPage(Status s)
    {
        var b = new StringBuilder();
        b.Append("# HLE progress\n\n");
        b.Append("<!-- Generated by build/hle-status.ps1 (VitaSharp.Desktop hle-coverage --status) from docs/hle/data/hle-status.tsv; do not edit by hand. -->\n\n");
        b.Append(Inv, $"How many of the functions that the PS Vita firmware {s.Firmware} exports to user code (the export tables of its user modules and the syscall libraries of its kernel modules) have an HLE implementation in VitaSharp. ");
        b.Append(Inv, $"\"Games\" are the exported functions that the {s.GameTitles} installed test games import, \"system\" the ones only the system software (shell, system apps and modules) imports, \"general\" the rest. ");
        b.Append("This is not a count of every PS Vita system function: functions that only kernel code calls, and imports no installed firmware module exports, are not counted. ");
        b.Append("The VitaSDK row counts the user functions of the VitaSDK NID list (`docs/hle/data/vitasdk-user-functions.tsv`). ");
        b.Append("A function counts as implemented when it has an `[Hle]` entry point. \"Default\" is the part of those that needs hardware or a service the emulator does not have (Bluetooth, USB, game card authentication, system update, PSP ad hoc, debug hooks...) and returns the result the firmware gives for that case (success or its specific error) without doing anything; \"real\" is the rest. \"LLE\" counts the functions of libraries the emulator provides by loading and running the real module (the game's own copy in app0:sce_module, or the firmware module the loader gives every program that imports the library; found by scanning the installed modules, listed below): they are covered, with no HLE.\n\n");
        b.Append("<picture>\n  <source media=\"(prefers-color-scheme: light)\" srcset=\"../assets/status/progress-map-light.svg\">\n");
        b.Append("  <img src=\"../assets/status/progress-map.svg\" alt=\"HLE progress map: one cell per firmware library\" width=\"100%\">\n</picture>\n\n");
        b.Append("| Group | Covered | Real | Default | LLE | Total | % |\n|---|---:|---:|---:|---:|---:|---:|\n");
        foreach (var (label, n, d, l, total) in new[] { ("Games imports", s.GamesImpl, s.GamesDefault, 0, s.Games), ("System imports", s.SystemImpl, s.SystemDefault, 0, s.System),
                     ("General", s.Impl - s.GamesImpl - s.SystemImpl, s.Default - s.GamesDefault - s.SystemDefault, 0, s.Total - s.Games - s.System),
                     ($"All firmware {s.Firmware} exports", s.Impl, s.Default, s.Lle, s.Total), ("VitaSDK user functions", s.VitaSdkImpl, s.VitaSdkDefault, s.VitaSdkLle, s.VitaSdk) })
        {
            b.Append(Inv, $"| {label} | {Count(n)} | {Count(n - d - l)} | {Count(d)} | {Count(l)} | {Count(total)} | {PctText(n, total)} |\n");
        }

        // the firmware libraries the measurement found LLE functions in (Hle.LleLibraries: installed modules that really
        // export them, found by scanning)
        b.Append("\n## Libraries provided by LLE\n\n| Library | Module | LLE functions |\n|---|---|---:|\n");
        foreach (var l in s.Libraries.Where(l => l.Lle > 0))
        {
            b.Append(Inv, $"| {l.Name} | `{l.Module}` | {Count(l.Lle)} |\n");
        }

        b.Append(Inv, $"\nVitaSDK user functions provided by LLE (the modules the installed games ship in app0:sce_module): {Count(s.VitaSdkLle)}.\n");

        b.Append(Inv, $"\n## Per library ({s.Libraries.Count})\n\n");
        b.Append("| Library | Module | Implemented / total | % | Default | Games | System |\n|---|---|---:|---:|---:|---:|---:|\n");
        foreach (var l in s.Libraries)
        {
            b.Append(Inv, $"| {l.Name} | `{l.Module}` | {l.Impl} / {l.Total} | {PctText(l.Impl, l.Total)} | {(l.Default == 0 ? "-" : Count(l.Default))} | {(l.Games == 0 ? "-" : $"{l.GamesImpl} / {l.Games}")} | {(l.System == 0 ? "-" : $"{l.SystemImpl} / {l.System}")} |\n");
        }

        return b.ToString();
    }
}
