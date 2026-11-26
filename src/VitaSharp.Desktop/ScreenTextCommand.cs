using System.Globalization;
using VitaSharp.Core.Capture;

namespace VitaSharp.Desktop;

/// <summary>
/// The agents' screen tools: <c>screen-text</c> (the text on a frame, with its position: Capture/ScreenText.cs).
/// See docs/agents/screens.md.
/// </summary>
static class ScreenTextCommand
{
    public static void PrintUsage()
    {
        Console.WriteLine("  screen-text <png|capture folder|frames.vsfr> [--lang=en,ja] [--frame=N]");
        Console.WriteLine("                           the text on the frame (OCR: Windows.Media.Ocr on Windows, tesseract on Linux/macOS)");
        Console.WriteLine("                           with its box and centre (0..1); a capture: its last frame (or frame N, from 0)");
    }

    static string? Opt(string[] args, string name) =>
        args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

    public static int ScreenTextMain(string[] args)
    {
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
}
