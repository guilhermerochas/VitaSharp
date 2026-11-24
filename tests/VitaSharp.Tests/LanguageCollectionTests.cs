using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace VitaSharp.Tests;

/// <summary>
/// The interface language (<see cref="VitaSharp.Core.Localization.Loc"/>) is one per process, as the app's setting is:
/// a test that switches it (<see cref="LanguageScope"/>, <c>Loc.SetLanguage</c>) runs in the "language" collection,
/// which never runs beside other tests. RunnerStartFailureTests switched it outside the collection, so a test in the
/// collection beside it put Portuguese back between its report and its expected text ("PCSB00123 could not be
/// started" against "Não foi possível iniciar PCSB00123", depending on the order the tests ran in).
/// </summary>
public sealed class LanguageCollectionTests
{
    private static readonly Regex s_switchesLanguage = new(@"new\s+LanguageScope\s*\(|Loc\.SetLanguage\s*\(", RegexOptions.Compiled);
    private static readonly Regex s_inLanguageCollection = new(@"\[Collection\(""language""\)\]", RegexOptions.Compiled);

    private static string TestsFolder([CallerFilePath] string source = "")
    {
        return Path.GetDirectoryName(source) ?? "";
    }

    [Fact]
    public void Every_test_class_that_switches_the_language_runs_in_the_language_collection()
    {
        string folder = TestsFolder();
        Assert.True(File.Exists(Path.Combine(folder, "LanguageScope.cs")), $"the tests' sources are not at {folder}");
        var outside = new List<string>();
        foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name == "LanguageScope.cs" || name == nameof(LanguageCollectionTests) + ".cs")
            {
                continue;
            }

            string text = File.ReadAllText(file);
            if (s_switchesLanguage.IsMatch(text) && !s_inLanguageCollection.IsMatch(text))
            {
                outside.Add(Path.GetRelativePath(folder, file));
            }
        }

        Assert.True(outside.Count == 0, "switch the interface language outside the \"language\" collection: " + string.Join(", ", outside));
    }
}
