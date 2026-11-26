using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VitaSharp.Core.Capture;
using VitaSharp.Core.Util;
using VitaSharp.Desktop;
using VitaSharp.Desktop.Host;
using VitaSharp.Desktop.Launcher;
using VitaSharp.Mobile.Core.Settings;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The player's debugging tools are F8 "Gravar tudo" (Shift+F8 marks the problem), the screenshot and the log, and
/// nothing else: no key, launcher option, Android setting or pause menu item for a debug mode (F9), a diagnostic
/// capture (F10) or a GPU dump alone (Ctrl+F8), and the user docs show none of them nor the terminal commands. The F8
/// capture has what those gave: the detailed log is on while it records or keeps its pre-roll (and its log.txt is the
/// session log), and it records the GPU dump.
/// </summary>
[Collection("guest")]
public sealed class PlayerDebugToolsTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "f8only");

    public void Dispose() => _harness.Dispose();

    private static string Root([CallerFilePath] string source = "")
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

    /// <summary>The removed tools as they are written in texts and docs.</summary>
    private static readonly Regex s_removedKeys = new(@"\bF9\b|\bF10\b|Ctrl\+F8|Ctrl\s*\+\s*F8", RegexOptions.Compiled);

    /// <summary>Of every key of the game window, with or without Ctrl and Shift, only F8 is a debugging key: alone or
    /// with Ctrl it is "Gravar tudo", with Shift the mark of the problem. F9 and F10 do nothing of the kind.</summary>
    [Fact]
    public void OnlyF8IsADebuggingKeyOfTheGameWindow()
    {
        foreach (Silk.NET.Input.Key key in Enum.GetValues<Silk.NET.Input.Key>())
        {
            foreach (bool ctrl in new[] { false, true })
            {
                foreach (bool shift in new[] { false, true })
                {
                    DebugKey action = WindowHost.DebugKeyFor(key, ctrl, shift);
                    if (key != Silk.NET.Input.Key.F8)
                    {
                        Assert.Equal(DebugKey.None, action);
                    }
                    else if (shift && !ctrl)
                    {
                        Assert.Equal(DebugKey.MarkProblem, action);
                    }
                    else
                    {
                        Assert.Equal(DebugKey.RecordEverything, action);
                    }
                }
            }
        }

        string[] events = typeof(WindowHost).GetEvents(BindingFlags.Public | BindingFlags.Instance).Select(e => e.Name).ToArray();
        Assert.DoesNotContain("DebugModeToggled", events);
        Assert.DoesNotContain("DiagnosticRequested", events);
        Assert.Contains("SessionCaptureToggled", events);
        Assert.Contains("BugMarked", events);
    }

    /// <summary>No debug mode switch in the launcher's settings or the Android app's (an old settings file that has
    /// one still loads), and the launcher never starts a game with --debug-mode.</summary>
    [Fact]
    public void NoAppHasADebugModeSwitch()
    {
        Assert.Null(typeof(LauncherSettings).GetProperty("DebugMode"));
        Assert.Null(typeof(AppPreferences).GetProperty("DebugMode"));
        Assert.DoesNotContain("--debug-mode", new LauncherSettings { CapturePreRoll = true }.RunOptions());

        string folder = Path.Combine(_harness.Root, "prefs");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "app.json");
        File.WriteAllText(path, "{\"DebugMode\":true,\"ShowFps\":true}");
        Assert.True(AppPreferences.Load(path).ShowFps);
    }

    /// <summary>The texts of the launcher and the game window (both languages) name F8 and Shift+F8 and none of the
    /// removed tools; the keys of the removed switches and folders are gone.</summary>
    [Fact]
    public void TheTextsNameOnlyF8()
    {
        string folder = Path.Combine(Root(), "src", "VitaSharp.Core", "Localization");
        foreach (string file in new[] { "Texts.resx", "Texts.pt-BR.resx" })
        {
            Dictionary<string, string> texts = XDocument.Load(Path.Combine(folder, file)).Root!.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");
            foreach (string key in new[] { "ShortcutsText", "NoCaptures", "NoGameCaptures" })
            {
                Assert.DoesNotMatch(s_removedKeys, texts[key]);
                Assert.Contains("F8", texts[key], StringComparison.Ordinal);
                Assert.Contains("Shift+F8", texts[key], StringComparison.Ordinal);
            }

            foreach (string gone in new[] { "DebugMode", "DebugModeDetail", "MDebugMode", "MDebugModeDetail", "GameDebugOn", "GameDebugOff", "DiagnosticsChip", "GpuDumps", "DiagnosticsFolder", "GpuDumpsFolder" })
            {
                Assert.False(texts.ContainsKey(gone), $"{file}: {gone}");
            }
        }
    }

    /// <summary>The Android pause menu and its in-game settings (outside the developer build's page) offer only save
    /// states, settings, recentre and "Gravar tudo" (record, stop, mark the problem, pre-roll), and the app's settings
    /// have no debug mode row.</summary>
    [Fact]
    public void TheAndroidPauseMenuOffersOnlyGravarTudo()
    {
        string game = Path.Combine(Root(), "src", "VitaSharp.Android", "Platforms", "Android", "Game", "GameActivity.PauseMenu.cs");
        string source = Regex.Replace(File.ReadAllText(game), @"#if VITASHARP_DEVELOPER.*?#endif", "", RegexOptions.Singleline);
        string[] items = Regex.Matches(source, @"(?:Tile|ToggleRow|ActionRow|ChoiceRow)\(Loc\.Get\(""(\w+)""\)").Select(m => m.Groups[1].Value).Distinct().ToArray();
        string[] allowed =
        [
            "MSaveState", "MLoadState", "MSettings", "MRecenter", "MRecordAll", "MStopRecording", "MMarkBug",
            "MRearTouch", "MOnScreenControls", "MMsaa", "FrameRate", "MPreRoll", "MRecenterMotion",
        ];
        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.Contains(item, allowed));
        Assert.Contains("MRecordAll", items);
        Assert.Contains("MMarkBug", items);

        string settings = File.ReadAllText(Path.Combine(Root(), "src", "VitaSharp.Android", "Pages", "SettingsView.cs"));
        Assert.DoesNotContain("MDebugMode", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("DebugMode.Enable", settings, StringComparison.Ordinal);
    }

    /// <summary>The user docs (README, user guide; also the captures and Android guides, which have developer parts)
    /// show none of the removed keys, and the README and the user guide none of the developer's terminal commands
    /// (docs/captures.md, docs/notes and docs/development.md have those).</summary>
    [Fact]
    public void TheUserDocsShowOnlyF8()
    {
        string root = Root();
        string[] docs =
        [
            "README.md", "README.pt-BR.md", "docs/user-guide.md", "docs/user-guide.pt-BR.md", "docs/captures.md",
            "docs/captures.pt-BR.md", "docs/android/README.md",
        ];
        string[] userOnly = ["README.md", "README.pt-BR.md", "docs/user-guide.md", "docs/user-guide.pt-BR.md"];
        var commands = new Regex(@"`(?:VitaSharp\.Desktop(?:\.exe)? )?(?:replay-gpu|screen-text|rerun|verify|screens)\b", RegexOptions.Compiled);
        foreach (string doc in docs)
        {
            string text = File.ReadAllText(Path.Combine(root, doc));
            foreach (Match match in s_removedKeys.Matches(text))
            {
                int line = text[..match.Index].Count(c => c == '\n') + 1;
                Assert.Fail($"{doc}:{line}: '{match.Value}' (the player's only debugging tool is F8)");
            }

            foreach (Match match in userOnly.Contains(doc) ? commands.Matches(text) : Enumerable.Empty<Match>())
            {
                int line = text[..match.Index].Count(c => c == '\n') + 1;
                Assert.Fail($"{doc}:{line}: '{match.Value}' (terminal commands go in the developer docs)");
            }

            Assert.Contains("F8", text, StringComparison.Ordinal);
        }
    }

    /// <summary>"Gravar tudo" switches the detailed log on while it records and while it keeps its pre-roll, and back
    /// off when neither runs (unless it was on for the whole run); its log.txt is the session log.</summary>
    [Fact]
    public void TheCaptureHasTheDetailedLog()
    {
        bool explicitBefore = DebugMode.Explicit;
        DebugMode.Disable();
        string root = Path.Combine(_harness.Root, "captures");
        string log = Path.Combine(_harness.Root, "session.log");
        File.WriteAllText(log, "[dbg] the session log\n");
        var cap = new SessionCapture(_harness.Emu, new SessionCaptureOptions
        {
            Root = root,
            SetPresentTap = _ => { },
            StartState = false,
            GpuDump = false,
            Zip = false,
            Threads = () => "no threads",
            Info = () => "test info",
            LogFile = () => log,
        });
        try
        {
            Assert.False(DebugMode.Enabled);
            Assert.True(cap.Start("test"));
            HleHarness.Until(() => cap.IsRecording, "the recording to start");
            Assert.True(DebugMode.Enabled);
            Assert.True(cap.Stop("test end"));
            Assert.True(cap.Idle.Wait(120_000), "the recording was not saved");
            Assert.False(DebugMode.Enabled);
            Assert.Contains("[dbg] the session log", File.ReadAllText(Path.Combine(cap.Dir!, "log.txt")));

            // the pre-roll keeps the seconds before F8: the detailed log is on for them too
            cap.SetPreRoll(5);
            Assert.True(DebugMode.Enabled);
            cap.SetPreRoll(0);
            Assert.False(DebugMode.Enabled);

            // on for the whole run (--debug-mode): a capture does not turn it off
            DebugMode.Enable();
            cap.SetPreRoll(5);
            cap.SetPreRoll(0);
            Assert.True(DebugMode.Enabled);
        }
        finally
        {
            cap.Hurry();
            cap.Dispose();
            if (explicitBefore)
            {
                DebugMode.Enable();
            }
            else
            {
                DebugMode.Disable();
            }
        }
    }

    /// <summary>The desktop's F8 capture records the GPU dump (with its header) and copies the session log.</summary>
    [Fact]
    public void TheDesktopCaptureRecordsTheGpuDumpAndTheLog()
    {
        Assert.True(new SessionCaptureOptions { Root = "", SetPresentTap = null! }.GpuDump);
        var options = new SessionCaptureOptions { Root = Path.Combine(_harness.Root, "captures_desktop"), SetPresentTap = null!, StartState = false, Zip = false };
        using var control = new SessionCaptureControl(_harness.Emu, new VulkanRendererOptions(), () => null, [], null, null, options);
        Assert.True(control.Capture.Options.GpuDump);
        Assert.NotNull(control.Capture.Options.DumpHeader);
        Assert.NotNull(control.Capture.Options.LogFile);
    }
}
