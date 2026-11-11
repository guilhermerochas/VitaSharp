using System.Text.RegularExpressions;
using VitaSharp.FormatTool;

namespace VitaSharp.Tests;

/// <summary>
/// "Código legível" (docs/agents/regras.md): no block with statements on a single line (`if (x) { a; }`,
/// `catch (E) { Log(); }`, `void M() { a; b; }`). The formatter keeps one-line blocks (it has to, for auto-property
/// accessor lists), so this test is the check; build/format-check --fix applies the same rules (SourceLayout).
/// Accessor lists (`{ get; set; }`) and lambda bodies (`=> { ...; }`) are allowed, strings and comments ignored; the
/// folders still listed in build/format-pending.txt are skipped until they are converted.
/// </summary>
public partial class OneLineBlockTests
{
    [GeneratedRegex(@"(^|[\\/])(obj|bin|obj-dev|bin-dev)([\\/]|$)")]
    private static partial Regex BuildOutput();

    private static string? RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) ?? "" })
        {
            for (DirectoryInfo? d = start.Length > 0 ? new DirectoryInfo(start) : null; d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "VitaSharp.sln")))
                {
                    return d.FullName;
                }
            }
        }

        return null;
    }

    [Fact]
    public void DetectsOneLineBlocksButNotAccessorsOrLambdas()
    {
        string src = """
            public int P { get; set; }
            public int Q { get => _q; private set => _q = value; }
            Action a = () => { Go(); };
            string s = "{ a; }";
            if (x) { Go(); }
            catch (Exception e) { Log(e); }
            """;
        int[] lines = SourceLayout.OneLineBlocks(src).Select(h => h.Line).ToArray();
        Assert.Equal([5, 6], lines);
    }

    [Fact]
    public void FixSplitsBlocksAndFoldsAccessors()
    {
        string src = string.Join("\n",
            "class A",
            "{",
            "    public int P",
            "    {",
            "        get; set;",
            "    }",
            "    void M() { if (x) { Go(); } else { Stop(); } } // note",
            "    void N() => Run(() => { try { a(); } finally { b(); } });",
            "}");
        (string text, int changes) = SourceLayout.Fix(src);
        Assert.True(changes > 0);
        Assert.Contains("    public int P { get; set; }", text);
        Assert.Contains("    void M() // note\n    {\n    if (x)\n    {\n    Go();\n    }\n    else", text);
        Assert.Contains("try\n    {\n    a();\n    }\n    finally", text);
        Assert.Empty(SourceLayout.OneLineBlocks(text));
    }

    [Fact]
    public void NoOneLineStatementBlocks()
    {
        string? root = RepoRoot();
        Assert.NotNull(root);
        string[] pending = File.ReadAllLines(Path.Combine(root!, "build", "format-pending.txt"))
            .Select(l => l.Split('#')[0].Trim())
            .Where(l => l.Length > 0)
            .Select(l => l.Replace('/', Path.DirectorySeparatorChar))
            .ToArray();
        List<string> bad = [];
        foreach (string top in new[] { "src", "tests", "tools", "build" })
        {
            string dir = Path.Combine(root!, top);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root!, f);
                if (BuildOutput().IsMatch(rel) || pending.Any(p => rel.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                {
                    continue;
                }

                foreach ((int line, string text) in SourceLayout.OneLineBlocks(File.ReadAllText(f)))
                {
                    bad.Add($"{rel}:{line}: {text}");
                }
            }
        }

        Assert.True(bad.Count == 0, $"{bad.Count} one-line blocks (build/format-check --fix splits them; docs/agents/regras.md \"Código legível\"):\n" + string.Join("\n", bad.Take(40)));
    }
}
