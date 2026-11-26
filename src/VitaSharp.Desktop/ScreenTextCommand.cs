using System.Globalization;
using VitaSharp.Core.Capture;

namespace VitaSharp.Desktop;

/// <summary>
/// The screen tools: <c>screen-text</c> (the text on a frame, with its position: Capture/ScreenText.cs) and
/// <c>screens</c> (the knowledge base of known screens, docs/games/screens.json: validate it, list it, or find which
/// known screen a frame shows and what to do there). See docs/notes/screens.md.
/// </summary>
static class ScreenTextCommand
{
    public static void PrintUsage()
    {
        Console.WriteLine("  screen-text <png|capture folder|frames.vsfr> [--lang=en,ja] [--frame=N]");
        Console.WriteLine("                           the text on the frame (OCR: Windows.Media.Ocr on Windows, tesseract on Linux/macOS)");
        Console.WriteLine("                           with its box and centre (0..1); a capture: its last frame (or frame N, from 0)");
        Console.WriteLine("  screens validate [file]  check docs/games/screens.json (or VITASHARP_SCREENS / the file given)");
        Console.WriteLine("  screens list [TITLEID]   the known screens and their actions (desktop --nav and Android UI script lines)");
        Console.WriteLine("  screens match <TITLEID> <png|capture folder> [--lang=..]  OCR the frame and name the known screen it shows");
    }

    static string? Opt(string[] args, string name) =>
        args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

    public static int ScreenTextMain(string[] args)
    {
        // the texts may be Japanese (menus, the knowledge base)
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string[] positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (positional.Length < 1)
        {
            PrintUsage();
            return 2;
        }

        int frame = int.TryParse(Opt(args, "--frame"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) ? f : -1;
        byte[] rgba;
        int width;
        int height;
        string what;
        try
        {
            (rgba, width, height, what) = ScreenText.LoadFrame(positional[0], frame);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"screen-text: {e.Message}");
            return 2;
        }

        ScreenTextResult result = ScreenText.Recognize(rgba, width, height, Opt(args, "--lang"));
        Console.WriteLine($"screen text of {what} ({width}x{height}, {result.Backend}):");
        foreach (string l in ScreenText.Format(result))
        {
            Console.WriteLine(l);
        }

        return result.Ok ? 0 : 3;
    }

    public static int ScreensMain(string[] args)
    {
        // the texts may be Japanese (menus, the knowledge base)
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string verb = args.Length > 0 ? args[0] : "";
        string[] rest = args.Skip(1).ToArray();
        string[] positional = rest.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        string? file = ScreenKnowledge.Find(verb == "validate" && positional.Length > 0 ? positional[0] : Opt(rest, "--file"));
        if (file == null || !File.Exists(file))
        {
            Console.Error.WriteLine($"screens: no knowledge base found (docs/games/screens.json above the working folder, or {ScreenKnowledge.PathVariable})");
            return 2;
        }

        ScreenKnowledge kb = ScreenKnowledge.Load(file);
        switch (verb)
        {
            case "validate":
            {
                List<string> errors = kb.Validate();
                foreach (string e in errors)
                {
                    Console.WriteLine("  error: " + e);
                }

                Console.WriteLine(errors.Count == 0
                    ? $"{file}: ok ({kb.Titles.Count} titles, {kb.Screens.Count} screens)"
                    : $"{file}: {errors.Count} errors");
                return errors.Count == 0 ? 0 : 1;
            }
            case "list":
            {
                string? title = positional.Length > 0 ? positional[0] : null;
                foreach (KnownTitle t in kb.Titles.Where(t => title == null || t.Title.Equals(title, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"{t.Title} {t.Name}: to the gameplay");
                    for (int i = 0; i < t.ToGameplay.Count; i++)
                    {
                        Console.WriteLine($"  {i + 1}. {t.ToGameplay[i]}");
                    }

                    foreach (KnownScreen s in kb.Screens.Where(s => s.Title == t.Title))
                    {
                        foreach (string l in ScreenKnowledge.Describe(s))
                        {
                            Console.WriteLine("  " + l);
                        }
                    }
                }

                return 0;
            }
            case "match" when positional.Length >= 2:
            {
                (byte[] rgba, int width, int height, string what) = ScreenText.LoadFrame(positional[1]);
                ScreenTextResult result = ScreenText.Recognize(rgba, width, height, Opt(rest, "--lang"));
                Console.WriteLine($"screen text of {what} ({result.Backend}):");
                foreach (string l in ScreenText.Format(result))
                {
                    Console.WriteLine(l);
                }

                KnownScreen? known = kb.Match(positional[0], result.Lines.Select(l => l.Text));
                if (known == null)
                {
                    Console.WriteLine($"no known screen of {positional[0]} matches: add it to {file} once its answer is known (docs/notes/screens.md)");
                    return 1;
                }

                foreach (string l in ScreenKnowledge.Describe(known))
                {
                    Console.WriteLine(l);
                }

                return 0;
            }
            default:
                PrintUsage();
                return 2;
        }
    }
}
