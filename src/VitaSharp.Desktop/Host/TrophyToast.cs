using SkiaSharp;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Host;

/// <summary>
/// The in-game trophy notification: a card with the trophy icon, "Troféu desbloqueado", the name and the grade, drawn on
/// the CPU (SkiaSharp, already shipped with Avalonia) and shown for a few seconds in the top-right corner of the emulator
/// window through <see cref="VitaSharp.Renderer.Vulkan.VulkanRenderer.SetOverlay"/>. Unlocks that arrive together are
/// shown one after the other.
/// </summary>
public sealed class TrophyToast : IDisposable
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);

    readonly Action<byte[]?, int, int> _show;
    readonly Func<int> _windowHeight;
    readonly Queue<TrophyUnlockedEvent> _queue = new();
    readonly Timer _timer;
    bool _showing;

    /// <param name="show">sets (or clears, null) the overlay image: BGRA bytes, width, height</param>
    /// <param name="windowHeight">framebuffer height, to scale the card</param>
    public TrophyToast(Action<byte[]?, int, int> show, Func<int> windowHeight)
    {
        _show = show;
        _windowHeight = windowHeight;
        _timer = new Timer(_ => Next(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Enqueue(TrophyUnlockedEvent e)
    {
        lock (_queue)
        {
            _queue.Enqueue(e);
            if (_showing) return;
        }
        Next();
    }

    void Next()
    {
        TrophyUnlockedEvent? e;
        lock (_queue)
        {
            _showing = _queue.TryDequeue(out e);
        }
        if (e == null) { _show(null, 0, 0); return; }
        try
        {
            float scale = Math.Clamp(_windowHeight() / 544f, 1f, 3f);
            var (bgra, w, h) = Render(e, scale);
            _show(bgra, w, h);
        }
        catch (Exception ex) // a missing native Skia must never take the game down
        {
            Log.WarnOnce("trophy-toast", $"Trophy notification could not be drawn: {ex.Message}");
        }
        _timer.Change(Duration, Timeout.InfiniteTimeSpan);
    }

    static SKColor GradeColor(TrophyGrade g) => g switch
    {
        TrophyGrade.Platinum => new SKColor(0xA9, 0xC4, 0xE8), TrophyGrade.Gold => new SKColor(0xE2, 0xB1, 0x3C),
        TrophyGrade.Silver => new SKColor(0xC3, 0xC7, 0xCE), _ => new SKColor(0xC0, 0x7A, 0x44),
    };

    public static string GradeName(TrophyGrade g) => g switch
    {
        TrophyGrade.Platinum => "Platina", TrophyGrade.Gold => "Ouro", TrophyGrade.Silver => "Prata", _ => "Bronze",
    };

    /// <summary>The card as BGRA bytes (opaque).</summary>
    public static (byte[] Bgra, int Width, int Height) Render(TrophyUnlockedEvent e, float scale)
    {
        int w = (int)(360 * scale), h = (int)(76 * scale);
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(0x1B, 0x1F, 0x27));
            var grade = GradeColor(e.Trophy.Grade);
            using (var bar = new SKPaint { Color = grade }) c.DrawRect(0, 0, 5 * scale, h, bar);
            float pad = 10 * scale, icon = h - 2 * pad, x = 5 * scale + pad;
            using (var iconBmp = e.Icon is { Length: > 0 } png ? SKBitmap.Decode(png) : null)
            {
                var dst = new SKRect(x, pad, x + icon, pad + icon);
                if (iconBmp != null)
                    using (var p = new SKPaint { IsAntialias = true }) c.DrawBitmap(iconBmp, dst, p);
                else
                    using (var p = new SKPaint { Color = grade.WithAlpha(90) }) c.DrawRect(dst, p);
            }
            float tx = x + icon + pad, right = w - pad;
            using var small = new SKFont(SKTypeface.Default, 12 * scale);
            using var big = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), 15 * scale);
            using var muted = new SKPaint { Color = new SKColor(0x9A, 0xA3, 0xB2), IsAntialias = true };
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var gradePaint = new SKPaint { Color = grade, IsAntialias = true };
            c.DrawText("Troféu desbloqueado", tx, pad + 12 * scale, small, muted);
            c.DrawText(Fit(e.Trophy.Name, big, right - tx), tx, pad + 32 * scale, big, white);
            c.DrawText(GradeName(e.Trophy.Grade), tx, pad + 50 * scale, small, gradePaint);
        }
        return (bmp.Bytes, w, h);
    }

    /// <summary>The text, shortened with an ellipsis to fit <paramref name="width"/>.</summary>
    static string Fit(string s, SKFont font, float width)
    {
        using var m = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        if (m.MeasureText(s) <= width) return s;
        while (s.Length > 1 && m.MeasureText(s + "…") > width) s = s[..^1];
        return s + "…";
    }

    public void Dispose() => _timer.Dispose();
}
