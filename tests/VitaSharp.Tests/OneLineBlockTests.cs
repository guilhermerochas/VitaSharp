using System.Text;
using System.Text.RegularExpressions;

namespace VitaSharp.Tests;

/// <summary>
/// "Código legível" (docs/agents/regras.md): no block with statements on a single line (`if (x) { a; }`,
/// `catch (E) { Log(); }`, `void M() { a; b; }`). The formatter keeps one-line blocks (it has to, for auto-property
/// accessor lists), so this test is the check: every `{ ...; }` on one line fails, except accessor lists
/// (`{ get; set; }`) and lambda bodies (`=> { ...; }`). Strings and comments are ignored; the folders still listed in
/// build/format-pending.txt are skipped until they are converted.
/// </summary>
public partial class OneLineBlockTests
{
    private const string Accessor = @"(?:\[[^\]]*\]\s*)?(?:(?:public|private|protected|internal)\s+)*(?:readonly\s+)?(?:get|set|init)\s*(?:;|=>[^;{}]*;)";

    [GeneratedRegex(@"\{([^{}]*;[^{}]*)\}")]
    private static partial Regex StatementBlock();

    [GeneratedRegex(@"^\s*(?:" + Accessor + @"\s*)+$")]
    private static partial Regex AccessorList();

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

    /// <summary>The source with string/char literal contents and comments blanked (same length, newlines kept).</summary>
    internal static string Mask(string src)
    {
        StringBuilder sb = new(src);
        int n = src.Length;
        int i = 0;

        void Blank(int from, int to)
        {
            for (int k = from; k < Math.Min(to, n); k++)
            {
                if (sb[k] != '\n' && sb[k] != '\r')
                {
                    sb[k] = '_';
                }
            }
        }

        while (i < n)
        {
            char c = src[i];
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                int j = src.IndexOf('\n', i);
                j = j < 0 ? n : j;
                Blank(i, j);
                i = j;
                continue;
            }

            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                int j = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                j = j < 0 ? n : j + 2;
                Blank(i, j);
                i = j;
                continue;
            }

            int p = i;
            bool verbatim = false;
            while (p < n && (src[p] == '$' || src[p] == '@'))
            {
                verbatim |= src[p] == '@';
                p++;
            }

            if (p < n && src[p] == '"')
            {
                int quotes = 0;
                while (p + quotes < n && src[p + quotes] == '"')
                {
                    quotes++;
                }

                if (quotes >= 3)
                {
                    string fence = new('"', quotes);
                    int end = src.IndexOf(fence, p + quotes, StringComparison.Ordinal);
                    end = end < 0 ? n : end;
                    Blank(p + quotes, end);
                    i = end + quotes;
                    continue;
                }

                int k = p + 1;
                while (k < n)
                {
                    if (verbatim && src[k] == '"' && k + 1 < n && src[k + 1] == '"')
                    {
                        k += 2;
                        continue;
                    }

                    if (src[k] == '"' || (!verbatim && src[k] == '\n'))
                    {
                        break;
                    }

                    k += !verbatim && src[k] == '\\' ? 2 : 1;
                }

                Blank(p + 1, k);
                i = k + 1;
                continue;
            }

            if (c == '\'')
            {
                int k = i + 1;
                while (k < n && k < i + 12 && src[k] != '\'' && src[k] != '\n')
                {
                    k += src[k] == '\\' ? 2 : 1;
                }

                if (k < n && src[k] == '\'')
                {
                    Blank(i + 1, k);
                    i = k + 1;
                    continue;
                }
            }

            i++;
        }

        return sb.ToString();
    }

    /// <summary>The one-line statement blocks of a source, as (line number, line).</summary>
    internal static IEnumerable<(int Line, string Text)> OneLineBlocks(string source)
    {
        string[] masked = Mask(source).Split('\n');
        string[] lines = source.Split('\n');
        for (int i = 0; i < masked.Length; i++)
        {
            foreach (Match m in StatementBlock().Matches(masked[i]))
            {
                string before = masked[i][..m.Index].TrimEnd();
                if (before.EndsWith("=>", StringComparison.Ordinal) || AccessorList().IsMatch(m.Groups[1].Value))
                {
                    continue;
                }

                yield return (i + 1, lines[i].Trim());
                break;
            }
        }
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
        int[] lines = OneLineBlocks(src).Select(h => h.Line).ToArray();
        Assert.Equal([5, 6], lines);
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

                foreach ((int line, string text) in OneLineBlocks(File.ReadAllText(f)))
                {
                    bad.Add($"{rel}:{line}: {text}");
                }
            }
        }

        Assert.True(bad.Count == 0, $"{bad.Count} one-line blocks (put the body on its own lines, docs/agents/regras.md \"Código legível\"):\n" + string.Join("\n", bad.Take(40)));
    }
}
