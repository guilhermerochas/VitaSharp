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

    /// <summary>A block marked done or partial names the source files that implement it, and they exist; KIRK stays
    /// "not emulated by policy" and the UMD drive, which the Vita does not have, is used by no module.</summary>
    [Fact]
    public void EveryImplementedBlockPointsAtCodeThatExists()
    {
        CompatCoverageReport.Coverage coverage = Committed();
        foreach (CompatCoverageReport.Block block in coverage.Blocks)
        {
            if (block.State is CompatCoverageReport.BlockState.Done or CompatCoverageReport.BlockState.Partial)
            {
                Assert.NotEmpty(block.Code);
            }

            Assert.All(block.Code, path => Assert.True(File.Exists(Path.Combine(Root(), path)), $"{block.Id}: {path} does not exist"));
            Assert.False(string.IsNullOrWhiteSpace(block.Note.En) || string.IsNullOrWhiteSpace(block.Note.Pt), block.Id);
        }

        Assert.Equal(CompatCoverageReport.BlockState.Policy, coverage.Blocks.Single(b => b.Id == "kirk").State);
        Assert.Equal(0, CompatCoverageReport.UsedBy(coverage, "umd"));
    }

    /// <summary>A module's colour is its worst block: missing, then not emulated by policy, then partial, then done.</summary>
    [Fact]
    public void AModuleTakesItsWorstBlock()
    {
        const string Json = """
            { "firmware": "6.61",
              "blocks": [
                { "id": "a", "state": "done", "name": { "en": "A", "pt": "A" }, "code": [], "note": { "en": "n", "pt": "n" } },
                { "id": "b", "state": "partial", "name": { "en": "B", "pt": "B" }, "code": [], "note": { "en": "n", "pt": "n" } },
                { "id": "c", "state": "policy", "name": { "en": "C", "pt": "C" }, "code": [], "note": { "en": "n", "pt": "n" } },
                { "id": "d", "state": "missing", "name": { "en": "D", "pt": "D" }, "code": [], "note": { "en": "n", "pt": "n" } },
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
            [CompatCoverageReport.BlockState.Done, CompatCoverageReport.BlockState.Partial, CompatCoverageReport.BlockState.Policy, CompatCoverageReport.BlockState.Missing],
            coverage.Modules.Select(m => CompatCoverageReport.Worst(coverage, m)));
        // the badge leaves the absent block out: 1 done of 4
        Assert.Equal(("Compat 6.61", 1, 4, 1), CompatCoverageReport.BadgeCounts(coverage));
        IReadOnlyDictionary<string, string> files = CompatCoverageReport.Render(coverage);
        Assert.Contains("25% (1/4 blocks, 1 partial)", files[CompatCoverageReport.BadgePath]);
        Assert.Contains("<title>kd/three.prx: not emulated by policy (b partial, c not emulated by policy, a done)</title>", files[CompatCoverageReport.MapPath]);
        Assert.Contains(">flash0 kd (3 modules): 1 with every block done<", files[CompatCoverageReport.MapLightPath]);
        Assert.Contains(">flash0 vsh/module (1 modules): 0 with every block done<", files[CompatCoverageReport.MapPath]);
        Assert.Contains("| `kd/two.prx` | r | `a`, `b` | parcial |", files[CompatCoverageReport.ProgressPtPath]);
    }

    [Fact]
    public void ASourceWithUnknownOrAbsentBlocksIsRefused()
    {
        const string Json = """
            { "firmware": "6.61",
              "blocks": [
                { "id": "a", "state": "done", "name": { "en": "A", "pt": "A" }, "code": [], "note": { "en": "n", "pt": "n" } },
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
        Assert.Throws<FormatException>(() => CompatCoverageReport.ParseState("maybe"));
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
