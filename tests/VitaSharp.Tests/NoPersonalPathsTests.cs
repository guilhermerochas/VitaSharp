using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace VitaSharp.Tests;

/// <summary>
/// The repository never names a person's machine: no Windows user folder (C:\Users\&lt;name&gt;), no /home/&lt;name&gt;.
/// Paths to the user's data are written with %APPDATA%, %USERPROFILE% or &lt;placeholders&gt;.
/// </summary>
public partial class NoPersonalPathsTests
{
    [GeneratedRegex(@"[A-Za-z]:[\\/]+Users[\\/]+(?!%|<|Public\b|Default\b|\$|\{)[A-Za-z0-9._-]+|/home/(?!<|\$)[a-z0-9._-]+/", RegexOptions.IgnoreCase)]
    private static partial Regex UserFolder();

    private static string? RepoRoot([CallerFilePath] string source = "")
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
    public void NoUserFoldersInTrackedText()
    {
        string? root = RepoRoot();
        Assert.NotNull(root);
        string[] exts = [".cs", ".md", ".txt", ".ps1", ".sh", ".json", ".xml", ".axaml", ".csproj", ".props", ".targets", ".yml", ".yaml", ".nav", ".editorconfig"];
        var bad = new List<string>();
        foreach (string top in new[] { "src", "tests", "build", "docs", "tools", ".github" })
        {
            string dir = Path.Combine(root!, top);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root!, f);
                string sep = Path.DirectorySeparatorChar.ToString();
                if (!exts.Contains(Path.GetExtension(f)) || rel.Contains(sep + "bin" + sep) || rel.Contains(sep + "obj" + sep)
                    || rel.Contains(sep + "obj-dev" + sep) // the developer APK's intermediates (VitaSharp.Android, git-ignored)
                    || f.EndsWith(nameof(NoPersonalPathsTests) + ".cs"))
                {
                    continue;
                }

                int n = 0;
                foreach (string line in File.ReadLines(f))
                {
                    n++;
                    if (UserFolder().IsMatch(line))
                    {
                        bad.Add($"{rel}:{n}");
                    }
                }
            }
        }

        foreach (string f in new[] { "README.md" })
        {
            string p = Path.Combine(root!, f);
            if (File.Exists(p) && File.ReadLines(p).Any(l => UserFolder().IsMatch(l)))
            {
                bad.Add(f);
            }
        }

        Assert.True(bad.Count == 0, "a user's folder is named in: " + string.Join(", ", bad.Take(20)));
    }
}
