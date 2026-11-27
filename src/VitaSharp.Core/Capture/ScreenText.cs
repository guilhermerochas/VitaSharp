using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VitaSharp.Core.Capture;

/// <summary>One line of text found on a frame: its text and box in the frame's pixels, and the box centre as a fraction
/// of the frame (0..1, the coordinates the touch scripts use).</summary>
public sealed record ScreenTextLine(string Text, int X, int Y, int Width, int Height)
{
    /// <summary>The frame size the box is in (set by the recognizer).</summary>
    public int FrameWidth { get; init; } = 1;
    public int FrameHeight { get; init; } = 1;
    public double CenterX => (X + Width / 2.0) / Math.Max(1, FrameWidth);
    public double CenterY => (Y + Height / 2.0) / Math.Max(1, FrameHeight);
}

/// <summary>What the OCR found, which backend ran, or why none could.</summary>
public sealed record ScreenTextResult(string Backend, IReadOnlyList<ScreenTextLine> Lines, string? Unavailable)
{
    public bool Ok => Unavailable == null;

    /// <summary>The system has no OCR to run (no Windows.Media.Ocr or no OCR language, no tesseract): not an error of
    /// the reader.</summary>
    public bool Missing { get; init; }
}

/// <summary>
/// Reads the text on a presented frame for navigation scripts, developers and the F8 report ("SECURITY SCAN", "TAP to open Drake's Journal",
/// "Press X"), so a screen is named by what it says instead of guessed from the picture. No new dependency: on Windows
/// the system OCR (Windows.Media.Ocr, through Windows PowerShell 5.1's WinRT projection), on Linux/macOS the
/// <c>tesseract</c> command when it is installed (<c>apt install tesseract-ocr</c>, <c>brew install tesseract</c>);
/// without either the result says so (<see cref="ScreenTextResult.Unavailable"/>). Small frames are scaled up first
/// (<see cref="OcrWidth"/>: the OCR misses the Vita's small UI text at 960x544); the boxes are given back in the
/// frame's pixels.
/// </summary>
public static class ScreenText
{
    /// <summary>Frames are scaled up (nearest, at most 4x) to about this width before the OCR: 960 wide 2x, a
    /// capture's 480 wide frames 4x.</summary>
    public const int OcrWidth = 1920;

    /// <summary>Recognizes the text of an RGBA8 frame. <paramref name="languages"/>: BCP-47 tags ("en", "ja"; several
    /// comma-separated are all run and merged), null = the user's profile languages (Windows) or "eng" (tesseract).</summary>
    public static ScreenTextResult Recognize(byte[] rgba, int width, int height, string? languages = null, int timeoutMs = 60000)
    {
        int scale = Math.Clamp(OcrWidth / Math.Max(1, width), 1, 4);
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string image = Path.Combine(dir, "frame.bmp");
            WriteBmp(image, rgba, width, height, scale);
            ScreenTextResult raw = OperatingSystem.IsWindows()
                ? RunWindows(dir, image, languages, timeoutMs)
                : RunTesseract(image, languages, timeoutMs);
            var lines = new List<ScreenTextLine>(raw.Lines.Count);
            foreach (ScreenTextLine l in raw.Lines)
            {
                lines.Add(new ScreenTextLine(l.Text, l.X / scale, l.Y / scale, Math.Max(1, l.Width / scale), Math.Max(1, l.Height / scale))
                {
                    FrameWidth = width,
                    FrameHeight = height,
                });
            }

            return raw with { Lines = lines };
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The frame a path names: a PNG, or a "Gravar tudo" capture folder or its frames.vsfr (the last frame, or
    /// frame <paramref name="frameIndex"/> counted from 0; negative counts from the end). The screen text belongs to
    /// the F8 capture's report: no other folder (a diagnostic's screenshot.png) is read for it.</summary>
    public static (byte[] Rgba, int Width, int Height, string What) LoadFrame(string path, int frameIndex = -1)
    {
        if (Directory.Exists(path))
        {
            path = Path.Combine(path, "frames.vsfr");
        }

        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            (byte[] rgba, int w, int h) = Util.PngReader.Read(path);
            return (rgba, w, h, Path.GetFileName(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("no PNG, capture folder or frames.vsfr", path);
        }

        var pictures = new List<(byte[] Rgba, int W, int H, long Present)>();
        int total = 0;
        foreach ((FramesFile.Frame frame, (byte[] Rgba, int W, int H)? picture) in FramesFile.ReadAll(path))
        {
            if (picture is not { } pic)
            {
                continue;
            }

            total++;
            if (frameIndex >= 0 && total - 1 != frameIndex)
            {
                continue;
            }

            pictures.Add((pic.Rgba, pic.W, pic.H, frame.Info.Present));
            if (frameIndex < 0 && pictures.Count > -frameIndex)
            {
                pictures.RemoveAt(0);
            }
        }

        if (pictures.Count == 0)
        {
            throw new InvalidDataException($"{path}: no frame {(frameIndex >= 0 ? frameIndex.ToString(CultureInfo.InvariantCulture) : "")} with a picture");
        }

        (byte[] Rgba, int W, int H, long Present) chosen = pictures[0];
        return (chosen.Rgba, chosen.W, chosen.H, $"{Path.GetFileName(path)} present #{chosen.Present}");
    }

    /// <summary>The printed form: one line per text, <c>"TEXT"  at x,y WxH  (centre 0.51, 0.46)</c>.</summary>
    public static List<string> Format(ScreenTextResult result, string indent = "  ")
    {
        var lines = new List<string>();
        if (!result.Ok)
        {
            lines.Add($"{indent}(no OCR: {result.Unavailable})");
            return lines;
        }

        if (result.Lines.Count == 0)
        {
            lines.Add($"{indent}(no text found; {result.Backend})");
            return lines;
        }

        foreach (ScreenTextLine l in result.Lines)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}\"{1}\"  at {2},{3} {4}x{5}  (centre {6:F2}, {7:F2})",
                indent, l.Text, l.X, l.Y, l.Width, l.Height, l.CenterX, l.CenterY));
        }

        return lines;
    }

    /// <summary>Why no OCR can run here, or null when one can.</summary>
    public static string? UnavailableReason()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsPowerShell() == null ? "Windows PowerShell 5.1 (powershell.exe) not found: it runs the system OCR (Windows.Media.Ocr)" : null;
        }

        return FindOnPath("tesseract") == null
            ? "the tesseract command is not installed (Linux: apt install tesseract-ocr [tesseract-ocr-jpn]; macOS: brew install tesseract tesseract-lang)"
            : null;
    }

    // ---- Windows: Windows.Media.Ocr through Windows PowerShell 5.1 (WinRT types load there without a new package)

    private const string WindowsScript = """
        param([string]$Path, [string]$Languages)
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        try {
            Add-Type -AssemblyName System.Runtime.WindowsRuntime
            $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
            $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
            $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
            $null = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]
        } catch {
            [Console]::Error.WriteLine('Windows.Media.Ocr is not available on this Windows: ' + $_.Exception.Message)
            exit 4
        }
        $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        } | Select-Object -First 1
        function Wait-Operation($operation, [Type]$type) {
            $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
            $null = $task.Wait(-1)
            return $task.Result
        }
        $file = Wait-Operation ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
        $stream = Wait-Operation ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $decoder = Wait-Operation ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Wait-Operation ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $engines = @()
        $available = @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages)
        if ($Languages) {
            foreach ($tag in $Languages.Split(',')) {
                $tag = $tag.Trim()
                $language = $available | Where-Object { $_.LanguageTag -eq $tag -or $_.LanguageTag.StartsWith($tag + '-') } | Select-Object -First 1
                if ($null -eq $language) {
                    [Console]::Error.WriteLine("OCR language not installed: $tag (installed: " + (($available | ForEach-Object { $_.LanguageTag }) -join ', ') + ')')
                } else {
                    $engines += [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language)
                }
            }
        }
        if ($engines.Count -eq 0) {
            $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
            if ($null -ne $engine) { $engines += $engine }
        }
        if ($engines.Count -eq 0) { [Console]::Error.WriteLine('no OCR language installed (Settings > Time & language > Language)'); exit 3 }
        foreach ($engine in $engines) {
            $result = Wait-Operation ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
            foreach ($line in $result.Lines) {
                $x0 = [double]::MaxValue; $y0 = [double]::MaxValue; $x1 = 0.0; $y1 = 0.0
                foreach ($word in $line.Words) {
                    $r = $word.BoundingRect
                    $x0 = [Math]::Min($x0, $r.X); $y0 = [Math]::Min($y0, $r.Y)
                    $x1 = [Math]::Max($x1, $r.X + $r.Width); $y1 = [Math]::Max($y1, $r.Y + $r.Height)
                }
                $text = $line.Text -replace "`t", ' '
                [Console]::Out.WriteLine(('L' + "`t" + [int]$x0 + "`t" + [int]$y0 + "`t" + [int]($x1 - $x0) + "`t" + [int]($y1 - $y0) + "`t" + $text))
            }
        }
        """;

    private static ScreenTextResult RunWindows(string dir, string image, string? languages, int timeoutMs)
    {
        string? powershell = WindowsPowerShell();
        if (powershell == null)
        {
            return new ScreenTextResult("windows-ocr", [], UnavailableReason()) { Missing = true };
        }

        string script = Path.Combine(dir, "ocr.ps1");
        // a BOM: Windows PowerShell 5.1 reads a script without one in the ANSI code page
        File.WriteAllText(script, WindowsScript, new UTF8Encoding(true));
        var arguments = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Path", image };
        if (!string.IsNullOrWhiteSpace(languages))
        {
            arguments.Add("-Languages");
            arguments.Add(languages);
        }

        (int exit, string output, string error) = RunProcess(powershell, arguments, timeoutMs);
        if (exit != 0)
        {
            // 3: no OCR language installed, 4: no Windows.Media.Ocr (a Windows without it, as Server Core)
            return new ScreenTextResult("windows-ocr", [], $"Windows.Media.Ocr failed (exit {exit}): {FirstLine(error)}") { Missing = exit is 3 or 4 };
        }

        string note = error.Trim().Length > 0 ? "; " + FirstLine(error) : "";
        return new ScreenTextResult($"windows-ocr (Windows.Media.Ocr{note})", ParseWindowsOutput(output), null);
    }

    /// <summary>The script's lines "L\tx\ty\tw\th\ttext" (duplicates of the same text at the same place, from two
    /// languages, are kept once).</summary>
    internal static List<ScreenTextLine> ParseWindowsOutput(string output)
    {
        var lines = new List<ScreenTextLine>();
        foreach (string raw in output.Split('\n'))
        {
            string[] f = raw.TrimEnd('\r').Split('\t', 6);
            if (f.Length < 6 || f[0] != "L" || f[5].Trim().Length == 0)
            {
                continue;
            }

            if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
                !int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ||
                !int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
            {
                continue;
            }

            AddUnique(lines, new ScreenTextLine(f[5].Trim(), x, y, w, h));
        }

        return lines;
    }

    // ---- Linux/macOS: tesseract's TSV (level 5 = words, grouped by block/paragraph/line)

    private static ScreenTextResult RunTesseract(string image, string? languages, int timeoutMs)
    {
        string? tesseract = FindOnPath("tesseract");
        if (tesseract == null)
        {
            return new ScreenTextResult("tesseract", [], UnavailableReason()) { Missing = true };
        }

        string lang = string.IsNullOrWhiteSpace(languages) ? "eng" : string.Join('+', languages.Split(',').Select(TesseractLanguage));
        (int exit, string output, string error) = RunProcess(tesseract, [image, "stdout", "-l", lang, "--psm", "11", "tsv"], timeoutMs);
        if (exit != 0)
        {
            return new ScreenTextResult("tesseract", [], $"tesseract failed (exit {exit}): {FirstLine(error)}");
        }

        return new ScreenTextResult("tesseract", ParseTesseractTsv(output), null);
    }

    private static string TesseractLanguage(string tag) => tag.Trim().ToLowerInvariant() switch
    {
        "en" => "eng",
        "ja" => "jpn",
        "pt" => "por",
        "fr" => "fra",
        "de" => "deu",
        "es" => "spa",
        "it" => "ita",
        string other => other,
    };

    /// <summary>tesseract's TSV: the words (level 5, confidence above 30) joined into their lines with the lines'
    /// union box.</summary>
    internal static List<ScreenTextLine> ParseTesseractTsv(string tsv)
    {
        var groups = new Dictionary<(int, int, int, int), (StringBuilder Text, int X0, int Y0, int X1, int Y1)>();
        var order = new List<(int, int, int, int)>();
        foreach (string raw in tsv.Split('\n'))
        {
            string[] f = raw.TrimEnd('\r').Split('\t');
            if (f.Length < 12 || f[0] != "5")
            {
                continue;
            }

            string word = f[11].Trim();
            if (word.Length == 0 || !double.TryParse(f[10], NumberStyles.Float, CultureInfo.InvariantCulture, out double conf) || conf < 30)
            {
                continue;
            }

            int[] n = new int[10];
            bool ok = true;
            for (int i = 0; i < 10; i++)
            {
                ok &= int.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i]);
            }

            if (!ok)
            {
                continue;
            }

            (int, int, int, int) key = (n[1], n[2], n[3], n[4]);
            int x0 = n[6], y0 = n[7], x1 = n[6] + n[8], y1 = n[7] + n[9];
            if (groups.TryGetValue(key, out (StringBuilder Text, int X0, int Y0, int X1, int Y1) g))
            {
                g.Text.Append(' ').Append(word);
                groups[key] = (g.Text, Math.Min(g.X0, x0), Math.Min(g.Y0, y0), Math.Max(g.X1, x1), Math.Max(g.Y1, y1));
            }
            else
            {
                groups[key] = (new StringBuilder(word), x0, y0, x1, y1);
                order.Add(key);
            }
        }

        var lines = new List<ScreenTextLine>(order.Count);
        foreach ((int, int, int, int) key in order)
        {
            (StringBuilder text, int x0, int y0, int x1, int y1) = groups[key];
            AddUnique(lines, new ScreenTextLine(text.ToString(), x0, y0, x1 - x0, y1 - y0));
        }

        return lines;
    }

    private static void AddUnique(List<ScreenTextLine> lines, ScreenTextLine line)
    {
        foreach (ScreenTextLine l in lines)
        {
            if (l.Text == line.Text && Math.Abs(l.X - line.X) < 8 && Math.Abs(l.Y - line.Y) < 8)
            {
                return;
            }
        }

        lines.Add(line);
    }

    // ---- helpers

    private static string? WindowsPowerShell()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string path = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : null;
    }

    private static string? FindOnPath(string command)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, command);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string FirstLine(string text)
    {
        string trimmed = text.Trim();
        int nl = trimmed.IndexOf('\n');
        return nl < 0 ? trimmed : trimmed[..nl].TrimEnd('\r');
    }

    private static (int Exit, string Output, string Error) RunProcess(string file, IReadOnlyList<string> arguments, int timeoutMs)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in arguments)
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"{file} did not start");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return (-1, "", $"timed out after {timeoutMs} ms");
        }

        process.WaitForExit();
        return (process.ExitCode, output.Result, error.Result);
    }

    /// <summary>A 32-bit BMP of the frame, scaled up <paramref name="scale"/>x (nearest), top-down rows.</summary>
    internal static void WriteBmp(string path, byte[] rgba, int width, int height, int scale)
    {
        int w = width * scale, h = height * scale;
        int pixelBytes = w * h * 4;
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(14 + 40 + pixelBytes);
        writer.Write(0);
        writer.Write(14 + 40);
        writer.Write(40);
        writer.Write(w);
        writer.Write(-h); // top-down
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0); // BI_RGB
        writer.Write(pixelBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        byte[] row = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            int source = (y / scale) * width * 4;
            for (int x = 0; x < w; x++)
            {
                int p = source + (x / scale) * 4;
                row[x * 4] = rgba[p + 2];
                row[x * 4 + 1] = rgba[p + 1];
                row[x * 4 + 2] = rgba[p];
                row[x * 4 + 3] = 255;
            }

            writer.Write(row);
        }
    }
}
