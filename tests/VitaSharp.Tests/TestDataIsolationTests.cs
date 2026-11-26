using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using VitaSharp.Desktop;

namespace VitaSharp.Tests;

/// <summary>
/// The suite never reads or writes the user's own data folder (%APPDATA%\VitaSharp, ~/.local/share/VitaSharp,
/// ~/Library/Application Support/VitaSharp: input.json, launcher.json, playtime.json, firmware, save states, shader
/// cache, logs): the run's data folder is a temporary one made by <see cref="TestDataIsolation"/>, so a run behaves the
/// same on a developer's machine and on the CI.
/// </summary>
public sealed partial class TestDataIsolationTests
{
    /// <summary>Everything the front-end resolves (Program.DataRoot: input.json, launcher.json, logs, shader cache,
    /// save states, diagnostics, GPU dumps, the child processes' --data) sits in the temporary folder of the run, never
    /// in the platform's default data folder.</summary>
    [Fact]
    public void TheDataFolderOfTheRunIsATemporaryOne()
    {
        string resolved = Path.GetFullPath(Program.DataRoot);
        string user = Path.GetFullPath(DataPaths.DefaultRoot());
        string temp = Path.GetFullPath(Path.GetTempPath());
        Assert.False(IsUnder(resolved, user), $"the tests resolve the user's data folder: {resolved}");
        Assert.True(IsUnder(resolved, temp), $"the tests' data folder is not a temporary one: {resolved}");
        Assert.True(IsUnder(Path.GetFullPath(HostInputConfig()), temp), HostInputConfig());
    }

    /// <summary>The run's own folder is the one the initializer made, under the temporary folder, and it exists.</summary>
    [Fact]
    public void TheRunHasItsOwnDataFolder()
    {
        Assert.True(IsUnder(TestDataIsolation.Root, Path.GetFullPath(Path.GetTempPath())), TestDataIsolation.Root);
        Assert.False(IsUnder(TestDataIsolation.Root, Path.GetFullPath(DataPaths.DefaultRoot())), TestDataIsolation.Root);
        Assert.True(Directory.Exists(TestDataIsolation.Root), TestDataIsolation.Root);
    }

    // %APPDATA% (or the variable that holds it) joined with "VitaSharp", or the front-end's default folder itself
    [GeneratedRegex(@"(SpecialFolder\.ApplicationData\)|\bappData)\s*,\s*""VitaSharp""|DataPaths\.DefaultRoot\(", RegexOptions.IgnoreCase)]
    private static partial Regex UserDataFolder();

    /// <summary>No test builds the path of the user's data folder by itself: modules a test wants from an installed
    /// data folder come from <see cref="TestDataIsolation.InstalledData"/> (an explicit, read-only opt-in).</summary>
    [Fact]
    public void NoTestBuildsTheUsersDataFolderPath()
    {
        var bad = new List<string>();
        foreach (string f in Directory.EnumerateFiles(TestsDir(), "*.cs", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(f);
            if (name is nameof(TestDataIsolationTests) + ".cs")
            {
                continue;
            }

            int n = 0;
            foreach (string line in File.ReadLines(f))
            {
                n++;
                if (UserDataFolder().IsMatch(line))
                {
                    bad.Add($"{name}:{n}: {line.Trim()}");
                }
            }
        }

        Assert.True(bad.Count == 0, "tests that reach the user's data folder:\n" + string.Join("\n", bad));
    }

    private static string HostInputConfig() => VitaSharp.Desktop.Input.HostInput.ConfigPath(Program.DataRoot);

    private static bool IsUnder(string path, string root)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        StringComparison cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return full.Equals(dir, cmp) || full.StartsWith(dir + Path.DirectorySeparatorChar, cmp);
    }

    private static string TestsDir([CallerFilePath] string source = "") => Path.GetDirectoryName(source)!;
}
