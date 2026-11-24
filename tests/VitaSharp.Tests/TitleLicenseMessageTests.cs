using VitaSharp.Core.Localization;
using VitaSharp.Core.Package;

namespace VitaSharp.Tests;

/// <summary>
/// The missing-license message (<see cref="TitleLicense.Message"/>, the launch error of SceAppMgr 0x80800004) follows the
/// interface language. It switches the process-wide language, so it runs in the "language" collection and puts back the
/// language it found; it ran in the "guest" collection beside other tests and left the system language behind.
/// </summary>
[Collection("language")]
public sealed class TitleLicenseMessageTests
{
    private const string TitleId = "LICX00001";

    [Fact]
    public void TheLicenseMessagesAreTranslated()
    {
        using var language = new LanguageScope();
        Loc.SetLanguage(Loc.Portuguese);
        string portuguese = TitleLicense.Message(TitleId);
        Assert.Contains("licença", portuguese);
        Assert.Contains("0x80800004", portuguese);
        Loc.SetLanguage(Loc.English);
        string english = TitleLicense.Message(TitleId);
        Assert.Contains("license", english);
        Assert.NotEqual(portuguese, english);
    }
}
