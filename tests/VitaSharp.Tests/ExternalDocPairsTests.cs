using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace VitaSharp.Tests;

/// <summary>
/// The external docs (what people who use the emulator read: README, compatibility list) come in English and Brazilian
/// Portuguese, side by side (<c>README.md</c> + <c>README.pt-BR.md</c>). Each file starts with
/// <c>&lt;!-- pair: &lt;other file&gt; sha256=&lt;hash of the other file&gt; --&gt;</c>, so a change to one of them without its
/// pair fails here: update the other language, then the two hash lines (the failure prints them). Internal docs
/// (roadmap, docs/agents, docs/notes, docs/hle) stay in one language.
/// </summary>
public class ExternalDocPairsTests
{
    /// <summary>The external docs that must have both languages (English path; the pair is *.pt-BR.md).</summary>
    static readonly string[] Required = ["README.md", "docs/compatibility.md"];

    const string Marker = "<!-- pair: ";

    static string Root([CallerFilePath] string source = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(source)!); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "VitaSharp.sln")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException("repository root");
    }

    /// <summary>SHA-256 of a doc without its own pair line, with LF line ends (the same on every checkout).</summary>
    public static string Hash(string path)
    {
        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0].StartsWith(Marker, StringComparison.Ordinal))
        {
            lines.RemoveAt(0);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    static string PairLine(string path) =>
        File.ReadLines(path).FirstOrDefault() is { } first && first.StartsWith(Marker, StringComparison.Ordinal) ? first : "";

    static string Expected(string other) => $"{Marker}{Path.GetFileName(other)} sha256={Hash(other)} -->";

    [Fact]
    public void EveryExternalDocHasItsTranslationInStep()
    {
        string root = Root();
        var english = Required.Select(p => Path.Combine(root, p)).ToList();
        foreach (var dir in new[] { root, Path.Combine(root, "docs") })
        {
            foreach (var pt in Directory.EnumerateFiles(dir, "*.pt-BR.md"))
            {
                english.Add(pt[..^".pt-BR.md".Length] + ".md");
            }
        }

        var bad = new List<string>();
        foreach (var en in english.Distinct())
        {
            string pt = en[..^".md".Length] + ".pt-BR.md";
            string name = Path.GetRelativePath(root, en);
            if (!File.Exists(en) || !File.Exists(pt))
            {
                bad.Add($"{name}: needs both {Path.GetFileName(en)} and {Path.GetFileName(pt)}");
                continue;
            }
            foreach (var (file, other) in new[] { (en, pt), (pt, en) })
            {
                string text = File.ReadAllText(file);
                if (!text.Contains($"({Path.GetFileName(other)})", StringComparison.Ordinal))
                {
                    bad.Add($"{Path.GetRelativePath(root, file)}: no \"English | Português\" link to {Path.GetFileName(other)}");
                }

                if (PairLine(file) != Expected(other))
                {
                    bad.Add($"{Path.GetRelativePath(root, file)}: {Path.GetFileName(other)} changed without this file (or the reverse). " +
                            $"Bring the two languages in step, then make the first line of each: {Expected(other)}  /  {Expected(file)}");
                }
            }
        }
        Assert.Empty(bad);
    }
}
