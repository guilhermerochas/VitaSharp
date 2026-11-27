using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VitaSharp.Core.Firmware;

/// <summary>
/// The compat side of the README status (the Vita's compatibility core, SceCompat's Allegrex). VitaSharp does not
/// reimplement the modules that run on it: like a Vita it runs the
/// firmware 6.61 of <c>pcff</c> (the kermit flash0) on its Allegrex core inside SceCompat
/// (docs/notes/psp-compat.md). So this measures the hardware.
/// <para>
/// The metric: every block lists its contract items, the interface that firmware drives on it (the registers of an
/// MMIO block, the instruction groups of a CPU unit, the request path and the Vita service of a kermit channel, from
/// the sources the block's code cites). An item is implemented when it has a <c>probe</c>, a text that must appear in
/// one of the block's code files (<see cref="MissingProbes"/>: the generator and CompatCoverageReportTests refuse a
/// probe the code does not have); an item the code does not do has a <c>gap</c> saying why. A block's percentage is
/// its implemented items over its items, its state follows from it (all: done, none: missing, else partial) except
/// KIRK (not emulated by policy) and the UMD drive (absent on the Vita), which are declared. A module's percentage is
/// the implemented items over the items of the blocks it uses (the mean of their percentages weighted by item count);
/// the global one is every block's implemented items over every block's items (absent left out).
/// </para>
/// Everything is rendered from the versioned source docs/compat/coverage.json (<c>VitaSharp.Desktop compat-coverage
/// --status=&lt;repo&gt;</c>, run by build/hle-status); CompatCoverageReportTests renders it again and compares, so the
/// committed map, badge and pages cannot drift from the source.
/// </summary>
public static class CompatCoverageReport
{
    public const string SourcePath = "docs/compat/coverage.json";
    public const string ProgressPath = "docs/compat/progress.md";
    public const string ProgressPtPath = "docs/compat/progress.pt-BR.md";
    public const string MapPath = HleStatusReport.AssetDir + "/progress-map-compat.svg";
    public const string MapLightPath = HleStatusReport.AssetDir + "/progress-map-compat-light.svg";
    public const string BadgePath = HleStatusReport.AssetDir + "/badge-compat.svg";

    /// <summary>A block's state in VitaSharp. Done, partial and missing are measured from the items. Policy: not
    /// emulated by a project decision (KIRK). Absent: hardware the Vita's compat core does not have (no module of the
    /// flash0 drives it).</summary>
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

    /// <summary>One contract item of a block: implemented when it has a probe (a text of the block's code), not
    /// implemented when it has a gap (why).</summary>
    public sealed record Item(string Id, string? Probe, Text? Gap)
    {
        public bool Done => Probe != null;
    }

    /// <summary>One emulated hardware block: id, state, the source files that implement it, a note, its items.</summary>
    public sealed record Block(string Id, BlockState State, Text Name, IReadOnlyList<string> Code, Text Note, IReadOnlyList<Item> Items)
    {
        /// <summary>The implemented items.</summary>
        public int DoneItems => Items.Count(i => i.Done);
    }

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

            List<Item> items = [];
            if (element.TryGetProperty("items", out JsonElement itemList))
            {
                foreach (JsonElement item in itemList.EnumerateArray())
                {
                    string? probe = item.TryGetProperty("probe", out JsonElement probeText) ? probeText.GetString() : null;
                    Text? gap = item.TryGetProperty("gap", out JsonElement gapText) ? ReadText(gapText) : null;
                    if ((probe == null) == (gap == null))
                    {
                        throw new FormatException($"item \"{item.GetProperty("id").GetString()}\" needs exactly one of probe and gap");
                    }

                    items.Add(new Item(item.GetProperty("id").GetString() ?? "", probe, gap));
                }
            }

            string id = element.GetProperty("id").GetString() ?? "";
            BlockState state = Measured(items);
            if (element.TryGetProperty("state", out JsonElement declared))
            {
                state = ParseState(declared.GetString() ?? "");
                if (state is not (BlockState.Policy or BlockState.Absent))
                {
                    throw new FormatException($"block \"{id}\" declares \"{declared.GetString()}\": done, partial and missing are measured from its items");
                }
            }

            blocks.Add(new Block(id, state, ReadText(element.GetProperty("name")), code, ReadText(element.GetProperty("note")), items));
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

    /// <summary>The state the items give: every one implemented done, none missing, else partial.</summary>
    private static BlockState Measured(IReadOnlyList<Item> items)
    {
        int done = items.Count(i => i.Done);
        if (items.Count > 0 && done == items.Count)
        {
            return BlockState.Done;
        }

        return done == 0 ? BlockState.Missing : BlockState.Partial;
    }

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

    /// <summary>What is wrong with the source: duplicate ids or modules, a block of the Vita without items, a module
    /// using a block the source does not have or one the Vita does not have (absent), a module with no block. Empty
    /// when consistent.</summary>
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

            if (block.State != BlockState.Absent && block.Items.Count == 0)
            {
                problems.Add($"block \"{block.Id}\" has no items to measure");
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

    /// <summary>The probes the code does not have: for every implemented item, its probe must appear in one of the
    /// block's code files (<paramref name="readSource"/>: repository-relative path to its text, null when the file
    /// does not exist). This is what makes the percentages measured: an item counts only while its code is there.</summary>
    public static IReadOnlyList<string> MissingProbes(Coverage coverage, Func<string, string?> readSource)
    {
        List<string> missing = [];
        foreach (Block block in coverage.Blocks)
        {
            List<string> sources = [];
            foreach (string path in block.Code)
            {
                string? text = readSource(path);
                if (text == null)
                {
                    missing.Add($"{block.Id}: {path} does not exist");
                }
                else
                {
                    sources.Add(text);
                }
            }

            foreach (Item item in block.Items)
            {
                if (item.Probe != null && !sources.Any(s => s.Contains(item.Probe, StringComparison.Ordinal)))
                {
                    missing.Add($"{block.Id}: item \"{item.Id}\": \"{item.Probe}\" is not in the block's code");
                }
            }
        }

        return missing;
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

    /// <summary>The worst state among the blocks a module uses.</summary>
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

    /// <summary>A module's score: the implemented items over the items of the blocks it uses (its percentage).</summary>
    public static (int Done, int Total) Score(Coverage coverage, Module module)
    {
        int done = 0;
        int total = 0;
        foreach (string id in module.Uses)
        {
            Block? block = Find(coverage, id);
            if (block != null)
            {
                done += block.DoneItems;
                total += block.Items.Count;
            }
        }

        return (done, total);
    }

    /// <summary>The modules' score together: the sum of their scores.</summary>
    public static (int Done, int Total) Score(Coverage coverage, IEnumerable<Module> modules)
    {
        int done = 0;
        int total = 0;
        foreach (Module module in modules)
        {
            (int moduleDone, int moduleTotal) = Score(coverage, module);
            done += moduleDone;
            total += moduleTotal;
        }

        return (done, total);
    }

    /// <summary>The global score: every block's implemented items over every block's items (absent left out).</summary>
    public static (int Done, int Total) Score(Coverage coverage)
    {
        int done = 0;
        int total = 0;
        foreach (Block block in coverage.Blocks)
        {
            if (block.State != BlockState.Absent)
            {
                done += block.DoneItems;
                total += block.Items.Count;
            }
        }

        return (done, total);
    }

    /// <summary>How many modules use a block.</summary>
    public static int UsedBy(Coverage coverage, string id) => coverage.Modules.Count(m => m.Uses.Contains(id, StringComparer.Ordinal));

    /// <summary>The badge's counts: implemented items of the blocks the Vita has (absent left out), and the blocks
    /// done of those blocks.</summary>
    public static (string Label, int Done, int Total, int BlocksDone, int Blocks) BadgeCounts(Coverage coverage)
    {
        var applicable = coverage.Blocks.Where(b => b.State != BlockState.Absent).ToList();
        (int done, int total) = Score(coverage);
        return ($"Compat {coverage.Firmware}", done, total, applicable.Count(b => b.State == BlockState.Done), applicable.Count);
    }

    /// <summary>The badge's value, as the Vita's: "89.7% (122/136 items)".</summary>
    public static string BadgeValue(Coverage coverage)
    {
        (string _, int done, int total, int _, int _) = BadgeCounts(coverage);
        return $"{HleStatusReport.PctText(done, total)} ({done}/{total} items)";
    }

    /// <summary>Every generated file: repository-relative path (forward slashes) to its content (LF line ends).</summary>
    public static IReadOnlyDictionary<string, string> Render(Coverage coverage)
    {
        SortedDictionary<string, string> files = new(StringComparer.Ordinal);
        (string label, int done, int total, int _, int _) = BadgeCounts(coverage);
        files[BadgePath] = HleStatusReport.Badge(label, BadgeValue(coverage), HleStatusReport.BadgeColor(HleStatusReport.Pct(done, total)));
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

    private static string PolicyColor(bool dark) => dark ? "#6e7681" : "#afb8c1";

    private static string AbsentColor(bool dark) => dark ? "#21262d" : "#eaeef2";

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

    /// <summary>One cell of the map: label, tooltip, colour, the percentage shown under the label (null: none) and
    /// its area weight.</summary>
    private sealed record Cell(string Name, string Title, string Color, string? Percent, bool Muted, int Weight);

    /// <summary>The map: a region with one cell per hardware block (area = modules that use it, colour and label = its
    /// measured percentage) and two regions with one cell per module of flash0's kd and vsh/module (area = blocks it
    /// uses, colour and label = its percentage), each region titled with its percentage as the Vita's map.</summary>
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
        (int globalDone, int globalTotal) = Score(coverage);
        string aria = $"Compat hardware map: {coverage.Blocks.Count} emulated blocks of the Vita's compat core, {HleStatusReport.PctText(globalDone, globalTotal)} of their items implemented, and the {coverage.Modules.Count} modules of its firmware {coverage.Firmware}";
        StringBuilder b = new();
        b.Append(s_inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(W)}\" height=\"{F(H)}\" viewBox=\"0 0 {F(W)} {F(H)}\" role=\"img\" aria-label=\"{Esc(aria)}\">\n");
        b.Append(s_inv, $"<rect width=\"{F(W)}\" height=\"{F(H)}\" rx=\"8\" fill=\"{bg}\"/>\n");
        b.Append("<g font-family=\"-apple-system,Segoe UI,Helvetica,Arial,sans-serif\">\n");
        b.Append(s_inv, $"<text x=\"{F(Pad + 4)}\" y=\"27\" font-size=\"17\" font-weight=\"600\" fill=\"{fg}\">Compat hardware map</text>\n");
        string subtitle = $"firmware {coverage.Firmware} (flash0): {coverage.Modules.Count} modules, % = contract items implemented";
        b.Append(s_inv, $"<text x=\"{F(Pad + 4 + HleStatusReport.TextWidth("Compat hardware map", 17) + 14)}\" y=\"27\" font-size=\"12\" fill=\"{muted}\">{Esc(subtitle)}</text>\n");
        // legend: 0% .. 100% as the Vita's map, then the policy swatch
        double legendX = W - Pad - 4 - 5 * 26 - 60 - 64;
        b.Append(s_inv, $"<text x=\"{F(legendX)}\" y=\"27\" font-size=\"11\" fill=\"{muted}\">0%</text>\n");
        for (int k = 0; k <= 4; k++)
        {
            b.Append(s_inv, $"<rect x=\"{F(legendX + 22 + k * 26)}\" y=\"16\" width=\"24\" height=\"14\" rx=\"2\" fill=\"{HleStatusReport.CellColor(k * 25, dark)}\"/>\n");
        }

        b.Append(s_inv, $"<text x=\"{F(legendX + 22 + 5 * 26 + 2)}\" y=\"27\" font-size=\"11\" fill=\"{muted}\">100%</text>\n");
        double policyX = legendX + 22 + 5 * 26 + 2 + HleStatusReport.TextWidth("100%") + 12;
        b.Append(s_inv, $"<rect x=\"{F(policyX)}\" y=\"16\" width=\"14\" height=\"14\" rx=\"2\" fill=\"{PolicyColor(dark)}\"/>\n");
        b.Append(s_inv, $"<text x=\"{F(policyX + 18)}\" y=\"27\" font-size=\"11\" fill=\"{muted}\">policy</text>\n");

        double cellsHeight = H - Top - Pad - 3 * RegionLabel - 2 * Gap;
        double hardwareHeight = cellsHeight * HardwareShare;
        double modulesHeight = cellsHeight - hardwareHeight;
        double kernelHeight = modulesHeight * kernel.Count / Math.Max(1, coverage.Modules.Count);
        double y = Top;
        int blocksDone = coverage.Blocks.Count(x => x.State == BlockState.Done);
        HleStatusReport.RegionTitle(b, $"Emulated hardware ({coverage.Blocks.Count} blocks): {HleStatusReport.PctText(globalDone, globalTotal)} implemented ({globalDone}/{globalTotal} items, {blocksDone} blocks done)", Pad, y, fg);
        y += RegionLabel;
        var blockCells = coverage.Blocks
            .Select(x => BlockCell(coverage, x, dark, UsedBy(coverage, x.Id) + HardwareCellFloor))
            .OrderByDescending(c => c.Weight).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
        Region(b, blockCells, Pad, y, W - 2 * Pad, hardwareHeight, dark);
        y += hardwareHeight + Gap;
        foreach ((string title, List<Module> modules, double height) in new[] { ("flash0 kd", kernel, kernelHeight), ("flash0 vsh/module", vsh, modulesHeight - kernelHeight) })
        {
            int ready = modules.Count(m => Worst(coverage, m) == BlockState.Done);
            (int done, int total) = Score(coverage, modules);
            HleStatusReport.RegionTitle(b, $"{title} ({modules.Count} modules): {HleStatusReport.PctText(done, total)} implemented ({done}/{total} items of the blocks used), {ready} with every block done", Pad, y, fg);
            y += RegionLabel;
            var cells = modules
                .Select(m => ModuleCell(coverage, m, dark))
                .OrderByDescending(c => c.Weight).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
            Region(b, cells, Pad, y, W - 2 * Pad, height, dark);
            y += height + Gap;
        }

        b.Append("</g>\n</svg>\n");
        return b.ToString();
    }

    private static Cell BlockCell(Coverage coverage, Block block, bool dark, int weight)
    {
        string used = $"used by {UsedBy(coverage, block.Id)} modules";
        if (block.State == BlockState.Absent)
        {
            return new Cell(block.Id, $"{block.Name.En}: {StateName(block.State, false)}, {used}", AbsentColor(dark), null, true, weight);
        }

        string percent = HleStatusReport.PctText(block.DoneItems, block.Items.Count);
        string title = $"{block.Name.En}: {percent} ({block.DoneItems}/{block.Items.Count} items), {StateName(block.State, false)}, {used}";
        string color = block.State == BlockState.Policy ? PolicyColor(dark) : HleStatusReport.CellColor(HleStatusReport.Pct(block.DoneItems, block.Items.Count), dark);
        return new Cell(block.Id, title, color, percent, false, weight);
    }

    private static Cell ModuleCell(Coverage coverage, Module module, bool dark)
    {
        (int done, int total) = Score(coverage, module);
        string percent = HleStatusReport.PctText(done, total);
        IEnumerable<string> parts = module.Uses.Select(id => Find(coverage, id) is Block block
            ? $"{id} {HleStatusReport.PctText(block.DoneItems, block.Items.Count)}"
            : $"{id} ?");
        string title = $"{module.Path}: {percent} ({done}/{total} items; {string.Join(", ", parts)})";
        return new Cell(ModuleName(module.Path), title, HleStatusReport.CellColor(HleStatusReport.Pct(done, total), dark), percent, false, module.Uses.Count);
    }

    private static void Region(StringBuilder b, List<Cell> cells, double regionX, double regionY, double regionWidth, double regionHeight, bool dark)
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
            Cell cell = cells[item.Impl];
            b.Append(s_inv, $"<g><title>{Esc(cell.Title)}</title>");
            b.Append(s_inv, $"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"{cell.Color}\" stroke=\"{stroke}\" stroke-width=\"1\"/>");
            double size = Math.Clamp(Math.Min(h / 2.6, w / 7), 0, 13);
            if (size >= 6 && HleStatusReport.TextWidth(cell.Name, size) + 6 > w)
            {
                size = Math.Max(6, (w - 6) / Math.Max(1, HleStatusReport.TextWidth(cell.Name, 1)));
            }

            if (size >= 6 && HleStatusReport.TextWidth(cell.Name, size) + 6 <= w && size + 4 <= h)
            {
                string fill = cell.Muted ? mutedLabel : label;
                b.Append(s_inv, $"<text x=\"{F(x + 3)}\" y=\"{F(y + size + 2)}\" font-size=\"{F(size)}\" fill=\"{fill}\">{Esc(cell.Name)}</text>");
                // the percentage under the label, as on the Vita's map, where the cell has room for a second line
                if (cell.Percent != null && h >= 2 * size + 6 && HleStatusReport.TextWidth(cell.Percent, size * 0.85) + 6 <= w)
                {
                    b.Append(s_inv, $"<text x=\"{F(x + 3)}\" y=\"{F(y + 2 * size + 4)}\" font-size=\"{F(size * 0.85)}\" fill=\"{label}\" fill-opacity=\".8\">{cell.Percent}</text>");
                }
            }

            b.Append("</g>\n");
        }
    }

    private static string Esc(string text) => HleStatusReport.Esc(text);

    private static string F(double value) => HleStatusReport.F(value);

    private static string ProgressPage(Coverage coverage, bool portuguese)
    {
        bool pt = portuguese;
        (int globalDone, int globalTotal) = Score(coverage);
        string globalPercent = HleStatusReport.PctText(globalDone, globalTotal);
        StringBuilder b = new();
        b.Append(pt ? "# Cobertura do hardware do Compat\n\n" : "# Compat hardware coverage\n\n");
        b.Append(pt ? "*English: [progress.md](progress.md).*\n\n" : "*Português: [progress.pt-BR.md](progress.pt-BR.md).*\n\n");
        b.Append(pt
            ? "<!-- Gerado por build/hle-status (VitaSharp.Desktop compat-coverage --status) a partir de docs/compat/coverage.json; não edite à mão. -->\n\n"
            : "<!-- Generated by build/hle-status (VitaSharp.Desktop compat-coverage --status) from docs/compat/coverage.json; do not edit by hand. -->\n\n");
        if (pt)
        {
            b.Append(s_inv, $"O que o firmware do Compat (o núcleo de compatibilidade do Vita: o Allegrex dentro do SoC, que o firmware chama de SceCompat) precisa do hardware emulado. O VitaSharp não reimplementa os módulos que rodam nele (não há HLE deste lado): como um Vita, ele roda o firmware {coverage.Firmware} que vem no `pcff` (o flash0 kermit que vem no app de sistema do emulador de PSP) nesse núcleo ([psp-compat.pt-BR.md](../notes/psp-compat.pt-BR.md)). ");
            b.Append(s_inv, $"Por isso esta página mede o hardware: **{globalPercent}** dos itens de contrato implementados ({globalDone}/{globalTotal}).\n\n");
            b.Append("## Como a porcentagem é medida\n\n");
            b.Append("- Cada bloco lista os seus **itens de contrato**: a interface que o firmware usa nele (os registradores de um bloco MMIO, os grupos de instruções de uma unidade da CPU, o caminho de pedidos e o serviço do Vita de um canal kermit), tirados das fontes que o código do bloco cita (uOFW, Jpcsp, o SDK do IPL, Adrenaline, [psp-compat.pt-BR.md](../notes/psp-compat.pt-BR.md)).\n");
            b.Append("- Um item está **implementado** quando tem uma *sonda*: um texto (o rótulo `case` do registrador, o nome do tratador ou da função HLE) que precisa estar num dos arquivos de código do bloco. O `compat-coverage` e o `CompatCoverageReportTests` recusam uma sonda que o código não tem, então a porcentagem segue o código e não uma opinião. Um item que o código não faz tem uma *lacuna* que diz por quê.\n");
            b.Append("- **Bloco:** itens implementados ÷ itens; o estado sai da conta (todos: feito, nenhum: falta, senão parcial). Só o KIRK (não emulado por política) e o drive de UMD (ausente no Vita) são declarados.\n");
            b.Append("- **Módulo do flash0:** itens implementados ÷ itens dos blocos que ele usa (a média das porcentagens desses blocos ponderada pelo número de itens). **Região e selo:** a soma do mesmo jeito; a global é a de todos os blocos (o ausente fica fora).\n");
            b.Append("- Os canais kermit (Memory Stick, flash, áudio, Media Engine, Wi-Fi, USB, syscon, utilitários) chegam ao ScePspemu real como pedidos; o item que falta em todos é rodá-los de ponta a ponta com ele.\n");
            b.Append("- Todo módulo do flash0 é cifrado (`~PSP`) e carregado pelo `memlmd`, que precisa do KIRK: não emulado por política, contado nos módulos que o chamam (memlmd e os de DRM). O código de boot (`pcbc`) continua cifrado para o processador seguro: o núcleo só sobe o firmware real com um `pcbc.bin` tirado pelo usuário (psp-compat.pt-BR.md).\n");
            b.Append("- Os nomes dos módulos vêm da tabela de arquivos do `pcff`; os papéis, do uOFW, dos canais kermit e das listas de módulos do psdevwiki (\"a confirmar\" onde nenhuma fonte dá o papel).\n\n");
        }
        else
        {
            b.Append(s_inv, $"What the Compat firmware (the Vita's compatibility core: the Allegrex inside the SoC, which the firmware calls SceCompat) needs from the emulated hardware. VitaSharp does not reimplement the modules that run on it (no HLE on this side): like a Vita, it runs the firmware {coverage.Firmware} that comes in `pcff` (the kermit flash0 that ships in the PSP emulator system app) on that core ([psp-compat.md](../notes/psp-compat.md)). ");
            b.Append(s_inv, $"So this page measures the hardware: **{globalPercent}** of the contract items implemented ({globalDone}/{globalTotal}).\n\n");
            b.Append("## How the percentage is measured\n\n");
            b.Append("- Every block lists its **contract items**: the interface the firmware drives on it (the registers of an MMIO block, the instruction groups of a CPU unit, the request path and the Vita service of a kermit channel), taken from the sources the block's code cites (uOFW, Jpcsp, the IPL SDK, Adrenaline, [psp-compat.md](../notes/psp-compat.md)).\n");
            b.Append("- An item is **implemented** when it has a *probe*: a text (the register's `case` label, the handler's or the HLE function's name) that must be in one of the block's code files. `compat-coverage` and `CompatCoverageReportTests` refuse a probe the code does not have, so the percentage follows the code, not an opinion. An item the code does not do has a *gap* that says why.\n");
            b.Append("- **Block:** implemented items ÷ items; its state follows (all: done, none: missing, else partial). Only KIRK (not emulated by policy) and the UMD drive (absent on the Vita) are declared.\n");
            b.Append("- **flash0 module:** implemented items ÷ the items of the blocks it uses (the mean of those blocks' percentages weighted by their item count). **Region and badge:** the same sum; the global one is over every block (the absent one left out).\n");
            b.Append("- The kermit channels (Memory Stick, flash, audio, Media Engine, Wi-Fi, USB, syscon, utilities) reach the real ScePspemu as requests; the item they all miss is running them end to end with it.\n");
            b.Append("- Every flash0 module is encrypted (`~PSP`) and loaded through `memlmd`, which needs KIRK: not emulated by policy, counted on the modules that call it (memlmd and the DRM ones). The boot code (`pcbc`) is still encrypted for the secure processor: the core boots the real firmware only with a `pcbc.bin` dumped by the user (psp-compat.md).\n");
            b.Append("- Module names come from the file table of `pcff`; roles from uOFW, the kermit channels and psdevwiki's PSP module lists (\"to confirm\" where no source gives the role).\n\n");
        }

        b.Append("<picture>\n  <source media=\"(prefers-color-scheme: light)\" srcset=\"../assets/status/progress-map-compat-light.svg\">\n");
        b.Append(pt
            ? "  <img src=\"../assets/status/progress-map-compat.svg\" alt=\"Mapa do hardware Compat: um bloco por célula e os módulos do flash0, cada um com a porcentagem medida dos itens implementados\" width=\"100%\">\n</picture>\n\n"
            : "  <img src=\"../assets/status/progress-map-compat.svg\" alt=\"Compat hardware map: one cell per block and the flash0 modules, each with the measured percentage of its items implemented\" width=\"100%\">\n</picture>\n\n");
        b.Append(pt ? "| Estado | Blocos | Itens | Módulos (pior bloco) |\n|---|---:|---:|---:|\n" : "| State | Blocks | Items | Modules (worst block) |\n|---|---:|---:|---:|\n");
        foreach (BlockState state in s_states)
        {
            var blocks = coverage.Blocks.Where(x => x.State == state).ToList();
            int modules = coverage.Modules.Count(m => Worst(coverage, m) == state);
            string items = state == BlockState.Absent ? "-" : $"{blocks.Sum(x => x.DoneItems)}/{blocks.Sum(x => x.Items.Count)}";
            b.Append(s_inv, $"| {StateName(state, pt)} | {blocks.Count} | {items} | {(state == BlockState.Absent ? "-" : modules.ToString(s_inv))} |\n");
        }

        b.Append(s_inv, $"| Total | {coverage.Blocks.Count} | {globalDone}/{globalTotal} ({globalPercent}) | {coverage.Modules.Count} |\n");
        b.Append(pt ? $"\n## Blocos de hardware ({coverage.Blocks.Count})\n\n" : $"\n## Hardware blocks ({coverage.Blocks.Count})\n\n");
        b.Append(pt ? "| Bloco | Id | % | Itens | Estado | Usado por | Código | Notas |\n|---|---|---:|---:|---|---:|---|---|\n" : "| Block | Id | % | Items | State | Used by | Code | Notes |\n|---|---|---:|---:|---|---:|---|---|\n");
        foreach (Block block in coverage.Blocks)
        {
            string code = block.Code.Count == 0 ? "-" : string.Join(", ", block.Code.Select(c => $"[{c[(c.LastIndexOf('/') + 1)..]}](../../{c})"));
            string percent = block.State == BlockState.Absent ? "-" : HleStatusReport.PctText(block.DoneItems, block.Items.Count);
            string items = block.State == BlockState.Absent ? "-" : $"{block.DoneItems}/{block.Items.Count}";
            b.Append(s_inv, $"| {block.Name.Get(pt)} | `{block.Id}` | {percent} | {items} | {StateName(block.State, pt)} | {UsedBy(coverage, block.Id)} | {code} | {block.Note.Get(pt)} |\n");
        }

        b.Append(pt ? "\n## Itens que faltam\n\n" : "\n## Missing items\n\n");
        b.Append(pt ? "| Bloco | Item | Por quê |\n|---|---|---|\n" : "| Block | Item | Why |\n|---|---|---|\n");
        foreach (Block block in coverage.Blocks)
        {
            foreach (Item item in block.Items.Where(i => !i.Done))
            {
                b.Append(s_inv, $"| `{block.Id}` | {item.Id} | {item.Gap!.Get(pt)} |\n");
            }
        }

        b.Append(pt ? "\n## Itens implementados (sondas)\n\n" : "\n## Implemented items (probes)\n\n");
        b.Append(pt ? "| Bloco | Item | Sonda no código |\n|---|---|---|\n" : "| Block | Item | Probe in the code |\n|---|---|---|\n");
        foreach (Block block in coverage.Blocks)
        {
            foreach (Item item in block.Items.Where(i => i.Done))
            {
                b.Append(s_inv, $"| `{block.Id}` | {item.Id} | `{item.Probe!.Replace("|", "\\|", StringComparison.Ordinal)}` |\n");
            }
        }

        b.Append(pt ? $"\n## Módulos do firmware {coverage.Firmware} ({coverage.Modules.Count})\n\n" : $"\n## Modules of firmware {coverage.Firmware} ({coverage.Modules.Count})\n\n");
        b.Append(pt ? "| Módulo | Papel | Blocos | % | Pior |\n|---|---|---|---:|---|\n" : "| Module | Role | Blocks | % | Worst |\n|---|---|---|---:|---|\n");
        foreach (Module module in coverage.Modules)
        {
            (int done, int total) = Score(coverage, module);
            b.Append(s_inv, $"| `{module.Path}` | {module.Role.Get(pt)} | {string.Join(", ", module.Uses.Select(u => $"`{u}`"))} | {HleStatusReport.PctText(done, total)} | {StateName(Worst(coverage, module), pt)} |\n");
        }

        return b.ToString();
    }
}
