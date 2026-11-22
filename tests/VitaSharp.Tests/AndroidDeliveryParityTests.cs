using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Desktop;
using VitaSharp.Mobile.Core.Cpu;
using VitaSharp.Mobile.Core.Settings;
using DesktopStrings = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Tests;

/// <summary>
/// What the desktop got on 20-21/11 also behaves the same on the Android app: the boot step of "Precompilar codigo
/// ARM" waits with its progress and "Cancelar" over the game (the desktop's status bar), the MSAA modes of the
/// desktop's F3 key (native, 2x supersampling, off) per game and in the pause menu, and real internet with the same
/// hosts (the PSP system update servers reachable, PSN blocked, VITASHARP_NET_ALLOW from env.txt). The MAUI screens do
/// not run on the PC, so their sources are checked for the wiring; the logic is in the platform-neutral Mobile.Core.
/// </summary>
[Collection("language")]
public sealed class AndroidDeliveryParityTests : IDisposable
{
    public AndroidDeliveryParityTests()
    {
        CodePrecompileLog.Reset();
    }

    public void Dispose()
    {
        CodePrecompileLog.Reset();
    }

    private static string AndroidSource(params string[] parts) => File.ReadAllText(Path.Combine([Root(), "src", "VitaSharp.Android", .. parts]));

    /// <summary>The repository of this source file (the tests may run from a build folder outside it).</summary>
    private static string Root([CallerFilePath] string source = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VitaSharp.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("repository root");
    }

    [Fact]
    public void TheArmCodeBannerFollowsTheBootStepWithTheDesktopTextAndHidesAtTheEnd()
    {
        var banner = new CodePrecompileBanner();
        var seen = new List<CodePrecompileProgress>();
        void OnReported(CodePrecompileProgress progress)
        {
            seen.Add(progress);
            banner.Show(progress);
        }

        CodePrecompileLog.Reported += OnReported;
        try
        {
            var reporter = new CodePrecompileLog.Reporter();
            reporter.Progress(new CodePrecompileProgress(25, 100, false));
            Assert.True(banner.Visible);
            Assert.Equal(DesktopStrings.PrecompileCodeStatus(25, 100), banner.Text);
            Assert.Equal(0.25, banner.Fraction, 3);
            Assert.Equal(DesktopStrings.PrecompileCodeCancel, CodePrecompileBanner.CancelText);

            reporter.Progress(new CodePrecompileProgress(100, 100, true));
            Assert.False(banner.Visible);
            reporter.End(); // the step's own finished progress was seen: no second one
        }
        finally
        {
            CodePrecompileLog.Reported -= OnReported;
        }

        Assert.Equal([new CodePrecompileProgress(25, 100, false), new CodePrecompileProgress(100, 100, true)], seen);
    }

    [Fact]
    public void CancelStartsTheGameAndTheStepStillEndsWithAFinishedProgress()
    {
        var banner = new CodePrecompileBanner();
        var seen = new List<CodePrecompileProgress>();
        void OnReported(CodePrecompileProgress progress)
        {
            seen.Add(progress);
            banner.Show(progress);
        }

        CodePrecompileLog.Reported += OnReported;
        try
        {
            var reporter = new CodePrecompileLog.Reporter();
            reporter.Progress(new CodePrecompileProgress(3, 40, false));
            Assert.True(banner.Visible);
            Assert.False(CodePrecompileLog.Token.IsCancellationRequested);

            banner.Cancel(); // the card's "Cancelar"
            Assert.True(CodePrecompileLog.Token.IsCancellationRequested);
            Assert.False(banner.Visible);

            // the precompiler stops without a finished progress of its own: the end is reported all the same
            reporter.End();
            reporter.End();
        }
        finally
        {
            CodePrecompileLog.Reported -= OnReported;
        }

        Assert.Equal(2, seen.Count);
        Assert.True(seen[1].Finished);
        Assert.Equal((3, 40), (seen[1].Done, seen[1].Total));
    }

    [Fact]
    public void TheAndroidGameWaitsForTheBootStepAsTheDesktopDoesWithItsCard()
    {
        Assert.True(new EmulatorOptions { FsRoot = "" }.PrecompileCodeWaits); // the desktop's default, which Android now keeps
        string session = AndroidSource("Platforms", "Android", "Game", "EmulationSession.cs");
        Assert.DoesNotContain("PrecompileCodeWaits", session, StringComparison.Ordinal);
        Assert.Contains("PrecompileCode = _settings.PrecompileCode", session, StringComparison.Ordinal);

        string activity = AndroidSource("Platforms", "Android", "Game", "GameActivity.cs");
        Assert.Contains("AddCodePrecompileBanner(root)", activity, StringComparison.Ordinal);
        Assert.Contains("RemoveCodePrecompileBanner()", activity, StringComparison.Ordinal);
        string card = AndroidSource("Platforms", "Android", "Game", "GameActivity.CodePrecompile.cs");
        Assert.Contains("CodePrecompileLog.Reported += ", card, StringComparison.Ordinal);
        Assert.Contains("_codePrecompile.Cancel()", card, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDesktopMsaaModeIsOfferedOnAndroidInTheF3Order()
    {
        Assert.Equal(GraphicsHotkeys.MsaaModes.Length, SettingsChoices.MsaaModes.Count);
        for (int i = 0; i < GraphicsHotkeys.MsaaModes.Length; i++)
        {
            string desktop = GraphicsHotkeys.MsaaModes[i];
            string android = SettingsChoices.MsaaModes[i].Value;
            Assert.Equal(desktop == "native" ? "" : desktop, android);
            // the renderer gets the desktop's value (null = native: VITASHARP_MSAA, else what the game asks)
            var effective = EffectiveSettings.Resolve(new AppPreferences(), new GameSettings { Msaa = android.Length > 0 ? android : null });
            Assert.Equal(desktop == "native" ? null : desktop, effective.MsaaValue);
            // the pause menu moves to the next mode as F3 does
            string next = GraphicsHotkeys.MsaaModes[(i + 1) % GraphicsHotkeys.MsaaModes.Length];
            Assert.Equal(next == "native" ? "" : next, SettingsChoices.NextMsaa(android));
        }

        // a value this build does not know is native
        Assert.Null(EffectiveSettings.Resolve(new AppPreferences(), new GameSettings { Msaa = "8x" }).MsaaValue);
        Assert.Equal("ss", SettingsChoices.NextMsaa(null));
    }

    [Fact]
    public void ThePauseMenuMsaaRowCyclesTheModesInsteadOfAnOnOffSwitch()
    {
        string menu = AndroidSource("Platforms", "Android", "Game", "GameActivity.PauseMenu.cs");
        Assert.Contains("SettingsChoices.NextMsaa(", menu, StringComparison.Ordinal);
        Assert.DoesNotContain("MsaaOff", menu, StringComparison.Ordinal);
        string session = AndroidSource("Platforms", "Android", "Game", "EmulationSession.cs");
        Assert.Contains("Msaa = _settings.MsaaValue", session, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("de01.psp.update.playstation.org", true)] // the PSP system update image (Adrenaline's installer)
    [InlineData("us.psp.update.playstation.org", true)]
    [InlineData("psp.update.playstation.org", true)]
    [InlineData("auth.np.ac.playstation.net", false)] // PSN stays blocked
    [InlineData("update.playstation.org", false)]
    [InlineData("example.org", true)]
    public void RealInternetReachesTheSameHostsOnAndroidAsOnTheDesktop(string host, bool reachable)
    {
        NetConfig desktop = new(NetMode.Real);
        NetConfig android = new NetworkSettings { RealInternet = true }.ToOptions("").Net;
        Assert.Equal(reachable, desktop.Permits(host, out _));
        Assert.Equal(reachable, android.Permits(host, out _));
        Assert.False(new NetworkSettings().ToOptions("").Net.Permits(host, out _)); // off: nothing leaves the phone
    }

    [Fact]
    public void TheEnvAllowListLimitsRealInternetAsTheDesktopNetAllowDoes()
    {
        const string Allow = "localhost, example.org";
        NetConfig desktop = new(NetMode.Real, Allow.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        NetConfig android = new NetworkSettings { RealInternet = true }.ToOptions("", Allow).Net;
        Assert.Equal(desktop.Allow, android.Allow);
        foreach (string host in new[] { "example.org", "www.example.org", "localhost", "other.net", "de01.psp.update.playstation.org" })
        {
            Assert.Equal(desktop.Permits(host, out _), android.Permits(host, out _));
        }

        Assert.Null(new NetworkSettings { RealInternet = true }.ToOptions("", null).Net.Allow);
        string session = AndroidSource("Platforms", "Android", "Game", "EmulationSession.cs");
        Assert.Contains("EnvVars.NetAllow", session, StringComparison.Ordinal);
    }
}
