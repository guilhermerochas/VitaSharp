using SkiaSharp;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Host;

/// <summary>
/// The "Pausado (P)" badge shown in the top-left corner of the game window while the emulation is paused
/// (<see cref="VitaSharp.Core.EmulationPause"/>), drawn on the CPU like the trophy card (<see cref="TrophyToast"/>) and
/// handed to <see cref="VitaSharp.Renderer.Vulkan.VulkanRenderer.SetStatusOverlay"/>.
/// </summary>
public static class PauseBadge
{
    /// <summary>"Pausado (P)", or "Pausado" when the pause has no key.</summary>
    public static string Text(string keyLabel) => keyLabel.Length > 0 ? $"Pausado ({keyLabel})" : "Pausado";

    /// <summary>The badge as BGRA bytes (opaque), scaled with the window (1x at 544 lines); null when Skia is missing.</summary>
    public static (byte[] Bgra, int Width, int Height)? Render(string text, int windowHeight)
    {
        try
        {
            float scale = Math.Clamp(windowHeight / 544f, 1f, 3f);
            using var font = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), 15 * scale);
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            float textW;
            using (var measure = new SKPaint { Typeface = font.Typeface, TextSize = font.Size }) textW = measure.MeasureText(text);
            float pad = 10 * scale, bar = 14 * scale, gap = 9 * scale;
            int w = (int)Math.Ceiling(pad + bar + gap + textW + pad), h = (int)Math.Ceiling(34 * scale);
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap(info);
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(new SKColor(0x1B, 0x1F, 0x27));
                // the pause sign: two bars
                using var accent = new SKPaint { Color = new SKColor(0xFF, 0xB5, 0x47), IsAntialias = true };
                float top = h * 0.28f, bottom = h * 0.72f, bw = bar * 0.36f;
                c.DrawRect(pad, top, bw, bottom - top, accent);
                c.DrawRect(pad + bar - bw, top, bw, bottom - top, accent);
                var metrics = font.Metrics;
                float baseline = h / 2f - (metrics.Ascent + metrics.Descent) / 2f;
                c.DrawText(text, pad + bar + gap, baseline, font, white);
            }
            return (bmp.Bytes, w, h);
        }
        catch (Exception e) // a missing native Skia must never take the game down
        {
            Log.WarnOnce("pause-badge", $"Pause badge could not be drawn: {e.Message}");
            return null;
        }
    }
}
