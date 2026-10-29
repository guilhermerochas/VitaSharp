using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VitaSharp.Core.Shell;

namespace VitaSharp.Desktop.Launcher.Shell;

/// <summary>
/// Base of the home screen and LiveArea views: draws in the Vita's 960x544 screen space, scaled to fit the control
/// and centred (the wallpaper fills the whole control), with the status bar (title on the left, Wi-Fi, battery and
/// clock on the right) along the top.
/// </summary>
public abstract class ShellCanvas : Control
{
    public const double W = 960, H = 544, StatusBarHeight = 30;
    protected readonly ShellVisuals Visuals;
    readonly DispatcherTimer _clock;
    DispatcherTimer? _anim;
    protected static readonly Typeface Ui = new(FontFamily.Default);
    protected static readonly Typeface UiBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    /// <summary>Fixed time shown in the clock (snapshots); null = now.</summary>
    public static DateTime? ClockOverride { get; set; }

    protected ShellCanvas(ShellVisuals visuals)
    {
        Visuals = visuals;
        Focusable = true;
        ClipToBounds = true;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _clock.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _clock.Stop();
        _anim?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Scale and offset from screen space to the control.</summary>
    protected (double Scale, double X, double Y) Fit()
    {
        double s = Math.Min(Bounds.Width / W, Bounds.Height / H);
        if (s <= 0 || double.IsNaN(s)) s = 1;
        return (s, (Bounds.Width - W * s) / 2, (Bounds.Height - H * s) / 2);
    }

    /// <summary>A control point in screen space.</summary>
    protected Point ToScreen(Point p)
    {
        var (s, x, y) = Fit();
        return new Point((p.X - x) / s, (p.Y - y) / s);
    }

    /// <summary>Runs <paramref name="step"/> about 60 times a second (and redraws) until it returns false.</summary>
    protected void Animate(Func<bool> step)
    {
        _anim?.Stop();
        _anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _anim.Tick += (_, _) =>
        {
            if (!step()) _anim!.Stop();
            InvalidateVisual();
        };
        _anim.Start();
    }

    public sealed override void Render(DrawingContext ctx)
    {
        var full = new Rect(Bounds.Size);
        ctx.FillRectangle(Brushes.Black, full);
        DrawWallpaper(ctx, full, Dim);
        var (s, x, y) = Fit();
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(x, y)))
        {
            using (ctx.PushClip(new Rect(0, 0, W, H))) RenderScreen(ctx);
        }
    }

    /// <summary>How much the wallpaper is darkened (0..1).</summary>
    protected virtual double Dim => 0;

    void DrawWallpaper(DrawingContext ctx, Rect full, double dim)
    {
        if (Visuals.Wallpaper is { } wp)
        {
            // uniform to fill
            double s = Math.Max(full.Width / wp.Size.Width, full.Height / wp.Size.Height);
            double w = wp.Size.Width * s, h = wp.Size.Height * s;
            ctx.DrawImage(wp, new Rect(wp.Size), new Rect((full.Width - w) / 2, (full.Height - h) / 2, w, h));
        }
        else DrawDefaultWave(ctx, full);
        if (dim > 0) ctx.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(dim * 255), 0, 0, 0)), full);
    }

    /// <summary>
    /// The default theme's wallpaper, which the shell renders rather than loads (the "wave" effect): deep blue at the
    /// top, a lighter blue wave rising to the right.
    /// </summary>
    static void DrawDefaultWave(DrawingContext ctx, Rect r)
    {
        ctx.FillRectangle(ShellVisuals.DefaultWallpaper, r);
        double w = r.Width, h = r.Height;
        StreamGeometry Wave(double left, double c1, double c2, double right)
        {
            var g = new StreamGeometry();
            using var c = g.Open();
            c.BeginFigure(new Point(0, h * left), true);
            c.CubicBezierTo(new Point(w * 0.35, h * c1), new Point(w * 0.6, h * c2), new Point(w, h * right));
            c.LineTo(new Point(w, h));
            c.LineTo(new Point(0, h));
            c.EndFigure(true);
            return g;
        }
        ctx.DrawGeometry(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(150, 40, 110, 235), 0), new GradientStop(Color.FromArgb(200, 70, 170, 245), 1) },
        }, null, Wave(0.78, 0.86, 0.35, 0.28));
        ctx.DrawGeometry(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(120, 60, 150, 245), 0), new GradientStop(Color.FromArgb(230, 95, 195, 250), 1) },
        }, null, Wave(0.95, 0.98, 0.55, 0.42));
        // a soft light along the crest
        var glow = new RadialGradientBrush
        {
            Center = new RelativePoint(0.8, 0.35, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.8, 0.35, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.4, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(70, 200, 235, 255), 0), new GradientStop(Color.FromArgb(0, 200, 235, 255), 1) },
        };
        ctx.FillRectangle(glow, r);
    }

    /// <summary>Draws the view in screen space (960x544).</summary>
    protected abstract void RenderScreen(DrawingContext ctx);

    /// <summary>The status bar: <paramref name="left"/> text, then Wi-Fi, battery and the clock at the right.</summary>
    protected void DrawStatusBar(DrawingContext ctx, string? left)
    {
        var barColor = Visuals.Theme?.BarColor is { } bc ? ShellVisuals.ToColor(bc) : Color.FromArgb(110, 0, 0, 0);
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(barColor, 0), new GradientStop(Color.FromArgb((byte)(barColor.A / 2), barColor.R, barColor.G, barColor.B), 1) },
        }, new Rect(0, 0, W, StatusBarHeight));
        var now = ClockOverride ?? DateTime.Now;
        var clock = Text(now.ToString("HH:mm", CultureInfo.CurrentCulture), 17, Brushes.White, UiBold);
        double x = W - 14 - clock.Width;
        ctx.DrawText(clock, new Point(x, (StatusBarHeight - clock.Height) / 2));
        x -= 10;
        // battery
        if (Visuals.Texture(ShellAssets.IndicatorRco, ShellAssets.Textures.Battery) is { } bat)
        {
            double bw = bat.Size.Width, bh = bat.Size.Height;
            x -= bw;
            ctx.DrawImage(bat, new Rect(x, (StatusBarHeight - bh) / 2, bw, bh));
        }
        else
        {
            x -= 30;
            var r = new Rect(x, 9, 26, 12);
            ctx.DrawRectangle(null, new Pen(Brushes.White, 1.5), r, 2, 2);
            ctx.FillRectangle(new SolidColorBrush(Color.Parse("#7CD33C")), r.Deflate(2.5));
            ctx.FillRectangle(Brushes.White, new Rect(r.Right, 12.5, 2.5, 5));
        }
        x -= 10;
        if (Visuals.Texture(ShellAssets.IndicatorRco, ShellAssets.Textures.Wifi) is { } wifi)
        {
            double ww = wifi.Size.Width, wh = wifi.Size.Height;
            x -= ww;
            ctx.DrawImage(wifi, new Rect(x, (StatusBarHeight - wh) / 2, ww, wh));
        }
        else
        {
            x -= 22;
            var pen = new Pen(Brushes.White, 2, lineCap: PenLineCap.Round);
            var o = new Point(x + 4, 23);
            ctx.DrawEllipse(Brushes.White, null, o, 2, 2);
            foreach (var rad in new[] { 7.0, 12.0, 17.0 })
                ctx.DrawGeometry(null, pen, Arc(o, rad));
        }
        if (!string.IsNullOrEmpty(left))
        {
            var t = Text(left, 15, Brushes.White, UiBold, x - 30);
            ctx.DrawText(t, new Point(14, (StatusBarHeight - t.Height) / 2));
        }
    }

    static Geometry Arc(Point o, double r)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(new Point(o.X, o.Y - r), false);
        c.ArcTo(new Point(o.X + r, o.Y), new Size(r, r), 0, false, SweepDirection.Clockwise);
        c.EndFigure(false);
        return g;
    }

    protected static FormattedText Text(string s, double size, IBrush brush, Typeface? face = null, double maxWidth = 0, TextAlignment align = TextAlignment.Left, int maxLines = 1)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face ?? Ui, size, brush);
        if (maxWidth > 0) { ft.MaxTextWidth = maxWidth; ft.Trimming = TextTrimming.CharacterEllipsis; }
        ft.TextAlignment = align;
        if (maxLines > 0) ft.MaxLineCount = maxLines;
        return ft;
    }

    /// <summary>Text with a soft dark outline (readable over any wallpaper).</summary>
    protected static void DrawShadowed(DrawingContext ctx, FormattedText ft, Point at, double alpha = 0.75)
    {
        using (ctx.PushOpacity(alpha))
        {
            var geo = ft.BuildGeometry(at + new Point(0, 1.2));
            if (geo != null) ctx.DrawGeometry(Brushes.Black, new Pen(Brushes.Black, 2.5, lineJoin: PenLineJoin.Round), geo);
        }
        ctx.DrawText(ft, at);
    }

    protected static void DrawImageFit(DrawingContext ctx, Bitmap bmp, Rect dest, bool fill)
    {
        double s = fill ? Math.Max(dest.Width / bmp.Size.Width, dest.Height / bmp.Size.Height)
                        : Math.Min(dest.Width / bmp.Size.Width, dest.Height / bmp.Size.Height);
        double w = bmp.Size.Width * s, h = bmp.Size.Height * s;
        ctx.DrawImage(bmp, new Rect(bmp.Size), new Rect(dest.X + (dest.Width - w) / 2, dest.Y + (dest.Height - h) / 2, w, h));
    }
}
