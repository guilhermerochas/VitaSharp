using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VitaSharp.Core.Firmware;

/// <summary>
/// The compat side of the README status (the Vita's compatibility core, SceCompat's Allegrex). VitaSharp does not
/// reimplement the modules that run on it: like a Vita it runs the
/// firmware 6.61 of <c>pcff</c> (the kermit flash0) on its Allegrex core inside SceCompat
/// (docs/notes/psp-compat.md). So this measures the hardware: which emulated blocks each firmware module uses and the
/// state of each block in VitaSharp, a module's colour being its worst block. Everything is rendered from the
/// versioned source docs/compat/coverage.json (<c>VitaSharp.Desktop compat-coverage --status=&lt;repo&gt;</c>, run by
/// build/hle-status); CompatCoverageReportTests renders it again and compares, so the committed map, badge and pages
/// cannot drift from the source.
/// </summary>
public static class CompatCoverageReport
{
    public const string SourcePath = "docs/compat/coverage.json";
    public const string ProgressPath = "docs/compat/progress.md";
    public const string ProgressPtPath = "docs/compat/progress.pt-BR.md";
    public const string MapPath = HleStatusReport.AssetDir + "/progress-map-compat.svg";
    public const string MapLightPath = HleStatusReport.AssetDir + "/progress-map-compat-light.svg";
    public const string BadgePath = HleStatusReport.AssetDir + "/badge-compat.svg";

    /// <summary>A block's state in VitaSharp. Policy: not emulated by a project decision (KIRK). Absent: hardware the
    /// Vita's compat core does not have (no module of the flash0 drives it).</summary>
    public enum BlockState
    {
        Done,
        Partial,
        Missing,
        Policy,
        Absent,
    }

    /// <summary>A text in English and Brazilian Portuguese (the two progress pages).</summary>
    public sealed record Text(string En, string Pt)
    {
        public string Get(bool portuguese) => portuguese ? Pt : En;
    }

    /// <summary>One emulated hardware block: id, state, the source files that implement it, a note.</summary>
    public sealed record Block(string Id, BlockState State, Text Name, IReadOnlyList<string> Code, Text Note);

    /// <summary>One module of the flash0 (path inside it), the blocks it uses and its role.</summary>
    public sealed record Module(string Path, IReadOnlyList<string> Uses, Text Role);

    public sealed record Coverage(string Firmware, IReadOnlyList<Block> Blocks, IReadOnlyList<Module> Modules);

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    public static Coverage Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string firmware = root.GetProperty("firmware").GetString() ?? "?";
        List<Block> blocks = [];
        foreach (JsonElement element in root.GetProperty("blocks").EnumerateArray())
        {
            List<string> code = [];
            foreach (JsonElement path in element.GetProperty("code").EnumerateArray())
            {
                code.Add(path.GetString() ?? "");
            }

            blocks.Add(new Block(element.GetProperty("id").GetString() ?? "", ParseState(element.GetProperty("state").GetString() ?? ""),
                ReadText(element.GetProperty("name")), code, ReadText(element.GetProperty("note"))));
        }

        List<Module> modules = [];
        foreach (JsonElement element in root.GetProperty("modules").EnumerateArray())
        {
            List<string> uses = [];
            foreach (JsonElement id in element.GetProperty("uses").EnumerateArray())
            {
                uses.Add(id.GetString() ?? "");
            }

            modules.Add(new Module(element.GetProperty("path").GetString() ?? "", uses, ReadText(element.GetProperty("role"))));
        }

        return new Coverage(firmware, blocks, modules);
    }

    private static Text ReadText(JsonElement element) =>
        new(element.GetProperty("en").GetString() ?? "", element.GetProperty("pt").GetString() ?? "");

    public static BlockState ParseState(string state) => state switch
    {
        "done" => BlockState.Done,
        "partial" => BlockState.Partial,
        "missing" => BlockState.Missing,
        "policy" => BlockState.Policy,
        "absent" => BlockState.Absent,
        _ => throw new FormatException($"unknown block state \"{state}\""),
    };

    /// <summary>How bad a state is for a module that needs the block: missing, then policy, then partial, then done.</summary>
    private static int Severity(BlockState state) => state switch
    {
        BlockState.Missing => 4,
        BlockState.Policy => 3,
        BlockState.Partial => 2,
        BlockState.Absent => 1,
        _ => 0,
    };

    /// <summary>What is wrong with the source: duplicate ids or modules, a module using a block the source does not
    /// have or one the Vita does not have (absent), a module with no block. Empty when consistent.</summary>
    public static IReadOnlyList<string> Problems(Coverage coverage)
    {
        List<string> problems = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (Block block in coverage.Blocks)
        {
            if (!ids.Add(block.Id))
            {
                problems.Add($"block \"{block.Id}\" is listed twice");
            }
        }

        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (Module module in coverage.Modules)
        {
            if (!paths.Add(module.Path))
            {
                problems.Add($"module {module.Path} is listed twice");
            }

            if (module.Uses.Count == 0)
            {
                problems.Add($"module {module.Path} uses no block");
            }

            foreach (string id in module.Uses)
            {
                Block? block = Find(coverage, id);
                if (block == null)
                {
                    problems.Add($"module {module.Path} uses \"{id}\", which is not a block of the source");
                }
                else if (block.State == BlockState.Absent)
                {
                    problems.Add($"module {module.Path} uses \"{id}\", which the Vita does not have (absent)");
                }
            }
        }

        return problems;
    }

    private static Block? Find(Coverage coverage, string id)
    {
        foreach (Block block in coverage.Blocks)
        {
            if (block.Id == id)
            {
                return block;
            }
        }

        return null;
    }

    /// <summary>The worst state among the blocks a module uses (its colour on the map).</summary>
    public static BlockState Worst(Coverage coverage, Module module)
    {
        BlockState worst = BlockState.Done;
        foreach (string id in module.Uses)
        {
            Block? block = Find(coverage, id);
            BlockState state = block?.State ?? BlockState.Missing;
            if (Severity(state) > Severity(worst))
            {
                worst = state;
            }
        }

        return worst;
    }

    /// <summary>How many modules use a block.</summary>
    public static int UsedBy(Coverage coverage, string id) => coverage.Modules.Count(m => m.Uses.Contains(id, StringComparer.Ordinal));

    /// <summary>The badge's counts: done blocks of the ones the Vita has (absent left out), and the partial ones.</summary>
    public static (string Label, int Done, int Total, int Partial) BadgeCounts(Coverage coverage)
    {
        var applicable = coverage.Blocks.Where(b => b.State != BlockState.Absent).ToList();
        return ($"Compat {coverage.Firmware}", applicable.Count(b => b.State == BlockState.Done), applicable.Count,
            applicable.Count(b => b.State == BlockState.Partial));
    }

    /// <summary>Every generated file: repository-relative path (forward slashes) to its content (LF line ends).</summary>
    public static IReadOnlyDictionary<string, string> Render(Coverage coverage)
    {
        SortedDictionary<string, string> files = new(StringComparer.Ordinal);
        (string label, int done, int total, int partial) = BadgeCounts(coverage);
        double pct = HleStatusReport.Pct(done, total);
        string value = $"{HleStatusReport.PctText(done, total)} ({done}/{total} blocks, {partial} partial)";
        files[BadgePath] = HleStatusReport.Badge(label, value, HleStatusReport.BadgeColor(pct));
        files[MapPath] = Map(coverage, dark: true);
        files[MapLightPath] = Map(coverage, dark: false);
        string english = ProgressPage(coverage, portuguese: false);
        string portuguese = ProgressPage(coverage, portuguese: true);
        files[ProgressPath] = PairLine("progress.pt-BR.md", portuguese) + english;
        files[ProgressPtPath] = PairLine("progress.md", english) + portuguese;
        return files;
    }

    /// <summary>The first line of a paired doc: the other file's name and the SHA-256 of its text without its own
    /// pair line (ExternalDocPairsTests checks it).</summary>
    private static string PairLine(string otherName, string otherBody) =>
        $"<!-- pair: {otherName} sha256={Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(otherBody)))} -->\n";

    private static string StateColor(BlockState state, bool dark) => state switch
    {
        BlockState.Done => HleStatusReport.CellColor(100, dark),
        BlockState.Partial => HleStatusReport.CellColor(50, dark),
        BlockState.Missing => HleStatusReport.CellColor(0, dark),
        BlockState.Policy => dark ? "#6e7681" : "#afb8c1",
        _ => dark ? "#21262d" : "#eaeef2",
    };

    private static string StateName(BlockState state, bool portuguese) => state switch
    {
        BlockState.Done => portuguese ? "feito" : "done",
        BlockState.Partial => portuguese ? "parcial" : "partial",
        BlockState.Missing => portuguese ? "falta" : "missing",
        BlockState.Policy => portuguese ? "não emulado por política" : "not emulated by policy",
        _ => portuguese ? "ausente no Vita" : "absent on the Vita",
    };

    private static readonly BlockState[] s_states = [BlockState.Done, BlockState.Partial, BlockState.Missing, BlockState.Policy, BlockState.Absent];

    /// <summary>The module's label: its file name without ".prx".</summary>
    private static string ModuleName(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        return name.EndsWith(".prx", StringComparison.Ordinal) ? name[..^4] : name;
    }

    /// <summary>The map: a region with one cell per hardware block (area = modules that use it, colour = its state)
    /// and two regions with one cell per module of flash0's kd and vsh/module (area = blocks it uses, colour = its
    /// worst block).</summary>
    private static string Map(Coverage coverage, bool dark)
    {
        const double W = 1000, H = 1100, Top = 44, Pad = 8, RegionLabel = 20, Gap = 6, HardwareShare = 0.28;
        // every block cell gets this much area on top of its modules, so the blocks few or no modules name (FPU, VFPU,
        // the absent UMD) stay big enough to read
        const int HardwareCellFloor = 6;
        string bg = dark ? "#0d1117" : "#ffffff";
        string fg = dark ? "#e6edf3" : "#1f2328";
        string muted = dark ? "#9198a1" : "#59636e";
        var kernel = coverage.Modules.Where(m => m.Path.StartsWith("kd/", StringComparison.Ordinal)).ToList();
        var vsh = coverage.Modules.Where(m => !m.Path.StartsWith("kd/", StringComparison.Ordinal)).ToList();
        string aria = $"Compat hardware map: {coverage.Blocks.Count} emulated blocks of the Vita's compat core and the {coverage.Modules.Count} modules of its firmware {coverage.Firmware}";
        StringBuilder b = new();
        b.Append(s_inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(W)}\" height=\"{F(H)}\" viewBox=\"0 0 {F(W)} {F(H)}\" role=\"img\" aria-label=\"{Esc(aria)}\">\n");
        b.Append(s_inv, $"<rect width=\"{F(W)}\" height=\"{F(H)}\" rx=\"8\" fill=\"{bg}\"/>\n");
        b.Append("<g font-family=\"-apple-system,Segoe UI,Helvetica,Arial,sans-serif\">\n");
        b.Append(s_inv, $"<text x=\"{F(Pad + 4)}\" y=\"27\" font-size=\"17\" font-weight=\"600\" fill=\"{fg}\">Compat hardware map</text>\n");
        string subtitle = $"firmware {coverage.Firmware} (flash0): {coverage.Modules.Count} modules, colour = worst block used";
        b.Append(s_inv, $"<text x=\"{F(Pad + 4 + HleStatusReport.TextWidth("Compat hardware map", 17) + 14)}\" y=\"27\" font-size=\"12\" fill=\"{muted}\">{Esc(subtitle)}</text>\n");
        // legend: one swatch per state a module can show
        double legendX = W - Pad - 4 - 300;
        foreach (BlockState state in new[] { BlockState.Done, BlockState.Partial, BlockState.Missing, BlockState.Policy })
        {
            string name = state == BlockState.Policy ? "policy" : StateName(state, portuguese: false);
            b.Append(s_inv, $"<rect x=\"{F(legendX)}\" y=\"16\" width=\"14\" height=\"14\" rx=\"2\" fill=\"{StateColor(state, dark)}\"/>\n");
            b.Append(s_inv, $"<text x=\"{F(legendX + 18)}\" y=\"27\" font-size=\"11\" fill=\"{muted}\">{name}</text>\n");
            legendX += 18 + HleStatusReport.TextWidth(name) + 14;
        }

        double cellsHeight = H - Top - Pad - 3 * RegionLabel - 2 * Gap;
        double hardwareHeight = cellsHeight * HardwareShare;
        double modulesHeight = cellsHeight - hardwareHeight;
        double kernelHeight = modulesHeight * kernel.Count / Math.Max(1, coverage.Modules.Count);
        double y = Top;
        IEnumerable<string> counts = s_states.Select(s => (State: s, Count: coverage.Blocks.Count(x => x.State == s))).Where(c => c.Count > 0)
            .Select(c => $"{c.Count} {(c.State == BlockState.Policy ? "policy" : c.State == BlockState.Absent ? "absent" : StateName(c.State, false))}");
        HleStatusReport.RegionTitle(b, $"Emulated hardware ({coverage.Blocks.Count} blocks): {string.Join(", ", counts)}", Pad, y, fg);
        y += RegionLabel;
        List<(string Name, string Title, BlockState State, int Weight)> blockCells = coverage.Blocks
            .Select(x => (x.Id, $"{x.Name.En}: {StateName(x.State, false)}, used by {UsedBy(coverage, x.Id)} modules", x.State, UsedBy(coverage, x.Id) + HardwareCellFloor))
            .OrderByDescending(c => c.Item4).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        Region(b, blockCells, Pad, y, W - 2 * Pad, hardwareHeight, dark);
        y += hardwareHeight + Gap;
        foreach ((string title, List<Module> modules, double height) in new[] { ("flash0 kd", kernel, kernelHeight), ("flash0 vsh/module", vsh, modulesHeight - kernelHeight) })
        {
            int ready = modules.Count(m => Worst(coverage, m) == BlockState.Done);
            HleStatusReport.RegionTitle(b, $"{title} ({modules.Count} modules): {ready} with every block done", Pad, y, fg);
            y += RegionLabel;
            List<(string Name, string Title, BlockState State, int Weight)> cells = modules
                .Select(m => (ModuleName(m.Path), ModuleTitle(coverage, m), Worst(coverage, m), m.Uses.Count))
                .OrderByDescending(c => c.Count).ThenBy(c => c.Item1, StringComparer.Ordinal).ToList();
            Region(b, cells, Pad, y, W - 2 * Pad, height, dark);
            y += height + Gap;
        }

        b.Append("</g>\n</svg>\n");
        return b.ToString();
    }

    private static string ModuleTitle(Coverage coverage, Module module)
    {
        IEnumerable<string> parts = module.Uses.Select(id => $"{id} {StateName(Find(coverage, id)?.State ?? BlockState.Missing, false)}");
        return $"{module.Path}: {StateName(Worst(coverage, module), false)} ({string.Join(", ", parts)})";
    }

    private static void Region(StringBuilder b, List<(string Name, string Title, BlockState State, int Weight)> cells, double regionX, double regionY,
        double regionWidth, double regionHeight, bool dark)
    {
        string stroke = dark ? "#0d1117" : "#ffffff";
        string label = dark ? "#ffffff" : "#1f2328";
        string mutedLabel = dark ? "#9198a1" : "#59636e";
        List<HleStatusReport.MapCell> items = [];
        for (int i = 0; i < cells.Count; i++)
        {
            // the treemap sizes by Total; Impl carries the index back to the cell
            items.Add(new HleStatusReport.MapCell(cells[i].Name, i, cells[i].Weight, 0));
        }

        foreach ((HleStatusReport.MapCell item, double x, double y, double w, double h) in HleStatusReport.Treemap(items, regionX, regionY, regionWidth, regionHeight))
        {
            (string name, string title, BlockState state, int _) = cells[item.Impl];
            b.Append(s_inv, $"<g><title>{Esc(title)}</title>");
            b.Append(s_inv, $"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"{StateColor(state, dark)}\" stroke=\"{stroke}\" stroke-width=\"1\"/>");
            double size = Math.Clamp(Math.Min(h / 2.6, w / 7), 0, 13);
            if (size >= 6 && HleStatusReport.TextWidth(name, size) + 6 > w)
            {
                size = Math.Max(6, (w - 6) / Math.Max(1, HleStatusReport.TextWidth(name, 1)));
            }

            if (size >= 6 && HleStatusReport.TextWidth(name, size) + 6 <= w && size + 4 <= h)
            {
                string fill = state == BlockState.Absent ? mutedLabel : label;
                b.Append(s_inv, $"<text x=\"{F(x + 3)}\" y=\"{F(y + size + 2)}\" font-size=\"{F(size)}\" fill=\"{fill}\">{Esc(name)}</text>");
            }

            b.Append("</g>\n");
        }
    }

    private static string Esc(string text) => HleStatusReport.Esc(text);

    private static string F(double value) => HleStatusReport.F(value);

    private static string ProgressPage(Coverage coverage, bool portuguese)
    {
        bool pt = portuguese;
        StringBuilder b = new();
        b.Append(pt ? "# Cobertura do hardware do PSP\n\n" : "# PSP hardware coverage\n\n");
        b.Append(pt ? "*English: [progress.md](progress.md).*\n\n" : "*Português: [progress.pt-BR.md](progress.pt-BR.md).*\n\n");
        b.Append(pt
            ? "<!-- Gerado por build/hle-status (VitaSharp.Desktop compat-coverage --status) a partir de docs/compat/coverage.json; não edite à mão. -->\n\n"
            : "<!-- Generated by build/hle-status (VitaSharp.Desktop compat-coverage --status) from docs/compat/coverage.json; do not edit by hand. -->\n\n");
        if (pt)
        {
            b.Append(s_inv, $"O que o firmware do Compat (o núcleo de compatibilidade do Vita: o Allegrex dentro do SoC, que o firmware chama de SceCompat) precisa do hardware emulado. O VitaSharp não reimplementa os módulos que rodam nele (não há HLE deste lado): como um Vita, ele roda o firmware {coverage.Firmware} que vem no `pcff` (o flash0 kermit que vem no app de sistema do emulador de PSP) nesse núcleo ([psp-compat.pt-BR.md](../notes/psp-compat.pt-BR.md)). ");
            b.Append("Por isso esta página mede o hardware: uma linha por bloco emulado com o estado dele no VitaSharp e uma linha por módulo do firmware com os blocos que ele usa; a cor do módulo é a do pior bloco.\n\n");
            b.Append("- Os blocos que o Vita atende do lado ARM (Memory Stick, flash, áudio, Media Engine, Wi-Fi, USB, syscon, utilitários) chegam ao ScePspemu real como pedidos kermit; ficam parciais até esse caminho rodar de ponta a ponta.\n");
            b.Append("- Todo módulo do flash0 é cifrado (`~PSP`) e carregado pelo `memlmd`, que precisa do KIRK: não emulado por política, mostrado nos módulos que o chamam (memlmd e os de DRM) em vez de repetido em todos.\n");
            b.Append("- O código de boot (`pcbc`) continua cifrado para o processador seguro: o núcleo só sobe o firmware real com um `pcbc.bin` tirado pelo usuário (psp-compat.pt-BR.md).\n");
            b.Append("- Os nomes dos módulos vêm da tabela de arquivos do `pcff`; os papéis, do uOFW, dos canais kermit e das listas de módulos do psdevwiki (\"a confirmar\" onde nenhuma fonte dá o papel). O estado de cada bloco é o que `src/VitaSharp.Core/Compat` implementa.\n\n");
        }
        else
        {
            b.Append(s_inv, $"What the Compat firmware (the Vita's compatibility core: the Allegrex inside the SoC, which the firmware calls SceCompat) needs from the emulated hardware. VitaSharp does not reimplement the modules that run on it (no HLE on this side): like a Vita, it runs the firmware {coverage.Firmware} that comes in `pcff` (the kermit flash0 that ships in the PSP emulator system app) on that core ([psp-compat.md](../notes/psp-compat.md)). ");
            b.Append("So this page measures the hardware: one row per emulated block with its state in VitaSharp, and one row per firmware module with the blocks it uses; a module's colour is its worst block.\n\n");
            b.Append("- The blocks the Vita serves on the ARM side (Memory Stick, flash, audio, Media Engine, Wi-Fi, USB, syscon, utilities) reach the real ScePspemu as kermit requests; they stay partial until that path runs end to end.\n");
            b.Append("- Every flash0 module is encrypted (`~PSP`) and loaded through `memlmd`, which needs KIRK: not emulated by policy, shown on the modules that call it (memlmd and the DRM ones) rather than repeated on every module.\n");
            b.Append("- The boot code (`pcbc`) is still encrypted for the secure processor: the core boots the real firmware only with a `pcbc.bin` dumped by the user (psp-compat.md).\n");
            b.Append("- Module names come from the file table of `pcff`; roles from uOFW, the kermit channels and psdevwiki's PSP module lists (\"to confirm\" where no source gives the role). Each block's state is what `src/VitaSharp.Core/Compat` implements.\n\n");
        }

        b.Append("<picture>\n  <source media=\"(prefers-color-scheme: light)\" srcset=\"../assets/status/progress-map-compat-light.svg\">\n");
        b.Append(pt
            ? "  <img src=\"../assets/status/progress-map-compat.svg\" alt=\"Mapa do hardware Compat: um bloco por célula e os módulos do flash0 coloridos pelo pior bloco que usam\" width=\"100%\">\n</picture>\n\n"
            : "  <img src=\"../assets/status/progress-map-compat.svg\" alt=\"Compat hardware map: one cell per block and the flash0 modules coloured by the worst block they use\" width=\"100%\">\n</picture>\n\n");
        b.Append(pt ? "| Estado | Blocos | Módulos (pior bloco) |\n|---|---:|---:|\n" : "| State | Blocks | Modules (worst block) |\n|---|---:|---:|\n");
        foreach (BlockState state in s_states)
        {
            int blocks = coverage.Blocks.Count(x => x.State == state);
            int modules = coverage.Modules.Count(m => Worst(coverage, m) == state);
            b.Append(s_inv, $"| {StateName(state, pt)} | {blocks} | {(state == BlockState.Absent ? "-" : modules.ToString(s_inv))} |\n");
        }

        b.Append(s_inv, $"| Total |{coverage.Blocks.Count} | {coverage.Modules.Count} |\n");
        b.Append(pt ? $"\n## Blocos de hardware ({coverage.Blocks.Count})\n\n" : $"\n## Hardware blocks ({coverage.Blocks.Count})\n\n");
        b.Append(pt ? "| Bloco | Id | Estado | Usado por | Código | Notas |\n|---|---|---|---:|---|---|\n" : "| Block | Id | State | Used by | Code | Notes |\n|---|---|---|---:|---|---|\n");
        foreach (Block block in coverage.Blocks)
        {
            string code = block.Code.Count == 0 ? "-" : string.Join(", ", block.Code.Select(c => $"[{c[(c.LastIndexOf('/') + 1)..]}](../../{c})"));
            b.Append(s_inv, $"| {block.Name.Get(pt)} | `{block.Id}` | {StateName(block.State, pt)} | {UsedBy(coverage, block.Id)} | {code} | {block.Note.Get(pt)} |\n");
        }

        b.Append(pt ? $"\n## Módulos do firmware {coverage.Firmware} ({coverage.Modules.Count})\n\n" : $"\n## Modules of firmware {coverage.Firmware} ({coverage.Modules.Count})\n\n");
        b.Append(pt ? "| Módulo | Papel | Blocos | Pior |\n|---|---|---|---|\n" : "| Module | Role | Blocks | Worst |\n|---|---|---|---|\n");
        foreach (Module module in coverage.Modules)
        {
            b.Append(s_inv, $"| `{module.Path}` | {module.Role.Get(pt)} | {string.Join(", ", module.Uses.Select(u => $"`{u}`"))} | {StateName(Worst(coverage, module), pt)} |\n");
        }

        return b.ToString();
    }
}
