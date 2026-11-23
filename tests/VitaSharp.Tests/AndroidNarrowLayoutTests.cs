using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Tests;

/// <summary>
/// The app's screens on a narrow (portrait) phone: a setting row's value goes below its title when it would squeeze it
/// (the desktop launcher's narrow-window rule), and the library's header is the title alone. The MAUI screens do not
/// run on the PC, so the screens' sources are checked for the layout they must use.
/// </summary>
public sealed class AndroidNarrowLayoutTests
{
    /// <summary>A source of the app, found from this file's repository (the tests may run from a build folder outside it).</summary>
    private static string AndroidSource(params string[] parts) => AndroidSourceIn(parts);

    private static string AndroidSourceIn(string[] parts, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        string dir = Path.GetDirectoryName(source)!;
        while (!File.Exists(Path.Combine(dir, "VitaSharp.sln")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("repository root");
        }

        return File.ReadAllText(Path.Combine([dir, "src", "VitaSharp.Android", .. parts]));
    }

    [Theory]
    [InlineData(360, "333 MHz", false)] // a short value stays beside the title on a small phone
    [InlineData(360, "Padrão (Automático, JIT ARM64 nativo)", true)]
    [InlineData(412, "Padrão (Automático, JIT ARM64 nativo)", true)]
    [InlineData(800, "Padrão (Automático, JIT ARM64 nativo)", false)] // a tablet or landscape: room beside it
    [InlineData(360, null, false)]
    [InlineData(360, "", false)]
    [InlineData(0, "Padrão (Automático, JIT ARM64 nativo)", false)] // width unknown: the wide layout
    public void ALongValueGoesBelowTheTitleOnANarrowScreen(double screenWidthDp, string? value, bool below)
    {
        Assert.Equal(below, NarrowLayout.ValueBelow(screenWidthDp, value));
    }

    [Fact]
    public void BesideTheTitleTheTitleKeepsItsWidth()
    {
        foreach (double width in new double[] { 320, 360, 393, 412, 480, 600, 800 })
        {
            foreach (string value in new[] { "On", "333 MHz", "Padrão (Ligado)", "Padrão (Automático, JIT ARM64 nativo)" })
            {
                if (!NarrowLayout.ValueBelow(width, value))
                {
                    double valueWidth = Math.Min(NarrowLayout.TextWidthDp(value, 14) + 8, NarrowLayout.MaxValueWidthDp);
                    Assert.True(width - NarrowLayout.RowChromeDp - valueWidth >= NarrowLayout.MinLabelWidthDp, $"{width} dp, '{value}'");
                }
            }
        }
    }

    [Fact]
    public void TheScreensWrapInsteadOfCutting()
    {
        string screen = AndroidSource("Ui", "Screen.cs");
        Assert.Contains("NarrowLayout.ValueBelow(", screen, StringComparison.Ordinal);
        Assert.Contains("FlexWrap.Wrap", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("TailTruncation", screen, StringComparison.Ordinal); // rows and page titles wrap
        Assert.Contains("ChipRow(", AndroidSource("Pages", "LibraryView.cs"), StringComparison.Ordinal);
        Assert.Contains("ChipRow(", AndroidSource("Pages", "TrophiesPage.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("MaximumWidthRequest(230)", AndroidSource("Pages", "GamePage.cs"), StringComparison.Ordinal);
    }

    /// <summary>The library's header is its title alone: no "VITASHARP" above it, no install chip or Ajustes button beside
    /// it (installing is the floating button and Ajustes > Instalar jogo; Ajustes is its tab).</summary>
    [Fact]
    public void TheLibraryHeaderIsTheTitleAlone()
    {
        string library = AndroidSource("Pages", "LibraryView.cs");
        Assert.DoesNotContain("\"VITASHARP\"", library, StringComparison.Ordinal);
        Assert.DoesNotContain("MInstallChip", library, StringComparison.Ordinal);
        Assert.DoesNotContain("RootTab.Settings", library, StringComparison.Ordinal);
        Assert.Contains("MInstallGamePlus", library, StringComparison.Ordinal); // the floating button stays
    }
}
