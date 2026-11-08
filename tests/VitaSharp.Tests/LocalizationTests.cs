using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using VitaSharp.Core.Localization;

namespace VitaSharp.Tests;

/// <summary>
/// The interface texts (src/VitaSharp.Core/Localization/Texts.resx in English, Texts.pt-BR.resx in Portuguese): both
/// files have the same keys with the same placeholders, every key the code asks for exists, and the desktop sources
/// hold no interface text of their own (they are ASCII-only, Ryubing rule 16: a Portuguese text in the code fails).
/// </summary>
public partial class LocalizationTests
{
    static readonly IReadOnlyDictionary<string, string> English = Loc.Table(CultureInfo.InvariantCulture);
    static readonly IReadOnlyDictionary<string, string> Portuguese = Loc.Table(CultureInfo.GetCultureInfo(Loc.Portuguese));

    [GeneratedRegex(@"\{(\d+)")]
    private static partial Regex Placeholder();

    static string Root([CallerFilePath] string source = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(source)!); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "VitaSharp.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    [Fact]
    public void BothLanguagesHaveTheSameKeysAndPlaceholders()
    {
        Assert.True(English.Count > 400, $"English table has {English.Count} texts");
        Assert.Empty(English.Keys.Except(Portuguese.Keys).Select(k => "missing in pt-BR: " + k));
        Assert.Empty(Portuguese.Keys.Except(English.Keys).Select(k => "missing in en: " + k));
        var bad = new List<string>();
        foreach (var (key, en) in English)
        {
            string pt = Portuguese[key];
            if (en.Trim().Length == 0 || pt.Trim().Length == 0) bad.Add($"{key}: empty");
            var a = Placeholder().Matches(en).Select(m => m.Groups[1].Value).Distinct().Order();
            var b = Placeholder().Matches(pt).Select(m => m.Groups[1].Value).Distinct().Order();
            if (!a.SequenceEqual(b)) bad.Add($"{key}: placeholders {string.Join(",", a)} vs {string.Join(",", b)}");
            try { _ = string.Format(CultureInfo.InvariantCulture, en, new object[10]); _ = string.Format(CultureInfo.InvariantCulture, pt, new object[10]); }
            catch (FormatException) { bad.Add($"{key}: not a valid format text"); }
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void LanguageFollowsTheChoiceAndFallsBackToEnglish()
    {
        Assert.Equal(Loc.Portuguese, Loc.Pick("pt-BR").Name);
        Assert.Equal(Loc.Portuguese, Loc.Pick("pt").Name);
        Assert.Equal(Loc.English, Loc.Pick("en").Name);
        Assert.Equal(Loc.English, Loc.Pick("fr-FR").Name);
        Assert.Equal("Biblioteca", Portuguese["Library"]);
        Assert.Equal("Library", English["Library"]);
    }

    /// <summary>Every key the sources ask for: Loc.Get/Format("Key"), T("Key"), and the launcher's Strings properties and
    /// methods that read the key of their own name (T()).</summary>
    [Fact]
    public void EveryKeyUsedInTheCodeExists()
    {
        string src = Path.Combine(Root(), "src");
        var used = new SortedSet<string>();
        var call = new Regex(@"\b(?:Loc\.(?:Get|Format)|T)\(""(\w+)""");
        var own = new Regex(@"public static string (\w+)(?:\([^)]*\))? => (?:T\(\)|F\(T\(\))");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string text = File.ReadAllText(file);
            foreach (Match m in call.Matches(text)) used.Add(m.Groups[1].Value);
            if (Path.GetFileName(file) == "Strings.cs")
                foreach (Match m in own.Matches(text)) used.Add(m.Groups[1].Value);
        }
        Assert.True(used.Count > 400, $"only {used.Count} keys found in the sources");
        var missing = used.Where(k => !English.ContainsKey(k)).ToList();
        Assert.Empty(missing);
    }

    /// <summary>The desktop sources (launcher, game window, notices) are ASCII-only: every interface text lives in the
    /// language files, symbols are \u escapes.</summary>
    [Fact]
    public void DesktopSourcesHaveNoTextOfTheirOwn()
    {
        string dir = Path.Combine(Root(), "src", "VitaSharp.Desktop");
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Any(c => c > 127 && c != '\uFEFF')) bad.Add($"{Path.GetRelativePath(dir, file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.Empty(bad);
    }
}
