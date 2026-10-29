using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VitaSharp.Core.Shell;

namespace VitaSharp.Desktop.Launcher.Shell;

/// <summary>
/// An app's LiveArea, drawn from its template.xml like the Vita shell: the background, the frames of the template
/// style (a1..a5, psmobile...) with their liveitems (background, image, styled text; autoflip rotation; tapping an
/// item with a web target opens it), and the gate with the startup image and the Start button that launches the
/// game. The status bar shows the title and version. Back: Esc / Circle / the back button / right click.
/// </summary>
public sealed class LiveAreaView : ShellCanvas
{
    readonly ShellApp _app;
    readonly LiveAreaTemplate? _template;
    readonly DispatcherTimer _flip;
    readonly Dictionary<string, int> _flipIndex = new();
    int _flipTick;
    double _launch = -1; // gate launch animation progress (0..1), -1 = idle
    bool _hoverGate, _hoverBack;
    string? _status;

    /// <summary>The gate was activated: launch the app.</summary>
    public event Action<ShellApp>? Started;
    /// <summary>Back to the home screen.</summary>
    public event Action? Closed;

    /// <summary>The app is running (the Start button shows it and does nothing).</summary>
    public bool Running { get; set; }

    public ShellApp App => _app;
    public LiveAreaTemplate? Template => _template;

    static readonly LaRect BackButton = new(6, 42, 46, 46);

    public LiveAreaView(ShellVisuals visuals, ShellApp app) : base(visuals)
    {
        _app = app;
        _template = LiveAreaTemplate.Load(app.Path, visuals.Assets.HasFirmware ? visuals.Assets.FsRoot : null,
            LiveAreaTemplate.VitaLanguage(CultureInfo.CurrentUICulture));
        _flip = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _flip.Tick += (_, _) => Autoflip();
    }

    protected override double Dim => 0.55;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_template?.Frames.Any(f => f.AutoflipSeconds > 0 && f.Items.Count > 1) == true) _flip.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _flip.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    void Autoflip()
    {
        _flipTick++;
        bool changed = false;
        foreach (var f in _template?.Frames ?? [])
            if (f.AutoflipSeconds > 0 && f.Items.Count > 1 && _flipTick % f.AutoflipSeconds == 0)
            {
                _flipIndex[f.Id] = (_flipIndex.GetValueOrDefault(f.Id) + 1) % f.Items.Count;
                changed = true;
            }
        if (changed) InvalidateVisual();
    }

    Bitmap? Image(string? file) => Visuals.File(_template?.Resolve(file));

    string Style => _template?.Style ?? "a1";

    protected override void RenderScreen(DrawingContext ctx)
    {
        var bgRect = R(LiveAreaLayout.BackgroundRect);
        // the page: background image, else the app's pic0, else a plain panel
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, bgRect.Translate(new Vector(0, 4)).Inflate(2), 6, 6);
        using (ctx.PushClip(new RoundedRect(bgRect, 6)))
        {
            var bg = Image(_template?.Background) ?? Visuals.File(Io("sce_sys/pic0.png"));
            if (bg != null) DrawImageFit(ctx, bg, bgRect, fill: true);
            else ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(148, 164, 173)), bgRect);
            DrawFrames(ctx);
        }

        DrawGate(ctx);
        DrawBackButton(ctx);
        if (_status != null)
        {
            var t = Text(_status, 14, Brushes.White, null, 600, TextAlignment.Center, 2);
            var box = new Rect((W - 620) / 2, H - 40, 620, 32);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 20, 24, 32)), null, box, 8, 8);
            ctx.DrawText(t, new Point(box.X + 10, box.Y + (box.Height - t.Height) / 2));
        }
        string version = _app.Version.Length > 0 ? $"   v{_app.Version}" : "";
        DrawStatusBar(ctx, $"{_app.Title}{version}   ·   {_app.TitleId}");

        if (_launch >= 0)
        {
            // the gate opens: the screen fades to black from the gate outwards
            var gate = R(LiveAreaLayout.Gate(Style));
            double t = _launch;
            var full = new Rect(0, 0, W, H);
            var r = new Rect(gate.X + (full.X - gate.X) * t, gate.Y + (full.Y - gate.Y) * t,
                gate.Width + (full.Width - gate.Width) * t, gate.Height + (full.Height - gate.Height) * t);
            var pic = Visuals.File(Io("sce_sys/pic0.png")) ?? Image(_template?.Gate);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(200 * Math.Min(1, t * 2)), 0, 0, 0)), full);
            using (ctx.PushOpacity(Math.Min(1, t * 3)))
            {
                if (pic != null) DrawImageFit(ctx, pic, r, fill: true);
                else ctx.FillRectangle(Brushes.Black, r);
            }
        }
    }

    string? Io(string rel)
    {
        var p = VitaSharp.Core.Io.Vfs.ResolveCaseInsensitive(_app.Path, rel);
        return File.Exists(p) ? p : null;
    }

    static Rect R(LaRect r) => new(r.X, r.Y, r.W, r.H);

    void DrawFrames(DrawingContext ctx)
    {
        if (_template == null) return;
        foreach (var frame in _template.Frames)
        {
            if (LiveAreaLayout.Frame(Style, frame.Id) is not { } fr || frame.Items.Count == 0) continue;
            var item = frame.Items[Math.Min(_flipIndex.GetValueOrDefault(frame.Id), frame.Items.Count - 1)];
            LaRect? bgPlaced = null, imgPlaced = null;
            using (ctx.PushClip(R(fr)))
            {
                if (item.Background is { } b && Image(b.File) is { } bb)
                {
                    var r = LiveAreaLayout.PlaceImage(fr, bb.Size.Width, bb.Size.Height, b);
                    ctx.DrawImage(bb, R(r));
                    bgPlaced = r;
                }
                if (item.Image is { } i && Image(i.File) is { } ib)
                {
                    var r = LiveAreaLayout.PlaceImage(fr, ib.Size.Width, ib.Size.Height, i);
                    ctx.DrawImage(ib, R(r));
                    imgPlaced = r;
                }
                if (item.Text is { } txt && txt.PlainText.Trim().Length > 0)
                    DrawLiveText(ctx, LiveAreaLayout.PlaceText(fr, bgPlaced, imgPlaced, txt), txt);
            }
        }
    }

    /// <summary>A liveitem text: the &lt;str&gt; runs with their colour, size, weight, slant, underline and shadow.</summary>
    static void DrawLiveText(DrawingContext ctx, LaTextPlacement place, LiveAreaText txt)
    {
        var runs = txt.Strings.Where(s => s.Text.Length > 0).ToList();
        if (runs.Count == 0 || place.Box.W <= 0 || place.Box.H <= 0) return;
        string all = string.Concat(runs.Select(r => r.Text));
        var first = runs[0];
        static double Size(LiveAreaString s) => s.Size > 0 ? s.Size : 20;
        var ft = new FormattedText(all, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Ui, Size(first),
            new SolidColorBrush(ShellVisuals.ToColor(first.Color)))
        {
            TextAlignment = place.Horizontal switch { LaAlign.Start => TextAlignment.Left, LaAlign.End => TextAlignment.Right, _ => TextAlignment.Center },
            MaxTextWidth = place.Wrap ? place.Box.W : 0,
            MaxTextHeight = place.Box.H,
            Trimming = txt.Ellipsis || !place.Wrap ? TextTrimming.CharacterEllipsis : TextTrimming.None,
        };
        if (!place.Wrap) ft.MaxTextWidth = place.Box.W;
        if (txt.LineSpace > 0) ft.LineHeight = Size(first) * 1.2 + txt.LineSpace;
        int at = 0;
        foreach (var r in runs)
        {
            ft.SetForegroundBrush(new SolidColorBrush(ShellVisuals.ToColor(r.Color)), at, r.Text.Length);
            ft.SetFontSize(Size(r), at, r.Text.Length);
            if (r.Bold) ft.SetFontWeight(FontWeight.Bold, at, r.Text.Length);
            if (r.Italic) ft.SetFontStyle(FontStyle.Oblique, at, r.Text.Length);
            if (r.Underline) ft.SetTextDecorations(TextDecorations.Underline, at, r.Text.Length);
            at += r.Text.Length;
        }
        double y = place.Vertical switch
        {
            LaAlign.Start => place.Box.Y,
            LaAlign.End => place.Box.Bottom - ft.Height,
            _ => place.Box.Y + (place.Box.H - ft.Height) / 2,
        };
        var origin = new Point(place.Box.X, y);
        if (runs.Any(r => r.Shadow) && ft.BuildGeometry(origin + new Point(1.5, 1.5)) is { } geo)
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), null, geo);
        ctx.DrawText(ft, origin);
    }

    void DrawGate(DrawingContext ctx)
    {
        var gate = R(LiveAreaLayout.Gate(Style));
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, gate.Translate(new Vector(0, 5)).Inflate(3), 12, 12);
        using (ctx.PushClip(new RoundedRect(gate, 10)))
        {
            if (Image(_template?.Gate) is { } g) ctx.DrawImage(g, new Rect(g.Size), gate);
            else
            {
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(47, 51, 50)), gate);
                if (Visuals.File(_app.IconPath) is { } icon)
                {
                    var ir = new Rect(gate.X + (gate.Width - 94) / 2, gate.Y + 15.5, 94, 94);
                    using (ctx.PushGeometryClip(new EllipseGeometry(ir))) DrawImageFit(ctx, icon, ir, fill: true);
                }
            }
            if (_hoverGate && !Running) ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), gate);
        }
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(210, 210, 210)), _hoverGate ? 4 : 3), gate, 10, 10);
        // the Start button at the bottom of the gate
        var btn = StartButtonRect(gate);
        var fill = _app.IsSystem ? Color.FromRgb(110, 118, 128) : Running ? Color.FromRgb(60, 150, 90) : Color.FromRgb(20, 168, 222);
        ctx.DrawRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.5), btn, 14, 14);
        string label = Running ? "Em execução" : "Iniciar";
        var t = Text(label, 15, Brushes.White, UiBold);
        ctx.DrawText(t, new Point(btn.X + (btn.Width - t.Width) / 2, btn.Y + (btn.Height - t.Height) / 2));
    }

    static Rect StartButtonRect(Rect gate) => new(gate.X + (gate.Width - 128) / 2, gate.Bottom - 40, 128, 32);

    void DrawBackButton(DrawingContext ctx)
    {
        var r = R(BackButton);
        var c = r.Center;
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(_hoverBack ? 200 : 140), 20, 24, 32)), new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 1.5), c, 22, 22);
        var pen = new Pen(Brushes.White, 3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        ctx.DrawLine(pen, c + new Point(8, 0), c + new Point(-8, 0));
        ctx.DrawLine(pen, c + new Point(-8, 0), c + new Point(-2, -6));
        ctx.DrawLine(pen, c + new Point(-8, 0), c + new Point(-2, 6));
    }

    // ------------------------------------------------------------------ input

    public void Start()
    {
        if (_launch >= 0 || Running) return;
        if (_app.IsSystem)
        {
            ShowStatus("Apps do sistema ainda não rodam no VitaSharp (a próxima etapa é o shell do Vita).");
            return;
        }
        _launch = 0;
        Animate(() =>
        {
            _launch += 0.06;
            if (_launch < 1) return true;
            _launch = -1;
            Started?.Invoke(_app);
            return false;
        });
    }

    public void Back() => Closed?.Invoke();

    public void ShowStatus(string text)
    {
        _status = text;
        InvalidateVisual();
        DispatcherTimer.RunOnce(() => { if (_status == text) { _status = null; InvalidateVisual(); } }, TimeSpan.FromSeconds(4));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = ToScreen(e.GetPosition(this));
        bool g = LiveAreaLayout.Gate(Style).Contains(p.X, p.Y), b = BackButton.Contains(p.X, p.Y);
        if (g != _hoverGate || b != _hoverBack) { _hoverGate = g; _hoverBack = b; InvalidateVisual(); }
        Cursor = g || b || LinkAt(p) != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = ToScreen(e.GetPosition(this));
        if (e.InitialPressMouseButton == MouseButton.Right) { Back(); return; }
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (BackButton.Contains(p.X, p.Y)) Back();
        else if (LiveAreaLayout.Gate(Style).Contains(p.X, p.Y)) Start();
        else if (LinkAt(p) is { } url) OpenLink(url);
    }

    /// <summary>The web target of the liveitem under a point (psts: store links and others are not opened).</summary>
    string? LinkAt(Point p)
    {
        foreach (var f in _template?.Frames ?? [])
        {
            if (f.Items.Count == 0 || LiveAreaLayout.Frame(Style, f.Id) is not { } fr || !fr.Contains(p.X, p.Y)) continue;
            var target = f.Items[Math.Min(_flipIndex.GetValueOrDefault(f.Id), f.Items.Count - 1)].Target;
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return target;
        }
        return null;
    }

    static void OpenLink(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Enter or Key.Space: Start(); break;
            case Key.Escape or Key.Back or Key.BrowserBack: Back(); break;
            default: return;
        }
        e.Handled = true;
    }
}
