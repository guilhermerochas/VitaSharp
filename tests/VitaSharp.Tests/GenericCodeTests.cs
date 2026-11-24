using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VitaSharp.Tests;

/// <summary>
/// The emulator is generic (the project rule): nothing in src/ compares a program with a
/// literal title ID or title-ID prefix to change what it does ("NPXS10008", "PCSB00123", "NPXS", "PCSE"...). What
/// the firmware decides by data comes from that data (param.sfo, the vs0: origin, app.db). Comments may name the
/// program where a bug showed up; tests may use any ID.
/// </summary>
public partial class GenericCodeTests
{
    /// <summary>A whole string literal that is a title ID (four letters, five digits) or a title-ID prefix of the
    /// system software or the store regions.</summary>
    [GeneratedRegex(@"^(?:[A-Z]{4}\d{5}|NPXS\d{0,4}|PCS[A-H]\d{0,4}|NP[A-Z][A-Z]\d{0,4})$")]
    private static partial Regex TitleIdLiteral();

    /// <summary>
    /// Title-ID-shaped literals that are data the firmware itself holds as a constant, written where VitaSharp stands
    /// for that firmware code (never compared with a program to choose what to do): file ('/' separators), literal and
    /// where in the firmware the value is (module, version, offset). The project rule allows a name fixed by the
    /// firmware; an entry without its source, or whose literal left the file, fails FirmwareConstantsAreJustifiedAndInUse.
    /// </summary>
    internal static readonly (string File, string Literal, string Reason)[] s_firmwareConstants =
    [
        ("src/VitaSharp.Core/Hle/Modules/SystemServicesVsh.EventLog.cs", "NPXS19999",
            "sysmem 3.74 read-only data (segment 0, offset 0x26FAC): the name copied into every event log record"),
        ("src/VitaSharp.Core/Hle/Modules/Compat.cs", "vs0:app/NPXS10028/pcff.skprx",
            "SceCompat 3.74 (os0:kd/bootimage.skprx) read-only data (segment 0, offset 0x4500): the path sceCompatInitEx loads the PSP compatibility firmware from"),
    ];

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

    /// <summary>The string literals of C# source outside comments, with their line numbers (regular, verbatim and
    /// interpolated strings; the text of an interpolation hole counts as part of the literal, which is enough here).</summary>
    internal static IEnumerable<(int Line, string Text)> StringLiterals(string source)
    {
        int line = 1;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '\n')
            {
                line++;
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                line++;
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                for (i += 2; i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'); i++)
                {
                    if (source[i] == '\n')
                    {
                        line++;
                    }
                }

                i++;
            }
            else if (c == '\'')
            {
                // a character literal: '"' must not open a string
                for (i++; i < source.Length && source[i] != '\''; i++)
                {
                    if (source[i] == '\\')
                    {
                        i++;
                    }
                }
            }
            else if (c == '"')
            {
                bool verbatim = i > 0 && (source[i - 1] == '@' || (i > 1 && source[i - 1] == '$' && source[i - 2] == '@'));
                int start = line;
                var text = new StringBuilder();
                for (i++; i < source.Length; i++)
                {
                    char d = source[i];
                    if (d == '\n')
                    {
                        line++;
                    }

                    if (verbatim && d == '"' && i + 1 < source.Length && source[i + 1] == '"')
                    {
                        text.Append('"');
                        i++;
                        continue;
                    }

                    if (!verbatim && d == '\\')
                    {
                        text.Append(d);
                        if (i + 1 < source.Length)
                        {
                            text.Append(source[++i]);
                        }

                        continue;
                    }

                    if (d == '"' || (!verbatim && d == '\n'))
                    {
                        break;
                    }

                    text.Append(d);
                }

                yield return (start, text.ToString());
            }
        }
    }

    [Fact]
    public void ScannerSkipsCommentsAndReadsLiterals()
    {
        const string Src = "// \"NPXS10008\"\nvar a = \"PCSB00123\"; /* \"NPXS\" */ char q = '\"'; var b = @\"x\"\"y\";\n/// \"PCSE\"\n";
        var found = StringLiterals(Src).ToList();
        Assert.Equal([(2, "PCSB00123"), (2, "x\"y")], found);
        Assert.Matches(TitleIdLiteral(), "PCSB00123");
        Assert.Matches(TitleIdLiteral(), "NPXS");
        Assert.DoesNotMatch(TitleIdLiteral(), "NPUMDSIG");
        Assert.DoesNotMatch(TitleIdLiteral(), "a system app such as NPXS10008");
    }

    [Fact]
    public void NoTitleIdLiteralsInSource()
    {
        string? root = RepoRoot();
        Assert.NotNull(root);
        string sep = Path.DirectorySeparatorChar.ToString();
        var bad = new List<string>();
        foreach (string f in Directory.EnumerateFiles(Path.Combine(root!, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root!, f);
            if (rel.Contains(sep + "bin" + sep) || rel.Contains(sep + "obj" + sep) || rel.Contains(sep + "obj-dev" + sep) || rel.Contains(sep + "bin-dev" + sep))
            {
                continue;
            }

            string relSlash = rel.Replace('\\', '/');
            foreach ((int line, string? text) in StringLiterals(File.ReadAllText(f)))
            {
                if (TitleIdLiteral().IsMatch(text) && !Array.Exists(s_firmwareConstants, c => c.File == relSlash && c.Literal == text))
                {
                    bad.Add($"{rel}:{line}: \"{text}\"");
                }
            }
        }

        Assert.True(bad.Count == 0, "title IDs compared in code (use param.sfo, the vs0: origin or app.db):\n" + string.Join("\n", bad));
    }

    /// <summary>Every firmware constant names its firmware source (module, version, offset) and is still a literal of
    /// its file (a stale entry would let the same literal in later for another reason).</summary>
    [Fact]
    public void FirmwareConstantsAreJustifiedAndInUse()
    {
        string? root = RepoRoot();
        Assert.NotNull(root);
        foreach ((string file, string literal, string reason) in s_firmwareConstants)
        {
            Assert.Matches(TitleIdLiteral(), literal);
            Assert.Matches(@"\d\.\d{2}\b.*0x[0-9A-F]+", reason);
            string path = Path.Combine(root!, file.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{file}: no such file");
            Assert.Contains(StringLiterals(File.ReadAllText(path)), found => found.Text == literal);
        }
    }
}
