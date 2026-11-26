using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VitaSharp.Core.Capture;

/// <summary>What a known screen waits for and how to answer it (docs/games/screens.json).</summary>
public sealed class ScreenAction
{
    /// <summary>tap, hold, swipe, press, rear-tap, wait (the screen moves on by itself), or none (still unknown).</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    /// <summary>Front/rear panel position, 0..1 of the screen (swipe: the start).</summary>
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    /// <summary>Swipe: the end.</summary>
    [JsonPropertyName("x2")] public double? X2 { get; set; }
    [JsonPropertyName("y2")] public double? Y2 { get; set; }
    /// <summary>How long the finger or the button stays (s); wait: how long the screen takes by itself.</summary>
    [JsonPropertyName("seconds")] public double? Seconds { get; set; }
    /// <summary>press: the button (cross, circle, square, triangle, start, select, up, down, left, right, ltrigger,
    /// rtrigger).</summary>
    [JsonPropertyName("button")] public string? Button { get; set; }

    /// <summary>The desktop navigation script lines (VitaSharp.Desktop --nav, 60 presented frames per second).</summary>
    public List<string> ToNav()
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        long frames = (long)Math.Round((Seconds ?? 0.15) * 60);
        return Kind switch
        {
            "tap" => [string.Format(inv, "tap {0} {1} {2}", X, Y, Math.Max(8, frames))],
            "hold" => [string.Format(inv, "tap {0} {1} {2}", X, Y, Math.Max(8, frames))],
            "rear-tap" => [string.Format(inv, "btap {0} {1} {2}", X, Y, Math.Max(8, frames))],
            "swipe" => [string.Format(inv, "swipe {0} {1} {2} {3} {4}", X, Y, X2, Y2, Math.Max(8, frames))],
            "press" => [string.Format(inv, "press {0} {1}", Button, Math.Max(8, frames))],
            "wait" => [string.Format(inv, "wait {0}", Math.Max(1, frames))],
            _ => [],
        };
    }

    /// <summary>The Android developer build's UI script lines (VITASHARP_ANDROID_UI_INPUT, Developer/UiScript.cs).</summary>
    public List<string> ToAndroid()
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        int ms = (int)Math.Round((Seconds ?? 0.08) * 1000);
        return Kind switch
        {
            "tap" => [string.Format(inv, "touch:{0}:{1}", X, Y)],
            "hold" => [string.Format(inv, "hold:{0}:{1}:{2}", X, Y, ms)],
            "swipe" => [string.Format(inv, "drag:{0}:{1}:{2}:{3}:{4}", X, Y, X2, Y2, ms)],
            "press" => [$"key:{AndroidKey(Button)}"],
            _ => [],
        };
    }

    private static string AndroidKey(string? button) => (button ?? "").ToLowerInvariant() switch
    {
        "cross" => "button_a",
        "circle" => "button_b",
        "square" => "button_x",
        "triangle" => "button_y",
        "start" => "button_start",
        "select" => "button_select",
        "ltrigger" => "button_l1",
        "rtrigger" => "button_r1",
        "up" => "dpad_up",
        "down" => "dpad_down",
        "left" => "dpad_left",
        "right" => "dpad_right",
        string other => other,
    };
}

/// <summary>One known screen of one title.</summary>
public sealed class KnownScreen
{
    /// <summary>The title ID (PCSC00045).</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>The screen's name ("SECURITY SCAN").</summary>
    [JsonPropertyName("screen")] public string Screen { get; set; } = "";
    /// <summary>Texts the OCR reads on it (any one matches, case-insensitive, as a part of a line).</summary>
    [JsonPropertyName("match")] public List<string> Match { get; set; } = [];
    /// <summary>What comes before it (how the player gets there).</summary>
    [JsonPropertyName("after")] public string? After { get; set; }
    /// <summary>What it waits for, in words.</summary>
    [JsonPropertyName("expects")] public string Expects { get; set; } = "";
    /// <summary>The answer, or null while it is unknown.</summary>
    [JsonPropertyName("action")] public ScreenAction? Action { get; set; }
    /// <summary>What the answer leads to.</summary>
    [JsonPropertyName("result")] public string? Result { get; set; }
    /// <summary>confirmed (seen working), investigating (the answer is not known yet).</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("notes")] public string? Notes { get; set; }
}

/// <summary>One title: its name and the way from the boot to the gameplay.</summary>
public sealed class KnownTitle
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>The steps from the boot to the gameplay, in order (screens by their name).</summary>
    [JsonPropertyName("toGameplay")] public List<string> ToGameplay { get; set; } = [];
}

/// <summary>
/// The versioned knowledge base of game screens for the screen tools (docs/games/screens.json, described in
/// docs/notes/screens.md): per title, each known screen (the text on it, what it waits for, the answer, how to get
/// there) and the way to the gameplay. Tooling data only: navigation scripts and developers read it to answer a screen
/// that waits for the player (<c>[input-wait] WAITING FOR INPUT</c>); the emulator never reads it to change its
/// behaviour.
/// </summary>
public sealed class ScreenKnowledge
{
    /// <summary>The environment variable naming the file (default: docs/games/screens.json above the working folder or
    /// the program).</summary>
    public const string PathVariable = Util.EnvVars.Screens;

    public static readonly string[] Kinds = ["tap", "hold", "rear-tap", "swipe", "press", "wait", "none"];
    public static readonly string[] Statuses = ["confirmed", "investigating"];
    public static readonly string[] Buttons = ["cross", "circle", "square", "triangle", "start", "select", "up", "down", "left", "right", "ltrigger", "rtrigger"];

    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("titles")] public List<KnownTitle> Titles { get; set; } = [];
    [JsonPropertyName("screens")] public List<KnownScreen> Screens { get; set; } = [];

    public static ScreenKnowledge Parse(string json) => JsonSerializer.Deserialize(json, ScreenKnowledgeJson.Default.ScreenKnowledge) ?? new ScreenKnowledge();

    public static ScreenKnowledge Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>The file to use: <paramref name="explicitPath"/>, else <see cref="PathVariable"/>, else
    /// docs/games/screens.json in the working folder or a folder above it, or above the program. Null when none.</summary>
    public static string? Find(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } fromEnv)
        {
            return fromEnv;
        }

        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (DirectoryInfo? dir = new(start); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "docs", "games", "screens.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Every problem of the file (empty = valid): fields missing, an action without what it needs, a position
    /// outside 0..1, an unknown kind/status/button, a duplicate screen, a title without its entry.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Version != 1)
        {
            errors.Add($"version {Version}: expected 1");
        }

        var titles = new HashSet<string>(StringComparer.Ordinal);
        foreach (KnownTitle t in Titles)
        {
            if (!IsTitleId(t.Title))
            {
                errors.Add($"title '{t.Title}': not a title ID (4 letters, 5 digits)");
            }

            if (!titles.Add(t.Title))
            {
                errors.Add($"title {t.Title}: listed twice");
            }

            if (t.Name.Length == 0)
            {
                errors.Add($"title {t.Title}: no name");
            }
        }

        var seen = new HashSet<(string, string)>();
        foreach (KnownScreen s in Screens)
        {
            string at = $"{s.Title} '{s.Screen}'";
            if (!titles.Contains(s.Title))
            {
                errors.Add($"{at}: title not in \"titles\"");
            }

            if (s.Screen.Length == 0)
            {
                errors.Add($"{at}: no screen name");
            }

            if (!seen.Add((s.Title, s.Screen)))
            {
                errors.Add($"{at}: listed twice");
            }

            if (s.Match.Count == 0 || s.Match.Any(m => m.Trim().Length == 0))
            {
                errors.Add($"{at}: no text to match (\"match\")");
            }

            if (s.Expects.Length == 0)
            {
                errors.Add($"{at}: no \"expects\"");
            }

            if (!Statuses.Contains(s.Status))
            {
                errors.Add($"{at}: status '{s.Status}' (expected {string.Join(", ", Statuses)})");
            }

            if (s.Status == "confirmed" && (s.Action == null || s.Action.Kind == "none"))
            {
                errors.Add($"{at}: confirmed without an action");
            }

            if (s.Action is { } a)
            {
                ValidateAction(at, a, errors);
            }
        }

        return errors;
    }

    private static void ValidateAction(string at, ScreenAction a, List<string> errors)
    {
        if (!Kinds.Contains(a.Kind))
        {
            errors.Add($"{at}: action kind '{a.Kind}' (expected {string.Join(", ", Kinds)})");
            return;
        }

        bool position = a.Kind is "tap" or "hold" or "rear-tap" or "swipe";
        if (position && (!InUnit(a.X) || !InUnit(a.Y)))
        {
            errors.Add($"{at}: {a.Kind} needs x and y in 0..1");
        }

        if (a.Kind == "swipe" && (!InUnit(a.X2) || !InUnit(a.Y2)))
        {
            errors.Add($"{at}: swipe needs x2 and y2 in 0..1");
        }

        if (a.Kind is "hold" or "swipe" or "wait" && a.Seconds is not > 0)
        {
            errors.Add($"{at}: {a.Kind} needs seconds > 0");
        }

        if (a.Kind == "press" && !Buttons.Contains(a.Button ?? ""))
        {
            errors.Add($"{at}: press needs a button ({string.Join(", ", Buttons)})");
        }
    }

    private static bool InUnit(double? v) => v is >= 0 and <= 1;

    private static bool IsTitleId(string id) => id.Length == 9 && id[..4].All(char.IsAsciiLetterUpper) && id[4..].All(char.IsAsciiDigit);

    /// <summary>The known screen of <paramref name="titleId"/> whose match text is in one of <paramref name="texts"/>
    /// (the OCR's lines), or null.</summary>
    public KnownScreen? Match(string titleId, IEnumerable<string> texts)
    {
        List<string> lines = [.. texts];
        foreach (KnownScreen s in Screens)
        {
            if (!string.Equals(s.Title, titleId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string m in s.Match)
            {
                foreach (string l in lines)
                {
                    if (l.Contains(m, StringComparison.OrdinalIgnoreCase))
                    {
                        return s;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The printed form of one screen (the <c>screens</c> command).</summary>
    public static List<string> Describe(KnownScreen s)
    {
        var lines = new List<string>
        {
            $"{s.Title} \"{s.Screen}\" ({s.Status})",
            $"  expects: {s.Expects}",
        };
        if (s.After != null)
        {
            lines.Add($"  after: {s.After}");
        }

        if (s.Action is { Kind: not "none" } a)
        {
            lines.Add($"  nav: {string.Join(" | ", a.ToNav())}");
            List<string> android = a.ToAndroid();
            if (android.Count > 0)
            {
                lines.Add($"  android: {string.Join(" | ", android)}");
            }
        }
        else
        {
            lines.Add("  action: unknown yet");
        }

        if (s.Result != null)
        {
            lines.Add($"  result: {s.Result}");
        }

        if (s.Notes != null)
        {
            lines.Add($"  notes: {s.Notes}");
        }

        return lines;
    }
}

/// <summary>The source-generated reader of the knowledge base (trimming and AOT safe).</summary>
[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ScreenKnowledge))]
internal sealed partial class ScreenKnowledgeJson : JsonSerializerContext;
