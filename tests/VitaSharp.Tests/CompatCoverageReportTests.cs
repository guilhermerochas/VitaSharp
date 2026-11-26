using System.Runtime.CompilerServices;
using VitaSharp.Core.Firmware;

namespace VitaSharp.Tests;

/// <summary>
/// The compat side of the README status (the Compat hardware map, its badge, docs/compat/progress.md and its pt-BR pair) is
/// generated from docs/compat/coverage.json by build/hle-status (VitaSharp.Desktop compat-coverage). Rendering the committed
/// source again must give the committed files, every block a module uses must be in the source, and every source file
/// a block names as its implementation must exist, so the map cannot claim hardware the code does not have.
/// </summary>
public class CompatCoverageReportTests
{
    private static string Root([CallerFilePath] string source = "")
    {
        for (DirectoryInfo? d = new(Path.GetDirectoryName(source)!); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "VitaSharp.sln")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException("repository root");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n");

    private static CompatCoverageReport.Coverage Committed() => CompatCoverageReport.Parse(Read(CompatCoverageReport.SourcePath));

    [Fact]
    public void CommittedFilesMatchTheSource()
    {
        CompatCoverageReport.Coverage coverage = Committed();
        Assert.NotEmpty(coverage.Blocks);
        Assert.NotEmpty(coverage.Modules);
        var stale = CompatCoverageReport.Render(coverage)
            .Where(f => !File.Exists(Path.Combine(Root(), f.Key)) || Read(f.Key) != f.Value)
            .Select(f => f.Key).ToList();
        Assert.Empty(stale);
    }

    [Fact]
    public void EveryBlockAModuleUsesIsInTheSource()
    {
        CompatCoverageReport.Coverage coverage = Committed();
        Assert.Empty(CompatCoverageReport.Problems(coverage));
        var ids = coverage.Blocks.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(coverage.Modules, m => Assert.All(m.Uses, id => Assert.Contains(id, ids)));
        // the flash0 layout: kernel modules in kd, the utilities in vsh/module
        Assert.All(coverage.Modules, m => Assert.True(m.Path.StartsWith("kd/", StringComparison.Ordinal)
            || m.Path.StartsWith("vsh/module/", StringComparison.Ordinal), m.Path));
    }

    /// <summary>Every block names code that exists and every implemented item's probe is in that code (what makes the
    /// percentages measured); KIRK stays "not emulated by policy" and the UMD drive, which the Vita does not have, is
    /// used by no module.</summary>
    [Fact]
    public void EveryImplementedItemIsInTheBlocksCode()
    {
        CompatCoverageReport.Coverage coverage = Committed();
        Assert.Empty(CompatCoverageReport.MissingProbes(coverage, ReadSource));
        foreach (CompatCoverageReport.Block block in coverage.Blocks)
        {
            if (block.DoneItems > 0)
            {
                Assert.NotEmpty(block.Code);
            }

            Assert.False(string.IsNullOrWhiteSpace(block.Note.En) || string.IsNullOrWhiteSpace(block.Note.Pt), block.Id);
            Assert.All(block.Items, item => Assert.True(item.Done || !string.IsNullOrWhiteSpace(item.Gap!.En) && !string.IsNullOrWhiteSpace(item.Gap.Pt), $"{block.Id}: {item.Id}"));
        }

        Assert.Equal(CompatCoverageReport.BlockState.Policy, coverage.Blocks.Single(b => b.Id == "kirk").State);
        Assert.Equal(0, CompatCoverageReport.UsedBy(coverage, "umd"));
    }

    private static string? ReadSource(string relative)
    {
        string path = Path.Combine(Root(), relative);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>A probe the code does not have is reported, so an item cannot count without its code.</summary>
    [Fact]
    public void AProbeTheCodeDoesNotHaveIsReported()
    {
        const string Json = """
            { "firmware": "6.61",
              "blocks": [
                { "id": "a", "name": { "en": "A", "pt": "A" }, "code": ["x.cs", "gone.cs"], "note": { "en": "n", "pt": "n" },
                  "items": [ { "id": "here", "probe": "case 0x08:" }, { "id": "elsewhere", "probe": "case 0x0C:" } ] } ],
              "modules": [ { "path": "kd/one.prx", "uses": ["a"], "role": { "en": "r", "pt": "r" } } ] }
            """;
        CompatCoverageReport.Coverage coverage = CompatCoverageReport.Parse(Json);
        IReadOnlyList<string> missing = CompatCoverageReport.MissingProbes(coverage, path => path == "x.cs" ? "switch { case 0x08: }" : null);
        Assert.Equal(["a: gone.cs does not exist", "a: item \"elsewhere\": \"case 0x0C:\" is not in the block's code"], missing);
    }

    /// <summary>The percentages: a block's implemented items over its items (its state follows), a module's over the
    /// items of the blocks it uses, the global over every block but the absent one; shown in every cell, in every
    /// region title and on the badge, as on the Vita's map.</summary>
    [Fact]
    public void ThePercentagesFollowTheItems()
    {
        const string Json = """
            { "firmware": "6.61",
              "blocks": [
                { "id": "a", "name": { "en": "A", "pt": "A" }, "code": [], "note": { "en": "n", "pt": "n" },
                  "items": [ { "id": "a1", "probe": "p" }, { "id": "a2", "probe": "p" } ] },
                { "id": "b", "name": { "en": "B", "pt": "B" }, "code": [], "note": { "en": "n", "pt": "n" },
                  "items": [ { "id": "b1", "probe": "p" }, { "id": "b2", "gap": { "en": "why", "pt": "porque" } }, { "id": "b3", "gap": { "en": "why", "pt": "porque" } } ] },
                { "id": "c", "state": "policy", "name": { "en": "C", "pt": "C" }, "code": [], "note": { "en": "n", "pt": "n" },
                  "items": [ { "id": "c1", "gap": { "en": "policy", "pt": "política" } } ] },
                { "id": "d", "name": { "en": "D", "pt": "D" }, "code": [], "note": { "en": "n", "pt": "n" },
                  "items": [ { "id": "d1", "gap": { "en": "why", "pt": "porque" } } ] },
                { "id": "e", "state": "absent", "name": { "en": "E", "pt": "E" }, "code": [], "note": { "en": "n", "pt": "n" } } ],
              "modules": [
                { "path": "kd/one.prx", "uses": ["a"], "role": { "en": "r", "pt": "r" } },
                { "path": "kd/two.prx", "uses": ["a", "b"], "role": { "en": "r", "pt": "r" } },
                { "path": "kd/three.prx", "uses": ["b", "c", "a"], "role": { "en": "r", "pt": "r" } },
                { "path": "vsh/module/four.prx", "uses": ["c", "d"], "role": { "en": "r", "pt": "r" } } ] }
            """;
        CompatCoverageReport.Coverage coverage = CompatCoverageReport.Parse(Json);
        Assert.Empty(CompatCoverageReport.Problems(coverage));
        Assert.Equal(
            [CompatCoverageReport.BlockState.Done, CompatCoverageReport.BlockState.Partial, CompatCoverageReport.BlockState.Policy,
                CompatCoverageReport.BlockState.Missing, CompatCoverageReport.BlockState.Absent],
            coverage.Blocks.Select(b => b.State));
        Assert.Equal([(2, 2), (3, 5), (3, 6), (0, 2)], coverage.Modules.Select(m => CompatCoverageReport.Score(coverage, m)));
        // global: a 2/2 + b 1/3 + c 0/1 + d 0/1, the absent e left out
        Assert.Equal((3, 7), CompatCoverageReport.Score(coverage));
        Assert.Equal(("Compat 6.61", 3, 7, 1, 4), CompatCoverageReport.BadgeCounts(coverage));
        IReadOnlyDictionary<string, string> files = CompatCoverageReport.Render(coverage);
        Assert.Contains("42.8% (3/7 items)", files[CompatCoverageReport.BadgePath]);
        string map = files[CompatCoverageReport.MapPath];
        Assert.Contains(">Emulated hardware (5 blocks): 42.8% implemented (3/7 items, 1 blocks done)<", map);
        Assert.Contains(">flash0 kd (3 modules): 61.5% implemented (8/13 items of the blocks used), 1 with every block done<", files[CompatCoverageReport.MapLightPath]);
        Assert.Contains(">flash0 vsh/module (1 modules): 0% implemented (0/2 items of the blocks used), 0 with every block done<", map);
        Assert.Contains("<title>B: 33.3% (1/3 items), partial, used by 2 modules</title>", map);
        Assert.Contains("<title>kd/three.prx: 50% (3/6 items; b 33.3%, c 0%, a 100%)</title>", map);
        Assert.Contains("<title>E: absent on the Vita, used by 0 modules</title>", map);
        Assert.Contains("| `kd/two.prx` | r | `a`, `b` | 60% | parcial |", files[CompatCoverageReport.ProgressPtPath]);
        Assert.Contains("| `b` | b2 | why |", files[CompatCoverageReport.ProgressPath]);
    }

    /// <summary>The committed map shows, in its cells and its region titles, the percentages coverage.json gives (counted
    /// here straight from the JSON, apart from the report's own code), and so does the badge.</summary>
    [Fact]
    public void TheCommittedMapShowsThePercentagesOfTheSource()
    {
        using var document = System.Text.Json.JsonDocument.Parse(Read(CompatCoverageReport.SourcePath));
        Dictionary<string, (int Done, int Total, string Name, bool Absent)> blocks = new(StringComparer.Ordinal);
        foreach (System.Text.Json.JsonElement block in document.RootElement.GetProperty("blocks").EnumerateArray())
        {
            bool absent = block.TryGetProperty("state", out System.Text.Json.JsonElement state) && state.GetString() == "absent";
            int done = 0;
            int total = 0;
            if (block.TryGetProperty("items", out System.Text.Json.JsonElement items))
            {
                foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
                {
                    total++;
                    done += item.TryGetProperty("probe", out _) ? 1 : 0;
                }
            }

            blocks[block.GetProperty("id").GetString()!] = (done, total, block.GetProperty("name").GetProperty("en").GetString()!, absent);
        }

        int globalDone = blocks.Values.Where(b => !b.Absent).Sum(b => b.Done);
        int globalTotal = blocks.Values.Where(b => !b.Absent).Sum(b => b.Total);
        foreach (string mapPath in new[] { CompatCoverageReport.MapPath, CompatCoverageReport.MapLightPath })
        {
            string map = Read(mapPath);
            Assert.Contains($"): {Percent(globalDone, globalTotal)} implemented ({globalDone}/{globalTotal} items, ", map);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(map, @"font-weight=""600""[^>]*>[^<]*: \d+(\.\d)?% implemented").Count);
            foreach ((string id, (int done, int total, string name, bool absent)) in blocks)
            {
                if (!absent)
                {
                    Assert.Contains($"<title>{name}: {Percent(done, total)} ({done}/{total} items)", map);
                }
            }

            // the percentage is written in the cells, not only in the tooltips
            Assert.Contains($"fill-opacity=\".8\">{Percent(blocks["cpu"].Done, blocks["cpu"].Total)}</text>", map);
            int cellPercents = System.Text.RegularExpressions.Regex.Matches(map, @"fill-opacity="".8"">\d+(\.\d)?%</text>").Count;
            Assert.True(cellPercents >= blocks.Count, $"{mapPath}: {cellPercents} cells show a percentage");
        }

        Assert.Contains($"{Percent(globalDone, globalTotal)} ({globalDone}/{globalTotal} items)", Read(CompatCoverageReport.BadgePath));
    }

    /// <summary>"99.1%", rounded down as the Vita's map does, so 100% only when nothing is missing.</summary>
    private static string Percent(int done, int total)
    {
        double value = Math.Floor(1000.0 * done / total) / 10;
        return value.ToString(value == Math.Floor(value) ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    [Fact]
    public void ASourceWithUnknownOrAbsentBlocksIsRefused()
    {
        const string Json = """
            { "firmware": "6.61",
              "blocks": [
                { "id": "a", "name": { "en": "A", "pt": "A" }, "code": [], "note": { "en": "n", "pt": "n" }, "items": [ { "id": "a1", "probe": "p" } ] },
                { "id": "f", "name": { "en": "F", "pt": "F" }, "code": [], "note": { "en": "n", "pt": "n" } },
                { "id": "e", "state": "absent", "name": { "en": "E", "pt": "E" }, "code": [], "note": { "en": "n", "pt": "n" } } ],
              "modules": [
                { "path": "kd/one.prx", "uses": ["a", "zz"], "role": { "en": "r", "pt": "r" } },
                { "path": "kd/one.prx", "uses": ["e"], "role": { "en": "r", "pt": "r" } },
                { "path": "kd/none.prx", "uses": [], "role": { "en": "r", "pt": "r" } } ] }
            """;
        IReadOnlyList<string> problems = CompatCoverageReport.Problems(CompatCoverageReport.Parse(Json));
        Assert.Contains(problems, p => p.Contains("\"zz\", which is not a block", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("\"e\", which the Vita does not have", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("kd/one.prx is listed twice", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("kd/none.prx uses no block", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("block \"f\" has no items to measure", StringComparison.Ordinal));
        Assert.Throws<FormatException>(() => CompatCoverageReport.ParseState("maybe"));
    }

    /// <summary>Done, partial and missing are measured, never declared; an item has a probe or a gap, not both.</summary>
    [Theory]
    [InlineData("""{ "id": "a", "state": "done", "name": { "en": "A", "pt": "A" }, "code": [], "note": { "en": "n", "pt": "n" }, "items": [] }""")]
    [InlineData("""{ "id": "a", "state": "partial", "name": { "en": "A", "pt": "A" }, "code": [], "note": { "en": "n", "pt": "n" }, "items": [] }""")]
    [InlineData("""{ "id": "a", "name": { "en": "A", "pt": "A" }, "code": [], "note": { "en": "n", "pt": "n" }, "items": [ { "id": "x" } ] }""")]
    [InlineData("""{ "id": "a", "name": { "en": "A", "pt": "A" }, "code": [], "note": { "en": "n", "pt": "n" }, "items": [ { "id": "x", "probe": "p", "gap": { "en": "w", "pt": "w" } } ] }""")]
    public void ADeclaredStateOrAnUnclearItemIsRefused(string block)
    {
        string json = $$"""{ "firmware": "6.61", "blocks": [ {{block}} ], "modules": [] }""";
        Assert.Throws<FormatException>(() => CompatCoverageReport.Parse(json));
    }

    /// <summary>Both READMEs show the Compat badge and the Compat map next to the Vita's.</summary>
    [Fact]
    public void TheReadmesShowTheCompatBadgeAndMap()
    {
        foreach (string readme in new[] { "README.md", "README.pt-BR.md" })
        {
            string text = Read(readme);
            Assert.Contains($"({CompatCoverageReport.BadgePath})", text);
            Assert.Contains($"srcset=\"{CompatCoverageReport.MapLightPath}\"", text);
            Assert.Contains($"src=\"{CompatCoverageReport.MapPath}\"", text);
            Assert.Contains("(docs/compat/progress", text);
        }
    }
}
