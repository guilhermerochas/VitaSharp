using System.Runtime.CompilerServices;
using VitaSharp.Core.Capture;

namespace VitaSharp.Tests;

/// <summary>
/// The screen knowledge base of the repository (docs/games/screens.json) is read and valid, its known screens are
/// found by the text the OCR reads and turn into the desktop navigation and Android UI script lines, and the
/// validation catches the mistakes an edit can make (an action without its position, a position outside 0..1, a
/// confirmed screen without an answer, a screen of a title that is not listed).
/// </summary>
public sealed class ScreenKnowledgeTests
{
    private static string RepoRoot([CallerFilePath] string source = "")
    {
        for (DirectoryInfo? directory = new(Path.GetDirectoryName(source)!); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VitaSharp.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("the repository");
    }

    private static ScreenKnowledge Repository() => ScreenKnowledge.Load(Path.Combine(RepoRoot(), "docs", "games", "screens.json"));

    [Fact]
    public void TheRepositoryBaseIsValid()
    {
        ScreenKnowledge kb = Repository();
        Assert.Empty(kb.Validate());
        Assert.NotEmpty(kb.Titles);
        Assert.All(kb.Titles, t => Assert.NotEmpty(t.ToGameplay));
    }

    [Fact]
    public void TheSecurityScanIsAThumbHeldOnTheRectangle()
    {
        ScreenKnowledge kb = Repository();
        KnownScreen? scan = kb.Match("PCSC00045", ["MISSION BRIEFING", "SECURITY SCAN"]);
        Assert.NotNull(scan);
        Assert.Equal("confirmed", scan.Status);
        Assert.Equal("hold", scan.Action!.Kind);
        Assert.Equal(["tap 0.51 0.46 720"], scan.Action.ToNav());
        Assert.Equal(["hold:0.51:0.46:12000"], scan.Action.ToAndroid());
        Assert.Null(kb.Match("PCSF00001", ["SECURITY SCAN"]));
        KnownScreen? journal = kb.Match("PCSF00001", ["TAP to open Drake's Journal"]);
        Assert.NotNull(journal);
        Assert.Equal("investigating", journal.Status);
        Assert.Contains(ScreenKnowledge.Describe(journal), l => l == "  action: unknown yet");
    }

    [Fact]
    public void TheValidationCatchesBrokenEntries()
    {
        const string Json = """
            {
              "version": 1,
              "titles": [ { "title": "PCSX00001", "name": "Synthetic", "toGameplay": ["a"] } ],
              "screens": [
                { "title": "PCSX00001", "screen": "A", "match": ["A"], "expects": "x", "status": "confirmed", "action": { "kind": "hold", "x": 0.5, "y": 1.5 } },
                { "title": "PCSX00001", "screen": "B", "match": [], "expects": "x", "status": "confirmed" },
                { "title": "PCSX00002", "screen": "C", "match": ["C"], "expects": "", "status": "maybe", "action": { "kind": "press", "button": "home" } },
                { "title": "PCSX00001", "screen": "A", "match": ["A"], "expects": "x", "status": "investigating", "action": { "kind": "jump" } }
              ]
            }
            """;
        List<string> errors = ScreenKnowledge.Parse(Json).Validate();
        Assert.Contains("PCSX00001 'A': hold needs x and y in 0..1", errors);
        Assert.Contains("PCSX00001 'A': hold needs seconds > 0", errors);
        Assert.Contains("PCSX00001 'B': no text to match (\"match\")", errors);
        Assert.Contains("PCSX00001 'B': confirmed without an action", errors);
        Assert.Contains("PCSX00002 'C': title not in \"titles\"", errors);
        Assert.Contains("PCSX00002 'C': no \"expects\"", errors);
        Assert.Contains(errors, e => e.StartsWith("PCSX00002 'C': status 'maybe'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("PCSX00002 'C': press needs a button", StringComparison.Ordinal));
        Assert.Contains("PCSX00001 'A': listed twice", errors);
        Assert.Contains(errors, e => e.StartsWith("PCSX00001 'A': action kind 'jump'", StringComparison.Ordinal));
    }

    [Fact]
    public void TheActionsBecomeScriptLines()
    {
        var swipe = new ScreenAction { Kind = "swipe", X = 0.1, Y = 0.5, X2 = 0.9, Y2 = 0.5, Seconds = 0.5 };
        Assert.Equal(["swipe 0.1 0.5 0.9 0.5 30"], swipe.ToNav());
        Assert.Equal(["drag:0.1:0.5:0.9:0.5:500"], swipe.ToAndroid());
        var press = new ScreenAction { Kind = "press", Button = "cross" };
        Assert.Equal(["press cross 9"], press.ToNav());
        Assert.Equal(["key:button_a"], press.ToAndroid());
        Assert.Equal(["btap 0.5 0.5 9"], new ScreenAction { Kind = "rear-tap", X = 0.5, Y = 0.5 }.ToNav());
        Assert.Equal(["wait 600"], new ScreenAction { Kind = "wait", Seconds = 10 }.ToNav());
        Assert.Empty(new ScreenAction { Kind = "none" }.ToNav());
    }
}
