using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VitaSharp.Tests;

/// <summary>
/// The Android app's dialogs (Choose/Confirm/Alert/Prompt and action sheets through MAUI, the framework dialogs of the
/// pad capture, storage setup, in-game keyboard and fatal error) use the app's palette through the colour tokens of
/// Platforms/Android/Resources/values/colors.xml, the same values as Ui/Theme.cs Palette, in the system's light and
/// dark modes alike: the stock dialogs followed the system theme while the screens are always dark. Checked on the
/// resource files (the Android project does not build in the desktop test run).
/// </summary>
public sealed partial class AndroidDialogThemeTests
{
    private static readonly string[] s_textAttributes =
        ["android:textColorPrimary", "android:textColorSecondary", "android:textColorAlertDialogListItem"];

    private static string AndroidPath(string[] parts, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        string dir = Path.GetDirectoryName(source)!;
        while (!File.Exists(Path.Combine(dir, "VitaSharp.sln")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("repository root");
        }

        return Path.Combine([dir, "src", "VitaSharp.Android", .. parts]);
    }

    private static string ResourceDir(string qualifier) => AndroidPath(["Platforms", "Android", "Resources", qualifier]);

    /// <summary>Colours and styles as the system resolves them in one mode: values-night overrides values.</summary>
    private static (Dictionary<string, string> Colors, Dictionary<string, Dictionary<string, string>> Styles) Resolve(bool night)
    {
        var colors = new Dictionary<string, string>();
        var styles = new Dictionary<string, Dictionary<string, string>>();
        string[] qualifiers = night ? ["values", "values-night"] : ["values"];
        foreach (string qualifier in qualifiers)
        {
            string dir = ResourceDir(qualifier);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(dir, "*.xml"))
            {
                XElement root = XDocument.Load(file).Root!;
                foreach (XElement color in root.Elements("color"))
                {
                    colors[(string)color.Attribute("name")!] = color.Value.Trim();
                }

                foreach (XElement style in root.Elements("style"))
                {
                    styles[(string)style.Attribute("name")!] = style.Elements("item")
                        .ToDictionary(item => (string)item.Attribute("name")!, item => item.Value.Trim());
                }
            }
        }

        return (colors, styles);
    }

    [GeneratedRegex("""public static readonly Color (\w+) = Color\.FromArgb\("(#[0-9A-Fa-f]{6})"\)""")]
    private static partial Regex PaletteEntry();

    private static Dictionary<string, string> Palette() =>
        PaletteEntry().Matches(File.ReadAllText(AndroidPath(["Ui", "Theme.cs"])))
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant());

    [Fact]
    public void TheColourTokensAreThePalette()
    {
        Dictionary<string, string> palette = Palette();
        (Dictionary<string, string> colors, _) = Resolve(night: false);
        Assert.Equal(palette["Surface"], colors["vs_surface"].ToUpperInvariant());
        Assert.Equal(palette["Text"], colors["vs_text"].ToUpperInvariant());
        Assert.Equal(palette["TextSoft"], colors["vs_text_soft"].ToUpperInvariant());
        Assert.Equal(palette["Muted"], colors["vs_muted"].ToUpperInvariant());
        Assert.Equal(palette["Accent"], colors["vs_accent"].ToUpperInvariant());
    }

    /// <summary>Every activity's dialogs go to the app's dialog styles: MAUI's (AndroidX) and the framework's.</summary>
    [Fact]
    public void EveryActivityThemeUsesTheDialogStyles()
    {
        foreach (bool night in new[] { false, true })
        {
            (_, Dictionary<string, Dictionary<string, string>> styles) = Resolve(night);
            Dictionary<string, string> main = styles["Maui.MainTheme"];
            Assert.Equal("@style/VitaSharp.AlertDialog", main["alertDialogTheme"]);
            Assert.Equal("@style/VitaSharp.AlertDialog", main["materialAlertDialogTheme"]);
            Assert.Equal("@style/VitaSharp.FrameworkAlertDialog", main["android:alertDialogTheme"]);
            Assert.Equal("@style/VitaSharp.FrameworkAlertDialog", styles["VitaSharp.GameTheme"]["android:alertDialogTheme"]);
        }

        Assert.Contains("Theme = \"@style/VitaSharp.GameTheme\"", File.ReadAllText(AndroidPath(["Platforms", "Android", "Game", "GameActivity.cs"])));
    }

    /// <summary>The dialog styles use the tokens, never a fixed colour, and their text reads on their background
    /// (WCAG AA: 4.5:1 for text, 3:1 for hints) in both system modes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DialogTextIsReadableInBothSystemModes(bool night)
    {
        (Dictionary<string, string> colors, Dictionary<string, Dictionary<string, string>> styles) = Resolve(night);
        foreach (string styleName in new[] { "VitaSharp.AlertDialog", "VitaSharp.FrameworkAlertDialog" })
        {
            Dictionary<string, string> style = styles[styleName];
            foreach ((string attribute, string value) in style)
            {
                Assert.True(value.StartsWith("@color/vs_", StringComparison.Ordinal), $"{styleName} {attribute} = {value}: not a colour token");
            }

            string background = Color(colors, style["android:colorBackground"]);
            foreach (string attribute in s_textAttributes)
            {
                double ratio = Contrast(Color(colors, style[attribute]), background);
                Assert.True(ratio >= 4.5, $"{styleName} {attribute}: contrast {ratio:F2} on {background}");
            }

            Assert.True(Contrast(Color(colors, style["android:textColorHint"]), background) >= 3);
            string accent = style.TryGetValue("colorAccent", out string? appCompatAccent) ? appCompatAccent : style["android:colorAccent"];
            Assert.True(Contrast(Color(colors, accent), background) >= 4.5, $"{styleName}: buttons");
        }

        Assert.Equal(styles["VitaSharp.AlertDialog"]["android:colorBackground"], styles["VitaSharp.AlertDialog"]["colorSurface"]);
        Assert.Equal(styles["VitaSharp.AlertDialog"]["android:textColorPrimary"], styles["VitaSharp.AlertDialog"]["colorOnSurface"]);
    }

    /// <summary>The contrast helper itself: black on white is 21:1, a colour on itself 1:1.</summary>
    [Fact]
    public void ContrastFollowsWcag()
    {
        Assert.Equal(21, Contrast("#000000", "#FFFFFF"), 1);
        Assert.Equal(1, Contrast("#6E9BFF", "#6E9BFF"), 3);
    }

    private static string Color(Dictionary<string, string> colors, string reference)
    {
        Assert.StartsWith("@color/", reference);
        return colors[reference["@color/".Length..]];
    }

    private static double Contrast(string first, string second)
    {
        double a = Luminance(first);
        double b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        int rgb = int.Parse(hex.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return 0.2126 * Channel(rgb >> 16) + 0.7152 * Channel(rgb >> 8) + 0.0722 * Channel(rgb);
    }

    private static double Channel(int value)
    {
        double c = (value & 0xFF) / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
