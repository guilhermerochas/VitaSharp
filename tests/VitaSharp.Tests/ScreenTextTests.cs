using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VitaSharp.Core.Capture;
using static StbTrueTypeSharp.StbTrueType;

namespace VitaSharp.Tests;

/// <summary>
/// The screen text reader: a frame with known text ("SECURITY SCAN", "Press X to start", drawn with a font of the
/// repository) is read back with the boxes where the text is. The OCR is the system's (Windows.Media.Ocr) or
/// tesseract: where neither exists the recognition test says so and returns (the parsers and the BMP are checked
/// everywhere).
/// </summary>
public sealed class ScreenTextTests
{
    private const int W = 960, H = 544;

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

    /// <summary>White text of <paramref name="pixels"/> px height at (x, y = the top) on a dark RGBA frame.</summary>
    private static unsafe void DrawText(byte[] rgba, string text, int x, int y, float pixels)
    {
        byte[] font = File.ReadAllBytes(Path.Combine(RepoRoot(), "src", "VitaSharp.Android", "Resources", "Fonts", "Inter-SemiBold.ttf"));
        byte* data = (byte*)NativeMemory.Alloc((nuint)font.Length);
        try
        {
            font.CopyTo(new Span<byte>(data, font.Length));
            var info = new stbtt_fontinfo();
            Assert.NotEqual(0, stbtt_InitFont(info, data, 0));
            float scale = stbtt_ScaleForPixelHeight(info, pixels);
            int ascent;
            int descent;
            int lineGap;
            stbtt_GetFontVMetrics(info, &ascent, &descent, &lineGap);
            int baseline = y + (int)(ascent * scale);
            float pen = x;
            foreach (char c in text)
            {
                int advance;
                int bearing;
                stbtt_GetCodepointHMetrics(info, c, &advance, &bearing);
                int gw;
                int gh;
                int xoff;
                int yoff;
                byte* glyph = stbtt_GetCodepointBitmap(info, scale, scale, c, &gw, &gh, &xoff, &yoff);
                for (int gy = 0; gy < gh; gy++)
                {
                    for (int gx = 0; gx < gw; gx++)
                    {
                        int px = (int)pen + xoff + gx;
                        int py = baseline + yoff + gy;
                        if (px < 0 || py < 0 || px >= W || py >= H)
                        {
                            continue;
                        }

                        byte a = glyph[gy * gw + gx];
                        int p = (py * W + px) * 4;
                        rgba[p] = Math.Max(rgba[p], a);
                        rgba[p + 1] = Math.Max(rgba[p + 1], a);
                        rgba[p + 2] = Math.Max(rgba[p + 2], a);
                    }
                }

                if (glyph != null)
                {
                    stbtt_FreeBitmap(glyph, null);
                }

                pen += advance * scale;
            }
        }
        finally
        {
            NativeMemory.Free(data);
        }
    }

    private static byte[] Frame()
    {
        byte[] rgba = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            rgba[i * 4] = 20;
            rgba[i * 4 + 1] = 24;
            rgba[i * 4 + 2] = 32;
            rgba[i * 4 + 3] = 255;
        }

        return rgba;
    }

    [Fact]
    public void TheTextOfAFrameIsReadWithItsPosition()
    {
        byte[] rgba = Frame();
        DrawText(rgba, "SECURITY SCAN", 360, 100, 32);
        DrawText(rgba, "Press X to start", 380, 420, 24);
        if (ScreenText.UnavailableReason() is { } why)
        {
            // no OCR on this machine (Linux/macOS without tesseract): the skip says what to install
            Console.WriteLine($"ScreenTextTests: recognition skipped: {why}");
            return;
        }

        // "en" when installed, else the user's languages (a Latin-script language reads this text too)
        ScreenTextResult result = ScreenText.Recognize(rgba, W, H, "en");
        if (result.Missing)
        {
            Console.WriteLine($"ScreenTextTests: recognition skipped: {result.Unavailable}");
            return;
        }

        Assert.True(result.Ok, result.Unavailable);
        ScreenTextLine scan = Assert.Single(result.Lines, l => l.Text.Contains("SECURITY SCAN", StringComparison.OrdinalIgnoreCase));
        ScreenTextLine press = Assert.Single(result.Lines, l => l.Text.Contains("Press X", StringComparison.OrdinalIgnoreCase));
        // the boxes in the frame's pixels: the title near (360, 100), the prompt near (380, 420)
        Assert.InRange(scan.X, 340, 380);
        Assert.InRange(scan.Y, 90, 120);
        Assert.InRange(scan.CenterY, 0.17, 0.25);
        Assert.InRange(press.Y, 410, 440);
        Assert.InRange(press.CenterX, 0.45, 0.6);
        List<string> printed = ScreenText.Format(result);
        Assert.Contains(printed, l => l.StartsWith("  \"SECURITY SCAN\"  at ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBackendsOutputIsParsedIntoLines()
    {
        List<ScreenTextLine> windows = ScreenText.ParseWindowsOutput("L\t10\t20\t100\t16\tSECURITY SCAN\r\nL\t10\t21\t100\t16\tSECURITY SCAN\r\nnoise\nL\t5\t300\t80\t12\tPress X\n");
        Assert.Equal(["SECURITY SCAN", "Press X"], windows.Select(l => l.Text));
        Assert.Equal((5, 300, 80, 12), (windows[1].X, windows[1].Y, windows[1].Width, windows[1].Height));
        string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
                     "5\t1\t1\t1\t1\t1\t100\t50\t60\t20\t95.1\tSECURITY\n" +
                     "5\t1\t1\t1\t1\t2\t170\t52\t40\t18\t93.0\tSCAN\n" +
                     "5\t1\t2\t1\t1\t1\t10\t400\t30\t10\t12.0\t~~\n" +
                     "5\t1\t3\t1\t1\t1\t20\t420\t50\t12\t88.0\tTAP\n";
        List<ScreenTextLine> tesseract = ScreenText.ParseTesseractTsv(tsv);
        Assert.Equal(["SECURITY SCAN", "TAP"], tesseract.Select(l => l.Text));
        Assert.Equal((100, 50, 110, 20), (tesseract[0].X, tesseract[0].Y, tesseract[0].Width, tesseract[0].Height));
        Assert.Equal(["  (no OCR: missing)"], ScreenText.Format(new ScreenTextResult("x", [], "missing")));
    }

    [Fact]
    public void TheFrameIsWrittenAsATopDownBmpScaledUp()
    {
        string path = Path.Combine(Path.GetTempPath(), "vitasharp-ocr-bmp-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255];
            ScreenText.WriteBmp(path, rgba, 2, 1, 2);
            byte[] bmp = File.ReadAllBytes(path);
            Assert.Equal(14 + 40 + 4 * 2 * 4, bmp.Length);
            Assert.Equal(4, BitConverter.ToInt32(bmp, 18));
            Assert.Equal(-2, BitConverter.ToInt32(bmp, 22));
            // BGRA: the red pixel twice, then the green one twice, on both rows
            Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 255, 0, 255, 0, 255, 0, 255 }, bmp[54..70]);
            Assert.Equal(bmp[54..70], bmp[70..86]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
