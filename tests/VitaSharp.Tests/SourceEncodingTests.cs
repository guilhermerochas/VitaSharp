using System.Text.RegularExpressions;

namespace VitaSharp.Tests;

/// <summary>
/// Source text must be plain UTF-8: text that was UTF-8, read as Windows-1252 and saved again as UTF-8 shows up as
/// "Ã¡" for "á", "Ã§" for "ç", "â€“" for "–" or "ðŸ" for an emoji (the Android library showed "JogÃ¡veis").
/// </summary>
public partial class SourceEncodingTests
{
    [GeneratedRegex("Ã[\u0080-¿]|â€|Â[ -¿]|ðŸ")]
    private static partial Regex DoubleEncoded();

    // The test output may live outside the repository (builds go to a temporary folder with -o), so the search also
    // starts from this source file's own path.
    static string? RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) ?? "" })
        {
            for (var d = start.Length > 0 ? new DirectoryInfo(start) : null; d != null; d = d.Parent)
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
    public void NoDoubleEncodedUtf8InSources()
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        string[] exts = [".cs", ".xml", ".axaml", ".xaml", ".json", ".csproj", ".props", ".targets"];
        var bad = new List<string>();
        foreach (var top in new[] { "src", "tests", "build" })
        {
            var dir = Path.Combine(root!, top);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root!, f);
                if (!exts.Contains(Path.GetExtension(f)) || rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || f.EndsWith(nameof(SourceEncodingTests) + ".cs"))
                {
                    continue;
                }

                var lines = File.ReadAllLines(f);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (DoubleEncoded().IsMatch(lines[i]))
                    {
                        bad.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }
        }
        Assert.True(bad.Count == 0, "double-encoded UTF-8 (fix with the original characters):\n" + string.Join("\n", bad.Take(30)));
    }
}
