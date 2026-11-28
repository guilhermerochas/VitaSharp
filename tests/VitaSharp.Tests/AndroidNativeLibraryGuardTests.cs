using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace VitaSharp.Tests;

/// <summary>
/// An APK built without libshaderc_shared.so opens no game ("Could not load from any of the possible library names" in
/// ShadercLoader), and the app project packages the native libraries only when they exist. So the project must fail the
/// build when the arm64-v8a libshaderc is missing, and warn (not stay silent) when libadrenotools is missing.
/// </summary>
public class AndroidNativeLibraryGuardTests
{
    private static XDocument Project([CallerFilePath] string source = "")
    {
        for (DirectoryInfo? directory = new(Path.GetDirectoryName(source)!); directory != null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "src", "VitaSharp.Android", "VitaSharp.Android.csproj");
            if (File.Exists(path))
            {
                return XDocument.Load(path);
            }
        }

        throw new FileNotFoundException("src/VitaSharp.Android/VitaSharp.Android.csproj");
    }

    private static IEnumerable<XElement> Targets(XDocument project) => project.Descendants("Target");

    private static XElement Target(string name) =>
        Targets(Project()).Single(target => (string?)target.Attribute("Name") == name);

    [Fact]
    public void MissingShadercFailsTheBuild()
    {
        XElement target = Target("_VitaSharpCheckShaderc");
        XElement error = Assert.Single(target.Elements("Error"));
        string text = (string)error.Attribute("Text")!;

        Assert.Contains("libshaderc_shared.so", text);
        Assert.Contains("build-android-shaderc", text);

        // the target runs only when the file is absent, and the file it looks for is the one the app packages
        string condition = (string)target.Attribute("Condition")!;
        Assert.Contains("!Exists('$(VitaSharpShadercArm64)')", condition);
        string property = Project().Descendants("VitaSharpShadercArm64").Single().Value;
        Assert.EndsWith(@"android-shaderc\libs\arm64-v8a\libshaderc_shared.so", property);
        Assert.Equal("PrepareForBuild", (string)target.Attribute("BeforeTargets")!);
    }

    [Fact]
    public void ShadercGuardIsNotConditionalOnTheLibraryOnly()
    {
        // the guard must not be switched off by default: only the explicit opt-out (and a non-arm64 build) skips it
        string condition = (string)Target("_VitaSharpCheckShaderc").Attribute("Condition")!;
        Assert.Contains("'$(VitaSharpAllowMissingNativeLibs)' != 'true'", condition);
        Assert.Contains("'$(_VitaSharpBuildsArm64)' == 'true'", condition);
        Assert.DoesNotContain("VitaSharpAllowMissingNativeLibs)' == ''", condition);

        // no property of the project turns the opt-out on by default
        Assert.DoesNotContain(Project().Descendants("VitaSharpAllowMissingNativeLibs"), element => element.Value == "true");
    }

    [Fact]
    public void MissingAdrenotoolsWarnsInsteadOfStayingSilent()
    {
        XElement target = Target("_VitaSharpWarnAdrenotools");
        XElement warning = Assert.Single(target.Elements("Warning"));
        string text = (string)warning.Attribute("Text")!;

        Assert.Contains("libadrenotools.so", text);
        Assert.Contains("build-android-adrenotools", text);
        Assert.Contains("!Exists('$(VitaSharpAdrenotoolsArm64)')", (string)target.Attribute("Condition")!);
    }

    [Fact]
    public void ShadercIsPackagedFromTheGuardedPath()
    {
        // the packaged arm64 file and the guarded file are the same one
        bool packaged = Project().Descendants("AndroidNativeLibrary")
            .Any(item => ((string?)item.Attribute("Include"))?.EndsWith(@"android-shaderc\libs\arm64-v8a\libshaderc_shared.so", StringComparison.Ordinal) == true);
        Assert.True(packaged);
    }
}
